using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>A page of a <see cref="PdfEditor"/>: its boxes and rotation.</summary>
public sealed class PdfEditorPage
{
    private readonly ObjectTable _table;
    private readonly PdfReference _page;

    internal PdfEditorPage(ObjectTable table, PdfReference page)
    {
        _table = table;
        _page = page;
    }

    /// <summary>The media box: the page's full size, in points, in its user space.</summary>
    public PdfRectangle MediaBox => Rectangle("MediaBox") ?? new PdfRectangle(0, 0, 612, 792); // US Letter, the default of ISO 32000-2.

    /// <summary>The crop box: the part of the page that is shown, in points (the media box when the page has none).</summary>
    public PdfRectangle CropBox => Rectangle("CropBox") is { } crop && Intersect(crop, MediaBox) is { } shown ? shown : MediaBox;

    /// <summary>
    /// How far the page is turned clockwise when it is shown: 0, 90, 180 or 270 degrees. Set it to turn the page; any
    /// multiple of 90 is accepted (-90 is 270).
    /// </summary>
    /// <exception cref="ArgumentException">The value set is not a multiple of 90.</exception>
    public int Rotation
    {
        get => _table.Resolve(Dictionary["Rotate"]) switch
        {
            PdfInteger n when n.Value % 90 == 0 => (int)(((n.Value % 360) + 360) % 360),
            _ => 0,
        };
        set
        {
            if (value % 90 != 0)
            {
                throw new ArgumentException("A page's rotation is a multiple of 90 degrees.", nameof(value));
            }

            var normalized = ((value % 360) + 360) % 360;
            Dictionary["Rotate"] = normalized == 0 ? null : new PdfInteger(normalized);
        }
    }

    /// <summary>The width of the page as shown, in points: the crop box's, or its height when the page is turned by 90 or 270 degrees.</summary>
    public double Width => Rotation is 90 or 270 ? CropBox.Height : CropBox.Width;

    /// <summary>The height of the page as shown, in points.</summary>
    public double Height => Rotation is 90 or 270 ? CropBox.Width : CropBox.Height;

    private PdfDictionary Dictionary => _table.Dictionary(_page)!;

    private PdfRectangle? Rectangle(string key)
    {
        if (_table.Array(Dictionary[key]) is not { Items.Count: 4 } array)
        {
            return null;
        }

        var n = array.Items.Select(i => _table.Resolve(i) switch { PdfInteger v => v.Value, PdfReal r => r.Value, _ => double.NaN }).ToArray();
        return n.All(double.IsFinite) ? new PdfRectangle(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Abs(n[2] - n[0]), Math.Abs(n[3] - n[1])) : null;
    }

    private static PdfRectangle? Intersect(PdfRectangle a, PdfRectangle b)
    {
        var (left, bottom) = (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var (right, top) = (Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));
        return right > left && top > bottom ? new PdfRectangle(left, bottom, right - left, top - bottom) : null;
    }
}
