using System.Diagnostics;
using System.Text;
using RaiCore.Pdf.Sheets.Csv;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Reading CSV text: RFC 4180, malformed data read leniently, delimiter and encoding detection.</summary>
public class CsvParserTests
{
    private static string[][] Parse(string text, char delimiter = ',') => [.. CsvParser.Parse(text, delimiter)];

    [Fact]
    public void Records_end_at_any_line_break()
    {
        string[][] expected = [["a", "b"], ["1", "2"], ["3", "4"], ["5", "6"]];
        Assert.Equal(expected, Parse("a,b\r\n1,2\n3,4\r5,6"));
        Assert.Equal(expected, Parse("a,b\r\n1,2\n3,4\r5,6\r\n"));
        Assert.Equal(expected, Parse("a,b\r\n1,2\n3,4\r5,6\n"));
        Assert.Equal(expected, Parse("a,b\r\n1,2\n3,4\r5,6\r"));
    }

    [Fact]
    public void Empty_lines_and_fields()
    {
        Assert.Empty(Parse(""));
        Assert.Equal([[""]], Parse("\n"));
        Assert.Equal([["a"], [""], ["b"]], Parse("a\n\nb"));
        Assert.Equal([["", "", ""]], Parse(",,"));
        Assert.Equal([["a", "b", ""], ["", "c", ""]], Parse("a,b,\n,c,"));
    }

    [Fact]
    public void Quoted_fields_hold_delimiters_line_breaks_and_doubled_quotes()
    {
        Assert.Equal([["a,b", "line 1\nline 2", "say \"hi\"", ""], ["x", "\r\n", "\"", "y"]],
            Parse("\"a,b\",\"line 1\nline 2\",\"say \"\"hi\"\"\",\"\"\nx,\"\r\n\",\"\"\"\",y"));
        Assert.Equal([["\"\"", "a\"\"b"]], Parse("\"\"\"\"\"\",\"a\"\"\"\"b\""));
        Assert.Equal([["quoted", "end"]], Parse("\"quoted\"  ,\"end\"\t"));
        Assert.Equal([["a"]], Parse("\"a\""));
    }

    [Fact]
    public void Quotes_inside_unquoted_fields_are_characters()
    {
        Assert.Equal([["5\" disk", "it's", "a\"\"b"]], Parse("5\" disk,it's,a\"\"b"));
        Assert.Equal([[" \"a\"", "b"]], Parse(" \"a\",b"));
    }

    [Fact]
    public void A_quoted_field_followed_by_text_is_unquoted()
    {
        // The closing quote must end the field: otherwise the quote is a stray one, and the records after it stay apart.
        Assert.Equal([["1", "\"unclosed", "3"], ["4", "5"], ["\"x\"y", "z"]], Parse("1,\"unclosed,3\n4,5\n\"x\"y,z"));
        Assert.Equal([["\"a\"b"]], Parse("\"a\"b"));
        Assert.Equal([["\"\"x", "y"]], Parse("\"\"x,y"));
    }

    [Fact]
    public void A_quote_never_closed_is_a_character()
    {
        Assert.Equal([["a", "b"], ["1", "\"open"], ["2", "3"]], Parse("a,b\n1,\"open\n2,3"));
        Assert.Equal([["\"tail"]], Parse("\"tail"));
        Assert.Equal([["\""]], Parse("\""));
        Assert.Equal([["x", "\"a\"\"b"]], Parse("x,\"a\"\"b"));
    }

    [Fact]
    public void Other_delimiters()
    {
        Assert.Equal([["a", "b,c"], ["1,5", "2"]], Parse("a;\"b,c\"\n1,5;2", ';'));
        Assert.Equal([["a b", "c"]], Parse("a b\tc", '\t'));
        Assert.Equal([["a", "b"]], Parse("a|b", '|'));
        Assert.Equal([["a", "b"], ["c"]], Parse("\"a\" b\nc", ' '));
    }

    [Fact]
    public void Malformed_quotes_are_read_in_linear_time()
    {
        // Every line opens a quote that the next line closes with text after it; and every quote after an unclosed one
        // is doubled. Each would rescan the rest of the text if fields were re-read from their start naively.
        var lines = string.Concat(Enumerable.Repeat("\"a\n", 200_000)) + "\"x";
        var doubled = "\"" + string.Concat(Enumerable.Repeat("a,\"\"b\n", 200_000));
        var watch = Stopwatch.StartNew();
        Assert.Equal(200_001, CsvParser.Parse(lines, ',').Count);
        Assert.Equal(200_000, CsvParser.Parse(doubled, ',').Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void The_cells_are_limited_before_they_take_up_memory()
    {
        var error = Assert.Throws<ResourceLimitExceededException>(() => CsvParser.Parse("a,b\n1,2\n3,4", ',', maxCells: 5));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), error.LimitName);
        Assert.Equal(6, CsvParser.Parse("a,b\n1,2\n3,4", ',', maxCells: 6).Sum(r => r.Length));

        // A short row counts as wide as the widest, and a single endless row is stopped too.
        Assert.Throws<ResourceLimitExceededException>(() => CsvParser.Parse("a,b,c\n1", ',', maxCells: 5));
        Assert.Throws<ResourceLimitExceededException>(() => CsvParser.Parse(new string(',', 1000), ',', maxCells: 100));
    }

