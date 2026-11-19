using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>A style of <c>styles.xml</c>: its properties as elements, applied when a format is resolved.</summary>
internal sealed class DocxStyle
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Name { get; init; }

    public string? BasedOn { get; init; }

    public XElement? ParagraphProperties { get; init; }

    public XElement? RunProperties { get; init; }

    public XElement? TableProperties { get; init; }

    public XElement? RowProperties { get; init; }

    public XElement? CellProperties { get; init; }

    /// <summary>The conditional formats of a table style by type (<c>firstRow</c>, <c>band1Horz</c>...).</summary>
    public Dictionary<string, XElement> Conditions { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The styles of a document (<c>word/styles.xml</c>): document defaults, and paragraph, character and table styles with
/// their <c>basedOn</c> chains. Latent styles are ignored.
/// </summary>
internal sealed class DocxStyles
{
    /// <summary>The longest <c>basedOn</c> chain followed (Word allows few levels; loops are cut).</summary>
    private const int MaxChain = 32;

    private readonly Dictionary<string, DocxStyle> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DocxStyle>> _chains = new(StringComparer.Ordinal);

    private DocxStyles(DocxTheme theme) => Theme = theme;

    public DocxTheme Theme { get; }

    /// <summary>The document's default run format (<c>w:rPrDefault</c>), Times New Roman 10 pt without one.</summary>
    public RunFormat DefaultRun { get; private set; } = RunFormat.Default with { FontFamily = "Times New Roman" };

    /// <summary>The document's default paragraph format (<c>w:pPrDefault</c>).</summary>
    public ParagraphFormat DefaultParagraph { get; private set; } = ParagraphFormat.Default;

    /// <summary>The id of the default paragraph style, or <c>null</c>.</summary>
    public string? DefaultParagraphStyle { get; private set; }

    /// <summary>The id of the default table style, or <c>null</c>.</summary>
    public string? DefaultTableStyle { get; private set; }

    /// <summary>Reads a styles part (or gives defaults for <c>null</c>).</summary>
    public static DocxStyles Read(XElement? root, DocxTheme theme)
    {
        var styles = new DocxStyles(theme);
        if (root is null)
        {
            return styles;
        }

        var defaults = Ooxml.Child(root, "docDefaults");
        styles.DefaultRun = DocxProperties.ApplyRun(styles.DefaultRun, Ooxml.Child(Ooxml.Child(defaults, "rPrDefault"), "rPr"), theme);
        styles.DefaultParagraph = DocxProperties.ApplyParagraph(styles.DefaultParagraph, Ooxml.Child(Ooxml.Child(defaults, "pPrDefault"), "pPr"), theme);
        foreach (var element in Ooxml.Children(root, "style"))
        {
            if (Ooxml.Attr(element, "styleId") is not { Length: > 0 } id)
            {
                continue;
            }

            var type = Ooxml.Attr(element, "type") ?? "paragraph";
            var style = new DocxStyle
            {
                Id = id,
                Type = type,
                Name = Ooxml.Val(element, "name"),
                BasedOn = Ooxml.Val(element, "basedOn"),
                ParagraphProperties = Ooxml.Child(element, "pPr"),
                RunProperties = Ooxml.Child(element, "rPr"),
                TableProperties = Ooxml.Child(element, "tblPr"),
                RowProperties = Ooxml.Child(element, "trPr"),
                CellProperties = Ooxml.Child(element, "tcPr"),
            };
            foreach (var condition in Ooxml.Children(element, "tblStylePr"))
            {
                if (Ooxml.Attr(condition, "type") is { } kind)
                {
                    style.Conditions.TryAdd(kind, condition);
                }
            }

            if (!styles._styles.TryAdd(id, style))
            {
                continue;
            }

            if (Ooxml.Attr(element, "default") is { } isDefault && Ooxml.IsOn(isDefault))
            {
                if (type == "paragraph")
                {
                    styles.DefaultParagraphStyle ??= id;
                }
                else if (type == "table")
                {
                    styles.DefaultTableStyle ??= id;
                }
            }
        }

        return styles;
    }

    /// <summary>A style by id, or <c>null</c>.</summary>
    public DocxStyle? this[string? id] => id is not null && _styles.TryGetValue(id, out var style) ? style : null;

    /// <summary>
    /// The style and the styles it is based on, the base first, all of the same type; empty for an unknown id. A
    /// paragraph style id that does not exist means the default paragraph style.
    /// </summary>
    public List<DocxStyle> Chain(string? id, string type)
    {
        if (id is null || this[id] is not { } style || style.Type != type)
        {
            return id is not null && type == "paragraph" && DefaultParagraphStyle is { } fallback && fallback != id ? Chain(fallback, type) : [];
        }

        if (_chains.TryGetValue(id, out var chain))
        {
            return chain;
        }

        chain = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = style; current is not null && current.Type == type && chain.Count < MaxChain && seen.Add(current.Id); current = this[current.BasedOn])
        {
            chain.Add(current);
        }

        chain.Reverse();
        _chains[id] = chain;
        return chain;
    }
}
