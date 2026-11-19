using System.Globalization;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Spreadsheet number formats: numbers, dates and text shown as a spreadsheet program shows them.</summary>
public class NumberFormatTests
{
    private static readonly FormatSettings Invariant = new(CultureInfo.InvariantCulture, date1904: false);

    private static string Format(string code, double value, FormatSettings? settings = null) =>
        NumberFormat.Parse(code).Format(value, settings ?? Invariant).Text;

    [Theory]
    [InlineData(1234.5678, "1234.5678")]
    [InlineData(0.30000000000000004, "0.3")]
    [InlineData(-5, "-5")]
    [InlineData(0, "0")]
    [InlineData(3.14159265358979, "3.141592654")]
    [InlineData(1234567.891234, "1234567.891")]
    [InlineData(12345678901, "12345678901")]
    [InlineData(123456789012, "1.23457E+11")]
    [InlineData(1e-10, "1E-10")]
    [InlineData(0.000012345678, "1.23457E-05")]
    [InlineData(0.000123, "0.000123")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(99999999999.9, "1E+11")]
    public void General_shows_up_to_eleven_characters(double value, string expected) => Assert.Equal(expected, Format("General", value));

    [Theory]
    [InlineData("0", 1234.5, "1235")]
    [InlineData("0", 2.5, "3")]
    [InlineData("0", -2.5, "-3")]
    [InlineData("0.00", 1234.5678, "1234.57")]
    [InlineData("0.00", 0.005, "0.01")]
    [InlineData("#,##0", 1234567.891, "1,234,568")]
    [InlineData("#,##0", 0, "0")]
    [InlineData("#,##0", -1234, "-1,234")]
    [InlineData("#,##0", 999, "999")]
    [InlineData("#,##0.00", 1234567.891, "1,234,567.89")]
    [InlineData("#,###", 0, "")]
    [InlineData("#.##", 0.5, ".5")]
    [InlineData("0.0#", 1.5, "1.5")]
    [InlineData("0.0#", 1.25, "1.25")]
    [InlineData("0.0#", 1.0, "1.0")]
    [InlineData("#.##", 1, "1.")]
    [InlineData("0.??", 1.5, "1.5 ")]
    [InlineData("00000", 42, "00042")]
    [InlineData("#,##0,", 1234567, "1,235")]
    [InlineData("0.0,,\"M\"", 1500000, "1.5M")]
    [InlineData("0%", 0.4567, "46%")]
    [InlineData("0.00%", 0.4567, "45.67%")]
    [InlineData("0.00E+00", 12345.678, "1.23E+04")]
    [InlineData("0.00E+00", 0.00012, "1.20E-04")]
    [InlineData("0.00E+00", 9.999, "1.00E+01")]
    [InlineData("0.00E+00", 0, "0.00E+00")]
    [InlineData("0.0E-0", 12345, "1.2E4")]
    [InlineData("##0.0E+0", 12345, "12.3E+3")]
    [InlineData("##0.0E+0", 0.00012, "120.0E-6")]
    [InlineData("000-00-0000", 123456789, "123-45-6789")]
    [InlineData("\"$\"#,##0.00", 1234.5, "$1,234.50")]
    [InlineData("[$\u20AC-407] #,##0.00", 1234.5, "\u20AC 1,234.50")]
    [InlineData("[$-409]#,##0", 1234, "1,234")]
    [InlineData("0 \"items\"", 5, "5 items")]
    [InlineData("0\\ \\k\\g", 5, "5 kg")]
    [InlineData("_(* #,##0_)", 1234, " 1,234 ")]
    [InlineData("_(* \"-\"??_)", 0, " -   ")]
    [InlineData("[Red]0.0", 1.25, "1.3")]
    [InlineData("General\" units\"", 2.5, "2.5 units")]
    public void Numbers_fill_their_placeholders(string code, double value, string expected) => Assert.Equal(expected, Format(code, value));

