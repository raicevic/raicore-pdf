using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>The font, color and style tables, and character, paragraph, border and shading formatting.</summary>
internal sealed partial class RtfReader
{
    /// <summary>The largest length in twips that is kept (22 inches); longer ones are clamped.</summary>
    private const int MaxTwips = 31680;

    /// <summary>How deep styles may be based on each other.</summary>
    private const int MaxStyleDepth = 16;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly Dictionary<int, RtfFont> _fonts = [];
    private readonly Dictionary<int, Encoding> _encodings = [];
    private readonly List<PdfColor?> _colors = [];
    private readonly Dictionary<int, RtfStyle> _paragraphStyles = [];
    private readonly Dictionary<int, RtfStyle> _characterStyles = [];
    private readonly Dictionary<RtfCharState, RunFormat> _formats = [];
    private readonly StringBuilder _title = new();
    private readonly StringBuilder _author = new();
    private RtfCharState _defaultChars = RtfCharState.Plain;
    private RtfParagraphState _defaultParagraph = RtfParagraphState.Plain;
    private int _defaultFont;
    private int _codePage = 1252;
    private RtfBorderTarget _borderTarget;

    // The font and color being read.
    private FontBuilder? _font;
    private (int Red, int Green, int Blue, bool Any) _color;

    private sealed class FontBuilder(int number)
    {
        public int Number { get; } = number;

        public int Charset { get; set; } = -1;

        public int CodePage { get; set; }

        public StringBuilder Name { get; } = new();

        public bool Done { get; set; }
    }

    /// <summary>The encoding of text in the current font: its code page, or the document's.</summary>
    private Encoding CurrentEncoding()
    {
        var group = Top;
        if (group.Destination is RtfDestination.FontEntry or RtfDestination.FontTable && _font is { } font)
        {
            return EncodingOf(font.CodePage != 0 ? font.CodePage : CodePageOfCharset(font.Charset));
        }

        return EncodingOf(FontOf(group.Chars.Font) is { CodePage: > 0 } f ? f.CodePage : _codePage);
    }

    /// <summary>An encoding by code page (from the code pages provider, not registered process-wide), else Windows-1252.</summary>
    private Encoding EncodingOf(int codePage)
    {
        if (codePage <= 0)
        {
            codePage = _codePage;
        }

        if (_encodings.TryGetValue(codePage, out var encoding))
        {
            return encoding;
        }

        encoding = codePage switch
        {
            65001 => Encoding.UTF8,
            28591 => Latin1,
            1200 or 1201 or 12000 or 12001 or 65000 => null, // Not byte code pages for RTF text.
            > 0 and < 65535 => CodePagesEncodingProvider.Instance.GetEncoding(codePage),
            _ => null,
        };
        encoding ??= codePage == 1252 ? Latin1 : EncodingOf(1252);
        if (_encodings.Count < 64)
        {
            _encodings[codePage] = encoding;
        }

        return encoding;
    }

    /// <summary>The code page of a font character set (<c>\fcharset</c>), or 0 for the document's.</summary>
    private static int CodePageOfCharset(int charset) => charset switch
    {
        77 => 10000,
        78 => 10001,
        79 => 10003,
        80 => 10008,
        81 => 10002,
        83 => 10005,
        84 => 10004,
        85 => 10006,
        86 => 10081,
        87 => 10021,
        88 => 10029,
        89 => 10007,
        128 => 932,
        129 => 949,
        130 => 1361,
        134 => 936,
        136 => 950,
        161 => 1253,
        162 => 1254,
        163 => 1258,
        177 => 1255,
        178 => 1256,
        186 => 1257,
        204 => 1251,
        222 => 874,
        238 => 1250,
        254 => 437,
        255 => 850,
        _ => 0,
    };

