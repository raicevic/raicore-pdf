using System.Globalization;
using System.Text;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Tests.Tagging;

/// <summary>A structure element as read back from a file: its type, attributes, text and children.</summary>
internal sealed class InspectedElement
{
    public required string Type { get; init; }

    public required PdfDictionary Dictionary { get; init; }

    public List<InspectedElement> Children { get; } = [];

    /// <summary>The marked content of the element itself (not its children), in order: (page index, stream, MCID).</summary>
    public List<(int Page, PdfObject? Stream, int Mcid)> Content { get; } = [];

    /// <summary>The link annotations of the element (OBJR kids).</summary>
    public List<PdfDictionary> Annotations { get; } = [];

    /// <summary>The text of the element's own marked content.</summary>
    public string OwnText { get; set; } = string.Empty;

    /// <summary>The element's kids in <c>/K</c> order: an index into <see cref="Content"/>, or a child element.</summary>
    public List<object> Order { get; } = [];

    /// <summary>The text of each piece of <see cref="Content"/>.</summary>
    public List<string> ContentTexts { get; } = [];

    /// <summary>The text of the element and its descendants, in reading order (<c>/K</c> order).</summary>
    public string Text => string.Concat(Order.Select(kid => kid is InspectedElement child ? child.Text : ContentTexts[(int)kid]));

    public string? Entry(string key) => Dictionary[key] is PdfString s ? PdfText.Decode(s.Bytes) : null;

    public IEnumerable<InspectedElement> Descendants() => Children.SelectMany(child => child.Descendants().Prepend(child));

    /// <summary>The element tree as text, e.g. <c>Document(H1 P(Link))</c>.</summary>
    public override string ToString() => Children.Count == 0 ? Type : $"{Type}({string.Join(' ', Children)})";
}

/// <summary>
/// Reads the structure tree of a tagged PDF back with <see cref="PdfReader"/> and checks the marked content against it:
/// every painting operator is marked (tagged or artifact), sequences are flat (no MCID inside another sequence), MCIDs are
/// unique per stream, and every MCID maps through the parent tree to the element whose <c>/K</c> refers to it.
/// </summary>
internal static class TaggedPdfInspector
{
    private static readonly HashSet<string> PaintingOperators = ["Tj", "TJ", "'", "\"", "f", "F", "f*", "B", "B*", "b", "b*", "S", "s", "sh", "Do", "BI"];

    public static InspectedElement Inspect(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        var catalog = reader.Catalog;
        Assert.True(reader.Resolve(catalog["MarkInfo"]) is PdfDictionary markInfo && markInfo["Marked"] is PdfBoolean { Value: true }, "/MarkInfo /Marked true");
        var treeRoot = Assert.IsType<PdfDictionary>(reader.Resolve(catalog["StructTreeRoot"]));
        var parentTree = ParentTree(reader, Assert.IsType<PdfDictionary>(reader.Resolve(treeRoot["ParentTree"])));

        var pages = reader.Pages.Select(page => page.Dictionary).ToList();
        int PageIndex(PdfObject? reference) => pages.FindIndex(page => ReferenceEquals(page, reader.Resolve(reference)));

        // Walk the elements, collecting what each one's /K says.
        var elementObjects = new Dictionary<PdfDictionary, InspectedElement>(ReferenceEqualityComparer.Instance);
        InspectedElement Read(PdfDictionary dictionary)
        {
            var element = new InspectedElement { Type = Assert.IsType<PdfName>(dictionary["S"]).Value, Dictionary = dictionary };
            Assert.True(elementObjects.TryAdd(dictionary, element), "An element appears twice in the structure tree.");
            var kids = reader.Resolve(dictionary["K"]) switch
            {
                PdfArray array => array.Items,
                null => [],
                var single => [single],
            };

            foreach (var kid in kids)
            {
                switch (reader.Resolve(kid))
                {
                    case PdfDictionary { } d when d["Type"] is PdfName { Value: "MCR" }:
                        element.Order.Add(element.Content.Count);
                        element.Content.Add((PageIndex(d["Pg"] ?? dictionary["Pg"]), d["Stm"], (int)((PdfInteger)d["MCID"]!).Value));
                        break;
                    case PdfDictionary { } d when d["Type"] is PdfName { Value: "OBJR" }:
                        element.Annotations.Add(Assert.IsType<PdfDictionary>(reader.Resolve(d["Obj"])));
                        break;
                    case PdfDictionary { } d:
                        Assert.Same(dictionary, reader.Resolve(d["P"]));
                        var child = Read(d);
                        element.Children.Add(child);
                        element.Order.Add(child);
                        break;
                    case PdfInteger mcid:
                        element.Order.Add(element.Content.Count);
                        element.Content.Add((PageIndex(dictionary["Pg"]), null, (int)mcid.Value));
                        break;
                    default:
                        Assert.Fail($"Unexpected structure kid {kid}.");
                        break;
                }
            }

            return element;
        }

        var root = Read(Assert.IsType<PdfDictionary>(reader.Resolve(treeRoot["K"])));

        // Check every content stream with marked content against the parent tree, and collect the text per MCID.
        var texts = new Dictionary<(int Page, PdfObject? Stream, int Mcid), string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var page = reader.Pages[i];
            Assert.Equal("S", (page.Dictionary["Tabs"] as PdfName)?.Value);
            var content = Encoding.Latin1.GetString(page.GetContent());
            CheckStream(reader, content, page.Resources, StructParents(page.Dictionary), parentTree, elementObjects, i, null, texts);
        }

