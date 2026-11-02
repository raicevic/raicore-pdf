using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Tests;

public class PdfObjectTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-0.000001, "0")]
    [InlineData(595.2755905511812, "595.27559")]
    [InlineData(1e10, "10000000000")]
    public void Reals_use_invariant_format_without_exponent(double value, string expected) =>
        Assert.Equal(expected, new PdfReal(value).ToString());

    [Theory]
    [InlineData("Type", "/Type")]
    [InlineData("A B", "/A#20B")]
    [InlineData("A#B", "/A#23B")]
    [InlineData("A/B(C)", "/A#2FB#28C#29")]
    [InlineData("\u00C6", "/#C3#86")]
    public void Names_escape_special_characters(string name, string expected) =>
        Assert.Equal(expected, new PdfName(name).ToString());

    [Fact]
    public void Literal_strings_escape_delimiters_and_non_printables()
    {
        var value = new PdfString([(byte)'a', (byte)'(', (byte)')', (byte)'\\', 0x0A, 0xE6]);
        Assert.Equal(@"(a\(\)\\\012\346)", value.ToString());
    }

    [Fact]
    public void Text_strings_use_utf16_with_bom_for_non_ascii()
    {
        Assert.Equal("(Hello)", PdfString.FromText("Hello").ToString());
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, 0xE6 }, PdfString.FromText("\u00E6").Bytes.ToArray());
    }

    [Fact]
    public void Dictionaries_keep_insertion_order_and_replace_existing_keys()
    {
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Count"] = new PdfInteger(1),
        };
        dict["Type"] = new PdfName("Pages");
        dict["Missing"] = null;

        Assert.Equal("<</Type /Pages/Count 1>>", dict.ToString());
    }

    [Fact]
    public void Arrays_write_integers_for_whole_numbers() =>
        Assert.Equal("[0 0 612 841.5]", PdfArray.OfNumbers(0, 0, 612, 841.5).ToString());

    [Fact]
    public void Compressed_streams_round_trip()
    {
        var data = "BT /F1 12 Tf (Hello) Tj ET"u8.ToArray();
        var stream = PdfStreamObject.CreateCompressed(data);

        Assert.Equal(data, Filters.Flate.Decode(stream.Data));
        Assert.Contains("/Filter /FlateDecode", stream.ToString(), StringComparison.Ordinal);
    }
}
