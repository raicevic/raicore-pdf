using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>A level of a list definition (<c>w:lvl</c>).</summary>
internal sealed class DocxLevel
{
    public int Start { get; init; }

    public NumberFormat Format { get; init; }

    public string Text { get; init; } = string.Empty;

    public LabelSuffix Suffix { get; init; }

    /// <summary>Whether all levels show as decimal numbers in this level's label (legal numbering).</summary>
    public bool Legal { get; init; }

    /// <summary>The 1-based level whose use restarts this one (0 for never), or <c>null</c> for any higher level.</summary>
    public int? Restart { get; init; }

    public XElement? ParagraphProperties { get; init; }

    public XElement? RunProperties { get; init; }

    /// <summary>The font of the label (for symbol bullets), or <c>null</c>.</summary>
    public string? Font { get; init; }

    /// <summary>Reads a level, or <c>null</c> if it has no level number.</summary>
    public static (int Level, DocxLevel Definition)? Read(XElement lvl)
    {
        if (Ooxml.Int(lvl, "ilvl") is not { } level || level is < 0 or > 8)
        {
            return null;
        }

        var rPr = Ooxml.Child(lvl, "rPr");
        var fonts = Ooxml.Child(rPr, "rFonts");
        return (level, new DocxLevel
        {
            Start = Math.Clamp(Ooxml.Int(Ooxml.Child(lvl, "start"), "val") ?? 0, -1_000_000, 1_000_000),
            Format = Ooxml.NumberFormatOf(Ooxml.Val(lvl, "numFmt")),
            Text = Ooxml.Val(lvl, "lvlText") ?? string.Empty,
            Suffix = Ooxml.Val(lvl, "suff") switch
            {
                "space" => LabelSuffix.Space,
                "nothing" => LabelSuffix.Nothing,
                _ => LabelSuffix.Tab,
            },
            Legal = Ooxml.OnOff(lvl, "isLgl") ?? false,
            Restart = Ooxml.Int(Ooxml.Child(lvl, "lvlRestart"), "val"),
            ParagraphProperties = Ooxml.Child(lvl, "pPr"),
            RunProperties = rPr,
            Font = Ooxml.Attr(fonts, "ascii") ?? Ooxml.Attr(fonts, "hAnsi"),
        });
    }
}

