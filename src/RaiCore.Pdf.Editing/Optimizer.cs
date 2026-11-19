using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>The work of <see cref="PdfEditor.Optimize"/> on the objects a save would write.</summary>
/// <param name="table">The document's objects.</param>
/// <param name="catalog">The document catalog.</param>
/// <param name="info">The document information dictionary, if any.</param>
/// <param name="dead">The objects that are no longer pages: not written, so not looked at.</param>
internal sealed class Optimizer(ObjectTable table, PdfReference catalog, PdfReference? info, HashSet<int> dead)
{
    /// <summary>The resource categories whose entries content names (ISO 32000-2, 7.8.3).</summary>
    private static readonly string[] Categories = ["Font", "XObject", "ExtGState", "Pattern", "Shading", "ColorSpace", "Properties"];

    /// <summary>Dictionaries whose identity does not matter, so identical ones may be merged (besides streams).</summary>
    private static readonly HashSet<string> MergeableTypes = new(StringComparer.Ordinal) { "Font", "FontDescriptor", "Encoding", "ExtGState" };

    /// <summary>The deepest a form without resources of its own is followed into (it uses the resources of what draws it).</summary>
    private const int MaxFormDepth = 16;

    /// <summary>How many rounds of merging are done: each round can make the objects that refer to merged ones identical.</summary>
    private const int MaxMergeRounds = 8;

