using System.Text;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Slides.Pptx;

namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>
/// Text: the text of a shape's client text box or of the slide list (an outline text reference), its paragraph and
/// character runs over the master's text styles for its text type and level, rulers, bullets, links and footer fields.
/// </summary>
internal sealed partial class PptReader
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

    /// <summary>
    /// The text of a text box: its text type (<c>TextHeaderAtom</c>), the text, and the records that style it.
    /// </summary>
    private sealed class TextGroup
    {
        /// <summary>0 title, 1 body, 2 notes, 4 other, 5 center body, 6 center title, 7 half body, 8 quarter body.</summary>
        public int Type { get; set; } = 4;

        public string Text { get; set; } = string.Empty;

        public Record? Style { get; set; }

        public Record? Ruler { get; set; }

        /// <summary>The characters that open hyperlinks: start, end (exclusive), target.</summary>
        public List<(int Start, int End, Uri Uri)> Links { get; } = [];

        /// <summary>Where fields (slide number, date, footer) are, by character position.</summary>
        public HashSet<int> Fields { get; } = [];
    }

    /// <summary>The text groups of a sequence of records (a text box's, or a slide's in the slide list).</summary>
    private List<TextGroup> TextGroups(IEnumerable<Record> records)
    {
        var groups = new List<TextGroup>();
        TextGroup Current()
        {
            if (groups.Count == 0)
            {
                groups.Add(new TextGroup());
            }

            return groups[^1];
        }

        Uri? link = null;
        foreach (var record in records)
        {
            var body = _stream.Body(record);
            switch (record.Type)
            {
                case Rt.TextHeaderAtom:
                    groups.Add(new TextGroup { Type = (int)Math.Min(Bytes.U32(body, 0), 8) });
                    link = null;
                    break;
                case Rt.TextCharsAtom:
                    Current().Text = Encoding.Unicode.GetString(body[..(body.Length & ~1)]);
                    break;
                case Rt.TextBytesAtom:
                    Current().Text = Windows1252.GetString(body);
                    break;
                case Rt.StyleTextPropAtom:
                    Current().Style = record;
                    break;
                case Rt.TextRulerAtom:
                    Current().Ruler = record;
                    break;
                case Rt.InteractiveInfo:
                    link = _stream.Child(record, Rt.InteractiveInfoAtom) is { } atom && Bytes.U8(_stream.Body(atom), 8) == 4
                        && _hyperlinks.TryGetValue(Bytes.I32(_stream.Body(atom), 4), out var uri) ? uri : null;
                    break;
                case Rt.TextInteractiveInfoAtom:
                    if (link is not null && Current().Links.Count < 1000)
                    {
                        Current().Links.Add((Bytes.I32(body, 0), Bytes.I32(body, 4), link));
                    }

                    link = null;
                    break;
                case Rt.SlideNumberMCAtom or Rt.DateTimeMCAtom or Rt.GenericDateMCAtom or Rt.FooterMCAtom or Rt.RtfDateTimeMCAtom:
                    Current().Fields.Add(Bytes.I32(body, 0));
                    break;
            }
        }

        return groups;
    }

    /// <summary>
    /// The text frame of a shape, or <c>null</c> when it has no text: its own text box's text, or the slide list's
    /// text it refers to; for a master's footer placeholder, <paramref name="field"/> in place of its fields.
    /// </summary>
    private TextFrame? Text(EscherShape shape, Sheet owner, Sheet slide, int placeholder, string? field)
    {
        if (shape.ClientTextbox is not { } textbox)
        {
            return null;
        }

        var records = _stream.Children(textbox).ToList();
        TextGroup? group;
        if (records.FirstOrDefault(r => r.Type == Rt.OutlineTextRefAtom) is { Type: Rt.OutlineTextRefAtom } reference)
        {
            var index = Bytes.I32(_stream.Body(reference), 0);
            group = index >= 0 && index < owner.OutlineText.Count ? owner.OutlineText[index] : null;
        }
        else
        {
            group = TextGroups(records).FirstOrDefault();
        }

        if (group is null || group.Text.Length == 0)
        {
            return null;
        }

        var frame = new TextFrame
        {
            LeftInset = Inset(shape, Op.TextLeft, 91440),
            TopInset = Inset(shape, Op.TextTop, 45720),
            RightInset = Inset(shape, Op.TextRight, 91440),
            BottomInset = Inset(shape, Op.TextBottom, 45720),
            Wrap = shape.Value(Op.WrapText, 0) != 2,
        };
        var anchor = shape.Value(Op.AnchorText, 0);
        frame.Anchor = anchor switch { 1 or 4 => TextAnchor.Middle, 2 or 5 or 7 or 9 => TextAnchor.Bottom, _ => TextAnchor.Top };
        frame.AnchorCenter = anchor is 3 or 4 or 5 or 8 or 9;
        frame.Flow = shape.Value(Op.TextFlow, 0) switch { 1 or 5 => TextFlow.Vertical, 2 => TextFlow.Vertical270, _ => TextFlow.Horizontal };

        var text = group.Text;
        var (paragraphList, characterList) = group.Style is { } style ? PptTextStyles.Runs(_stream.Body(style), text.Length) : ([], []);
        var paragraphRuns = new RunTable<ParagraphRun>(paragraphList, r => r.Count);
        var characterRuns = new RunTable<CharacterRun>(characterList, r => r.Count);
        var ruler = group.Ruler is { } rulerRecord ? PptTextStyles.Ruler(_stream.Body(rulerRecord)) : null;
        var styles = StyleSheet(owner);
        var type = placeholder is 7 or 8 or 9 ? 4 : group.Type;

        var start = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOf('\r', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var (level, runProps) = At(paragraphRuns, start);
            var (masterParagraph, masterCharacter) = Resolve(styles, type, level);
            var paragraphProps = masterParagraph;
            if (ruler is not null)
            {
                paragraphProps = paragraphProps.Merge(new ParagraphProps { TextOffset = ruler.TextOffsets[level], BulletOffset = ruler.BulletOffsets[level] });
            }

            paragraphProps = paragraphProps.Merge(runProps);
            var endStyle = RunStyle(masterCharacter.Merge(At(characterRuns, end)), slide);
            var paragraph = Paragraph(paragraphProps, level, endStyle, slide);
            AddRuns(paragraph, text, start, end, group, masterCharacter, characterRuns, slide, field);
            frame.Paragraphs.Add(paragraph);
            if (end >= text.Length)
            {
                break;
            }

            start = end + 1;
        }

        return frame;
    }

    private static double Inset(EscherShape shape, int id, int fallback) => Math.Clamp(shape.Value(id, fallback) / Emu, 0, 1000);

    /// <summary>The runs of a paragraph's characters: split where the character style, a link or a field changes.</summary>
    private void AddRuns(TextParagraph paragraph, string text, int start, int end, TextGroup group, CharacterProps master,
        RunTable<CharacterRun> runs, Sheet slide, string? field)
    {
        var builder = new StringBuilder();
        CharacterProps? current = null;
        Uri? currentLink = null;
        void Flush()
        {
            if (builder.Length > 0 && current is not null)
            {
                paragraph.Runs.Add(new TextRun { Text = builder.ToString(), Style = RunStyle(master.Merge(current), slide), Link = currentLink });
            }

            builder.Clear();
        }

        for (var i = start; i < end; i++)
        {
            var props = At(runs, i);
            var link = group.Links.Count == 0 ? null : group.Links.FirstOrDefault(l => i >= l.Start && i < l.End).Uri;
            if (!ReferenceEquals(props, current) || link != currentLink)
            {
                Flush();
                (current, currentLink) = (props, link);
            }

            var c = text[i];
            if (field is not null && (group.Fields.Contains(i) || group.Fields.Count == 0))
            {
                // A footer placeholder's field shows the footer, date or slide number; without fields, all its text.
                if (group.Fields.Count > 0 || i == 0)
                {
                    builder.Append(field);
                }

                continue;
            }

            if (c == '\v')
            {
                Flush();
                paragraph.Runs.Add(new TextRun { Text = "\n", Style = RunStyle(master.Merge(props), slide) });
            }
            else if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
            {
                // A surrogate pair stays in one run, even when the style runs split it.
                builder.Append(c).Append(text[++i]);
            }
            else if (c >= ' ' || c == '\t')
            {
                builder.Append(c);
            }
        }

        Flush();
    }

    /// <summary>The props of the run covering a character position (the last run's past the end; empty without runs).</summary>
    private static CharacterProps At(RunTable<CharacterRun> runs, int position) => runs.At(position) is { } run ? run.Props : EmptyCharacter;

    private static readonly CharacterProps EmptyCharacter = new();

    /// <summary>The level and props of the paragraph run covering a character position.</summary>
    private static (int Level, ParagraphProps? Props) At(RunTable<ParagraphRun> runs, int position) =>
        runs.At(position) is { } run ? (run.Level, run.Props) : (0, null);

    /// <summary>Runs of text properties, found by character position (a binary search of where they end).</summary>
    private sealed class RunTable<T>
        where T : struct
    {
        private readonly List<T> _runs;
        private readonly long[] _ends;

        public RunTable(List<T> runs, Func<T, int> count)
        {
            _runs = runs;
            _ends = new long[runs.Count];
            var end = 0L;
            for (var i = 0; i < runs.Count; i++)
            {
                end += count(runs[i]);
                _ends[i] = end;
            }
        }

        /// <summary>The run covering a position (the last one past the end), or <c>null</c> without runs.</summary>
        public T? At(int position)
        {
            if (_runs.Count == 0)
            {
                return null;
            }

            var index = Array.BinarySearch(_ends, (long)position);
            index = index >= 0 ? index + 1 : ~index;
            return _runs[Math.Min(index, _runs.Count - 1)];
        }
    }

    /// <summary>The main master whose text styles a sheet's text uses.</summary>
    private static Sheet? StyleSheet(Sheet sheet)
    {
        var s = (Sheet?)sheet;
        for (var depth = 0; s is not null && depth <= MaxMasterDepth; depth++, s = s.Master)
        {
            if (s.Styles.Count > 0)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// The paragraph and character style of a text type at a level: the document's defaults, the master's style of
    /// the type it is based on (title or body), then of the type itself, each level over the ones above it.
    /// </summary>
    private (ParagraphProps Paragraph, CharacterProps Character) Resolve(Sheet? master, int type, int level)
    {
        var paragraph = _defaultParagraph;
        var character = _defaultCharacter;
        (ParagraphProps?, CharacterProps?)[]? Levels(int t) =>
            master is not null && master.Styles.TryGetValue(t, out var styles) ? styles
            : t == 4 && _environmentStyles.TryGetValue(4, out var defaults) ? defaults : null;

        void Apply((ParagraphProps? Paragraph, CharacterProps? Character)[]? levels)
        {
            if (levels is null)
            {
                return;
            }

            for (var l = 0; l <= level && l < levels.Length; l++)
            {
                paragraph = paragraph.Merge(levels[l].Paragraph);
                character = character.Merge(levels[l].Character);
            }
        }

        var baseType = type switch { 5 or 7 or 8 => 1, 6 => 0, _ => type };
        Apply(Levels(baseType));
        if (baseType != type)
        {
            Apply(Levels(type));
        }

        return (paragraph, character);
    }

    /// <summary>A paragraph of resolved props: alignment, indents, spacing, bullet.</summary>
    private TextParagraph Paragraph(ParagraphProps props, int level, RunStyle endStyle, Sheet slide)
    {
        static Spacing Space(int? value, Spacing fallback) => value switch
        {
            null => fallback,
            >= 0 => new Spacing(Math.Min(value.Value, 10000) / 100.0, false),
            _ => new Spacing(Math.Min(-(long)value.Value, 100000) / MasterUnits, true),
        };

        var textOffset = (props.TextOffset ?? 0) / MasterUnits;
        var bulletOffset = (props.BulletOffset ?? 0) / MasterUnits;
        var lineSpacing = props.LineSpacing is 0 ? Spacing.Single : Space(props.LineSpacing, Spacing.Single);
        return new TextParagraph
        {
            Alignment = props.Align switch { 1 => ParagraphAlignment.Center, 2 => ParagraphAlignment.Right, 3 or 4 or 5 or 6 => ParagraphAlignment.Justify, _ => ParagraphAlignment.Left },
            Level = level,
            MarginLeft = textOffset,
            Indent = bulletOffset - textOffset,
            SpaceBefore = Space(props.SpaceBefore, Spacing.None),
            SpaceAfter = Space(props.SpaceAfter, Spacing.None),
            LineSpacing = lineSpacing,
            Bullet = props.HasBullet == true ? Bullet(props, slide) : null,
            EndStyle = endStyle,
        };
    }

    /// <summary>A paragraph's bullet: its character (symbol font characters as Unicode), font, color and size.</summary>
    private Bullet Bullet(ParagraphProps props, Sheet slide)
    {
        var font = props.BulletHasFont == true && props.BulletFont is { } index ? FontName(index) : null;
        var character = props.BulletChar is { } c and not '\0' && !char.IsSurrogate(c) ? c : '\u2022';
        string text;
        if (font is not null && (PptxReader.IsSymbolFont(font) || _symbolFonts.Contains(props.BulletFont!.Value)) || character is >= '\uF000' and <= '\uF0FF')
        {
            text = PptxReader.SymbolBullets.TryGetValue(character, out var unicode)
                || PptxReader.SymbolBullets.TryGetValue((char)(character & 0xFF), out unicode) ? unicode : "\u2022";
            font = null;
        }
        else
        {
            text = character.ToString();
        }

        var size = props.BulletHasSize == true ? props.BulletSize : null;
        return new Bullet
        {
            Text = text,
            Font = font,
            Color = props.BulletHasColor == true && props.BulletColor is { } color ? TextColor(color, slide) : null,
            SizeFactor = size is > 0 ? Math.Clamp(size.Value / 100.0, 0.25, 4) : 1,
            SizePoints = size is < 0 ? Math.Clamp(-size.Value, 1, 4000) : null,
        };
    }

    /// <summary>A run style of resolved character props.</summary>
    private RunStyle RunStyle(CharacterProps props, Sheet slide) => new()
    {
        Font = props.Font is { } font ? FontName(font) ?? "Arial" : "Arial",
        EastAsianFont = props.EastAsianFont is { } eastAsian ? FontName(eastAsian) : null,
        Size = props.Size is > 0 and < 4000 ? props.Size.Value : 18,
        Bold = props.Bold == true,
        Italic = props.Italic == true,
        Underline = props.Underline == true,
        Color = props.Color is { } color ? TextColor(color, slide) : slide.Scheme[1],
        Baseline = Math.Clamp((props.Position ?? 0) / 100.0, -1, 1),
    };

    /// <summary>A font of the font collection by index, or <c>null</c>.</summary>
    private string? FontName(int index) => index >= 0 && index < _fonts.Count && _fonts[index].Length > 0 ? _fonts[index] : null;

    /// <summary>A text color: red, green and blue, or a color of the slide's scheme.</summary>
    private static PdfColor TextColor(ColorIndex color, Sheet slide) =>
        color.Index == 0xFE ? PdfColor.FromRgb(color.Red, color.Green, color.Blue)
        : color.Index < slide.Scheme.Length ? slide.Scheme[color.Index]
        : slide.Scheme[1];
}
