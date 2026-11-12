using System.Buffers.Binary;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Fuzzing;

/// <summary>
/// Replays the fuzzing seeds and every input fuzzing ever found failing (Fixtures/Fuzz/Crashes/&lt;target&gt;) through
/// the fuzz targets, so a fixed crash stays fixed: each may only fail with a documented exception.
/// </summary>
public class FuzzCorpusTests
{
    public static TheoryData<string, string> Inputs()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in new[] { "Seeds", "Crashes" })
        {
            var root = Fixtures.Path(Path.Combine("Fuzz", kind));
            foreach (var target in FuzzTargets.All.Keys.Order(StringComparer.Ordinal))
            {
                var folder = Path.Combine(root, target);
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
                    {
                        data.Add(target, Path.GetRelativePath(Fixtures.Path("Fuzz"), file));
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Fails_only_with_documented_exceptions(string target, string input)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine("Fuzz", input)));
        Exception? error = null;

        // On a thread with a stack like a server's: a stack overflow would end the test run, not just fail.
        var thread = new Thread(() => error = Record.Exception(() => FuzzTargets.Run(target, data)), 1024 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"{input} hangs.");

        Assert.Null(error);
    }

    [Fact]
    public void Every_target_has_seeds()
    {
        foreach (var target in new[] { "html", "css", "woff", "woff2" })
        {
            Assert.NotEmpty(Directory.GetFiles(Fixtures.Path(Path.Combine("Fuzz", "Seeds", target))));
        }
    }

    [Fact]
    public void A_web_font_whose_license_forbids_embedding_is_skipped()
    {
        // Found by fuzzing the font parser: such a font loads, but drawing it throws, which failed the conversion.
        var font = Fixtures.Read("RaiCoreTestTT-Regular.ttf");
        var tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < tables; i++)
        {
            if (font.AsSpan(12 + (16 * i), 4).SequenceEqual("OS/2"u8))
            {
                BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(12 + (16 * i) + 8)) + 8), 0x0002); // fsType: restricted.
            }
        }

        var html = $$"""
            <style>@font-face { font-family: Restricted; src: url(data:font/ttf;base64,{{Convert.ToBase64String(font)}}); }</style>
            <p style="font-family: Restricted, sans-serif">Text</p>
            """;
        var flow = HtmlToPdf.CreateFlowDocument(html);
        var paragraph = flow.Blocks.SelectMany(Flatten).OfType<ParagraphBlock>().First();

        Assert.Equal("LiberationSans", paragraph.Paragraph.Spans[0].Style.Fonts[0].PostScriptName);
        Assert.NotEmpty(HtmlToPdf.ConvertToBytes(html));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void Flex_lengths_with_factors_that_are_not_numbers_resolve_instead_of_looping(double grow)
    {
        // Found by fuzzing CSS: flex-grow: 1e3082 parsed as infinity, and resolving the lengths never ended.
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([new FlexMainInput(10, 0, double.PositiveInfinity, grow, 1, 0), new FlexMainInput(10, 0, double.PositiveInfinity, 1, 1, 0)], 300);

        Assert.All(sizes, size => Assert.True(double.IsFinite(size), $"{size}"));
    }

    [Fact]
    public void Infinite_css_numbers_are_ignored()
    {
        var flow = HtmlToPdf.CreateFlowDocument("<div style='display: flex'><div style='flex: 1e3082 0 auto'>a</div><div>b</div></div>");

        Assert.NotEmpty(flow.Render().Pages);
    }

    private static IEnumerable<Block> Flatten(Block block) =>
        block is ContainerBlock container ? container.Children.SelectMany(Flatten).Prepend(block) : [block];
}
