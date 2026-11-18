using System.Text;

namespace RaiCore.Pdf.Extraction.Text;

/// <summary>
/// Puts the text items of a page together into text (see <see cref="TextExtractionMode"/>). Items are grouped by
/// writing direction; within a direction, items drawn one after the other on a baseline become segments (words, runs
/// of words, table cells), segments are split into blocks by a recursive XY cut that keeps columns apart, and the
/// segments of a block are grouped into lines by their vertical overlap.
/// </summary>
internal static class TextLayout
{
    /// <summary>A gap wider than this (in ems of the font size) between two glyphs is a word space.</summary>
    internal const double WordGap = 0.12;

    /// <summary>A gap wider than this (in ems) between two glyphs on a baseline starts a new segment.</summary>
    internal const double SegmentGap = 1.2;

    /// <summary>The narrowest gap (in ems) between two columns.</summary>
    internal const double ColumnGap = 1.0;

    /// <summary>Columns of text have lines at least this long (in ems, the median per side); narrower ones are table columns, read row by row.</summary>
    internal const double ColumnLineLength = 6;

    /// <summary>How many candidate column gaps of a region are tried (the widest).</summary>
    internal const int MaxColumnCandidates = 32;

    /// <summary>A vertical gap at least this tall (in ems) between blocks of lines separates paragraphs.</summary>
    internal const double ParagraphGap = 0.6;

    /// <summary>Two segments are on one line when their extents across the baseline overlap by at least this much of the smaller one.</summary>
    internal const double LineOverlap = 0.5;

    public static string Compose(IReadOnlyList<TextItem> items, TextExtractionMode mode) => mode switch
    {
        _ when items.Count == 0 => string.Empty,
        TextExtractionMode.Raw => Raw(items),
        TextExtractionMode.Layout => string.Join("\n\n", ByDirection(items).Select(Physical).Where(t => t.Length > 0)),
        _ => ComposeReading(items).ToString(),
    };

    /// <summary>The text in reading order (<see cref="TextExtractionMode.Reading"/>), with the item each character came from.</summary>
    public static MappedText ComposeReading(IReadOnlyList<TextItem> items) =>
        MappedText.Join("\n\n", ByDirection(items).Select(Reading).Where(t => t.Length > 0));

    /// <summary>The items grouped by direction: ordinary text first, then other directions in the order they first appear.</summary>
    private static IEnumerable<List<TextItem>> ByDirection(IReadOnlyList<TextItem> items) =>
        items.GroupBy(i => i.Angle).OrderBy(g => g.Key == 0 ? 0 : 1).ThenBy(g => g.Min(i => i.Order)).Select(g => g.ToList());

    /// <summary>Reading order: blocks from the XY cut, their lines top to bottom, paragraphs separated by an empty line.</summary>
    private static MappedText Reading(List<TextItem> items)
    {
        var segments = Segments(items);
        if (segments.Count == 0)
        {
            return new MappedText(); // Only spaces.
        }

        var page = new PageContext(
            Bidi.Direction(segments.SelectMany(s => s.Tokens())) > 0,
            segments.Min(s => s.U0),
            segments.Max(s => s.U1),
            Median(segments.Select(s => s.Size)));
        var blocks = Blocks(segments);
        return MappedText.Join("\n\n", blocks.Select(b => BlockText(b, page)).Where(b => b.Length > 0));
    }

    /// <summary>
    /// A block's lines, in the block's direction (see <see cref="IsRightToLeft"/>).
    /// </summary>
    private static MappedText BlockText(List<Segment> block, PageContext page)
    {
        var lines = Lines(block);
        var rightToLeft = IsRightToLeft(block, lines, page);
        return MappedText.Join("\n", lines.Select(line => LineText(line, rightToLeft)));
    }

