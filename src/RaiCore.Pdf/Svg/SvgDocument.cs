using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using RaiCore.Pdf.Images;

namespace RaiCore.Pdf.Svg;

/// <summary>A node of an SVG document: an element or character data.</summary>
internal abstract class SvgNode
{
    public SvgElement? Parent { get; set; }
}

internal sealed class SvgTextNode(string text) : SvgNode
{
    public string Text { get; } = text;
}

/// <summary>An SVG element. Names are local names; namespaces are ignored.</summary>
internal sealed class SvgElement(string name) : SvgNode
{
    private string[]? _classes;

    public string Name { get; } = name;

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    public List<SvgNode> Children { get; } = [];

    /// <summary>The style declarations that apply to this element, in cascade order (later wins).</summary>
    public Dictionary<string, string> Declared { get; } = new(StringComparer.Ordinal);

    public string? Id => Attributes.GetValueOrDefault("id");

    public IEnumerable<SvgElement> Elements => Children.OfType<SvgElement>();

    public string? this[string attribute] => Attributes.GetValueOrDefault(attribute);

    public bool HasClass(string name)
    {
        _classes ??= Attributes.TryGetValue("class", out var value)
            ? value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            : [];
        return _classes.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>The concatenated character data of this element and its descendants.</summary>
    public string TextContent()
    {
        var text = new StringBuilder();
        foreach (var element in SvgDocument.Descendants(this))
        {
            foreach (var child in element.Children.OfType<SvgTextNode>())
            {
                text.Append(child.Text);
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// A parsed SVG document: the element tree with styles resolved from presentation attributes, <c>&lt;style&gt;</c>
/// sheets and <c>style</c> attributes, the elements by id, and the intrinsic size of the root.
/// </summary>
internal sealed partial class SvgDocument
{
    private const string XLinkNamespace = "http://www.w3.org/1999/xlink";

    /// <summary>The deepest element nesting accepted (real drawings stay far below; this bounds the work per element).</summary>
    private const int MaxDepth = 256;

    /// <summary>Properties that can be set as presentation attributes as well as in CSS.</summary>
    private static readonly HashSet<string> PresentationAttributes = new(StringComparer.Ordinal)
    {
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "stroke-opacity", "opacity", "display",
        "visibility", "color", "font-size", "font-family", "font-weight", "font-style", "text-anchor", "clip-path",
        "clip-rule", "stop-color", "stop-opacity",
    };

    private readonly Dictionary<string, SvgElement> _ids = new(StringComparer.Ordinal);

    private SvgDocument(SvgElement root)
    {
        Root = root;
        Index(root);
        ApplyStyles();
    }

    public SvgElement Root { get; }

    /// <summary>
    /// Whether data looks like an SVG document: after a byte order mark and whitespace it starts with <c>&lt;svg</c>, or
    /// with an XML declaration, comment or doctype followed later by an <c>&lt;svg</c> tag.
    /// </summary>
    public static bool IsSvg(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            data = data[3..];
        }
        else if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            // UTF-16: decode the start and look at the characters.
            var encoding = data[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            var length = Math.Min(data.Length - 2, 2048) & ~1;
            return IsSvg(Encoding.UTF8.GetBytes(encoding.GetString(data.Slice(2, length))));
        }

        data = data.TrimStart(" \t\r\n"u8);
        if (data.StartsWith("<svg"u8))
        {
            return true;
        }

        if (!data.StartsWith("<?xml"u8) && !data.StartsWith("<!--"u8) && !data.StartsWith("<!DOCTYPE"u8))
        {
            return false;
        }

        return data[..Math.Min(data.Length, 4096)].IndexOf("<svg"u8) >= 0;
    }

    public static SvgDocument Parse(Stream stream)
    {
        // DTDs are ignored and nothing external is ever resolved: SVG may come from untrusted HTML.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 1024,
        };

        // Undeclared xlink: prefixes are common in hand-written SVG; predeclare the namespace.
        var names = new NameTable();
        var namespaces = new XmlNamespaceManager(names);
        namespaces.AddNamespace("xlink", XLinkNamespace);
        var context = new XmlParserContext(names, namespaces, null, XmlSpace.None);

        try
        {
            using var reader = XmlReader.Create(new StringReader(DecodeText(stream)), settings, context);
            return new SvgDocument(ReadTree(reader));
        }
        catch (XmlException ex)
        {
            throw new ImageFormatException($"Invalid SVG: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes the markup to text before XML parsing, so that the XML reader never switches encodings itself: its
    /// switch to the encoding an XML declaration names throws <see cref="ArgumentOutOfRangeException"/> on some
    /// malformed declarations (fuzzing found <c>&lt;?xml version="1.0\xFF...</c>). The encoding is the byte order
    /// mark's, else the declaration's when .NET knows it, else UTF-8; bytes it cannot decode become U+FFFD.
    /// </summary>
    private static string DecodeText(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var (encoding, bom) = data switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (Encoding.UTF8, 3),
            [0xFF, 0xFE, 0, 0, ..] => (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4),
            [0, 0, 0xFE, 0xFF, ..] => (new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4),
            [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
            [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
            _ => (DeclaredEncoding(data) ?? Encoding.UTF8, 0),
        };

        return encoding.GetString(data[bom..]);
    }

    /// <summary>The encoding named by an XML declaration at the start of ASCII-compatible markup, if .NET has it.</summary>
    private static Encoding? DeclaredEncoding(ReadOnlySpan<byte> data)
    {
        var prolog = Encoding.Latin1.GetString(data[..Math.Min(data.Length, 256)]);
        if (EncodingDeclarationRegex().Match(prolog) is not { Success: true } match)
        {
            return null;
        }

        try
        {
            var encoding = Encoding.GetEncoding(match.Groups[1].Value);
            return encoding.IsSingleByte || encoding.CodePage == Encoding.UTF8.CodePage ? encoding : null; // A UTF-16 declaration without a byte order mark is not ASCII-compatible: not believed.
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^<\?xml[^>]*?\sencoding\s*=\s*[""']([A-Za-z][A-Za-z0-9._-]*)[""']")]
    private static partial Regex EncodingDeclarationRegex();

    public SvgElement? GetElementById(string? id) => id is not null && _ids.TryGetValue(id, out var element) ? element : null;

    /// <summary>The element an <c>href</c> (or <c>xlink:href</c>) of the form <c>#id</c> refers to.</summary>
    public SvgElement? GetReferenced(SvgElement element) => GetElementById(ParseFragment(element["href"]));

    /// <summary>The id in a local reference such as <c>#logo</c>; null for anything else (external references).</summary>
    public static string? ParseFragment(string? reference)
    {
        if (reference is null)
        {
            return null;
        }

        reference = reference.Trim().Trim('"', '\'').Trim();
        return reference.Length > 1 && reference[0] == '#' ? reference[1..] : null;
    }

    /// <summary>The id in a functional reference such as <c>url(#clip)</c> or <c>url("#clip")</c>.</summary>
    public static string? ParseUrl(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var close = value.IndexOf(')', StringComparison.Ordinal);
        return close < 0 ? null : ParseFragment(value[4..close]);
    }

    private static SvgElement ReadTree(XmlReader reader)
    {
        SvgElement? root = null;
        SvgElement? current = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth >= MaxDepth)
                    {
                        throw new ImageFormatException("Invalid SVG: elements are nested too deeply.");
                    }

                    var element = new SvgElement(reader.LocalName) { Parent = current };
                    ReadAttributes(reader, element);
                    if (current is null)
                    {
                        if (root is not null)
                        {
                            throw new ImageFormatException("Invalid SVG: more than one root element.");
                        }

                        root = element;
                    }
                    else
                    {
                        current.Children.Add(element);
                    }

                    if (!reader.IsEmptyElement)
                    {
                        current = element;
                    }

                    break;
                case XmlNodeType.EndElement:
                    current = current?.Parent;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    current?.Children.Add(new SvgTextNode(reader.Value) { Parent = current });
                    break;
            }
        }

        if (root is null || root.Name != "svg")
        {
            throw new ImageFormatException("Not an SVG document: the root element must be <svg>.");
        }

        return root;
    }

    private static void ReadAttributes(XmlReader reader, SvgElement element)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return;
        }

        do
        {
            var ns = reader.NamespaceURI;
            if (ns.Length == 0)
            {
                element.Attributes[reader.LocalName] = reader.Value;
            }
            else if (ns == XLinkNamespace && reader.LocalName == "href")
            {
                // SVG 2: a plain href wins over xlink:href.
                element.Attributes.TryAdd("href", reader.Value);
            }
            else if (reader.Prefix == "xml" && reader.LocalName == "space")
            {
                element.Attributes["xml:space"] = reader.Value;
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
    }

    private void Index(SvgElement root)
    {
        foreach (var element in Descendants(root))
        {
            if (element.Id is { } id)
            {
                _ids.TryAdd(id, element); // The first element with an id wins, as in browsers.
            }
        }
    }

    /// <summary>
    /// Resolves the declared style of every element. Cascade order: presentation attributes, style sheet rules (by
    /// specificity, then order), the <c>style</c> attribute, then <c>!important</c> rules.
    /// </summary>
    private void ApplyStyles()
    {
        var rules = new List<SvgCssRule>();
        var order = 0;
        foreach (var style in Descendants(Root).Where(e => e.Name == "style"))
        {
            var type = style["type"];
            if (type is null || type.Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase))
            {
                rules.AddRange(SvgCss.Parse(style.TextContent(), ref order));
            }
        }

        rules.Sort((a, b) => a.Selector.Specificity != b.Selector.Specificity
            ? a.Selector.Specificity.CompareTo(b.Selector.Specificity)
            : a.Order.CompareTo(b.Order));

        foreach (var element in Descendants(Root))
        {
            var declared = element.Declared;
            foreach (var (name, value) in element.Attributes)
            {
                if (PresentationAttributes.Contains(name))
                {
                    declared[name] = value;
                }
            }

            var matching = rules.Count == 0 ? [] : rules.Where(r => r.Selector.Matches(element)).ToList();
            foreach (var rule in matching)
            {
                foreach (var (name, value, important) in rule.Declarations)
                {
                    if (!important)
                    {
                        declared[name] = value;
                    }
                }
            }

            if (element["style"] is { } inline)
            {
                foreach (var (name, value, _) in SvgCss.ParseDeclarations(inline))
                {
                    declared[name] = value;
                }
            }

            foreach (var rule in matching)
            {
                foreach (var (name, value, important) in rule.Declarations)
                {
                    if (important)
                    {
                        declared[name] = value;
                    }
                }
            }
        }
    }

    /// <summary>The element and its descendants in document order.</summary>
    public static IEnumerable<SvgElement> Descendants(SvgElement element)
    {
        var stack = new Stack<SvgElement>();
        stack.Push(element);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            yield return next;
            for (var i = next.Children.Count - 1; i >= 0; i--)
            {
                if (next.Children[i] is SvgElement child)
                {
                    stack.Push(child);
                }
            }
        }
    }
}
