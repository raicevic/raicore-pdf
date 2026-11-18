using System.Text;
using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Extraction.Text;

/// <summary>
/// A piece of text on a page, in the frame of its writing direction: <see cref="U0"/> to <see cref="U1"/> along the
/// baseline, <see cref="V"/> the baseline across it (growing downwards on the page), <see cref="Top"/> and
/// <see cref="Bottom"/> its extent across.
/// </summary>
internal sealed record TextItem(string Text, int Angle, double U0, double U1, double V, double Top, double Bottom, double Size, int Order)
{
    public bool IsSpace { get; } = string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Collects the text items of a page from the interpreter's glyphs: replaces a marked-content sequence that has
/// <c>/ActualText</c> by that text, leaves out invisible text (on request) and text clipped away or outside the page, drops glyphs
/// drawn twice on top of each other (fake bold), and normalizes ligatures and presentation forms.
/// </summary>
internal sealed class GlyphCollector(double pageWidth, double pageHeight, bool includeInvisible) : ContentSink
{
    /// <summary>How many earlier items are checked for a glyph drawn again at the same place.</summary>
    private const int DuplicateWindow = 16;

    private readonly Stack<bool> _actualTextStarts = new();
    private List<TextGlyph>? _actualGlyphs;
    private string? _actualText;
    private int _order;

    public List<TextItem> Items { get; } = [];

    public override void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
        var starts = _actualText is null && properties?["ActualText"] is PdfString actual;
        if (starts)
        {
            _actualText = PdfText.Decode(((PdfString)properties!["ActualText"]!).Bytes);
            _actualGlyphs = [];
        }

        _actualTextStarts.Push(starts);
    }

    public override void EndMarkedContent()
    {
        if (_actualTextStarts.Count > 0 && _actualTextStarts.Pop())
        {
            FlushActualText();
        }
    }

    public override void Glyph(in TextGlyph glyph)
    {
        if ((!includeInvisible && glyph.RenderMode is 3 or 7) || glyph.IsClipped || !IsOnPage(glyph.Bounds))
        {
            return;
        }

        if (_actualGlyphs is not null)
        {
            _actualGlyphs.Add(glyph);
            return;
        }

        var text = Normalize(glyph.Text);
        if (text.Length > 0)
        {
            Add(ItemOf(glyph, text));
        }
    }

    /// <summary>Ends an <c>/ActualText</c> sequence: its glyphs become one item with the actual text.</summary>
    private void FlushActualText()
    {
        var glyphs = _actualGlyphs!;
        var text = _actualText!.Replace("\0", string.Empty, StringComparison.Ordinal);
        _actualGlyphs = null;
        _actualText = null;
        if (glyphs.Count == 0 || text.Length == 0)
        {
            return;
        }

        var first = ItemOf(glyphs[0], text);
        foreach (var glyph in glyphs.Skip(1))
        {
            var item = ItemOf(glyph, text);
            if (item.Angle == first.Angle)
            {
                first = first with
                {
                    U0 = Math.Min(first.U0, item.U0),
                    U1 = Math.Max(first.U1, item.U1),
                    Top = Math.Min(first.Top, item.Top),
                    Bottom = Math.Max(first.Bottom, item.Bottom),
                };
            }
        }

        Add(first);
    }

    private void Add(TextItem item)
    {
        for (var i = Items.Count - 1; i >= 0 && i >= Items.Count - DuplicateWindow; i--)
        {
            var other = Items[i];
            if (other.Angle == item.Angle && other.Text == item.Text && !item.IsSpace
                && Math.Abs(other.U0 - item.U0) < 0.1 * item.Size && Math.Abs(other.V - item.V) < 0.1 * item.Size)
            {
                return;
            }
        }

        Items.Add(item);
    }

    private TextItem ItemOf(TextGlyph glyph, string text)
    {
        var angle = (int)Math.Round(Math.Atan2(glyph.Direction.Y, glyph.Direction.X) * 180 / Math.PI / 2) * 2;
        angle = angle <= -180 ? angle + 360 : angle;
        var radians = angle * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var size = Math.Max(glyph.FontSize, 0.01);

        // The frame: u along the writing direction, v across it, growing downwards on the page.
        PdfPoint Frame(PdfPoint p) => new((p.X * cos) + (p.Y * sin), (-p.X * sin) + (p.Y * cos));

        if (glyph.Font.IsVertical)
        {
            var box = glyph.Bounds;
            PdfPoint[] corners = [Frame(new(box.Left, box.Top)), Frame(new(box.Right, box.Top)), Frame(new(box.Right, box.Bottom)), Frame(new(box.Left, box.Bottom))];
            var (top, bottom) = (corners.Min(c => c.Y), corners.Max(c => c.Y));
            return new TextItem(text, angle, corners.Min(c => c.X), corners.Max(c => c.X), (top + bottom) / 2, top, bottom, size, _order++);
        }

        var origin = Frame(glyph.Origin);
        var end = Frame(glyph.End);
        return new TextItem(
            text, angle, Math.Min(origin.X, end.X), Math.Max(origin.X, end.X), origin.Y,
            origin.Y - (glyph.Font.Ascent * size), origin.Y - (glyph.Font.Descent * size), size, _order++);
    }

    /// <summary>Whether a glyph box touches the page (text outside it is hidden, often on purpose).</summary>
    private bool IsOnPage(Box box) =>
        box.Right >= -1 && box.Bottom >= -1 && box.Left <= pageWidth + 1 && box.Top <= pageHeight + 1;

    /// <summary>
    /// The text of a glyph as extracted: ligatures and Arabic presentation forms become the letters they stand for,
    /// a no-break space a space, and control characters nothing.
    /// </summary>
    internal static string Normalize(string text)
    {
        var plain = true;
        foreach (var c in text)
        {
            if (c < ' ' || c is '\u00A0' or '\u007F' || PresentationForms.IsPresentationForm(c))
            {
                plain = false;
                break;
            }
        }

        if (plain)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\u00A0')
            {
                builder.Append(' ');
            }
            else if (PresentationForms.Decompose(c) is { } letters)
            {
                builder.Append(letters);
            }
            else if (c >= ' ' && c is not ('\u007F' or '\uFEFF'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
