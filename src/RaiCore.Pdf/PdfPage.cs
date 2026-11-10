using System.Globalization;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf;

/// <summary>A page in a <see cref="PdfDocument"/>.</summary>
public sealed class PdfPage
{
    private readonly List<(PdfFont Font, string Name, FontUsage Usage)> _fonts = [];
    private readonly List<(PdfImage Image, string Name)> _images = [];

    internal PdfPage(PdfPageSize size)
    {
        Size = size;
        Canvas = new PdfCanvas(this);
    }

    /// <summary>The page size in points.</summary>
    public PdfPageSize Size { get; }

    /// <summary>The page width in points.</summary>
    public double Width => Size.Width;

    /// <summary>The page height in points.</summary>
    public double Height => Size.Height;

    /// <summary>Draws on the page.</summary>
    public PdfCanvas Canvas { get; }

    internal IReadOnlyList<(PdfFont Font, string Name, FontUsage Usage)> Fonts => _fonts;

    internal IReadOnlyList<(PdfImage Image, string Name)> Images => _images;

    /// <summary>Returns the page-local resource name (e.g. <c>Im1</c>) for an image, registering it on first use.</summary>
    internal string UseImage(PdfImage image)
    {
        foreach (var entry in _images)
        {
            if (ReferenceEquals(entry.Image, image))
            {
                return entry.Name;
            }
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"Im{_images.Count + 1}");
        _images.Add((image, name));
        return name;
    }

    /// <summary>Returns the page-local resource name (e.g. <c>F1</c>) for a font and the record of glyphs used from it.</summary>
    internal (string Name, FontUsage Usage) UseFont(PdfFont font)
    {
        font = font.EmbeddedFont; // Views of a font share its embedding.
        foreach (var entry in _fonts)
        {
            if (ReferenceEquals(entry.Font, font))
            {
                return (entry.Name, entry.Usage);
            }
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"F{_fonts.Count + 1}");
        var usage = new FontUsage();
        _fonts.Add((font, name, usage));
        return (name, usage);
    }
}

/// <summary>A page size in PDF points (1/72 inch).</summary>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>ISO A4 portrait (210 × 297 mm).</summary>
    public static PdfPageSize A4 { get; } = FromMillimeters(210, 297);

    /// <summary>ISO A3 portrait (297 × 420 mm).</summary>
    public static PdfPageSize A3 { get; } = FromMillimeters(297, 420);

    /// <summary>ISO A5 portrait (148 × 210 mm).</summary>
    public static PdfPageSize A5 { get; } = FromMillimeters(148, 210);

    /// <summary>US Letter portrait (8.5 × 11 in).</summary>
    public static PdfPageSize Letter { get; } = new(612, 792);

    /// <summary>US Legal portrait (8.5 × 14 in).</summary>
    public static PdfPageSize Legal { get; } = new(612, 1008);

    /// <summary>The same size rotated to landscape.</summary>
    public PdfPageSize Landscape => Width >= Height ? this : new(Height, Width);

    /// <summary>Creates a size from millimeters.</summary>
    public static PdfPageSize FromMillimeters(double width, double height) => new(width * 72 / 25.4, height * 72 / 25.4);

    // The generated record ToString would print Landscape, which is itself a PdfPageSize: infinite recursion.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Width = {Width}, Height = {Height}");
        return true;
    }
}
