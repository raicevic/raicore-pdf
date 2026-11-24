using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Formatting;
using static RaiCore.Pdf.Tests.Xfa.Xdp;

namespace RaiCore.Pdf.Tests.Xfa;

/// <summary>Picture clauses (XFA 3.3, "Picture Clause Specification") with the form's locales.</summary>
public class XfaPictureTests
{
    private static readonly LocaleSet Locales = new(null);

    private static string? Format(string picture, string value, string kind = "text", string locale = "en_US") =>
        Picture.Format(picture, value, kind, Locales.Get(locale), Locales);

    [Theory]
    [InlineData("num{z,zz9.99}", "1234.5", "1,234.50")]
    [InlineData("num{z,zz9.99}", "5", "5.00")]
    [InlineData("num{zzz9}", "12.6", "13")]
    [InlineData("num{9999}", "12", "0012")]
    [InlineData("num{ZZZ9}", "12", "  12")]
    [InlineData("num{$z,zz9.99}", "1234567.891", "$1,234,567.89")]
    [InlineData("num{z,zz9.99s}", "-42.5", "42.50-")]
    [InlineData("num{s9.99}", "-1", "-1.00")]
    [InlineData("num{(z9.99)}", "-3", "(3.00)")]
    [InlineData("num{(z9.99)}", "3", " 3.00 ")]
    [InlineData("num{z9.zz}", "4.5", "4.5")]
    [InlineData("num{z9.zz}", "4", "4")]
    [InlineData("num{z9%}", "45", "45%")]
    [InlineData("num{z9.99 'USD'}", "7", "7.00 USD")]
    [InlineData("num{z9.99CR}", "-7", "7.00CR")]
    [InlineData("num.integer{}", "1234567", "1,234,567")]
    [InlineData("num{z9.99}", "abc", null)]
    public void Numbers(string picture, string value, string? expected)
    {
        Assert.Equal(expected, Format(picture, value, "decimal"));
    }

    [Theory]
    [InlineData("date{MM/DD/YYYY}", "2024-03-05", "03/05/2024")]
    [InlineData("date{M/D/YY}", "2024-03-05", "3/5/24")]
    [InlineData("date{MMMM D, YYYY}", "20240305", "March 5, 2024")]
    [InlineData("date{EEE, MMM D}", "2024-03-05", "Tue, Mar 5")]
    [InlineData("date{EEEE 'the' D}", "2024-03-05", "Tuesday the 5")]
    [InlineData("date{JJJ G}", "2024-03-05", "065 AD")]
    [InlineData("date{}", "2024-03-05", "Mar 5, 2024")]
    [InlineData("date.long{}", "2024-03-05", "March 5, 2024")]
    [InlineData("date{MM/DD/YYYY}", "not a date", null)]
    public void Dates(string picture, string value, string? expected)
    {
        Assert.Equal(expected, Format(picture, value, "date"));
    }

    [Theory]
    [InlineData("time{h:MM A}", "13:05:09", "1:05 PM")]
    [InlineData("time{HH:MM:SS}", "09:05:00", "09:05:00")]
    [InlineData("time{K:MM}", "00:30", "24:30")]
    [InlineData("date{MM/DD/YYYY} time{h:MM A}", "2024-03-05T08:15:00", "03/05/2024 8:15 AM")]
    public void Times_and_date_times(string picture, string value, string expected)
    {
        Assert.Equal(expected, Format(picture, value, "time"));
    }

    [Theory]
    [InlineData("text{999-99-9999}", "123456789", "123-45-6789")]
    [InlineData("text{(999) 999-9999}", "5551234567", "(555) 123-4567")]
    [InlineData("text{AA-99}", "ab12", "ab-12")]
    [InlineData("text{999-99-9999}", "12345", null)]
    [InlineData("text{A9}", "1a", null)]
    [InlineData("text{99}", "123", null)]
    public void Text(string picture, string value, string? expected)
    {
        Assert.Equal(expected, Format(picture, value));
    }

    [Theory]
    [InlineData("null{'N/A'}|num{z9.99}", "", "N/A")]
    [InlineData("null{'N/A'}|num{z9.99}", "3", "3.00")]
    [InlineData("zero{'none'}|num{z9.99}", "0", "none")]
    [InlineData("num{z9.99}|text{X}", "x", "x")]
    public void Alternatives(string picture, string value, string expected)
    {
        Assert.Equal(expected, Format(picture, value, "decimal"));
    }

    [Fact]
    public void Locales_come_from_the_locale_set_then_dotnet()
    {
        var set = new LocaleSet(XElement.Parse("""
            <localeSet><locale name="xx_XX"><calendarSymbols><monthNames><month>Jan1</month><month>Feb2</month><month>Mar3</month><month>Apr4</month><month>May5</month><month>Jun6</month><month>Jul7</month><month>Aug8</month><month>Sep9</month><month>Oct10</month><month>Nov11</month><month>Dec12</month></monthNames></calendarSymbols>
            <numberSymbols><numberSymbol name="decimal">;</numberSymbol><numberSymbol name="grouping">_</numberSymbol></numberSymbols></locale></localeSet>
            """));

        Assert.Equal("Mar3 5", Picture.Format("date{MMMM D}", "2024-03-05", "date", set.Get("xx_XX"), set));
        Assert.Equal("1_234;50", Picture.Format("num{z,zz9.99}", "1234.5", "decimal", set.Get("xx_XX"), set));

        // A locale the set lacks comes from .NET's culture data (German uses a comma), the clause's own locale first.
        var german = set.Get("de_DE");
        if (german.Decimal == ",")
        {
            Assert.Equal("1.234,50", Picture.Format("num{z,zz9.99}", "1234.5", "decimal", german, set));
            Assert.Equal("1.234,50", Picture.Format("num(de_DE){z,zz9.99}", "1234.5", "decimal", set.Get("en_US"), set));
        }
    }

    [Fact]
    public void Fields_show_their_value_formatted_by_their_display_picture()
    {
        var xdp = Form(
            Field("Amount", content: "<ui><numericEdit/></ui><format><picture>num{$z,zz9.99}</picture></format>")
            + Field("When", content: "<format><picture>date{D MMMM YYYY}</picture></format><value><date/></value>")
            + Field("Raw", content: "<format><picture>num{z9.99}</picture></format>"),
            "<form1><Amount>1500</Amount><When>2023-12-24</When><Raw>not a number</Raw></form1>");

        var text = Pages(Convert(xdp))[0];
        Assert.Contains("$1,500.00", text, StringComparison.Ordinal);
        Assert.Contains("24 December 2023", text, StringComparison.Ordinal);
        Assert.Contains("not a number", text, StringComparison.Ordinal);
    }
}
