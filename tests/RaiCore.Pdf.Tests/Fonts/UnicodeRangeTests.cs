using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Fonts;

public class UnicodeRangeTests
{
    [Fact]
    public void A_restricted_view_only_covers_its_ranges()
    {
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var latin = font.WithUnicodeRanges(new UnicodeRange(0x20, 0x7E));

        Assert.True(latin.HasGlyph('A'));
        Assert.False(latin.HasGlyph(0xE9)); // é is in the font, but outside the range.
        Assert.True(font.HasGlyph(0xE9));
        Assert.Equal(font.PostScriptName, latin.PostScriptName);
        Assert.False(latin.WithUnicodeRanges(new UnicodeRange(0xE0, 0xFF)).HasGlyph('A'));
    }

    [Fact]
    public void Standard_fonts_cannot_be_restricted() =>
        Assert.Throws<NotSupportedException>(() => PdfFont.Standard(StandardFont.Helvetica).WithUnicodeRanges(new UnicodeRange(0, 0x7F)));

    [Fact]
    public void A_font_and_its_views_are_embedded_once()
    {
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var view = font.WithUnicodeRanges(new UnicodeRange(0x20, 0x7E));
        var document = new PdfDocument();
        var page = document.AddPage(PdfPageSize.A4);
        page.Canvas.DrawText("Original é", font, 12, 72, 700).DrawText("View", view, 12, 72, 680);

        var pdf = Encoding.Latin1.GetString(document.ToArray());
        Assert.Single(Regex.Matches(pdf, "/FontFile2"));
    }
}
