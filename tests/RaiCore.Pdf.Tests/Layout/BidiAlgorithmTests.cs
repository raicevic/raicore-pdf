using System.Globalization;
using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

public class BidiAlgorithmTests
{
    /// <summary>
    /// Every case in BidiTest.txt (same Unicode version as the data): sequences of bidi classes, each run with the
    /// paragraph directions its bitset names (1 = auto, 2 = LTR, 4 = RTL), checked for levels and visual order.
    /// </summary>
    [Fact]
    public void Passes_BidiTest()
    {
        using var reader = Open("BidiTest.txt.gz");
        Assert.Contains(BidiData.UnicodeVersion, reader.ReadLine()!, StringComparison.Ordinal);

        string[] levels = [];
        string[] order = [];
        var cases = 0;
        var failures = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var data = line.Split('#', 2)[0].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                levels = data["@Levels:".Length..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            if (data.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                order = data["@Reorder:".Length..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            if (data.StartsWith('@'))
            {
                continue;
            }

            var parts = data.Split(';');
            var types = parts[0].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<BidiClass>).ToArray();
            var bitset = int.Parse(parts[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            foreach (var (bit, level) in new[] { (1, (int?)null), (2, 0), (4, 1) })
            {
                if ((bitset & bit) == 0)
                {
                    continue;
                }

                cases++;
                var (actualLevels, actualOrder) = Run(types, new BracketType[types.Length], new int[types.Length], level);
                if (!actualLevels.SequenceEqual(levels) || !actualOrder.SequenceEqual(order))
                {
                    failures.Add($"{parts[0].Trim()} (paragraph {level?.ToString(CultureInfo.InvariantCulture) ?? "auto"}): levels {string.Join(' ', actualLevels)}, order {string.Join(' ', actualOrder)}; expected {string.Join(' ', levels)} / {string.Join(' ', order)}");
                }
            }
        }

        Assert.True(cases > 400_000, $"only {cases} cases");
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} failed, e.g.:\n{string.Join('\n', failures.Take(15))}");
    }

    /// <summary>Every case in BidiCharacterTest.txt: code points (with paired brackets), direction, levels and order.</summary>
    [Fact]
    public void Passes_BidiCharacterTest()
    {
        using var reader = Open("BidiCharacterTest.txt.gz");
        Assert.Contains(BidiData.UnicodeVersion, reader.ReadLine()!, StringComparison.Ordinal);

        var cases = 0;
        var failures = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split(';');
            var codePoints = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            int? direction = fields[1] switch { "0" => 0, "1" => 1, _ => null };
            var expectedParagraph = int.Parse(fields[2], CultureInfo.InvariantCulture);
            var expectedLevels = fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expectedOrder = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var types = codePoints.Select(BidiData.GetClass).ToArray();
            var brackets = codePoints.Select(BidiData.GetBracket).ToArray();
            cases++;
            var (levels, order, paragraph) = RunWithParagraph(types, brackets.Select(b => b.Type).ToArray(), brackets.Select(b => b.Pair).ToArray(), direction);
            if (paragraph != expectedParagraph || !levels.SequenceEqual(expectedLevels) || !order.SequenceEqual(expectedOrder))
            {
                failures.Add($"{fields[0]} (direction {fields[1]}): paragraph {paragraph}, levels {string.Join(' ', levels)}, order {string.Join(' ', order)}; expected {expectedParagraph} / {fields[3]} / {fields[4]}");
            }
        }

        Assert.True(cases > 90_000, $"only {cases} cases");
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} failed, e.g.:\n{string.Join('\n', failures.Take(15))}");
    }

    [Theory]
    [InlineData("abc", null, 0, "0 0 0")]
    [InlineData("\u05D0\u05D1 12", null, 1, "1 1 1 2 2")]       // Hebrew then digits: RTL paragraph, numbers at level 2.
    [InlineData("ab \u05D0\u05D1 cd", 0, 0, "0 0 0 1 1 0 0 0")]
    [InlineData("\u05D0\u05D1 ab", 1, 1, "1 1 1 2 2")]
    public void Resolves_levels_of_text(string text, int? direction, int paragraph, string expected)
    {
        var bidi = BidiAlgorithm.Resolve(text, direction);

        Assert.Equal(paragraph, bidi.ParagraphLevelAt(0));
        Assert.Equal(expected, string.Join(' ', bidi.LineLevels(0, text.Length)));
    }

    [Fact]
    public void Each_paragraph_takes_its_own_direction_when_automatic()
    {
        var bidi = BidiAlgorithm.Resolve("abc\n\u05D0\u05D1", null);

        Assert.Equal([(0, 4, 0), (4, 6, 1)], bidi.Paragraphs);
    }

    [Fact]
    public void Surrogate_pairs_share_their_code_point_level()
    {
        var bidi = BidiAlgorithm.Resolve("\U00010800a", 0); // A Cypriot letter (R), then Latin.

        Assert.Equal([1, 1, 0], bidi.Levels);
    }

    private static StreamReader Open(string name)
    {
        var file = File.OpenRead(Fixtures.Path(Path.Combine("Unicode", name)));
        return new StreamReader(new GZipStream(file, CompressionMode.Decompress), Encoding.UTF8);
    }

    private static (string[] Levels, string[] Order) Run(BidiClass[] types, BracketType[] brackets, int[] pairs, int? direction)
    {
        var (levels, order, _) = RunWithParagraph(types, brackets, pairs, direction);
        return (levels, order);
    }

    private static (string[] Levels, string[] Order, int Paragraph) RunWithParagraph(BidiClass[] types, BracketType[] brackets, int[] pairs, int? direction)
    {
        var levels = new byte[types.Length];
        var paragraph = BidiAlgorithm.ResolveParagraph(types, brackets, pairs, direction, levels);
        var line = BidiAlgorithm.LineLevels(levels, types, paragraph);

        var kept = Enumerable.Range(0, types.Length).Where(i => !BidiAlgorithm.IsRemovedByX9(types[i])).ToArray();
        var visual = BidiAlgorithm.VisualOrder(kept.Select(i => line[i]).ToArray());
        var shownLevels = Enumerable.Range(0, types.Length)
            .Select(i => BidiAlgorithm.IsRemovedByX9(types[i]) ? "x" : line[i].ToString(CultureInfo.InvariantCulture)).ToArray();
        var shownOrder = visual.Select(v => kept[v].ToString(CultureInfo.InvariantCulture)).ToArray();
        return (shownLevels, shownOrder, paragraph);
    }
}