    /// <summary>A font by number (the default font for -1 or a number the table lacks), or <c>null</c>.</summary>
    private RtfFont? FontOf(int number) =>
        _fonts.TryGetValue(number, out var font) ? font : _fonts.TryGetValue(_defaultFont, out var fallback) ? fallback : null;

    private void FontWord(string word, int parameter, bool hasParameter)
    {
        switch (word)
        {
            case "f" when hasParameter:
                EndFont();
                _font = new FontBuilder(parameter);
                break;
            case "fcharset" when _font is not null:
                _font.Charset = parameter;
                break;
            case "cpg" when _font is not null:
                _font.CodePage = parameter;
                break;
        }
    }

    private void FontText(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (_font is null)
            {
                continue;
            }

            if (c == ';')
            {
                EndFont();
            }
            else if (_font.Name.Length < 128)
            {
                _font.Name.Append(c);
            }
        }
    }

    /// <summary>Adds the font being read (the first definition of a number wins).</summary>
    private void EndFont()
    {
        if (_font is not { } font)
        {
            return;
        }

        _font = null;
        var name = font.Name.ToString().Trim();
        var symbol = font.Charset == 2 || name.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase);
        var codePage = font.CodePage > 0 ? font.CodePage : symbol ? 28591 : CodePageOfCharset(font.Charset);
        if (_fonts.Count < 4096)
        {
            _fonts.TryAdd(font.Number, new RtfFont(name, codePage, symbol));
        }
    }

    private void ColorWord(string word, int parameter)
    {
        var value = Math.Clamp(parameter, 0, 255);
        switch (word)
        {
            case "red":
                _color = (value, _color.Green, _color.Blue, true);
                break;
            case "green":
                _color = (_color.Red, value, _color.Blue, true);
                break;
            case "blue":
                _color = (_color.Red, _color.Green, value, true);
                break;
        }
    }

    /// <summary>Ends a color table entry: a color, or automatic when it has no components.</summary>
    private void EndColor()
    {
        if (_colors.Count < 4096)
        {
            _colors.Add(_color.Any ? PdfColor.FromRgb((byte)_color.Red, (byte)_color.Green, (byte)_color.Blue) : null);
        }

        _color = default;
    }

    /// <summary>A color table entry, or <c>null</c> for automatic (index 0, or one the table lacks).</summary>
    private PdfColor? ColorAt(int index) => index > 0 && index < _colors.Count ? _colors[index] : null;

    private void StyleWord(string word, int parameter, bool hasParameter)
    {
        var group = Top;
        switch (word)
        {
            case "s" or "cs" or "ts" or "ds":
                group.Data = new RtfStyle { Kind = word[0], Number = parameter };
                return;
            case "sbasedon":
                (group.Data as RtfStyle ?? NewStyle(group)).BasedOn = parameter;
                return;
        }

        var style = group.Data as RtfStyle ?? NewStyle(group);
        if (style.Words.Count < 256)
        {
            style.Words.Add((word, parameter, hasParameter));
        }
    }

    /// <summary>A paragraph style entry without <c>\s</c>: style 0, Normal.</summary>
    private static RtfStyle NewStyle(Group group)
    {
        var style = new RtfStyle();
        group.Data = style;
        return style;
    }

    private void EndStyle(Group group)
    {
        if (group.Data is not RtfStyle style)
        {
            return;
        }

        style.Name = style.Name.Trim().TrimEnd(';').Trim();
        var name = style.Name;
        if (name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(8), System.Globalization.CultureInfo.InvariantCulture, out var level)
            && level is >= 1 and <= 9)
        {
            style.Words.Insert(0, ("outlinelevel", level - 1, true));
        }

        var styles = style.Kind switch
        {
            's' => _paragraphStyles,
            'c' => _characterStyles,
            _ => null,
        };
        if (styles is not null && styles.Count < 4096)
        {
            styles.TryAdd(style.Number, style);
        }
    }

    /// <summary>Applies a style's control words (after those of the styles it is based on) to formatting.</summary>
    private void ApplyStyle(Dictionary<int, RtfStyle> styles, int number, ref RtfCharState chars, ref RtfParagraphState paragraph, bool withParagraph)
    {
        // The chain from the style to its root, at most MaxStyleDepth deep (a loop ends there).
        Span<int> chain = stackalloc int[MaxStyleDepth];
        var count = 0;
        for (var n = number; count < MaxStyleDepth && styles.TryGetValue(n, out var style); n = style.BasedOn)
        {
            if (chain[..count].Contains(n))
            {
                break;
            }

            chain[count++] = n;
            if (style.BasedOn < 0)
            {
                break;
            }
        }

        var target = _borderTarget;
        for (var i = count - 1; i >= 0; i--)
        {
            foreach (var (word, parameter, hasParameter) in styles[chain[i]].Words)
            {
                if (word is "s" or "cs" or "plain" or "pard")
                {
                    continue;
                }

                if (!ApplyCharacter(ref chars, word, parameter, hasParameter) && withParagraph)
                {
                    ApplyParagraph(ref paragraph, word, parameter, hasParameter);
                }
            }
        }

        _borderTarget = target;
    }

    /// <summary>Applies a character formatting control word, returning whether it was one.</summary>
    private bool ApplyCharacter(ref RtfCharState c, string word, int parameter, bool hasParameter)
    {
        var on = !hasParameter || parameter != 0;
        switch (word)
        {
            case "plain":
                c = _defaultChars;
                break;
            case "f":
                c.Font = parameter;
                break;
            case "fs":
                c.FontSize = hasParameter ? Math.Clamp(parameter, 2, 3276) : 24;
                break;
            case "b":
                c.Bold = on;
                break;
            case "i":
                c.Italic = on;
                break;
            case "strike":
            case "striked":
                c.Strike = on;
                break;
            case "caps":
                c.Caps = on;
                break;
            case "scaps":
                c.SmallCaps = on;
                break;
            case "v":
                c.Hidden = on;
                break;
            case "deleted":
                c.Deleted = on;
                break;
            case "ul":
            case "ulw":
            case "ulth":
                c.Underline = on ? UnderlineStyle.Single : UnderlineStyle.None;
                break;
            case "uld":
            case "ulthd":
                c.Underline = on ? UnderlineStyle.Dotted : UnderlineStyle.None;
                break;
            case "uldb":
                c.Underline = on ? UnderlineStyle.Double : UnderlineStyle.None;
                break;
            case "uldash":
            case "uldashd":
            case "uldashdd":
            case "ulldash":
            case "ulthdash":
            case "ulthdashd":
            case "ulthdashdd":
            case "ulthldash":
                c.Underline = on ? UnderlineStyle.Dashed : UnderlineStyle.None;
                break;
            case "ulwave":
            case "ulhwave":
            case "ululdbwave":
                c.Underline = on ? UnderlineStyle.Wavy : UnderlineStyle.None;
                break;
            case "ulnone":
                c.Underline = UnderlineStyle.None;
                break;
            case "cf":
                c.Color = parameter;
                break;
            case "cb":
            case "chcbpat":
                c.Background = parameter;
                break;
            case "highlight":
                c.Highlight = parameter;
                break;
            case "super":
                c.Position = on ? VerticalPosition.Superscript : VerticalPosition.Baseline;
                break;
            case "sub":
                c.Position = on ? VerticalPosition.Subscript : VerticalPosition.Baseline;
                break;
            case "nosupersub":
                c.Position = VerticalPosition.Baseline;
                break;
            case "up":
                c.Raise = hasParameter ? Math.Clamp(parameter, -400, 400) : 6;
                break;
            case "dn":
                c.Raise = -(hasParameter ? Math.Clamp(parameter, -400, 400) : 6);
                break;
            case "expnd":
                c.Spacing = Math.Clamp(parameter, -4000, 4000) * 5; // Quarter points.
                break;
            case "expndtw":
                c.Spacing = Math.Clamp(parameter, -20000, 20000);
                break;
            case "rtlch":
                c.RightToLeft = true;
                break;
            case "ltrch":
                c.RightToLeft = false;
                break;
            case "cs":
                var paragraph = default(RtfParagraphState);
                ApplyStyle(_characterStyles, parameter, ref c, ref paragraph, withParagraph: false);
                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>Applies a paragraph formatting control word, returning whether it was one.</summary>
    private bool ApplyParagraph(ref RtfParagraphState p, string word, int parameter, bool hasParameter)
    {
        var on = !hasParameter || parameter != 0;
        var twips = Math.Clamp(parameter, -MaxTwips, MaxTwips);
        switch (word)
        {
            case "pard":
                p = _defaultParagraph;
                break;
            case "ql":
                p.Alignment = WordAlignment.Start;
                break;
            case "qc":
                p.Alignment = WordAlignment.Center;
                break;
            case "qr":
                p.Alignment = WordAlignment.End;
                break;
            case "qj":
            case "qd":
                p.Alignment = WordAlignment.Justify;
                break;
            case "li":
            case "lin":
                p.LeftIndent = twips;
                p.HasIndent = true;
                break;
            case "ri":
            case "rin":
                p.RightIndent = twips;
                break;
            case "fi":
                p.FirstIndent = twips;
                p.HasIndent = true;
                break;
            case "sb":
                p.SpaceBefore = Math.Max(0, twips);
                break;
            case "sa":
                p.SpaceAfter = Math.Max(0, twips);
                break;
            case "sl":
                p.LineSpacing = twips;
                break;
            case "slmult":
                p.LineMultiple = on;
                break;
            case "keep":
                p.KeepLines = on;
                break;
            case "keepn":
                p.KeepWithNext = on;
                break;
            case "pagebb":
                p.PageBreakBefore = on;
                break;
            case "widctlpar":
                p.WidowControl = true;
                break;
            case "nowidctlpar":
                p.WidowControl = false;
                break;
            case "rtlpar":
                p.RightToLeft = true;
                break;
            case "ltrpar":
                p.RightToLeft = false;
                break;
            case "contextualspace":
                p.ContextualSpacing = on;
                break;
            case "intbl":
                p.InTable = on;
                break;
            case "itap":
                p.NestingLevel = Math.Max(0, parameter);
                break;
            case "s":
                var keep = p;
                var chars = _defaultChars;
                p = _defaultParagraph;
                ApplyStyle(_paragraphStyles, parameter, ref chars, ref p, withParagraph: true);
                (p.InTable, p.NestingLevel, p.Numbering, p.Style) = (keep.InTable, keep.NestingLevel, keep.Numbering, parameter);
                if (_depth > 0 && Top.Destination is RtfDestination.Text or RtfDestination.ListText)
                {
                    Top.Chars = chars;
                }

                break;
            case "outlinelevel":
                p.Outline = parameter is >= 0 and <= 8 ? parameter : -1;
                break;
            case "tqr":
                p.PendingTabAlignment = TabAlignment.Right;
                break;
            case "tqc":
                p.PendingTabAlignment = TabAlignment.Center;
                break;
            case "tqdec":
                p.PendingTabAlignment = TabAlignment.Decimal;
                break;
            case "tldot":
            case "tlmdot":
                p.PendingTabLeader = '.';
                break;
            case "tlhyph":
                p.PendingTabLeader = '-';
                break;
            case "tlul":
            case "tlth":
                p.PendingTabLeader = '_';
                break;
            case "tleq":
                p.PendingTabLeader = '=';
                break;
            case "tx":
                var tabs = p.Tabs ?? [];
                if (tabs.Length < 64 && twips >= 0)
                {
                    p.Tabs = [.. tabs.Where(t => Math.Abs(t.Position - (twips / 20.0)) > 0.01), new TabStop(twips / 20.0, p.PendingTabAlignment, p.PendingTabLeader == '\0' ? null : p.PendingTabLeader)];
                }

                (p.PendingTabAlignment, p.PendingTabLeader) = (TabAlignment.Left, '\0');
                break;
            case "tb":
                (p.PendingTabAlignment, p.PendingTabLeader) = (TabAlignment.Left, '\0');
                break;
            case "brdrt":
                StartBorder(ref p, RtfBorderTarget.ParagraphTop);
                break;
            case "brdrb":
                StartBorder(ref p, RtfBorderTarget.ParagraphBottom);
                break;
            case "brdrl":
                StartBorder(ref p, RtfBorderTarget.ParagraphLeft);
                break;
            case "brdrr":
                StartBorder(ref p, RtfBorderTarget.ParagraphRight);
                break;
            case "box":
                StartBorder(ref p, RtfBorderTarget.ParagraphBox);
                break;
            case "brdrbtw":
                StartBorder(ref p, RtfBorderTarget.ParagraphBetween);
                break;
            case "cbpat":
                p.BackgroundColor = parameter;
                break;
            case "cfpat":
                p.PatternColor = parameter;
                break;
            case "shading":
                p.Shading = Math.Clamp(parameter, 0, 10000);
                break;
            case "ls":
                p.List = parameter;
                break;
            case "ilvl":
                p.Level = Math.Clamp(parameter, 0, 8);
                break;
            default:
                return Border(ref p, word, parameter, hasParameter);
        }

        return true;
    }

    /// <summary>Starts a paragraph border side: a line with no style until <c>\brdrs</c> or the like gives it one.</summary>
    private void StartBorder(ref RtfParagraphState p, RtfBorderTarget target)
    {
        _borderTarget = target;
        var line = new BorderLine(0.5, PdfColor.Black, BorderLineStyle.None);
        SetBorder(ref p, target, line);
    }

    /// <summary>Applies a border control word (<c>\brdrs</c>, <c>\brdrw</c>, <c>\brdrcf</c>) to the side being described.</summary>
    private bool Border(ref RtfParagraphState p, string word, int parameter, bool hasParameter)
    {
        if (!word.StartsWith("brdr", StringComparison.Ordinal) || _borderTarget == RtfBorderTarget.None)
        {
            return false;
        }

        var line = GetBorder(ref p, _borderTarget) ?? new BorderLine(0.5, PdfColor.Black, BorderLineStyle.None);
        line = word switch
        {
            "brdrw" => line with { Width = Math.Clamp(parameter, 0, 255) / 20.0 },
            "brdrcf" => line with { Color = ColorAt(parameter) ?? PdfColor.Black },
            "brdrnone" or "brdrnil" or "brdrtbl" => line with { Style = BorderLineStyle.None },
            "brdrdb" or "brdrtriple" or "brdrthtnsg" or "brdrtnthsg" or "brdrtnthtnsg" or "brdrthtnmg" or "brdrtnthmg" or "brdrtnthtnmg"
                or "brdrthtnlg" or "brdrtnthlg" or "brdrtnthtnlg" or "brdrinset" or "brdroutset" => line with { Style = BorderLineStyle.Double },
            "brdrdot" => line with { Style = BorderLineStyle.Dotted },
            "brdrdash" or "brdrdashsm" or "brdrdashd" or "brdrdashdd" or "brdrdashdotstr" => line with { Style = BorderLineStyle.Dashed },
            "brdrhair" => line with { Style = BorderLineStyle.Single, Width = Math.Min(line.Width, 0.5) },
            "brdrth" => line with { Style = BorderLineStyle.Single, Width = Math.Max(line.Width, 1) },
            "brdrs" or "brdrsh" or "brdrwavy" or "brdrwavydb" or "brdremboss" or "brdrengrave" or "brdrframe" => line with { Style = BorderLineStyle.Single },
            _ => line,
        };
        SetBorder(ref p, _borderTarget, line);
        return true;
    }

    private BorderLine? GetBorder(ref RtfParagraphState p, RtfBorderTarget target)
    {
        var row = _depth > 0 ? Top.Row : null;
        return target switch
        {
            RtfBorderTarget.ParagraphTop or RtfBorderTarget.ParagraphBox => p.Borders.Top,
            RtfBorderTarget.ParagraphBottom => p.Borders.Bottom,
            RtfBorderTarget.ParagraphLeft => p.Borders.Left,
            RtfBorderTarget.ParagraphRight => p.Borders.Right,
            RtfBorderTarget.ParagraphBetween => p.Between,
            RtfBorderTarget.RowTop => row?.Borders.Top,
            RtfBorderTarget.RowBottom => row?.Borders.Bottom,
            RtfBorderTarget.RowLeft => row?.Borders.Left,
            RtfBorderTarget.RowRight => row?.Borders.Right,
            RtfBorderTarget.RowHorizontal => row?.InsideHorizontal,
            RtfBorderTarget.RowVertical => row?.InsideVertical,
            RtfBorderTarget.CellTop => row?.Pending.Borders.Top,
            RtfBorderTarget.CellBottom => row?.Pending.Borders.Bottom,
            RtfBorderTarget.CellLeft => row?.Pending.Borders.Left,
            RtfBorderTarget.CellRight => row?.Pending.Borders.Right,
            _ => null,
        };
    }

    private void SetBorder(ref RtfParagraphState p, RtfBorderTarget target, BorderLine line)
    {
        var row = _depth > 0 ? Top.Row : null;
        switch (target)
        {
            case RtfBorderTarget.ParagraphTop:
                p.Borders = p.Borders with { Top = line };
                break;
            case RtfBorderTarget.ParagraphBottom:
                p.Borders = p.Borders with { Bottom = line };
                break;
            case RtfBorderTarget.ParagraphLeft:
                p.Borders = p.Borders with { Left = line };
                break;
            case RtfBorderTarget.ParagraphRight:
                p.Borders = p.Borders with { Right = line };
                break;
            case RtfBorderTarget.ParagraphBox:
                p.Borders = new BoxSides(line, line, line, line);
                break;
            case RtfBorderTarget.ParagraphBetween:
                p.Between = line;
                break;
        }

        if (row is null)
        {
            return;
        }

        switch (target)
        {
            case RtfBorderTarget.RowTop:
                row.Borders = row.Borders with { Top = line };
                break;
            case RtfBorderTarget.RowBottom:
                row.Borders = row.Borders with { Bottom = line };
                break;
            case RtfBorderTarget.RowLeft:
                row.Borders = row.Borders with { Left = line };
                break;
            case RtfBorderTarget.RowRight:
                row.Borders = row.Borders with { Right = line };
                break;
            case RtfBorderTarget.RowHorizontal:
                row.InsideHorizontal = line;
                break;
            case RtfBorderTarget.RowVertical:
                row.InsideVertical = line;
                break;
            case RtfBorderTarget.CellTop:
                row.Pending.Borders = row.Pending.Borders with { Top = line };
                break;
            case RtfBorderTarget.CellBottom:
                row.Pending.Borders = row.Pending.Borders with { Bottom = line };
                break;
            case RtfBorderTarget.CellLeft:
                row.Pending.Borders = row.Pending.Borders with { Left = line };
                break;
            case RtfBorderTarget.CellRight:
                row.Pending.Borders = row.Pending.Borders with { Right = line };
                break;
        }
    }

    /// <summary>
    /// A background from a pattern color and a background color with a shading percentage (a solid fill at 100%), or
    /// <c>null</c> for none.
    /// </summary>
    private PdfColor? Shading(int background, int pattern, int shading)
    {
        var back = ColorAt(background);
        if (shading <= 0)
        {
            return back;
        }

        var fore = ColorAt(pattern) ?? PdfColor.Black;
        var under = back ?? PdfColor.White;
        var t = shading / 10000.0;
        return new PdfColor(
            (fore.Red * t) + (under.Red * (1 - t)),
            (fore.Green * t) + (under.Green * (1 - t)),
            (fore.Blue * t) + (under.Blue * (1 - t)));
    }

    /// <summary>The run format of a character state (made once per distinct state).</summary>
    private RunFormat Format(RtfCharState c)
    {
        if (_formats.TryGetValue(c, out var format))
        {
            return format;
        }

        var font = FontOf(c.Font);
        format = new RunFormat
        {
            FontFamily = font is { Symbol: false, Name.Length: > 0 } ? font.Name : null,
            FontSize = Math.Max(1, c.FontSize / 2.0),
            Bold = c.Bold,
            Italic = c.Italic,
            Underline = c.Underline,
            Strike = c.Strike,
            Color = ColorAt(c.Color),
            Background = ColorAt(c.Highlight) ?? ColorAt(c.Background),
            Position = c.Position,
            BaselineShift = c.Raise / 2.0,
            Caps = c.Caps,
            SmallCaps = c.SmallCaps,
            LetterSpacing = Math.Clamp(c.Spacing / 20.0, -10, 100),
            RightToLeft = c.RightToLeft,
            Hidden = c.Hidden,
            Deleted = c.Deleted,
        };
        if (_formats.Count > 4096)
        {
            _formats.Clear();
        }

        _formats[c] = format;
        return format;
    }

    /// <summary>The paragraph format of a paragraph state.</summary>
    private ParagraphFormat Format(RtfParagraphState p)
    {
        var (spacing, rule) = p.LineSpacing switch
        {
            0 => (1.0, LineSpacingRule.Multiple),
            > 0 when p.LineMultiple => (Math.Clamp(p.LineSpacing / 240.0, 0.1, 20), LineSpacingRule.Multiple),
            > 0 => (p.LineSpacing / 20.0, LineSpacingRule.AtLeast),
            _ => (-p.LineSpacing / 20.0, LineSpacingRule.Exact),
        };
        var tabs = p.Tabs is { Length: > 0 } t ? t.OrderBy(s => s.Position).ToArray() : [];
        return new ParagraphFormat
        {
            Alignment = p.Alignment,
            RightToLeft = p.RightToLeft,
            IndentStart = p.LeftIndent / 20.0,
            IndentEnd = p.RightIndent / 20.0,
            FirstLineIndent = p.FirstIndent / 20.0,
            SpaceBefore = p.SpaceBefore / 20.0,
            SpaceAfter = p.SpaceAfter / 20.0,
            LineSpacing = spacing,
            LineRule = rule,
            ContextualSpacing = p.ContextualSpacing,
            KeepWithNext = p.KeepWithNext,
            KeepLines = p.KeepLines,
            PageBreakBefore = p.PageBreakBefore,
            WidowControl = p.WidowControl,
            OutlineLevel = p.Outline is >= 0 and <= 8 ? p.Outline : null,
            StyleId = "s" + p.Style.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Shading = Shading(p.BackgroundColor, p.PatternColor, p.Shading),
            Borders = p.Borders,
            BetweenBorder = p.Between,
            TabStops = tabs,
        };
    }

    /// <summary>
    /// Text in a symbol font (Symbol, Wingdings, Webdings) as Unicode: its characters (bytes, or private-use code
    /// points) mapped to the characters they show.
    /// </summary>
    private string MapSymbols(string text, int font)
    {
        if (FontOf(font) is not { Symbol: true } symbol)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c < 0x20 ? c.ToString() : Docx.Ooxml.SymbolCharacter(c, symbol.Name));
        }

        return builder.ToString();
    }
}
