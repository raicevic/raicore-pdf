namespace RaiCore.Pdf.Fonts;

/// <summary>
/// The 14 standard Type 1 fonts every PDF viewer provides. They need no embedding but only cover
/// Western European text (WinAnsi). Embedded TrueType/OpenType fonts come in a later phase.
/// </summary>
public enum StandardFont
{
    /// <summary>Helvetica.</summary>
    Helvetica,

    /// <summary>Helvetica Bold.</summary>
    HelveticaBold,

    /// <summary>Helvetica Oblique.</summary>
    HelveticaOblique,

    /// <summary>Helvetica Bold Oblique.</summary>
    HelveticaBoldOblique,

    /// <summary>Times Roman.</summary>
    TimesRoman,

    /// <summary>Times Bold.</summary>
    TimesBold,

    /// <summary>Times Italic.</summary>
    TimesItalic,

    /// <summary>Times Bold Italic.</summary>
    TimesBoldItalic,

    /// <summary>Courier.</summary>
    Courier,

    /// <summary>Courier Bold.</summary>
    CourierBold,

    /// <summary>Courier Oblique.</summary>
    CourierOblique,

    /// <summary>Courier Bold Oblique.</summary>
    CourierBoldOblique,

    /// <summary>Symbol.</summary>
    Symbol,

    /// <summary>Zapf Dingbats.</summary>
    ZapfDingbats,
}

internal static class StandardFontInfo
{
    public static string PostScriptName(StandardFont font) => font switch
    {
        StandardFont.Helvetica => "Helvetica",
        StandardFont.HelveticaBold => "Helvetica-Bold",
        StandardFont.HelveticaOblique => "Helvetica-Oblique",
        StandardFont.HelveticaBoldOblique => "Helvetica-BoldOblique",
        StandardFont.TimesRoman => "Times-Roman",
        StandardFont.TimesBold => "Times-Bold",
        StandardFont.TimesItalic => "Times-Italic",
        StandardFont.TimesBoldItalic => "Times-BoldItalic",
        StandardFont.Courier => "Courier",
        StandardFont.CourierBold => "Courier-Bold",
        StandardFont.CourierOblique => "Courier-Oblique",
        StandardFont.CourierBoldOblique => "Courier-BoldOblique",
        StandardFont.Symbol => "Symbol",
        StandardFont.ZapfDingbats => "ZapfDingbats",
        _ => throw new ArgumentOutOfRangeException(nameof(font), font, null),
    };

    /// <summary>Symbol and ZapfDingbats use their own built-in encoding; the rest use WinAnsi.</summary>
    public static bool UsesWinAnsi(StandardFont font) => font is not (StandardFont.Symbol or StandardFont.ZapfDingbats);
}
