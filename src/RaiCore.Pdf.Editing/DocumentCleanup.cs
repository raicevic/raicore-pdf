using System.Text;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>
/// Removes what points at deleted pages before a save: named destinations, outline entries and links to them, and
/// the form fields whose widgets were on them. Outline entries that still have entries below them stay, without their
/// destination.
/// </summary>
/// <param name="table">The document's objects.</param>
/// <param name="catalog">The document catalog.</param>
/// <param name="pages">The pages left.</param>
/// <param name="dead">The objects that were pages (or page tree nodes) and are no longer.</param>
/// <param name="stripped">Outline items whose destination an earlier cleanup removed: they go once they have no items below them.</param>
internal sealed class DocumentCleanup(ObjectTable table, PdfDictionary catalog, IReadOnlyList<PdfReference> pages, HashSet<int> dead, HashSet<PdfDictionary> stripped)
{
    /// <summary>The deepest outline or field tree followed.</summary>
    private const int MaxDepth = 64;

    /// <summary>The most entries of an outline, name tree or field tree visited (a guard against loops).</summary>
    private const int MaxNodes = 1_000_000;

    private readonly HashSet<string> _removedNames = new(StringComparer.Ordinal);
    private Dictionary<string, PdfObject>? _names;
    private int _visited;

    public void Run()
    {
        if (dead.Count == 0)
        {
            return;
        }

        _names = NamedDestinations();
        RemoveDeadNames();
        if (table.Dictionary(catalog["Outlines"]) is { } outlines)
        {
            var (count, _) = CleanOutline(outlines, 0);
            outlines["Count"] = count > 0 ? new PdfInteger(count) : null;
        }

        foreach (var page in pages)
        {
            RemoveDeadLinks(table.Dictionary(page)!);
        }

        RemoveDeadFields();
    }

