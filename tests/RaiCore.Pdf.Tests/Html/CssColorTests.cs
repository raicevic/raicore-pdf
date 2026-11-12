using RaiCore.Pdf.Html.Css;

namespace RaiCore.Pdf.Tests.Html;

public class CssColorTests
{
    [Theory]
    [InlineData("rgb(255, 0, 0)", 255, 0, 0, 1)]
    [InlineData("rgba(0, 128, 255, 0.5)", 0, 128, 255, 0.5)]
    [InlineData("rgb(10 20 30 / 50%)", 10, 20, 30, 0.5)]
    [InlineData("rgb(100% 0% 20%)", 255, 0, 51, 1)]
    [InlineData("rgb(300 -5 1e2)", 255, 0, 100, 1)]
    public void ParsesRgb(string value, int red, int green, int blue, double alpha)
    {
        var color = CssValues.ParseColor(value);

        Assert.NotNull(color);
        Assert.Equal(PdfColor.FromRgba((byte)red, (byte)green, (byte)blue, alpha), color);
    }

    [Theory]
    [InlineData("rgb(10 x 20)")]
    [InlineData("rgb(10 20 30 / x)")]
    [InlineData("rgb(10 \0\u0001 20 30)")]
    [InlineData("rgb(1e999 0 0)")]
    [InlineData("rgb(10 20 30 / NaN)")]
    [InlineData("rgb(10 % 20)")]
    public void InvalidChannelsMakeTheColorInvalid(string value) => Assert.Null(CssValues.ParseColor(value));

    [Fact]
    public void AnInvalidColorInAMarginBoxIsIgnored()
    {
        var pdf = RaiCore.Pdf.Html.HtmlToPdf.ConvertToBytes(
            """<style>@page { @bottom-right { content: "1"; color: rgb(10 \0 20 30 / 50%) } }</style><p>Text</p>""");

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }
}
