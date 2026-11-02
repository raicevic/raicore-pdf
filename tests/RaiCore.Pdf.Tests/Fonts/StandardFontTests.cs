using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Fonts;

public class StandardFontTests
{
    // Expected widths come from Adobe's AFM files for the standard fonts.
    [Theory]
    [InlineData(StandardFont.Helvetica, "Hello", 22.78)] // 722 + 556 + 222 + 222 + 556
    [InlineData(StandardFont.HelveticaBold, "Hello", 24.45)] // 722 + 556 + 278 + 278 + 611
    [InlineData(StandardFont.TimesRoman, "Hello", 22.22)] // 722 + 444 + 278 + 278 + 500
    [InlineData(StandardFont.Courier, "Hello", 30)] // 5 × 600
    public void Measures_with_adobe_compatible_widths(StandardFont font, string text, double expected) =>
        Assert.Equal(expected, PdfFont.Standard(font).MeasureText(text, 10), 2);

    [Fact]
    public void Knows_which_characters_win_ansi_covers()
    {
        var helvetica = PdfFont.Standard(StandardFont.Helvetica);

        Assert.True(helvetica.HasGlyphs("Bl\u00E5b\u00E6rgr\u00F8d \u20AC ?"));
        Assert.False(helvetica.HasGlyph('\u0142'));
        Assert.False(helvetica.HasGlyph(0x1F600));
    }

    [Fact]
    public void Reports_style()
    {
        Assert.Equal(700, PdfFont.Standard(StandardFont.TimesBoldItalic).Weight);
        Assert.True(PdfFont.Standard(StandardFont.TimesBoldItalic).IsItalic);
        Assert.True(PdfFont.Standard(StandardFont.CourierOblique).IsMonospaced);
        Assert.Equal("Helvetica", PdfFont.Standard(StandardFont.HelveticaBoldOblique).FamilyName);
    }

    [Fact]
    public void Symbol_fonts_cannot_be_measured_yet() =>
        Assert.Throws<NotSupportedException>(() => PdfFont.Standard(StandardFont.Symbol).MeasureText("a", 12));

    [Fact]
    public void Standard_fonts_are_shared_instances() =>
        Assert.Same(PdfFont.Standard(StandardFont.Courier), PdfFont.Standard(StandardFont.Courier));
}
