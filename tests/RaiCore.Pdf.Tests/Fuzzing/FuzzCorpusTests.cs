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

    public static TheoryData<string, string, OutputMode> HtmlInputs()
    {
        var data = new TheoryData<string, string, OutputMode>();
        foreach (var row in Inputs())
        {
            var (target, input) = row.Data;
            if (target is "html" or "css")
            {
                foreach (var mode in Enum.GetValues<OutputMode>())
                {
                    data.Add(target, input, mode);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HtmlInputs))]
    public void Html_and_css_inputs_fail_only_with_documented_exceptions_in_every_output_mode(string target, string input, OutputMode mode)
    {
        // The fuzzer picks the mode from the input's last byte; here every input is replayed tagged, as PDF/UA and as PDF/A too.
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine("Fuzz", input)));
        Exception? error = null;
        var thread = new Thread(
            () => error = Record.Exception(() =>
            {
                using (FuzzTargets.Limits.Enforce(CancellationToken.None))
                {
                    if (target == "html")
                    {
                        FuzzTargets.Html(data, mode);
                    }
                    else
                    {
                        FuzzTargets.Css(data, mode);
                    }
                }
            }),
            1024 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"{input} hangs.");

        Assert.True(error is null || FuzzTargets.IsExpected(error), error?.ToString());
    }

    [Theory]
    [InlineData(OutputMode.Plain, null)]
    [InlineData(OutputMode.Tagged, "/StructTreeRoot")]
    [InlineData(OutputMode.PdfUA, "pdfuaid:part")]
    [InlineData(OutputMode.PdfA, "pdfaid:part")]
    [InlineData(OutputMode.TaggedStream, "/StructTreeRoot")]
    public void The_output_modes_write_what_they_say(OutputMode mode, string? marker)
    {
        // The table seed breaks no rule of PDF/UA or PDF/A, so it converts in every mode.
        var html = File.ReadAllText(Fixtures.Path(Path.Combine("Fuzz", "Seeds", "html", "table.html")));
        string text;
        using (FuzzTargets.Limits.Enforce(CancellationToken.None))
        {
            text = System.Text.Encoding.Latin1.GetString(FuzzTargets.ConvertStrictly(html, mode));
        }

        Assert.Contains("%PDF", text, StringComparison.Ordinal);
        if (marker is not null)
        {
            Assert.Contains(marker, text, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("/StructTreeRoot", text, StringComparison.Ordinal);
        }

        Assert.Equal(Enum.GetValues<OutputMode>(), Enumerable.Range(5, 5).Select(n => FuzzTargets.ModeOf(new byte[n])));
    }

    [Fact]
    public void Choosing_a_target_runs_no_library_code()
    {
        // Under libFuzzer the library is instrumented, and instrumented code crashes before LibFuzzer.Run sets up its
        // coverage map: initializing the targets (as choosing one by name does) must create no library objects. A fresh
        // copy of the type is initialized in a load context of its own, since this process has used it already.
        var context = new System.Runtime.Loader.AssemblyLoadContext("fuzz-targets", isCollectible: true);
        try
        {
            var type = context.LoadFromAssemblyPath(typeof(FuzzTargets).Assembly.Location).GetType(typeof(FuzzTargets).FullName!)!;
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            var fields = type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(f => f.FieldType.Assembly.GetName().Name!.StartsWith("RaiCore.Pdf", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(fields); // The lazily built limits.
            Assert.All(fields, f => Assert.Null(f.GetValue(null)));
        }
        finally
        {
            context.Unload();
        }
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

        var flex = flow.Blocks.SelectMany(Flatten).OfType<FlexBlock>().Single();
        Assert.Equal(0, flex.Items[0].Grow); // Not a number CSS can use: the initial value.
        Assert.NotEmpty(flow.Render().Pages);
    }

    [Fact]
    public void Declarations_the_css_parser_cannot_read_are_dropped_and_the_others_kept()
    {
        // Found by fuzzing CSS: AngleSharp throws reading such a value, which failed the conversion.
        var flow = HtmlToPdf.CreateFlowDocument("<style>p { width: clamp(1,(,0); color: red }</style><p style='height: clamp(1,(,0); margin-left: 7pt'>Text</p>");
        var paragraph = flow.Blocks.SelectMany(Flatten).OfType<ParagraphBlock>().First();

        Assert.Equal(PdfColor.FromRgb(255, 0, 0), paragraph.Paragraph.Spans[0].Style.Color);
        Assert.Equal(7, paragraph.Style.Margin.Left, 3);
    }

    private static IEnumerable<Block> Flatten(Block block) =>
        block is ContainerBlock container ? container.Children.SelectMany(Flatten).Prepend(block) : [block];
}
