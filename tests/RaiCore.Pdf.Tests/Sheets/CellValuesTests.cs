using RaiCore.Pdf.Sheets;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Telling numbers and dates (right-aligned) from text in CSV cells.</summary>
public class CellValuesTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("-42")]
    [InlineData("+42")]
    [InlineData("\u221242")]
    [InlineData("  12.5  ")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1,234")]
    [InlineData("1,234,567.89")]
    [InlineData("1 234 567.89")]
    [InlineData("1\u00A0234.5")]
    [InlineData("1\u202F234.5")]
    [InlineData("1'234'567.50")]
    [InlineData("1.5e10")]
    [InlineData("1.5E-3")]
    [InlineData("2e+5")]
    [InlineData("$1,234.50")]
    [InlineData("-$12.00")]
    [InlineData("$-12.00")]
    [InlineData("\u20AC 12.5")]
    [InlineData("12.5 \u20AC")]
    [InlineData("12.5%")]
    [InlineData("12.5 %")]
    [InlineData("3\u2030")]
    [InlineData("(1,234.00)")]
    [InlineData("( 12 )")]
    public void Numbers_with_a_decimal_point(string text) => Assert.Equal(CellKind.Number, CellValues.Classify(text, '.'));

    [Theory]
    [InlineData("1.234,56")]
    [InlineData("12,5")]
    [InlineData("1.234")]
    [InlineData("1 234,5 \u20AC")]
    [InlineData("-0,75")]
    [InlineData(",5")]
    public void Numbers_with_a_decimal_comma(string text) => Assert.Equal(CellKind.Number, CellValues.Classify(text, ','));

    [Theory]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1,2345")] // Groups are of three digits.
    [InlineData("12,34")]
    [InlineData("1234,567")] // A first group has one to three digits.
    [InlineData("1,234 567")] // One group separator throughout.
    [InlineData("1,234,")]
    [InlineData("1.234,56")] // A comma after the decimal point.
    [InlineData("1.2.3")]
    [InlineData("--1")]
    [InlineData("$$1")]
    [InlineData("1$$")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("e5")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("%")]
    [InlineData("12%%")]
    [InlineData("(12")]
    [InlineData("+45 12 34 56 78")]
    [InlineData("ID-12")]
    public void Not_numbers(string text) => Assert.NotEqual(CellKind.Number, CellValues.Classify(text, '.'));

    [Theory]
    [InlineData("2026-11-19")]
    [InlineData("2026-11-19T08:30")]
    [InlineData("2026-11-19T08:30:15.250Z")]
    [InlineData("2026-11-19 08:30:15+01:00")]
    [InlineData("19.11.2026")]
    [InlineData("19/11/26")]
    [InlineData("11-19-2026")]
    [InlineData("8:30")]
    [InlineData("08:30:15")]
    [InlineData("8:30 PM")]
    [InlineData("8:30 a.m.")]
    [InlineData("19.11.2026 8:30")]
    public void Dates_and_times(string text) => Assert.Equal(CellKind.Date, CellValues.Classify(text, '.'));

    [Theory]
    [InlineData("19.11/2026")] // One separator throughout.
    [InlineData("2026-11")]
    [InlineData("19.11.202")]
    [InlineData("8:3")]
    [InlineData("2026-11-19T")]
    public void Not_dates(string text) => Assert.Equal(CellKind.Text, CellValues.Classify(text, '.'));

    [Fact]
    public void Empty_and_long_cells()
    {
        Assert.Equal(CellKind.Empty, CellValues.Classify("", '.'));
        Assert.Equal(CellKind.Empty, CellValues.Classify(" \t ", '.'));
        Assert.Equal(CellKind.Text, CellValues.Classify(new string('1', 65), '.'));
        Assert.Equal(CellKind.Number, CellValues.Classify(new string('1', 64), '.'));
    }

    [Fact]
    public void The_decimal_separator_is_what_the_numbers_need()
    {
        Assert.Equal(',', CellValues.DetectDecimalSeparator(["12,5", "1.234,56", "7", "x", "1.5"], ','));
        Assert.Equal('.', CellValues.DetectDecimalSeparator(["12.5", "1,234.56", "1.234,5"], ';'));

        // Where nothing tells (whole numbers, groups that could be either), semicolons go with decimal commas.
        Assert.Equal(',', CellValues.DetectDecimalSeparator(["1,234", "12", "abc", ""], ';'));
        Assert.Equal('.', CellValues.DetectDecimalSeparator(["1,234", "12"], ','));
        Assert.Equal('.', CellValues.DetectDecimalSeparator(["1,234", "12"], '\t'));
        Assert.Equal('.', CellValues.DetectDecimalSeparator([new string('1', 63) + ",5"], ','));
    }
}
