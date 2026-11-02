namespace RaiCore.Pdf;

/// <summary>An RGB color with components in the range 0–1.</summary>
public readonly record struct PdfColor
{
    /// <summary>Creates a color from components in the range 0–1.</summary>
    public PdfColor(double red, double green, double blue)
    {
        Red = Clamp(red);
        Green = Clamp(green);
        Blue = Clamp(blue);
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

    /// <summary>Creates a color from 8-bit components (0–255).</summary>
    public static PdfColor FromRgb(byte red, byte green, byte blue) => new(red / 255.0, green / 255.0, blue / 255.0);

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
