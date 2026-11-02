using System.Globalization;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf;

/// <summary>A page in a <see cref="PdfDocument"/>.</summary>
public sealed class PdfPage
{
    private readonly Dictionary<StandardFont, string> _fonts = [];

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

    internal IReadOnlyDictionary<StandardFont, string> Fonts => _fonts;

    /// <summary>Returns the page-local resource name (e.g. <c>F1</c>) for a font, registering it on first use.</summary>
    internal string UseFont(StandardFont font)
    {
        if (!_fonts.TryGetValue(font, out var name))
        {
            name = string.Create(CultureInfo.InvariantCulture, $"F{_fonts.Count + 1}");
            _fonts[font] = name;
        }

        return name;
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
}