    /// <summary>Whether a destination (explicit, named, or a GoTo action's) leads to a deleted page or a removed name.</summary>
    private bool IsDead(PdfObject? destination)
    {
        for (var hops = 0; hops < 4; hops++)
        {
            switch (table.Resolve(destination))
            {
                case PdfArray { Items.Count: > 0 } array:
                    return array.Items[0] is PdfReference page && dead.Contains(page.ObjectNumber);
                case PdfDictionary dictionary:
                    destination = dictionary["D"];
                    break;
                case PdfName name:
                    destination = LookUp(name.Value);
                    if (destination is null)
                    {
                        return _removedNames.Contains(name.Value);
                    }

                    break;
                case PdfString text:
                    var key = Encoding.Latin1.GetString(text.Bytes);
                    destination = LookUp(key);
                    if (destination is null)
                    {
                        return _removedNames.Contains(key);
                    }

                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private PdfObject? LookUp(string name) => _names!.GetValueOrDefault(name);

    /// <summary>The destination of an outline item or link: its <c>/Dest</c>, or the <c>/D</c> of its GoTo action.</summary>
    private PdfObject? DestinationOf(PdfDictionary item) =>
        item["Dest"] ?? (table.Dictionary(item["A"]) is { } action && table.Resolve(action["S"]) is PdfName { Value: "GoTo" } ? action["D"] : null);

    /// <summary>The named destinations: the catalog's <c>/Dests</c> (PDF 1.1) and the <c>/Names /Dests</c> name tree.</summary>
    private Dictionary<string, PdfObject> NamedDestinations()
    {
        var names = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (table.Dictionary(catalog["Dests"]) is { } dests)
        {
            foreach (var (key, value) in dests.Entries)
            {
                names.TryAdd(key.Value, value);
            }
        }

        if (table.Dictionary(table.Dictionary(catalog["Names"])?["Dests"]) is { } tree)
        {
            _visited = 0;
            CollectNames(tree, names, 0);
        }

        return names;
    }

    private void CollectNames(PdfDictionary node, Dictionary<string, PdfObject> names, int depth)
    {
        if (depth > MaxDepth || ++_visited > MaxNodes)
        {
            return;
        }

        if (table.Array(node["Names"]) is { } pairs)
        {
            for (var i = 0; i + 1 < pairs.Items.Count; i += 2)
            {
                if (table.Resolve(pairs.Items[i]) is PdfString key)
                {
                    names.TryAdd(Encoding.Latin1.GetString(key.Bytes), pairs.Items[i + 1]);
                }
            }
        }

        foreach (var kid in table.Array(node["Kids"])?.Items ?? [])
        {
            if (table.Dictionary(kid) is { } child)
            {
                CollectNames(child, names, depth + 1);
            }
        }
    }

    /// <summary>Removes the named destinations that lead to deleted pages.</summary>
    private void RemoveDeadNames()
    {
        // A name's destination is checked against the names as they were, so that a name pointing at another name still resolves.
        var deadNames = _names!.Where(n => IsDead(n.Value)).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        if (deadNames.Count == 0)
        {
            return;
        }

        _removedNames.UnionWith(deadNames);
        foreach (var name in deadNames)
        {
            _names!.Remove(name);
        }

        if (table.Dictionary(catalog["Dests"]) is { } dests)
        {
            foreach (var name in deadNames)
            {
                dests[name] = null;
            }
        }

        if (table.Dictionary(table.Dictionary(catalog["Names"])?["Dests"]) is { } tree)
        {
            _visited = 0;
            CleanNameTree(tree, deadNames, isRoot: true, 0);
        }
    }

    /// <summary>Removes names from a name tree node and its kids; returns whether the node still has names.</summary>
    private bool CleanNameTree(PdfDictionary node, HashSet<string> deadNames, bool isRoot, int depth)
    {
        if (depth > MaxDepth || ++_visited > MaxNodes)
        {
            return true;
        }

        var keys = new List<PdfString>();
        if (table.Array(node["Names"]) is { } pairs)
        {
            var kept = new PdfArray();
            for (var i = 0; i + 1 < pairs.Items.Count; i += 2)
            {
                if (table.Resolve(pairs.Items[i]) is PdfString key && !deadNames.Contains(Encoding.Latin1.GetString(key.Bytes)))
                {
                    kept.Items.Add(pairs.Items[i]);
                    kept.Items.Add(pairs.Items[i + 1]);
                    keys.Add(key);
                }
            }

            node["Names"] = kept;
        }

        if (table.Array(node["Kids"]) is { } kids)
        {
            var kept = new PdfArray();
            foreach (var kid in kids.Items)
            {
                if (table.Dictionary(kid) is { } child && CleanNameTree(child, deadNames, isRoot: false, depth + 1))
                {
                    kept.Items.Add(kid);
                    if (table.Array(child["Limits"]) is { Items.Count: 2 } limits)
                    {
                        keys.AddRange(limits.Items.Select(table.Resolve).OfType<PdfString>());
                    }
                }
            }

            node["Kids"] = kept;
        }

        if (!isRoot && keys.Count > 0)
        {
            // Keys are sorted in a name tree: the limits are the first and last left.
            var sorted = keys.OrderBy(k => k.Bytes.ToArray(), ByteOrder.Instance).ToList();
            node["Limits"] = new PdfArray(sorted[0], sorted[^1]);
        }

        return keys.Count > 0;
    }

    /// <summary>
    /// Removes outline items that lead to deleted pages, keeping those with items left below them (without their
    /// destination), and fixes the links between items and their counts. Returns the number of items visible below
    /// <paramref name="parent"/> when it is open, and whether it has any.
    /// </summary>
    private (int Visible, bool Any) CleanOutline(PdfDictionary parent, int depth)
    {
        var kept = new List<(PdfObject Reference, PdfDictionary Item)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var reference = parent["First"];
        while (depth <= MaxDepth && table.Dictionary(reference) is { } item && seen.Add(item) && ++_visited <= MaxNodes)
        {
            var next = item["Next"];
            var (below, hasChildren) = CleanOutline(item, depth + 1);
            var open = table.Resolve(item["Count"]) is PdfInteger { Value: > 0 };
            if (hasChildren)
            {
                item["Count"] = new PdfInteger(open ? below : -below);
            }
            else
            {
                item["Count"] = null;
            }

            if (IsDead(DestinationOf(item)) || stripped.Contains(item))
            {
                if (!hasChildren)
                {
                    reference = next;
                    continue;
                }

                item["Dest"] = null;
                item["A"] = null;
                stripped.Add(item);
            }

            kept.Add((reference!, item));
            reference = next;
        }

        for (var i = 0; i < kept.Count; i++)
        {
            kept[i].Item["Prev"] = i > 0 ? kept[i - 1].Reference : null;
            kept[i].Item["Next"] = i + 1 < kept.Count ? kept[i + 1].Reference : null;
        }

        parent["First"] = kept.Count > 0 ? kept[0].Reference : null;
        parent["Last"] = kept.Count > 0 ? kept[^1].Reference : null;
        var visible = kept.Sum(k => 1 + (table.Resolve(k.Item["Count"]) is PdfInteger { Value: > 0 } n ? (int)n.Value : 0));
        return (visible, kept.Count > 0);
    }

    /// <summary>Removes a page's links to deleted pages.</summary>
    private void RemoveDeadLinks(PdfDictionary page)
    {
        if (table.Array(page["Annots"]) is not { } annotations)
        {
            return;
        }

        var kept = new PdfArray(annotations.Items.Where(a => !(table.Dictionary(a) is { } annotation
            && table.Resolve(annotation["Subtype"]) is PdfName { Value: "Link" }
            && IsDead(DestinationOf(annotation)))));
        if (kept.Items.Count != annotations.Items.Count)
        {
            page["Annots"] = kept.Items.Count > 0 ? kept : null;
        }
    }

    /// <summary>Removes the widgets of deleted pages from the form's field tree, and the fields left without widgets.</summary>
    private void RemoveDeadFields()
    {
        if (table.Dictionary(catalog["AcroForm"]) is not { } form || table.Array(form["Fields"]) is not { } fields)
        {
            return;
        }

        var deadAnnotations = new HashSet<int>();
        foreach (var number in dead)
        {
            if (table.Dictionary(new PdfReference(number)) is { } page && table.Resolve(page["Type"]) is PdfName { Value: "Page" })
            {
                foreach (var annotation in table.Array(page["Annots"])?.Items ?? [])
                {
                    if (annotation is PdfReference r)
                    {
                        deadAnnotations.Add(r.ObjectNumber);
                    }
                }
            }
        }

        // An annotation on a page that is still there stays, even when a deleted page listed it too.
        foreach (var page in pages)
        {
            foreach (var annotation in table.Array(table.Dictionary(page)!["Annots"])?.Items ?? [])
            {
                if (annotation is PdfReference r)
                {
                    deadAnnotations.Remove(r.ObjectNumber);
                }
            }
        }

        if (deadAnnotations.Count > 0)
        {
            _visited = 0;
            form["Fields"] = CleanFields(fields, deadAnnotations, 0);
        }
    }

    private PdfArray CleanFields(PdfArray fields, HashSet<int> deadAnnotations, int depth)
    {
        var kept = new PdfArray();
        foreach (var item in fields.Items)
        {
            if (item is PdfReference r && deadAnnotations.Contains(r.ObjectNumber))
            {
                continue; // A widget (or a field merged with its widget) on a deleted page.
            }

            if (depth < MaxDepth && ++_visited <= MaxNodes && table.Dictionary(item) is { } field && table.Array(field["Kids"]) is { Items.Count: > 0 } kids)
            {
                var left = CleanFields(kids, deadAnnotations, depth + 1);
                if (left.Items.Count == 0)
                {
                    continue; // All its widgets were on deleted pages.
                }

                field["Kids"] = left;
            }

            kept.Items.Add(item);
        }

        return kept;
    }

    /// <summary>Byte-wise order, as name trees sort their keys.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
