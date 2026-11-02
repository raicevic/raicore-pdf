using System.Globalization;
using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

public class LineBreakerTests
{
    /// <summary>
    /// Every case in the official Unicode conformance file (LineBreakTest.txt, same version as the data).
    /// Lines look like "× 0041 × 0308 ÷ 0020 ÷ # comment", with × = no break and ÷ = break.
    /// </summary>
    [Fact]
    public void Passes_the_unicode_conformance_test()
    {
        using var file = File.OpenRead(Fixtures.Path(Path.Combine("Unicode", "LineBreakTest.txt.gz")));
        using var reader = new StreamReader(new GZipStream(file, CompressionMode.Decompress), Encoding.UTF8);

        var header = reader.ReadLine()!;
        Assert.Contains(LineBreaker.UnicodeVersion, header, StringComparison.Ordinal);

        var cases = 0;
        var failures = new List<string>();
        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var data = line.Split('#', 2)[0].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            var tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var codePoints = new List<int>();
            var expected = new List<bool>(); // Break before each code point (index 0 = sot, ignored).
            for (var t = 0; t + 1 < tokens.Length; t += 2)
            {
                expected.Add(tokens[t] == "\u00F7");
                codePoints.Add(int.Parse(tokens[t + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }

            cases++;
            var actual = LineBreaker.Analyze(codePoints);
            for (var i = 1; i < codePoints.Count; i++)
            {
                if (expected[i] != (actual[i] != LineBreaker.Break.None))
                {
                    failures.Add($"line {lineNumber}, before code point {i}: expected {(expected[i] ? "break" : "no break")}\n  {line}");
                    break;
                }
            }
        }

        Assert.True(cases > 15_000, $"Only {cases} cases were read.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases failed. First ones:\n{string.Join("\n", failures.Take(15))}");
    }

    [Fact]
    public void Reports_positions_as_utf16_indices_including_the_end()
    {
        var breaks = LineBreaker.GetBreakOpportunities("Hi \U0001F600 there");

        Assert.Equal([3, 6, 11], breaks.Select(b => b.Position)); // The emoji is two UTF-16 units; the text is 11 long.
        Assert.All(breaks, b => Assert.False(b.Required));
    }

    [Fact]
    public void Marks_hard_line_breaks_as_required()
    {
        var breaks = LineBreaker.GetBreakOpportunities("one\r\ntwo\nthree");

        Assert.Contains(new LineBreakOpportunity(5, Required: true), breaks);
        Assert.Contains(new LineBreakOpportunity(9, Required: true), breaks);
        Assert.DoesNotContain(breaks, b => b.Position == 4); // Never between CR and LF.
    }

    [Theory]
    [InlineData("well-known", new[] { 5, 10 })] // After a hyphen.
    [InlineData("$12.50 (approx.)", new[] { 7, 16 })] // Numbers and their prefixes stay together.
    [InlineData("person(s) here", new[] { 10, 14 })] // No break before "(" inside a word.
    [InlineData("100\u00A0km", new[] { 6 })] // No-break space.
    [InlineData("\u65E5\u672C\u8A9E", new[] { 1, 2, 3 })] // Ideographs break anywhere.
    public void Finds_typical_break_opportunities(string text, int[] expected) =>
        Assert.Equal(expected, LineBreaker.GetBreakOpportunities(text).Select(b => b.Position));

    [Fact]
    public void Empty_text_has_no_breaks() => Assert.Empty(LineBreaker.GetBreakOpportunities(string.Empty));
}