        // Every element's MCIDs must exist in their streams.
        foreach (var element in elementObjects.Values)
        {
            var own = new StringBuilder();
            foreach (var key in element.Content)
            {
                var match = texts.Keys.FirstOrDefault(k => k.Page == key.Page && k.Mcid == key.Mcid && SameObject(reader, k.Stream, key.Stream));
                Assert.True(texts.ContainsKey(match) && match.Page == key.Page, $"{element.Type} refers to MCID {key.Mcid} on page {key.Page}, which is not in the content.");
                own.Append(texts[match]);
                element.ContentTexts.Add(texts[match]);
            }

            element.OwnText = own.ToString();
            foreach (var annotation in element.Annotations)
            {
                Assert.True(annotation["StructParent"] is PdfInteger key && ReferenceEquals(parentTree[(int)key.Value], element.Dictionary), "An annotation's StructParent leads back to its element.");
            }
        }

        return root;
    }

    /// <summary>The page's <c>/StructParents</c>, or -1.</summary>
    private static int StructParents(PdfDictionary dictionary) => dictionary["StructParents"] is PdfInteger key ? (int)key.Value : -1;

    private static bool SameObject(PdfReader reader, PdfObject? a, PdfObject? b) =>
        (a is null && b is null) || (a is not null && b is not null && ReferenceEquals(reader.Resolve(a), reader.Resolve(b)));

    private static Dictionary<int, object> ParentTree(PdfReader reader, PdfDictionary tree)
    {
        Assert.Null(tree["Kids"]); // One leaf is all this writer produces.
        var nums = Assert.IsType<PdfArray>(reader.Resolve(tree["Nums"])).Items;
        var result = new Dictionary<int, object>();
        for (var i = 0; i < nums.Count; i += 2)
        {
            var key = (int)Assert.IsType<PdfInteger>(nums[i]).Value;
            result[key] = reader.Resolve(nums[i + 1]) switch
            {
                PdfArray array => array.Items.Select(item => (object?)reader.Resolve(item)).ToList(),
                var value => value!,
            };
        }

        return result;
    }

    /// <summary>
    /// Tokenizes a content stream, checking the marking rules and recording the text of each MCID (decoded through the
    /// fonts' ToUnicode maps). Forms painted with <c>Do</c> that carry <c>/StructParents</c> are checked recursively.
    /// </summary>
    private static void CheckStream(
        PdfReader reader, string content, PdfDictionary resources, int structParents, Dictionary<int, object> parentTree,
        Dictionary<PdfDictionary, InspectedElement> elements, int page, PdfObject? stream, Dictionary<(int, PdfObject?, int), string> texts,
        bool inheritedMark = false)
    {
        var lexer = new PdfLexer(Encoding.Latin1.GetBytes(content));
        var operands = new List<string>();
        var open = new Stack<(string Tag, int? Mcid, bool Artifact)>();
        var seen = new HashSet<int>();
        PdfDictionary? font = null;
        var depth = 0; // Inside << >> or [ ].
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End)
            {
                break;
            }

            var text = Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(content), token.Start, token.Length);
            if (token.Kind is TokenKind.DictionaryStart or TokenKind.ArrayStart)
            {
                depth++;
            }
            else if (token.Kind is TokenKind.DictionaryEnd or TokenKind.ArrayEnd)
            {
                depth--;
            }

            if (token.Kind != TokenKind.Keyword || depth > 0)
            {
                operands.Add(text);
                continue;
            }

            switch (text)
            {
                case "BDC" or "BMC":
                {
                    var tag = operands[0];
                    var mcidIndex = operands.IndexOf("/MCID");
                    int? mcid = mcidIndex >= 0 ? int.Parse(operands[mcidIndex + 1], CultureInfo.InvariantCulture) : null;
                    var artifact = tag == "/Artifact";
                    if (mcid is not null || artifact)
                    {
                        Assert.False(open.Any(o => o.Mcid is not null || o.Artifact), $"{tag} is nested inside another marked sequence (page {page}).");
                    }

                    if (mcid is { } id)
                    {
                        Assert.True(seen.Add(id), $"MCID {id} is used twice in one stream (page {page}).");
                        Assert.True(structParents >= 0, "A stream with MCIDs has /StructParents.");
                        var owners = Assert.IsType<List<object?>>(parentTree[structParents]);
                        var owner = Assert.IsType<PdfDictionary>(owners[id]);
                        var element = elements[owner];
                        Assert.Equal("/" + element.Type, tag);
                        Assert.Contains(element.Content, c => c.Page == page && c.Mcid == id && SameObject(reader, c.Stream, stream));
                        texts[(page, stream, id)] = string.Empty;
                    }

                    open.Push((tag, mcid, artifact));
                    break;
                }

                case "EMC":
                    Assert.NotEmpty(open);
                    open.Pop();
                    break;
                case "Tf":
                    font = reader.Resolve((reader.Resolve(resources["Font"]) as PdfDictionary)?[operands[0][1..]]) as PdfDictionary;
                    break;
                default:
                    var marked = inheritedMark || open.Any(o => o.Mcid is not null || o.Artifact);
                    var xobject = text == "Do" ? reader.Resolve((reader.Resolve(resources["XObject"]) as PdfDictionary)?[operands[0][1..]]) : null;
                    if (xobject is PdfStreamObject { Dictionary: var formDictionary } && formDictionary["Subtype"] is PdfName { Value: "Form" } && !marked)
                    {
                        // An unmarked form (a transparency group) must mark its own content; its MCIDs have its /StructParents.
                        var formReference = (reader.Resolve(resources["XObject"]) as PdfDictionary)![operands[0][1..]];
                        CheckStream(
                            reader, Encoding.Latin1.GetString(reader.Store.Decode((PdfStreamObject)xobject)),
                            Assert.IsType<PdfDictionary>(reader.Resolve(formDictionary["Resources"])),
                            formDictionary["StructParents"] is PdfInteger formKey ? (int)formKey.Value : -1, parentTree, elements, page, formReference, texts);
                    }
                    else if (PaintingOperators.Contains(text))
                    {
                        Assert.True(marked, $"'{text}' on page {page} is neither tagged nor an artifact.");
                        if (open.FirstOrDefault(o => o.Mcid is not null) is { Mcid: { } current } && text is "Tj" or "TJ")
                        {
                            texts[(page, stream, current)] += Decode(reader, font, operands);
                        }
                    }

                    break;
            }

            operands.Clear();
        }

        Assert.Empty(open);
    }

    /// <summary>Decodes the text of a Tj/TJ operand through the font's ToUnicode map (two-byte codes, bfchar entries).</summary>
    private static string Decode(PdfReader reader, PdfDictionary? font, List<string> operands)
    {
        if (font is null || reader.Resolve(font["ToUnicode"]) is not PdfStreamObject toUnicode)
        {
            return string.Empty;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cmap = Encoding.Latin1.GetString(reader.Store.Decode(toUnicode));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(cmap, "<([0-9A-Fa-f]{4})>\\s*<([0-9A-Fa-f]+)>"))
        {
            var utf16 = Convert.FromHexString(m.Groups[2].Value);
            map[m.Groups[1].Value] = Encoding.BigEndianUnicode.GetString(utf16);
        }

        var result = new StringBuilder();
        foreach (var operand in operands.Where(o => o.StartsWith('<') && !o.StartsWith("<<", StringComparison.Ordinal)))
        {
            var hex = operand.Trim('<', '>');
            for (var i = 0; i + 4 <= hex.Length; i += 4)
            {
                result.Append(map.GetValueOrDefault(hex.Substring(i, 4), "?"));
            }
        }

        return result.ToString();
    }
}