    [Theory]
    [InlineData("# ?/?", 1.5, "1 1/2")]
    [InlineData("# ?/?", 0.5, " 1/2")]
    [InlineData("# ?/?", 2, "2    ")]
    [InlineData("# ?/?", 0, "0    ")]
    [InlineData("?/?", 0.75, "3/4")]
    [InlineData("?/?", 1.75, "7/4")]
    [InlineData("# ??/??", 3.14159265358979, "3 14/99")]
    [InlineData("# ??/??", 0.3125, "  5/16")]
    [InlineData("# ??/??", 0.5, "  1/2 ")]
    [InlineData("# ?/8", 0.3, " 2/8")]
    [InlineData("# ?/8", 0.99, "1    ")]
    [InlineData("# ??/100", 0.25, " 25/100")]
    [InlineData("# ?/?", -1.5, "-1 1/2")]
    public void Fractions_approximate_within_their_denominators(string code, double value, string expected) =>
        Assert.Equal(expected, Format(code, value));

    [Theory]
    [InlineData("yyyy-mm-dd", 46345, "2026-11-19")]
    [InlineData("d/m/yy", 46345, "19/11/26")]
    [InlineData("dd.mm.yyyy", 46345, "19.11.2026")]
    [InlineData("mmm", 46345, "Nov")]
    [InlineData("mmmm", 46345, "November")]
    [InlineData("mmmmm", 46345, "N")]
    [InlineData("ddd", 46345, "Thu")]
    [InlineData("dddd, mmmm d, yyyy", 46345, "Thursday, November 19, 2026")]
    [InlineData("[$-F800]dddd\\,\\ mmmm\\ dd\\,\\ yyyy", 46345, "Thursday, November 19, 2026")]
    [InlineData("YYYY-MM-DD", 46345, "2026-11-19")]
    [InlineData("h:mm:ss", 0.75, "18:00:00")]
    [InlineData("hh:mm AM/PM", 0.75, "06:00 PM")]
    [InlineData("h:mm AM/PM", 0.25, "6:00 AM")]
    [InlineData("h AM/PM", 0, "12 AM")]
    [InlineData("h:mm am/pm", 0.75, "6:00 pm")]
    [InlineData("h:mm A/P", 0.75, "6:00 P")]
    [InlineData("h:mm a/p", 0.25, "6:00 a")]
    [InlineData("[h]:mm", 1.5, "36:00")]
    [InlineData("[mm]:ss", 0.0104166666666667, "15:00")]
    [InlineData("[ss]", 0.001, "86")]
    [InlineData("mm:ss.00", 1.234 / 86400, "00:01.23")]
    [InlineData("h:mm:ss.000", 0.5 + (0.0005 / 86400), "12:00:00.001")]
    [InlineData("h:mm", 0.99999999, "0:00")]
    [InlineData("yyyy-mm-dd h:mm", 46345.999999, "2026-11-20 0:00")]
    [InlineData("m/d/yyyy h:mm", 46345.5, "11/19/2026 12:00")]
    [InlineData("mm:ss", 0.000694444444444444, "01:00")]
    [InlineData("yyyy-mm-dd", 60, "1900-02-29")]
    [InlineData("yyyy-mm-dd", 61, "1900-03-01")]
    [InlineData("yyyy-mm-dd", 59, "1900-02-28")]
    [InlineData("yyyy-mm-dd", 1, "1900-01-01")]
    [InlineData("yyyy-mm-dd", 0, "1900-01-00")]
    [InlineData("yyyy-mm-dd", 2958465, "9999-12-31")]
    public void Dates_and_times_follow_their_codes(string code, double value, string expected) => Assert.Equal(expected, Format(code, value));

    [Theory]
    [InlineData(-1)]
    [InlineData(2958466)]
    public void Serial_numbers_that_are_not_dates_are_shown_as_numbers(double value) =>
        Assert.Equal(NumberFormat.FormatGeneral(value, Invariant), Format("yyyy-mm-dd", value));

    [Fact]
    public void The_1904_date_system_counts_from_1904()
    {
        var settings = new FormatSettings(CultureInfo.InvariantCulture, date1904: true);
        Assert.Equal("1904-01-01", Format("yyyy-mm-dd", 0, settings));
        Assert.Equal("2026-11-19", Format("yyyy-mm-dd", 46345 - 1462, settings));
        Assert.Equal(NumberFormat.FormatGeneral(2957004, settings), Format("yyyy-mm-dd", 2957004, settings));
    }