/// <summary>
/// The list definitions of a document (<c>word/numbering.xml</c>) and the counters of the lists while the document is
/// read in order: abstract definitions with up to nine levels, list instances with level and start overrides, and
/// labels built from the level texts (<c>%1.%2.</c>).
/// </summary>
internal sealed class DocxNumbering
{
    private readonly Dictionary<string, DocxLevel?[]> _abstracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _numStyleLinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumberingInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?[]> _counters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);

    private sealed class NumberingInstance
    {
        public required string AbstractId { get; init; }

        public DocxLevel?[] Overrides { get; } = new DocxLevel?[9];

        public int?[] StartOverrides { get; } = new int?[9];
    }

    /// <summary>Reads a numbering part (or gives no lists for <c>null</c>).</summary>
    public static DocxNumbering Read(XElement? root)
    {
        var numbering = new DocxNumbering();
        if (root is null)
        {
            return numbering;
        }

        foreach (var element in Ooxml.Children(root, "abstractNum"))
        {
            if (Ooxml.Attr(element, "abstractNumId") is not { } id || numbering._abstracts.ContainsKey(id))
            {
                continue;
            }

            var levels = new DocxLevel?[9];
            foreach (var lvl in Ooxml.Children(element, "lvl"))
            {
                if (DocxLevel.Read(lvl) is var (level, definition))
                {
                    levels[level] ??= definition;
                }
            }

            numbering._abstracts[id] = levels;
            if (Ooxml.Val(element, "numStyleLink") is { } link)
            {
                numbering._numStyleLinks[id] = link;
            }
        }

        foreach (var element in Ooxml.Children(root, "num"))
        {
            if (Ooxml.Attr(element, "numId") is not { } id || Ooxml.Val(element, "abstractNumId") is not { } abstractId
                || numbering._instances.ContainsKey(id))
            {
                continue;
            }

            var instance = new NumberingInstance { AbstractId = abstractId };
            foreach (var levelOverride in Ooxml.Children(element, "lvlOverride"))
            {
                if (Ooxml.Int(levelOverride, "ilvl") is not { } level || level is < 0 or > 8)
                {
                    continue;
                }

                if (Ooxml.Int(Ooxml.Child(levelOverride, "startOverride"), "val") is { } start)
                {
                    instance.StartOverrides[level] = Math.Clamp(start, -1_000_000, 1_000_000);
                }

                if (Ooxml.Child(levelOverride, "lvl") is { } lvl && DocxLevel.Read(lvl) is var (_, definition))
                {
                    instance.Overrides[level] = definition;
                }
            }

            numbering._instances[id] = instance;
        }

        return numbering;
    }

    /// <summary>
    /// The abstract definition of a list, following a numbering style link (<c>w:numStyleLink</c>) to the list the
    /// style names, once.
    /// </summary>
    private string? AbstractOf(string numId, DocxStyles styles, int depth = 0)
    {
        if (!_instances.TryGetValue(numId, out var instance))
        {
            return null;
        }

        if (depth < 4 && _numStyleLinks.TryGetValue(instance.AbstractId, out var styleId)
            && styles[styleId]?.ParagraphProperties is { } pPr
            && Ooxml.Val(Ooxml.Child(pPr, "numPr"), "numId") is { } linked && linked != numId)
        {
            return AbstractOf(linked, styles, depth + 1) ?? instance.AbstractId;
        }

        return instance.AbstractId;
    }

    /// <summary>The definition of a level of a list (with the instance's override), or <c>null</c>.</summary>
    public DocxLevel? Level(string numId, int level, DocxStyles styles)
    {
        if (level is < 0 or > 8 || !_instances.TryGetValue(numId, out var instance))
        {
            return null;
        }

        return instance.Overrides[level] ?? (AbstractOf(numId, styles) is { } id && _abstracts.TryGetValue(id, out var levels) ? levels[level] : null);
    }

    /// <summary>
    /// Counts the next paragraph of a list at a level and returns its label text, or <c>null</c> if the list or level
    /// is not defined. Lists sharing an abstract definition share counters; an instance's start overrides apply the
    /// first time it is used.
    /// </summary>
    public string? Next(string numId, int level, DocxStyles styles)
    {
        if (Level(numId, level, styles) is not { } definition || AbstractOf(numId, styles) is not { } abstractId)
        {
            return null;
        }

        var instance = _instances[numId];
        if (!_counters.TryGetValue(abstractId, out var counters))
        {
            counters = new int?[9];
            _counters[abstractId] = counters;
        }

        if (_started.Add(numId))
        {
            for (var l = 0; l < 9; l++)
            {
                if (instance.StartOverrides[l] is { } start)
                {
                    counters[l] = start - 1;
                }
            }
        }

        counters[level] = counters[level] is { } current ? current + 1 : definition.Start;
        for (var deeper = level + 1; deeper < 9; deeper++)
        {
            var restart = Level(numId, deeper, styles)?.Restart ?? deeper;
            if (level < restart)
            {
                counters[deeper] = null;
            }
        }

        return Label(definition, numId, styles, counters, level);
    }

    /// <summary>Builds a label: the level text with <c>%1</c> to <c>%9</c> replaced by the levels' numbers.</summary>
    private string Label(DocxLevel definition, string numId, DocxStyles styles, int?[] counters, int level)
    {
        if (definition.Format == NumberFormat.Bullet)
        {
            return Bullet(definition.Text, definition.Font);
        }

        var text = definition.Text;
        var result = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 1 < text.Length && text[i + 1] is >= '1' and <= '9')
            {
                var referenced = text[i + 1] - '1';
                var levelDefinition = Level(numId, referenced, styles);
                var value = counters[referenced] ?? levelDefinition?.Start ?? 1;
                var format = levelDefinition?.Format ?? NumberFormat.Decimal;
                if (definition.Legal && referenced != level && format != NumberFormat.None)
                {
                    format = NumberFormat.Decimal;
                }

                result.Append(Numbering.Format(value, format == NumberFormat.Bullet ? NumberFormat.Decimal : format));
                i++;
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>A bullet's text, with the private-use characters of symbol fonts mapped to Unicode bullets.</summary>
    private static string Bullet(string text, string? font)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c is >= '\uF000' and <= '\uF0FF' || (font is not null && (font.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
                || font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase)))
                ? Ooxml.SymbolCharacter(c, font)
                : c == 'o' && font is not null && font.StartsWith("Courier", StringComparison.OrdinalIgnoreCase) ? "\u25E6" : c.ToString());
        }

        return builder.ToString();
    }
}
