using RaiCore.Pdf.Charts;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>The number formats of chart labels, against what Excel shows for the same codes.</summary>
public class ChartNumberFormatTests
{
    [Theory]
    [InlineData(0, null, "0")]
    [InlineData(8.200000000000001, "General", "8.2")]
    [InlineData(0.30000000000000004, null, "0.3")]
    [InlineData(1234567.891, "General", "1234567.891")]
    [InlineData(123456789012, null, "1.23457E+11")]
    [InlineData(12345678901, null, "12345678901")]
    [InlineData(-45, null, "-45")]
    [InlineData(1234.5, "0", "1235")]
    [InlineData(1234.5, "0.00", "1234.50")]
    [InlineData(1234.5, "#,##0", "1,235")]
    [InlineData(1234567.25, "#,##0.00", "1,234,567.25")]
    [InlineData(0.25, "0%", "25%")]
    [InlineData(0.256, "0.0%", "25.6%")]
    [InlineData(-0.5, "0%", "-50%")]
    [InlineData(5, "0.##", "5.")]
    [InlineData(5.25, "0.##", "5.25")]
    [InlineData(0.5, "#.##", ".5")]
    [InlineData(12345, "0.00E+00", "1.23E+04")]
    [InlineData(0.00012, "0.0E+00", "1.2E-04")]
    [InlineData(1500, "#,##0,\"K\"", "2K")]
    [InlineData(2500000, "0.0,,\"M\"", "2.5M")]
    [InlineData(42, "\"$\"#,##0", "$42")]
    [InlineData(-42, "\"$\"#,##0;(\"$\"#,##0)", "($42)")]
    [InlineData(0, "0;-0;\"zero\"", "zero")]
    [InlineData(-3, "0;[Red]-0", "-3")]
    [InlineData(7, "[$\u20AC-407] #,##0.00", "\u20AC 7.00")]
    [InlineData(7, "#,##0.00\\ [$\u20AC-407]", "7.00 \u20AC")]
    [InlineData(7, "0_);(0)", "7 ")]
    [InlineData(-7, "General;General", "7")]
    [InlineData(12, "00000", "00012")]
    [InlineData(double.NaN, "0", "")]
    public void Numbers_are_formatted_as_Excel_does(double value, string? code, string expected) =>
        Assert.Equal(expected, ChartNumberFormat.Format(value, code));

    [Theory]
    [InlineData(45658, "m/d/yyyy", "1/1/2025")]
    [InlineData(45658, "d-mmm-yy", "1-Jan-25")]
    [InlineData(45658, "mmm yyyy", "Jan 2025")]
    [InlineData(45658, "mmmm", "January")]
    [InlineData(45658, "dddd", "Wednesday")]
    [InlineData(45658.75, "h:mm", "18:00")]
    [InlineData(45658.75, "h:mm AM/PM", "6:00 PM")]
    [InlineData(45658.5, "hh:mm:ss", "12:00:00")]
    [InlineData(61, "yyyy-mm-dd", "1900-03-01")]
    [InlineData(59, "yyyy-mm-dd", "1900-02-28")]
    [InlineData(60, "yyyy-mm-dd", "1900-02-28")]  // Excel's 29 February 1900 does not exist: the day before it.
    [InlineData(37257, "dd/mm/yyyy", "01/01/2002")]
    public void Dates_count_days_since_1900_with_Excels_leap_day(double serial, string code, string expected) =>
        Assert.Equal(expected, ChartNumberFormat.Format(serial, code));

    [Fact]
    public void Dates_can_count_from_1904_and_out_of_range_serials_stay_numbers()
    {
        Assert.Equal("1904-01-02", ChartNumberFormat.Format(1, "yyyy-mm-dd", date1904: true));
        Assert.Equal("-5", ChartNumberFormat.Format(-5, "yyyy-mm-dd"));
        Assert.Equal("10000000", ChartNumberFormat.Format(1e7, "yyyy"));
    }

    [Fact]
    public void Odd_codes_do_not_throw()
    {
        foreach (var code in new[] { "\"", "[", "\\", "_", "*", ";;;", "E+", "0E+", "@", new string('0', 400), "[$", "0.0.0", ",,,,", "%%%%" })
        {
            _ = ChartNumberFormat.Format(1234.5678, code);
            _ = ChartNumberFormat.Format(-0.001, code);
        }
    }
}