    [Theory]
    [InlineData("0;(0)", -5, "(5)")]
    [InlineData("0;(0)", 0, "0")]
    [InlineData("0;-0;\"zero\"", 0, "zero")]
    [InlineData("0;-0;\"zero\"", -3, "-3")]
    [InlineData("0;;", -1, "")]
    [InlineData("0;;", 0, "")]
    [InlineData("0;\"minus\"", -1, "minus")]
    [InlineData("[>=100]\"big\";[<0]\"negative\";\"other\"", 150, "big")]
    [InlineData("[>=100]\"big\";[<0]\"negative\";\"other\"", -5, "negative")]
    [InlineData("[>=100]\"big\";[<0]\"negative\";\"other\"", 5, "other")]
    [InlineData("[<=-1]0;0", -3, "3")]
    [InlineData("[>10]0;0", -3, "-3")]
    [InlineData("[=1]\"one\";0", 1, "one")]
    [InlineData("[<>1]\"not one\";0", 2, "not one")]
    [InlineData("[>5]\"big\"", 1, "1")]
    public void Sections_choose_by_sign_or_condition(string code, double value, string expected) => Assert.Equal(expected, Format(code, value));

    [Fact]
    public void Colors_in_brackets_color_the_value()
    {
        var format = NumberFormat.Parse("#,##0.00;[Red]-#,##0.00");
        Assert.Null(format.Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), format.Format(-1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), NumberFormat.Parse("[Blue]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), NumberFormat.Parse("[Color4]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0, 0, 0), NumberFormat.Parse("[black]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(255, 255, 255), NumberFormat.Parse("[WHITE]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0x00, 0xFF, 0xFF), NumberFormat.Parse("[Cyan]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0xFF, 0x00, 0xFF), NumberFormat.Parse("[Magenta]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0xFF, 0xFF, 0x00), NumberFormat.Parse("[Yellow]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0x00, 0xFF, 0x00), NumberFormat.Parse("[Green]0").Format(1, Invariant).Color);
        Assert.Equal(PdfColor.FromRgb(0x33, 0x33, 0x33), NumberFormat.Parse("[Color56]0").Format(1, Invariant).Color);
        Assert.Null(NumberFormat.Parse("[Color57]0").Format(1, Invariant).Color);
    }

    [Fact]
    public void Text_uses_the_text_section()
    {
        Assert.Equal("Text: x", NumberFormat.Parse("0;0;0;\"Text: \"@").FormatText("x").Text);
        Assert.Equal("[x]", NumberFormat.Parse("\"[\"@\"]\"").FormatText("x").Text);
        Assert.Equal("x", NumberFormat.Parse("0.00").FormatText("x").Text);
        Assert.Equal("x", NumberFormat.Parse("@").FormatText("x").Text);
        Assert.Equal("1.5", Format("@", 1.5)); // A text format shows numbers as General.
        Assert.Equal("2", Format("0;@", 2));
        Assert.Equal(CellKind.Text, NumberFormat.Parse("@").FormatText("x").Kind);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), NumberFormat.Parse("0;0;0;[Red]@").FormatText("x").Color);
    }

    [Theory]
    [InlineData("\"unclosed")]
    [InlineData("[Red")]
    [InlineData("[<]0")]
    [InlineData("[=>1]0")]
    [InlineData("[<abc]0")]
    [InlineData("")]
    [InlineData("General")]
    public void Codes_that_cannot_be_read_are_General(string code)
    {
        Assert.Same(NumberFormat.General, NumberFormat.Parse(code));
        Assert.Equal("1234.5", Format(code, 1234.5));
    }

    [Fact]
    public void Kinds_tell_dates_from_numbers()
    {
        Assert.Equal(CellKind.Date, NumberFormat.Parse("yyyy-mm-dd").Format(1, Invariant).Kind);
        Assert.Equal(CellKind.Number, NumberFormat.Parse("0.00").Format(1, Invariant).Kind);
        Assert.True(NumberFormat.Parse("h:mm").IsDate);
        Assert.False(NumberFormat.Parse("0.00").IsDate);
        Assert.False(NumberFormat.General.IsDate);
    }

    [Theory]
    [InlineData(0, 1234.5, "1234.5")]
    [InlineData(1, 1234.5, "1235")]
    [InlineData(2, 1234.5, "1234.50")]
    [InlineData(3, 1234.5, "1,235")]
    [InlineData(4, 1234.5, "1,234.50")]
    [InlineData(5, -1234.5, "($1,235)")]
    [InlineData(7, 1234.5, "$1,234.50 ")]
    [InlineData(9, 0.5, "50%")]
    [InlineData(10, 0.5, "50.00%")]
    [InlineData(11, 1234.5, "1.23E+03")]
    [InlineData(12, 0.5, " 1/2")]
    [InlineData(13, 0.5, "  1/2 ")]
    [InlineData(14, 46345, "2026-11-19")]
    [InlineData(15, 46345, "19-Nov-26")]
    [InlineData(16, 46345, "19-Nov")]
    [InlineData(17, 46345, "Nov-26")]
    [InlineData(18, 0.75, "6:00 PM")]
    [InlineData(19, 0.75, "6:00:00 PM")]
    [InlineData(20, 0.75, "18:00")]
    [InlineData(21, 0.75, "18:00:00")]
    [InlineData(22, 46345.75, "2026-11-19 18:00")]
    [InlineData(37, -5, "(5)")]
    [InlineData(38, 5, "5 ")]
    [InlineData(39, -5, "(5.00)")]
    [InlineData(40, 5, "5.00")]
    [InlineData(41, 1234, " 1,234 ")]
    [InlineData(42, 1234, " $1,234 ")]
    [InlineData(43, 0, " -   ")]
    [InlineData(44, -1, " $(1.00)")]
    [InlineData(45, 0.0104166666666667, "15:00")]
    [InlineData(46, 1.5, "36:00:00")]
    [InlineData(47, 0.5 / 86400, "00:00.5")]
    [InlineData(48, 12345, "12.3E+3")]
    [InlineData(49, 5, "5")]
    public void Built_in_formats_have_their_codes(int id, double value, string expected)
    {
        var code = NumberFormat.BuiltInCode(id, Invariant);
        Assert.NotNull(code);
        Assert.Equal(expected, Format(code, value));
    }

    [Fact]
    public void Unknown_built_in_ids_have_no_code() => Assert.Null(NumberFormat.BuiltInCode(200, Invariant));

    [Fact]
    public void A_culture_sets_separators_names_and_the_short_date()
    {
        var german = new FormatSettings(CultureInfo.GetCultureInfo("de-DE"), date1904: false);
        Assert.Equal("1.234,50", Format("#,##0.00", 1234.5, german));
        Assert.Equal("1234,5", Format("General", 1234.5, german));
        Assert.Equal("19. November 2026", Format("d. mmmm yyyy", 46345, german));
        Assert.Equal("19.11.2026", Format(NumberFormat.BuiltInCode(14, german)!, 46345, german));
        Assert.Equal("1.234,50 \u20AC", Format(NumberFormat.BuiltInCode(7, german)!, 1234.5, german).TrimEnd());

        var us = new FormatSettings(CultureInfo.GetCultureInfo("en-US"), date1904: false);
        Assert.Equal("11/19/2026", Format(NumberFormat.BuiltInCode(14, us)!, 46345, us));
    }

    [Theory]
    [InlineData(0.5, 9, 1, 2)]
    [InlineData(0.333333, 9, 1, 3)]
    [InlineData(3.14159265358979, 99, 311, 99)]
    [InlineData(3.14159265358979, 999, 355, 113)]
    [InlineData(0, 9, 0, 1)]
    [InlineData(0.999, 9, 1, 1)]
    public void Fractions_are_the_closest_within_the_denominator(double value, long max, long numerator, long denominator) =>
        Assert.Equal((numerator, denominator), NumberFormat.Approximate(value, max));

    [Fact]
    public void Huge_and_tiny_values_do_not_overflow()
    {
        Assert.Equal("1.80E+308", Format("0.00E+00", 1.7976931348623157e308));
        Assert.Equal("1" + new string('0', 300) + "    ", Format("# ?/?", 1e300));
        Assert.Equal("123456789012345" + new string('0', 285), Format("0", 1.23456789012345e299));
        Assert.Equal("100000000000000000000", Format("0", 1e20));
        Assert.Equal(NumberFormat.FormatGeneral(1e300, Invariant), Format("?/?", 1e300));
        Assert.NotEmpty(Format("0.00E+00", double.Epsilon));
        Assert.NotEmpty(Format("#,##0.00", 1e300));
        Assert.Equal(new string('0', 401) + ".5", Format(new string('0', 401) + ".0", 0.5));
    }
}
