using System.Diagnostics;
using System.Text;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>Resource limits in HTML conversion: structural limits fail it, oversized resources are skipped.</summary>
public class HtmlResourceLimitsTests
{
    private static readonly Uri Base = new("https://example.com/");

    [Fact]
    public void Elements_nested_deeper_than_the_limit_fail_the_conversion()
    {
        var html = Nested("div", 300);

        var ex = Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.Convert(html));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), ex.LimitName);
        Assert.Equal(256, ex.Limit);

        var limited = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 20 } };
        Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.Convert(Nested("div", 19), limited)); // html, body and 19 divs.
        Assert.Single(HtmlToPdf.Convert(Nested("div", 18), limited).Pages);
    }

    [Theory]
    [InlineData("div")]
    [InlineData("span")]
    [InlineData("li")]
    public void Deeply_nested_documents_convert_on_the_conversion_threads_large_stack(string tag)
    {
        // About 500 levels need several megabytes of stack: more than a thread pool thread has.
        var options = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 600 } };
        var html = tag == "li" ? string.Concat(Enumerable.Repeat("<ul><li>", 150)) + "deep" : Nested(tag, 500);

        Assert.NotEmpty(HtmlToPdf.Convert(html, options).Pages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deeply_nested_tagged_documents_convert_and_save_on_a_default_stack(bool pdfUA)
    {
        // The structure tree is as deep as the document: building it runs on the conversion thread, writing it (ToArray)
        // on the caller's.
        var options = new HtmlConverterOptions { Tagged = true, PdfUA = pdfUA, Title = "Deep", Language = "en", Limits = ResourceLimits.Default with { MaxNestingDepth = 600 } };
        var pdf = HtmlToPdf.Convert(Nested("div", 500), options);
        byte[]? bytes = null;
        var thread = new Thread(() => bytes = pdf.ToArray(), 1024 * 1024);
        thread.Start();
        thread.Join();

        Assert.True(pdf.Tagged);
        Assert.NotEmpty(bytes!);
    }

    [Fact]
    public void Tagged_pdf_a_and_encrypted_conversions_keep_their_options_on_the_conversion_thread()
    {
        var html = "<html lang='en'><title>T</title><h1>Heading</h1><p>Text</p></html>";

        var tagged = HtmlToPdf.Convert(html, new HtmlConverterOptions { Tagged = true, PdfUA = true });
        var archived = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA2B });
        var encrypted = HtmlToPdf.Convert(html, new HtmlConverterOptions { Encryption = new PdfEncryption { OwnerPassword = "o" } });

        Assert.True(tagged.Tagged);
        Assert.True(tagged.PdfUA);
        Assert.Equal(PdfAConformance.PdfA2B, archived.PdfAConformance);
        Assert.NotNull(encrypted.Encryption);
    }

    [Fact]
    public void A_flow_document_rendered_on_a_small_stack_fails_with_an_exception_not_a_crash()
    {
        var flow = HtmlToPdf.CreateFlowDocument(Nested("div", 400), new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 600 } });

        Exception? error = null;
        var thread = new Thread(
            () =>
            {
                using ((ResourceLimits.Default with { MaxNestingDepth = 600 }).Enforce(TestContext.Current.CancellationToken))
                {
                    error = Record.Exception(() => flow.Render());
                }
            },
            256 * 1024);
        thread.Start();
        thread.Join();

        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), Assert.IsType<ResourceLimitExceededException>(error).LimitName);
    }

    [Fact]
    public async Task Converting_to_a_stream_applies_the_limits_on_the_conversion_thread()
    {
        var pages = string.Concat(Enumerable.Range(1, 12).Select(i => $"<p style='break-after: page'>Page {i}</p>"));
        var deep = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 600 } };

        // Deep nesting needs the conversion thread's large stack; the page limit holds while pages are written one by one.
        using (var output = new MemoryStream())
        {
            HtmlToPdf.ConvertToStream(Nested("div", 500), output, deep);
            Assert.True(output.Length > 0);
        }

        using (var output = new MemoryStream())
        {
            var ex = Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.ConvertToStream(pages, output, new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxPages = 11 } }));
            Assert.Equal(nameof(ResourceLimits.MaxPages), ex.LimitName);
            Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.ConvertToStream(Nested("div", 300), output));
        }

        using (var output = new MemoryStream())
        {
            var options = new HtmlConverterOptions { Tagged = true, Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromMilliseconds(500) } };
            var ex = await Assert.ThrowsAsync<ResourceLimitExceededException>(() => HtmlToPdf.ConvertToStreamAsync(string.Concat(Enumerable.Repeat("<table><tr><td>", 40)) + "x", output, options, TestContext.Current.CancellationToken));
            Assert.Equal(nameof(ResourceLimits.Timeout), ex.LimitName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlToPdf.ConvertToStreamAsync(pages, output, cancellationToken: new CancellationToken(canceled: true)));
        }
    }

    [Fact]
    public void Documents_with_more_elements_than_the_limit_fail()
    {
        var html = string.Concat(Enumerable.Repeat("<p>x</p>", 100)); // With html, head and body: 103 elements.
        var options = (int max) => new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxElements = max } };

        var ex = Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.Convert(html, options(102)));
        Assert.Equal(nameof(ResourceLimits.MaxElements), ex.LimitName);
        Assert.Equal(102, ex.Limit);
        Assert.NotEmpty(HtmlToPdf.Convert(html, options(103)).Pages);
    }

    [Fact]
    public void Documents_with_more_pages_than_the_limit_fail()
    {
        var html = string.Concat(Enumerable.Range(1, 12).Select(i => $"<p style='break-after: page'>Page {i}</p>"));
        var options = (int max) => new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxPages = max } };

        Assert.Equal(12, HtmlToPdf.Convert(html, options(12)).Pages.Count);
        var ex = Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.Convert(html, options(11)));
        Assert.Equal(nameof(ResourceLimits.MaxPages), ex.LimitName);
    }

    [Fact]
    public void Tables_with_more_cells_than_the_limit_fail()
    {
        var row = "<tr>" + string.Concat(Enumerable.Repeat("<td>x</td>", 10)) + "</tr>";
        var html = "<table>" + string.Concat(Enumerable.Repeat(row, 10)) + "</table>";
        var options = (int max) => new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxTableCells = max } };

        Assert.Single(HtmlToPdf.Convert(html, options(100)).Pages);
        var ex = Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.Convert(html, options(99)));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), ex.LimitName);
    }

    [Theory]
    [InlineData("<table><tr><td colspan='2000000000'>a</td><td>b</td></tr></table>")]
    [InlineData("<table><tr><td rowspan='2000000000'>a</td></tr><tr><td>b</td></tr></table>")]
    [InlineData("<table><col span='2000000000'><tr><td>a</td></tr></table>")]
    public void Huge_spans_are_clamped_as_html_says(string html)
    {
        var started = Stopwatch.StartNew();
        Assert.Single(HtmlToPdf.Convert(html).Pages);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Took {started.Elapsed}.");

        // Clamped to 1000 columns, a span still counts: with a small cell limit it is too large.
        var limited = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxTableCells = 999 } };
        if (!html.Contains("rowspan", StringComparison.Ordinal))
        {
            Assert.Throws<ResourceLimitExceededException>(() => HtmlToPdf.Convert(html, limited));
        }
    }

    [Fact]
    public void An_image_with_too_many_pixels_is_skipped_and_its_alt_text_shown()
    {
        var bomb = ResourceLimitsTests.Png(100_000, 100_000, ResourceLimitsTests.Zlib(new byte[16]));
        var html = $"<p><img alt='too big' src='data:image/png;base64,{Convert.ToBase64String(bomb)}'></p>";

        var blocks = Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).ToList();

        Assert.False(HasImage(blocks));
        Assert.Contains("too big", string.Concat(blocks.OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void Resources_larger_than_the_limit_are_skipped()
    {
        var png = ResourceLimitsTests.Png(20, 20, ResourceLimitsTests.Zlib(new byte[20 * 21]));
        var css = Encoding.UTF8.GetBytes("p { color: red; } /*" + new string(' ', 2000) + "*/");
        byte[]? Loader(Uri uri) => uri.AbsolutePath.EndsWith(".css", StringComparison.Ordinal) ? css : png;
        const string html = "<link rel='stylesheet' href='a.css'><p>Text</p><img src='a.png'>";
        FlowDocument Flow(long max) => HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions
        {
            BaseUri = Base,
            ResourceLoader = Loader,
            Limits = ResourceLimits.Default with { MaxResourceBytes = max },
        });

        var large = Flatten(Flow(png.Length + css.Length).Blocks).ToList();
        Assert.True(HasImage(large));
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), large.OfType<ParagraphBlock>().First().Paragraph.Spans[0].Style.Color);

        var small = Flatten(Flow(Math.Min(png.Length, css.Length) - 1).Blocks).ToList();
        Assert.False(HasImage(small));
        Assert.Equal(PdfColor.FromRgb(0, 0, 0), small.OfType<ParagraphBlock>().First().Paragraph.Spans[0].Style.Color);
    }

    [Fact]
    public void The_default_loader_does_not_read_files_over_the_limit()
    {
        var path = new Uri(Fixtures.Path("LiberationSans-Regular.woff"));
        Assert.NotNull(Resources.Default(path));

        using ((ResourceLimits.Default with { MaxResourceBytes = 1000 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Null(Resources.Default(path));
        }
    }

    [Fact]
    public void A_web_font_that_decompresses_beyond_the_limit_is_skipped()
    {
        var woff = Fixtures.Read("LiberationSans-Regular.woff");
        var html = $$"""
            <style>@font-face { font-family: Web; src: url(data:font/woff;base64,{{Convert.ToBase64String(woff)}}); }</style>
            <p style="font-family: Web, serif">Text</p>
            """;
        string FontOf(long maxFontBytes) => Flatten(HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxFontBytes = maxFontBytes } }).Blocks)
            .OfType<ParagraphBlock>().First().Paragraph.Spans[0].Style.Fonts[0].PostScriptName;

        Assert.Equal("LiberationSans", FontOf(ResourceLimits.Default.MaxFontBytes));
        Assert.Equal("LiberationSerif", FontOf(1000)); // The default font instead.
    }

    [Fact]
    public async Task A_pathological_document_is_stopped_by_the_time_limit_during_layout()
    {
        // Nested tables are measured and laid out again at every level: layout time more than doubles per level.
        var html = string.Concat(Enumerable.Repeat("<table><tr><td>", 40)) + "x";
        var options = new HtmlConverterOptions { Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromMilliseconds(500) } };

        var started = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ResourceLimitExceededException>(() => HtmlToPdf.ConvertAsync(html, options, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ResourceLimits.Timeout), ex.LimitName);
        Assert.Equal(500, ex.Limit);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Took {started.Elapsed}.");
    }

    [Fact]
    public async Task A_pathological_document_is_canceled_during_layout()
    {
        var html = string.Concat(Enumerable.Repeat("<table><tr><td>", 40)) + "x";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(500);

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlToPdf.ConvertAsync(html, cancellationToken: cancel.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Took {started.Elapsed}.");
    }

    [Fact]
    public async Task Cancellation_is_checked_while_the_boxes_are_built()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loaded = new List<string>();
        byte[]? Loader(Uri uri)
        {
            loaded.Add(uri.AbsolutePath);
            cancel.Cancel();
            return null;
        }

        var html = "<img src='first.png'>" + string.Concat(Enumerable.Repeat("<p>text</p>", 50)) + "<img src='second.png'>";
        var options = new HtmlConverterOptions { BaseUri = Base, ResourceLoader = Loader };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlToPdf.ConvertAsync(html, options, cancel.Token));
        Assert.Equal(["/first.png"], loaded);
    }

    [Fact]
    public async Task A_canceled_token_cancels_before_starting()
    {
        var task = HtmlToPdf.ConvertAsync("<p>x</p>", cancellationToken: new CancellationToken(canceled: true));

        Assert.True(task.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Parsing_is_stopped_by_the_time_limit()
    {
        // Elements beyond the HTML parser's nesting cap make it slow (quadratic): parsing checks the time limit too.
        var html = string.Concat(Enumerable.Repeat("<div>", 60_000));
        var options = new HtmlConverterOptions { Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromMilliseconds(300), MaxNestingDepth = 1000 } };

        var started = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ResourceLimitExceededException>(() => HtmlToPdf.ConvertAsync(html, options, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(ResourceLimits.Timeout), ex.LimitName);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"Took {started.Elapsed}.");
    }

    [Fact]
    public void Null_limits_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert("<p>x</p>", new HtmlConverterOptions { Limits = null! }));
    }

    private static bool HasImage(IEnumerable<Block> blocks) =>
        blocks.Any(b => b is ImageBlock || (b is ParagraphBlock p && p.Paragraph.Spans.Any(s => s.Image is not null)));

    private static string Nested(string tag, int depth) => string.Concat(Enumerable.Repeat($"<{tag}>", depth)) + "deep";

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            ParagraphBlock p => Flatten(p.Paragraph.Spans.Where(s => s.Box is not null).Select(s => s.Box!.Content)).Prepend(b),
            _ => [b],
        });
}