    [Fact]
    public void Records_can_be_sampled_and_the_checkpoint_runs()
    {
        var text = string.Concat(Enumerable.Repeat("a,b\n", 5000));
        Assert.Equal(3, CsvParser.Parse(text, ',', maxRecords: 3).Count);
        var calls = 0;
        CsvParser.Parse(text, ',', checkpoint: () => calls++);
        Assert.Equal(4, calls);
        Assert.Throws<OperationCanceledException>(() => CsvParser.Parse(text, ',', checkpoint: () => throw new OperationCanceledException()));
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6", ',')]
    [InlineData("a;b;c\n1,5;2,5;3\n4;5,25;6", ';')]
    [InlineData("Name;Price\nApple;1,50\nPear;2,25\nPlum;0,99", ';')]
    [InlineData("a\tb\n\"x,y\"\t2\n3\t4", '\t')]
    [InlineData("a|b|c\n1|2|3", '|')]
    [InlineData("\"a;b\",c\n\"1;2\",3\n\"4;5\",6", ',')]
    [InlineData("Name;Description\nX;Hello, world\nY;Foo, bar, baz", ';')]
    [InlineData("one column\nvalue\nother", ',')]
    [InlineData("", ',')]
    [InlineData("a;b\n\n\n\n\n1;2", ';')]
    // Quoted line breaks split the records differently per delimiter: the share of records counts, not their number.
    [InlineData("h1;h2,x\n\"p\nq\";r,s\n\"t\nu\";v,w", ';')]
    public void Delimiters_are_detected(string text, char expected) => Assert.Equal(expected, CsvDialect.Detect(text));

    [Fact]
    public void Detection_looks_at_whole_records_at_the_start()
    {
        // Records past the sample, and the record the sample cuts off, do not count.
        var text = string.Concat(Enumerable.Repeat("a;b;c\n", 300)) + string.Concat(Enumerable.Repeat("x,y,z,w\n", 10_000));
        Assert.Equal(';', CsvDialect.Detect(text));
        var cut = "a;b\n" + new string('x', 70_000) + ",y,z\n";
        Assert.Equal(';', CsvDialect.Detect(cut));
        Assert.Equal(',', CsvDialect.Detect(new string('x', 70_000) + ";y\n"));
    }

    [Theory]
    [InlineData("sep=;\r\na;b", ';', 7)]
    [InlineData("sep=;\na;b", ';', 6)]
    [InlineData("sep=\t\ra", '\t', 6)]
    [InlineData("SEP=|", '|', 5)]
    [InlineData("sep=;x\na", null, 0)]
    [InlineData("sep=\na", null, 0)]
    [InlineData("sep=\"\na", null, 0)]
    [InlineData("sep", null, 0)]
    [InlineData("a,b", null, 0)]
    public void Excel_names_the_delimiter_in_a_sep_line(string text, char? delimiter, int length) =>
        Assert.Equal((delimiter, length), CsvDialect.SepLine(text));

    [Fact]
    public void A_byte_order_mark_names_the_encoding()
    {
        Assert.Equal("a\u00E9", CsvText.Decode([0xEF, 0xBB, 0xBF, (byte)'a', 0xC3, 0xA9], null));
        Assert.Equal("a\u00E9", CsvText.Decode([0xFF, 0xFE, (byte)'a', 0, 0xE9, 0], null));
        Assert.Equal("a\u00E9", CsvText.Decode([0xFE, 0xFF, 0, (byte)'a', 0, 0xE9], null));
        Assert.Equal("a", CsvText.Decode([0xFF, 0xFE, 0, 0, (byte)'a', 0, 0, 0], null));
        Assert.Equal("a", CsvText.Decode([0, 0, 0xFE, 0xFF, 0, 0, 0, (byte)'a'], null));

        // Over the encoding given too, and the mark is not text.
        Assert.Equal("a\u00E9", CsvText.Decode([0xEF, 0xBB, 0xBF, (byte)'a', 0xC3, 0xA9], Encoding.Latin1));
        Assert.Equal("x", CsvText.Decode([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'x'], null));
    }

    [Fact]
    public void Without_a_mark_the_bytes_tell()
    {
        // Valid UTF-8, else Windows-1252 (the euro sign is 0x80 there), unless an encoding is given.
        Assert.Equal("M\u00FCsli \u20AC", CsvText.Decode(Encoding.UTF8.GetBytes("M\u00FCsli \u20AC"), null));
        Assert.Equal("M\u00FCsli \u20AC", CsvText.Decode([(byte)'M', 0xFC, (byte)'s', (byte)'l', (byte)'i', (byte)' ', 0x80], null));
        Assert.Equal("\u0080", CsvText.Decode([0x80], Encoding.Latin1));
        Assert.Equal("ab", CsvText.Decode("ab"u8, null));

        // UTF-16 without a mark: zeros in every other byte.
        Assert.Equal("a,b\n1,2", CsvText.Decode(Encoding.Unicode.GetBytes("a,b\n1,2"), null));
        Assert.Equal("a,b\n1,2", CsvText.Decode(Encoding.BigEndianUnicode.GetBytes("a,b\n1,2"), null));
        Assert.Equal("\0\0\0\0", CsvText.Decode([0, 0, 0, 0], null));
        Assert.Equal("a\0", CsvText.Decode([(byte)'a', 0], null));
    }
}