    /// <summary>Removes the resource entries no content names. Returns how many were removed.</summary>
    public int RemoveUnusedResources()
    {
        // The names each resources dictionary's users need; null when one of them is not read (keep everything).
        var usage = new Dictionary<PdfDictionary, HashSet<string>?>(ReferenceEqualityComparer.Instance);
        foreach (var (_, container) in Reachable())
        {
            var dictionary = container is PdfStreamObject stream ? stream.Dictionary : container as PdfDictionary;
            if (dictionary is null || table.Dictionary(dictionary["Resources"]) is not { } resources)
            {
                continue;
            }

            var names = NamesUsedBy(container, dictionary, resources);
            if (!usage.TryGetValue(resources, out var used))
            {
                usage[resources] = names;
            }
            else if (used is not null)
            {
                if (names is null)
                {
                    usage[resources] = null;
                }
                else
                {
                    used.UnionWith(names);
                }
            }
        }

        // A category dictionary (/Font << ... >>) may be shared by several resources dictionaries.
        var categories = new Dictionary<PdfDictionary, HashSet<string>?>(ReferenceEqualityComparer.Instance);
        foreach (var (resources, used) in usage)
        {
            foreach (var category in Categories)
            {
                if (table.Dictionary(resources[category]) is not { } names)
                {
                    continue;
                }

                var needed = used?.Where(n => n.StartsWith(category + "/", StringComparison.Ordinal)).Select(n => n[(category.Length + 1)..]).ToHashSet(StringComparer.Ordinal);
                if (!categories.TryGetValue(names, out var known))
                {
                    categories[names] = needed;
                }
                else if (known is not null)
                {
                    if (needed is null)
                    {
                        categories[names] = null;
                    }
                    else
                    {
                        known.UnionWith(needed);
                    }
                }
            }
        }

        var removed = 0;
        foreach (var (names, needed) in categories)
        {
            if (needed is null)
            {
                continue;
            }

            foreach (var key in names.Entries.Select(e => e.Key).Where(k => !needed.Contains(k.Value)).ToList())
            {
                names[key] = null;
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Merges identical objects into one and points every reference at it. Returns how many were merged away.</summary>
    public int MergeDuplicates()
    {
        var merged = new Dictionary<int, int>();
        int Final(int number)
        {
            while (merged.TryGetValue(number, out var to))
            {
                number = to;
            }

            return number;
        }

        var candidates = Reachable()
            .Where(c => c.Container is PdfStreamObject s ? IsMergeableStream(s) : c.Container is PdfDictionary d && IsMergeableDictionary(d))
            .Where(c => ReferenceEquals(table.Resolve(new PdfReference(c.Number)), c.Container))
            .Select(c => c.Number)
            .Distinct()
            .ToList();

        for (var round = 0; round < MaxMergeRounds; round++)
        {
            var added = 0;
            var keys = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var number in candidates.Where(n => !merged.ContainsKey(n)).Order())
            {
                var key = Key(table.Resolve(new PdfReference(number))!, Final);
                if (keys.TryGetValue(key, out var first))
                {
                    merged[number] = first;
                    added++;
                }
                else
                {
                    keys[key] = number;
                }
            }

            if (added == 0)
            {
                break;
            }
        }

        if (merged.Count > 0)
        {
            foreach (var (_, container) in Reachable())
            {
                Redirect(container, Final);
            }
        }

        return merged.Count;
    }

    /// <summary>Compresses the streams that have no filter, where that makes them smaller. Returns how many were compressed.</summary>
    public int CompressStreams()
    {
        var compressed = 0;
        foreach (var number in Reachable().Select(c => c.Number).Distinct().ToList())
        {
            var reference = new PdfReference(number);
            if (table.Resolve(reference) is not PdfStreamObject stream
                || stream.Dictionary["Filter"] is not null
                || table.Resolve(stream.Dictionary["Type"]) is PdfName { Value: "Metadata" or "XRef" or "ObjStm" })
            {
                continue;
            }

            var data = Flate.Encode(stream.Data);
            if (data.Length >= stream.Data.Length)
            {
                continue;
            }

            var dictionary = new PdfDictionary();
            foreach (var (key, value) in stream.Dictionary.Entries)
            {
                if (key.Value != "DecodeParms")
                {
                    dictionary.AddUnchecked(key, value);
                }
            }

            dictionary["Filter"] = new PdfName("FlateDecode");
            table.Set(reference, new PdfStreamObject(data, dictionary));
            compressed++;
        }

        return compressed;
    }

    /// <summary>
    /// Every container (dictionary, array, stream) a save would write, with the number of the indirect object it is in:
    /// indirect objects once, direct ones inside them.
    /// </summary>
    private List<(int Number, PdfObject Container)> Reachable()
    {
        var result = new List<(int, PdfObject)>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        void Enqueue(PdfObject? obj)
        {
            if (obj is PdfReference reference && !dead.Contains(reference.ObjectNumber) && seen.Add(reference.ObjectNumber))
            {
                queue.Enqueue(reference.ObjectNumber);
            }
        }

        Enqueue(catalog);
        Enqueue(info);
        var stack = new Stack<PdfObject>();
        while (queue.TryDequeue(out var number))
        {
            if (table.Resolve(new PdfReference(number)) is not { } obj)
            {
                continue;
            }

            stack.Push(obj);
            while (stack.TryPop(out var container))
            {
                if (container is not (PdfDictionary or PdfArray or PdfStreamObject))
                {
                    continue;
                }

                result.Add((number, container));
                IEnumerable<PdfObject> children = container switch
                {
                    PdfStreamObject s => s.Dictionary.Entries.Select(e => e.Value),
                    PdfDictionary d => d.Entries.Select(e => e.Value),
                    PdfArray a => a.Items,
                    _ => [],
                };
                foreach (var child in children)
                {
                    if (child is PdfReference)
                    {
                        Enqueue(child);
                    }
                    else
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The resource names (<c>Font/F1</c>, <c>XObject/Im1</c>, ...) the content of a resources dictionary's owner uses,
    /// or <c>null</c> when the owner's content is not read (an owner of an unknown kind, or content that cannot be
    /// decoded): then its resources are kept whole.
    /// </summary>
    private HashSet<string>? NamesUsedBy(PdfObject owner, PdfDictionary dictionary, PdfDictionary resources)
    {
        var contents = new List<PdfStreamObject>();
        if (table.Resolve(dictionary["Type"]) is PdfName { Value: "Page" })
        {
            switch (table.Resolve(dictionary["Contents"]))
            {
                case PdfStreamObject single:
                    contents.Add(single);
                    break;
                case PdfArray array:
                    contents.AddRange(array.Items.Select(table.Resolve).OfType<PdfStreamObject>());
                    break;
            }
        }
        else if (owner is PdfStreamObject stream
            && (table.Resolve(dictionary["Subtype"]) is PdfName { Value: "Form" } || table.Resolve(dictionary["PatternType"]) is PdfInteger { Value: 1 }))
        {
            contents.Add(stream);
        }
        else if (table.Resolve(dictionary["Subtype"]) is PdfName { Value: "Type3" } && table.Dictionary(dictionary["CharProcs"]) is { } procedures)
        {
            contents.AddRange(procedures.Entries.Select(e => table.Resolve(e.Value)).OfType<PdfStreamObject>());
        }
        else
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in contents)
        {
            if (!Scan(content, resources, names, 0))
            {
                return null;
            }
        }

        return names;
    }

    /// <summary>Adds the resource names a content stream uses; follows forms without resources of their own. Returns false when it cannot be read.</summary>
    private bool Scan(PdfStreamObject content, PdfDictionary resources, HashSet<string> names, int depth)
    {
        byte[] data;
        try
        {
            data = StreamDecoder.Decode(content, table.Resolve);
        }
        catch (PdfFormatException)
        {
            return false;
        }

        var parser = new ContentParser(data, new ExtractionDiagnostics());
        while (parser.TryRead(out var operation))
        {
            var operands = operation.Operands;
            PdfName? Operand(int index) => index >= 0 && index < operands.Count ? operands[index] as PdfName : null;
            switch (operation.Operator)
            {
                case "Tf":
                    Use("Font", Operand(0));
                    break;
                case "Do":
                    Use("XObject", Operand(operands.Count - 1));
                    if (Operand(operands.Count - 1) is { } form && depth < MaxFormDepth
                        && table.Resolve(table.Dictionary(resources["XObject"])?[form]) is PdfStreamObject drawn
                        && table.Resolve(drawn.Dictionary["Subtype"]) is PdfName { Value: "Form" }
                        && drawn.Dictionary["Resources"] is null
                        && !Scan(drawn, resources, names, depth + 1))
                    {
                        return false;
                    }

                    break;
                case "gs":
                    Use("ExtGState", Operand(operands.Count - 1));
                    break;
                case "sh":
                    Use("Shading", Operand(operands.Count - 1));
                    break;
                case "cs" or "CS":
                    Use("ColorSpace", Operand(operands.Count - 1));
                    break;
                case "scn" or "SCN":
                    Use("Pattern", Operand(operands.Count - 1));
                    break;
                case "BDC" or "DP":
                    Use("Properties", Operand(1));
                    break;
                case "BI" when operation.Image is { } image:
                    Use("ColorSpace", (image.Dictionary["CS"] ?? image.Dictionary["ColorSpace"]) as PdfName);
                    break;
            }
        }

        return true;

        void Use(string category, PdfName? name)
        {
            if (name is not null)
            {
                names.Add(category + "/" + name.Value);
            }
        }
    }

    private bool IsMergeableStream(PdfStreamObject stream) =>
        table.Resolve(stream.Dictionary["Type"]) is not PdfName { Value: "XRef" or "ObjStm" };

    private bool IsMergeableDictionary(PdfDictionary dictionary) =>
        table.Resolve(dictionary["Type"]) is PdfName type && MergeableTypes.Contains(type.Value);

    /// <summary>A key that is the same for two objects exactly when they are identical, references compared after merging.</summary>
    private static string Key(PdfObject obj, Func<int, int> final)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(obj, hash, final, 0);
        return Convert.ToHexString(hash.GetHashAndReset());

        static void Append(PdfObject obj, IncrementalHash hash, Func<int, int> final, int depth)
        {
            void Text(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));
            switch (obj)
            {
                case PdfReference reference:
                    Text($"R{final(reference.ObjectNumber)} ");
                    break;
                case PdfStreamObject stream:
                    Append(stream.Dictionary, hash, final, depth + 1);
                    Text($"stream {stream.Data.Length} ");
                    hash.AppendData(stream.Data);
                    break;
                case PdfDictionary dictionary:
                    Text("<<");
                    foreach (var (key, value) in dictionary.Entries.Where(e => e.Key.Value != "Length").OrderBy(e => e.Key.Value, StringComparer.Ordinal))
                    {
                        Text($"/{key.Value} ");
                        Append(value, hash, final, depth + 1);
                    }

                    Text(">>");
                    break;
                case PdfArray array:
                    Text("[");
                    foreach (var item in array.Items)
                    {
                        Append(item, hash, final, depth + 1);
                    }

                    Text("]");
                    break;
                case PdfName name:
                    Text($"/{name.Value} ");
                    break;
                case PdfString text:
                    Text($"({text.Bytes.Length})");
                    hash.AppendData(text.Bytes);
                    break;
                case PdfInteger integer:
                    Text(integer.Value.ToString(CultureInfo.InvariantCulture) + " ");
                    break;
                case PdfReal real:
                    Text(real.Value.ToString("R", CultureInfo.InvariantCulture) + " ");
                    break;
                case PdfBoolean boolean:
                    Text(boolean.Value ? "true " : "false ");
                    break;
                default:
                    Text("null ");
                    break;
            }
        }
    }

    /// <summary>Points the references of a container at the objects they were merged into.</summary>
    private static void Redirect(PdfObject container, Func<int, int> final)
    {
        switch (container)
        {
            case PdfStreamObject stream:
                Redirect(stream.Dictionary, final);
                break;
            case PdfDictionary dictionary:
                foreach (var (key, value) in dictionary.Entries.ToList())
                {
                    if (value is PdfReference reference && final(reference.ObjectNumber) != reference.ObjectNumber)
                    {
                        dictionary[key] = new PdfReference(final(reference.ObjectNumber));
                    }
                }

                break;
            case PdfArray array:
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (array.Items[i] is PdfReference reference && final(reference.ObjectNumber) != reference.ObjectNumber)
                    {
                        array.Items[i] = new PdfReference(final(reference.ObjectNumber));
                    }
                }

                break;
        }
    }
}
