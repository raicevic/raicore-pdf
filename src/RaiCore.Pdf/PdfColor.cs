namespace RaiCore.Pdf;

/// <summary>
/// An RGB color with components in the range 0–1, and an alpha (opacity) from 0 (fully transparent) to 1 (opaque,
/// the default). Canvas operations that set a color also set its alpha, which PDF applies as a constant opacity.
/// </summary>
public readonly record struct PdfColor
{
    // Stored as transparency so that default(PdfColor) is opaque black, as it was before colors had an alpha.
    private readonly double _transparency;

    /// <summary>Creates an opaque color from components in the range 0–1.</summary>
    public PdfColor(double red, double green, double blue)
        : this(red, green, blue, 1)
    {
    }

    /// <summary>Creates a color from components and an alpha from 0 to 1 (0 is fully transparent, 1 opaque).</summary>
    public PdfColor(double red, double green, double blue, double alpha)
    {
        Red = Clamp(red);
        Green = Clamp(green);
        Blue = Clamp(blue);
        _transparency = 1 - Clamp(alpha);
    }

    /// <summary>Black.</summary>
    public static PdfColor Black { get; } = new(0, 0, 0);

    /// <summary>White.</summary>
    public static PdfColor White { get; } = new(1, 1, 1);

    /// <summary>Red component (0–1).</summary>
    public double Red { get; }

    /// <summary>Green component (0–1).</summary>
    public double Green { get; }

    /// <summary>Blue component (0–1).</summary>
    public double Blue { get; }

    /// <summary>Alpha: 1 is opaque, 0 fully transparent, values between are translucent.</summary>
    public double Alpha => 1 - _transparency;

    /// <summary>Whether the color is fully opaque (<see cref="Alpha"/> is 1).</summary>
    public bool IsOpaque => _transparency <= 0;

    /// <summary>Creates an opaque color from 8-bit components (0–255).</summary>
    public static PdfColor FromRgb(byte red, byte green, byte blue) => new(red / 255.0, green / 255.0, blue / 255.0);

    /// <summary>Creates a color from 8-bit components (0–255) and an alpha from 0 to 1.</summary>
    public static PdfColor FromRgba(byte red, byte green, byte blue, double alpha) => new(red / 255.0, green / 255.0, blue / 255.0, alpha);

    /// <summary>The same color with another alpha (0 to 1).</summary>
    public PdfColor WithAlpha(double alpha) => new(Red, Green, Blue, alpha);

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