    /// <summary>
    /// Whether a block is a right-to-left paragraph. Its letters decide when they clearly do; when close (a heading
    /// that starts in Latin letters, a table row with a product name in a Hebrew invoice), its alignment does (right
    /// to-left paragraphs are right-aligned), then the direction of its first line's rightmost word (where a
    /// right-to-left line starts), then the page's direction.
    /// </summary>
    private static bool IsRightToLeft(List<Segment> block, List<List<Segment>> lines, PageContext page)
    {
        var direction = Bidi.Direction(block.SelectMany(s => s.Tokens()));
        if (direction != 0)
        {
            return direction > 0;
        }

        var left = block.Min(s => s.U0) - page.Left;
        var right = page.Right - block.Max(s => s.U1);
        if (right <= page.Em && left > 2 * page.Em)
        {
            return true;
        }

        if (left <= page.Em && right > 2 * page.Em)
        {
            return false;
        }

        foreach (var token in lines[0].AsEnumerable().Reverse().SelectMany(s => s.Tokens().Reverse()))
        {
            if (Bidi.Direction([token]) is var tokenDirection && token.Any(char.IsLetter))
            {
                return tokenDirection > 0;
            }
        }

        return page.RightToLeft;
    }

    /// <summary>The physical layout: every line across the page, segments placed at their horizontal position in characters.</summary>
    private static string Physical(List<TextItem> items)
    {
        var segments = Segments(items);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var lines = Lines(segments);
        var glyphs = items.Where(i => !i.IsSpace && i.Text.Length > 0).ToList();
        // A little under the median glyph width, so that proportional text rarely runs past the next segment's place.
        var charWidth = 0.8 * Median(glyphs.Select(i => (i.U1 - i.U0) / i.Text.Length).Where(w => w > 0).DefaultIfEmpty(5));
        charWidth = Math.Max(charWidth, 0.01);
        var left = segments.Min(s => s.U0);
        var lineHeight = Median(segments.Select(s => s.Bottom - s.Top));
        var text = new StringBuilder();
        double? previousBottom = null;
        foreach (var line in lines)
        {
            if (previousBottom is { } bottom && line.Min(s => s.Top) - bottom > lineHeight)
            {
                text.Append('\n'); // A visible gap between lines: an empty line.
            }

            var row = new StringBuilder();
            Segment? previous = null;
            foreach (var segment in line)
            {
                var column = (int)Math.Round((segment.U0 - left) / charWidth);
                column = Math.Min(column, row.Length + 500);
                var gap = previous is null ? double.PositiveInfinity : segment.U0 - previous.U1;
                var least = previous is null ? 0 : gap >= ColumnGap * segment.Size ? 2 : IsWordBreak(previous, segment) ? 1 : 0;
                row.Append(' ', Math.Max(least, column - row.Length));
                row.Append(Bidi.ToLogical(segment.Tokens()));
                previous = segment;
            }

            text.Append(row.ToString().TrimEnd()).Append('\n');
            previousBottom = line.Max(s => s.Bottom);
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Content order, with a line break where the baseline changes and a space for a gap.</summary>
    private static string Raw(IReadOnlyList<TextItem> items)
    {
        var text = new StringBuilder();
        TextItem? previous = null;
        foreach (var item in items)
        {
            if (previous is not null)
            {
                var sameLine = item.Angle == previous.Angle && Math.Abs(item.V - previous.V) <= 0.5 * Math.Max(item.Size, previous.Size);
                if (!sameLine)
                {
                    TrimSpace(text);
                    text.Append('\n');
                }
                else if (!item.IsSpace && !previous.IsSpace
                    && (item.U0 - previous.U1 > WordGap * item.Size || item.U0 < previous.U0 - item.Size))
                {
                    text.Append(' ');
                }
            }

            if (!(item.IsSpace && (text.Length == 0 || text[^1] is ' ' or '\n')))
            {
                text.Append(item.IsSpace ? " " : item.Text);
            }

            previous = item;
        }

        return string.Join('\n', text.ToString().Split('\n').Select(l => CollapseSpaces(l).TrimEnd())).Trim('\n');
    }

    /// <summary>Splits items, in drawing order, into segments: runs on one baseline without a wide gap.</summary>
    internal static List<Segment> Segments(List<TextItem> items)
    {
        var segments = new List<Segment>();
        Segment? current = null;
        foreach (var item in items)
        {
            if (current is not null && current.Continues(item))
            {
                current.Add(item);
            }
            else
            {
                current = new Segment(item);
                segments.Add(current);
            }
        }

        return [.. segments.Where(s => s.HasText)];
    }

    /// <summary>
    /// The blocks of a region, in reading order, by a recursive XY cut (done with a stack, so that deep nesting
    /// cannot overflow): columns are cut apart left to right, other regions into bands top to bottom.
    /// </summary>
    internal static List<List<Segment>> Blocks(List<Segment> segments)
    {
        var blocks = new List<List<Segment>>();
        var pending = new Stack<List<Segment>>();
        pending.Push(segments);
        while (pending.Count > 0)
        {
            var region = pending.Pop();
            var parts = region.Count > 1 ? CutColumns(region) ?? CutBands(region) : null;
            if (parts is null)
            {
                blocks.Add(region);
                continue;
            }

            for (var i = parts.Count - 1; i >= 0; i--)
            {
                pending.Push(parts[i]);
            }
        }

        return blocks;
    }

    /// <summary>
    /// Cuts a region into columns. The region's lines are horizontal strips; a candidate column gap is a wide gap
    /// between two segments of a strip. Strips with a segment across the gap (headings, table rows, footers) stay
    /// whole, and runs of the other strips become a band whose left and right sides are read one after the other,
    /// when both sides are columns of text. Of the candidates, the one that splits the most segments wins.
    /// </summary>
    private static List<List<Segment>>? CutColumns(List<Segment> region)
    {
        var em = Median(region.Select(s => s.Size));
        var strips = Lines(region);
        var candidates = new List<(double Left, double Right)>();
        foreach (var strip in strips)
        {
            for (var i = 1; i < strip.Count; i++)
            {
                var (left, right) = (strip[i - 1].U1, strip[i].U0);
                if (right - left < ColumnGap * em)
                {
                    continue;
                }

                // Gaps of different lines at the same place are one candidate: the part free in all of them.
                var same = candidates.FindIndex(c => c.Left < right && c.Right > left);
                if (same < 0)
                {
                    candidates.Add((left, right));
                }
                else if (Math.Min(right, candidates[same].Right) - Math.Max(left, candidates[same].Left) >= ColumnGap * em)
                {
                    candidates[same] = (Math.Max(left, candidates[same].Left), Math.Min(right, candidates[same].Right));
                }
            }
        }

        List<List<Segment>>? best = null;
        var bestScore = 0;
        foreach (var candidate in candidates.OrderByDescending(c => c.Right - c.Left).Take(MaxColumnCandidates))
        {
            var (parts, score) = SplitAt(strips, candidate.Left, candidate.Right, em);
            if (score > bestScore && parts.Count > 1)
            {
                (best, bestScore) = (parts, score);
            }
        }

        return best;
    }

    /// <summary>
    /// The parts of a region split at a column gap (see <see cref="CutColumns"/>), and how many segments were put in
    /// columns (0 when the gap separates no columns of text).
    /// </summary>
    private static (List<List<Segment>> Parts, int Score) SplitAt(List<List<Segment>> strips, double gapLeft, double gapRight, double em)
    {
        var parts = new List<List<Segment>>();
        var other = new List<Segment>();
        var band = new List<List<Segment>>();
        var score = 0;

        void FlushBand()
        {
            var left = band.SelectMany(s => s).Where(s => s.U1 <= gapLeft).ToList();
            var right = band.SelectMany(s => s).Where(s => s.U0 >= gapRight).ToList();
            if (left.Count >= 2 && right.Count >= 2 && IsTextColumn(left, em) && IsTextColumn(right, em))
            {
                if (other.Count > 0)
                {
                    parts.Add(other);
                    other = [];
                }

                parts.Add(left);
                parts.Add(right);
                score += left.Count + right.Count;
            }
            else
            {
                other.AddRange(band.SelectMany(s => s));
            }

            band.Clear();
        }

        foreach (var strip in strips)
        {
            // A strip across the gap, or of short segments (a table row), is not part of a band of columns.
            if (strip.Any(s => s.U0 < gapRight && s.U1 > gapLeft) || Median(strip.Select(s => s.Width)) < ColumnLineLength * em)
            {
                FlushBand();
                other.AddRange(strip);
            }
            else
            {
                band.Add(strip);
            }
        }

        FlushBand();
        if (other.Count > 0)
        {
            parts.Add(other);
        }

        return (parts, score);
    }

    private static bool IsTextColumn(IEnumerable<Segment> side, double em)
    {
        var widths = side.Select(s => s.Width).ToList();
        return widths.Count > 0 && Median(widths) >= ColumnLineLength * em;
    }

    /// <summary>Cuts a region into bands at vertical gaps between paragraphs.</summary>
    private static List<List<Segment>>? CutBands(List<Segment> region)
    {
        var em = Median(region.Select(s => s.Size));
        var bands = new List<List<Segment>>();
        var reach = double.NegativeInfinity;
        foreach (var segment in region.OrderBy(s => s.Top))
        {
            if (bands.Count == 0 || segment.Top - reach >= ParagraphGap * em)
            {
                bands.Add([]);
            }

            bands[^1].Add(segment);
            reach = Math.Max(reach, segment.Bottom);
        }

        return bands.Count > 1 ? bands : null;
    }

    /// <summary>The lines of a block, top to bottom, each with its segments from left to right.</summary>
    internal static List<List<Segment>> Lines(List<Segment> block)
    {
        var lines = new List<(double Top, double Bottom, List<Segment> Segments)>();
        foreach (var segment in block.OrderBy(s => s.Top).ThenBy(s => s.U0))
        {
            var placed = false;
            for (var i = lines.Count - 1; i >= 0 && i >= lines.Count - 8; i--)
            {
                // A segment joins a line it overlaps enough across the baseline, unless it would overlap one of the
                // line's segments along it: a large segment (a title, a page number) can span two lines of small text.
                var (top, bottom, members) = lines[i];
                var overlap = Math.Min(bottom, segment.Bottom) - Math.Max(top, segment.Top);
                if (overlap >= LineOverlap * Math.Min(bottom - top, segment.Bottom - segment.Top)
                    && !members.Any(m => Math.Min(m.U1, segment.U1) - Math.Max(m.U0, segment.U0) > 0.3 * Math.Min(m.Size, segment.Size)))
                {
                    members.Add(segment);
                    lines[i] = (Math.Min(top, segment.Top), Math.Max(bottom, segment.Bottom), members);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                lines.Add((segment.Top, segment.Bottom, [segment]));
            }
        }

        return [.. lines.OrderBy(l => l.Segments.Average(s => s.V)).Select(l => l.Segments.OrderBy(s => s.U0).ToList())];
    }

    /// <summary>
    /// A line's text in logical order. A line breaks into cells at gaps as wide as between columns (table cells, a
    /// label and its value); each cell is put in logical order on its own, and the cells are read from the line's start:
    /// right to left in a right-to-left paragraph.
    /// </summary>
    private static MappedText LineText(List<Segment> line, bool rightToLeft)
    {
        var cells = new List<MappedText>();
        var tokens = new List<Token>();
        for (var i = 0; i < line.Count; i++)
        {
            if (i > 0 && line[i].U0 - line[i - 1].U1 >= ColumnGap * line[i].Size)
            {
                cells.Add(Logical(tokens, rightToLeft));
                tokens = [];
            }
            else if (i > 0 && IsWordBreak(line[i - 1], line[i]))
            {
                tokens.Add(Token.Space);
            }

            tokens.AddRange(line[i].MappedTokens());
        }

        cells.Add(Logical(tokens, rightToLeft));
        if (rightToLeft && cells.Count > 1)
        {
            cells.Reverse();
        }

        return MappedText.Join(" ", cells.Select(c => c.CollapseSpaces())).CollapseSpaces();
    }

    /// <summary>Tokens in visual order put in logical order (see <see cref="Bidi.LogicalOrder"/>).</summary>
    private static MappedText Logical(List<Token> tokens, bool rightToLeft)
    {
        var text = new MappedText();
        foreach (var index in Bidi.LogicalOrder([.. tokens.Select(t => t.Text)], rightToLeft))
        {
            text.Append(tokens[index].Text, tokens[index].Source);
        }

        return text;
    }

    /// <summary>
    /// Whether two segments of a line, left to right, are separate words: there is a gap between them. Segments also
    /// split where a producer draws the parts of a line out of order (bidirectional runs), without a gap.
    /// </summary>
    private static bool IsWordBreak(Segment left, Segment right) => right.U0 - left.U1 > WordGap * right.Size;

    private static string CollapseSpaces(string text)
    {
        if (!text.Contains("  ", StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c != ' ' || builder.Length == 0 || builder[^1] != ' ')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static void TrimSpace(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    /// <summary>A piece of a line's text: a glyph's text and its item, or a space between words (no item).</summary>
    internal readonly record struct Token(string Text, TextItem? Source)
    {
        public static Token Space { get; } = new(" ", null);
    }

    /// <summary>What a page's text is like: its direction and horizontal extent, and its typical font size.</summary>
    private readonly record struct PageContext(bool RightToLeft, double Left, double Right, double Em);

    /// <summary>Text items drawn one after the other on a baseline.</summary>
    internal sealed class Segment
    {
        private readonly List<TextItem> _items = [];

        public Segment(TextItem first) => Add(first);

        public double U0 { get; private set; } = double.PositiveInfinity;

        public double U1 { get; private set; } = double.NegativeInfinity;

        public double Top { get; private set; } = double.PositiveInfinity;

        public double Bottom { get; private set; } = double.NegativeInfinity;

        /// <summary>The baseline (of the first glyph).</summary>
        public double V { get; private set; }

        /// <summary>The largest font size.</summary>
        public double Size { get; private set; }

        public double Width => U1 - U0;

        public bool HasText => _items.Any(i => !i.IsSpace);

        private TextItem Last => _items[^1];

        /// <summary>Whether an item continues the segment: on its baseline, after its end, without a wide gap.</summary>
        public bool Continues(TextItem item)
        {
            var last = Last;
            var size = Math.Max(item.Size, last.Size);
            return item.Angle == last.Angle
                && Math.Abs(item.V - last.V) <= 0.25 * size
                && item.U0 >= last.U1 - (0.5 * size)
                && item.U0 - last.U1 <= SegmentGap * size;
        }

        public void Add(TextItem item)
        {
            if (_items.Count == 0)
            {
                V = item.V;
            }

            _items.Add(item);
            if (!item.IsSpace)
            {
                U0 = Math.Min(U0, item.U0);
                U1 = Math.Max(U1, item.U1);
                Top = Math.Min(Top, item.Top);
                Bottom = Math.Max(Bottom, item.Bottom);
            }

            Size = Math.Max(Size, item.Size);
        }

        /// <summary>The segment's text as tokens in drawing order: each glyph's text, and a space where words break.</summary>
        public IEnumerable<string> Tokens() => MappedTokens().Select(t => t.Text);

        /// <summary>The segment's tokens (see <see cref="Tokens"/>) with the items they come from.</summary>
        public IEnumerable<Token> MappedTokens()
        {
            TextItem? previous = null;
            var spaced = false;
            foreach (var item in _items)
            {
                if (item.IsSpace)
                {
                    spaced = previous is not null;
                    continue;
                }

                if (previous is not null && (spaced || item.U0 - previous.U1 > WordGap * item.Size))
                {
                    yield return Token.Space;
                }

                yield return new Token(item.Text, item);
                previous = item;
                spaced = false;
            }
        }
    }
}
