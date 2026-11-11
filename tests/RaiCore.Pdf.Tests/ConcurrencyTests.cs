using System.Globalization;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;
using RaiCore.Pdf.Razor;
using RaiCore.Pdf.Tests.Razor;
using RaiCore.Pdf.Tests.Razor.Components;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// Thread safety: fonts are shared between documents and threads without copies, and conversions running in parallel
/// give exactly the bytes of the same conversions run one after another.
/// </summary>
public class ConcurrencyTests
{
    /// <summary>A fixed date, so that output does not depend on when it was made.</summary>
    private static readonly DateTimeOffset Created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly Uri FixturesUri = new(Fixtures.Path("x"));

    private static readonly ParallelOptions Parallelism = new() { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_bundled_collection_returns_the_same_font_instances()
    {
        var first = BundledFonts.CreateCollection();
        var second = new FontCollection().AddBundledFonts();

        foreach (var family in new[] { "Liberation Sans", "Liberation Serif", "Liberation Mono" })
        {
            foreach (var (weight, italic) in new[] { (400, false), (700, false), (400, true), (700, true) })
            {
                var font = first.Find(family, weight, italic);
                Assert.NotNull(font);
                Assert.Same(font, second.Find(family, weight, italic));
            }
        }
    }

    [Fact]
    public void A_document_using_an_already_loaded_bundled_font_does_not_copy_its_data()
    {
        static byte[] Render(PdfFont font)
        {
            var document = new PdfDocument { IncludeXmpMetadata = false };
            var page = document.AddPage(PdfPageSize.A4);
            new Paragraph(new TextStyle(font, 12)).Add("Shared fonts are not copied per document.").Layout(400).Draw(page.Canvas, 72, 700);
            page.Canvas.DrawText("Nor per thread.", font, 12, 72, 600);
            return document.ToArray();
        }

        static long Allocated(Func<byte[]> action, out byte[] result)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            result = action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // The first use reads and parses the font and makes its HarfBuzz face; later documents only subset it.
        var font = BundledFonts.CreateCollection().Find("Liberation Sans")!;
        var fontData = ((OpenTypePdfFont)font).Font.Data.Length;
        var expected = Render(font);

        var sameFont = Allocated(() => Render(font), out var first);
        var newCollection = Allocated(() => Render(BundledFonts.CreateCollection().Find("Liberation Sans")!), out var second);
        var reloaded = Allocated(() => Render(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"))), out var third);

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(expected, third);

        // A document's own work (layout, subset, compression) is smaller than the font; a new collection adds next to
        // nothing, while loading the font again (what sharing avoids) costs at least the size of its data.
        Assert.True(sameFont < fontData, $"A document allocated {sameFont} bytes; the font data is {fontData} bytes.");
        Assert.True(newCollection - sameFont < fontData / 10, $"A new collection added {newCollection - sameFont} bytes; the font data is {fontData} bytes.");
        Assert.True(reloaded - sameFont > fontData, $"Reloading the font added only {reloaded - sameFont} bytes.");
    }

    [Fact]
    public void Html_conversions_without_a_font_collection_share_the_bundled_fonts()
    {
        var first = HtmlToPdf.Convert("<p>One</p>");
        var second = HtmlToPdf.Convert("<p>Two</p>");

        Assert.Same(first.Pages[0].Fonts[0].Font, second.Pages[0].Fonts[0].Font);
    }

    [Fact]
    public void The_faces_of_a_collection_file_share_its_data()
    {
        var collection = new FontCollection().AddFile(Fixtures.Path("RaiCoreTestTT.ttc"));

        var regular = (OpenTypePdfFont)collection.Find("RaiCore Test TT", 400)!;
        var bold = (OpenTypePdfFont)collection.Find("RaiCore Test TT", 700)!;

        Assert.NotSame(regular, bold);
        Assert.Same(regular.Font.Data, bold.Font.Data);
        Assert.Same(regular, collection.Find("RaiCore Test TT", 400));
    }

    [Fact]
    public void Parallel_html_conversions_sharing_a_font_collection_match_sequential_ones()
    {
        var documents = HtmlDocuments();

        // The parallel run goes first, with fonts not loaded yet, so that threads race to load and parse them.
        var parallelFonts = SharedFonts();
        var parallel = new byte[documents.Count * 4][];
        Parallel.For(0, parallel.Length, Parallelism, i => parallel[i] = ConvertHtml(documents[i % documents.Count], parallelFonts));

        var sequentialFonts = SharedFonts();
        var sequential = documents.Select(html => ConvertHtml(html, sequentialFonts)).ToList();

        for (var i = 0; i < parallel.Length; i++)
        {
            Assert.Equal(sequential[i % documents.Count], parallel[i]);
        }

        // The documents really use the shared fonts, web fonts, images and hyphenation they are meant to test.
        var text = Encoding.Latin1.GetString(sequential[0]);
        Assert.Contains("LiberationSerif", text, StringComparison.Ordinal);
        Assert.Contains("NotoSansArabic", text, StringComparison.Ordinal);
        Assert.Contains("NotoSansJP", text, StringComparison.Ordinal);
        Assert.Contains("LiberationMono", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.True(sequential.Distinct(new ByteArrayComparer()).Count() == documents.Count, "Each document differs from the others.");
    }

    [Fact]
    public async Task Parallel_razor_conversions_match_sequential_ones()
    {
        var options = new HtmlConverterOptions { Fonts = BundledFonts.CreateCollection() };
        var renderer = new RazorPdfRenderer();
        async Task<byte[]> Convert(int lines)
        {
            var parameters = new Dictionary<string, object?> { [nameof(InvoiceDocument.Invoice)] = RazorConversionTests.SampleInvoice(lines) };
            var pdf = await renderer.ConvertAsync<InvoiceDocument>(parameters, options, Ct);
            pdf.Info.CreationDate = Created;
            return pdf.ToArray();
        }

        var sizes = Enumerable.Range(0, 24).Select(i => 5 + (i % 6 * 7)).ToList();
        var parallel = await Task.WhenAll(sizes.Select(n => Task.Run(() => Convert(n), Ct)));
        var sequential = new Dictionary<int, byte[]>();
        foreach (var n in sizes.Distinct())
        {
            sequential[n] = await Convert(n);
        }

        for (var i = 0; i < sizes.Count; i++)
        {
            Assert.Equal(sequential[sizes[i]], parallel[i]);
        }
    }

    [Fact]
    public void Parallel_documents_sharing_fonts_and_images_match_sequential_ones()
    {
        // Loaded here, so that the threads below are the first to map characters, shape and subset with them.
        var sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
        var cff = PdfFont.Load(Fixtures.Path("NotoSansJP-Subset.otf"));
        var photo = PdfImage.Load(Fixtures.Path("Images/rgb.jpg"));
        var gif = PdfImage.Load(Fixtures.Path("Images/transparent.gif"));
        var drawing = PdfImage.LoadSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="teal" opacity="0.5"/></svg>""");

        byte[] Render(int variant)
        {
            var style = new TextStyle([sans, arabic, cff], 11) { Language = "en-US", Hyphens = Hyphens.Auto };
            var flow = new FlowDocument(PageSetup.A4);
            for (var i = 0; i < 6 + variant; i++)
            {
                var paragraph = new Paragraph(style) { Alignment = TextAlignment.Justify }
                    .Add($"Paragraph {i.ToString(CultureInfo.InvariantCulture)} of variant {variant.ToString(CultureInfo.InvariantCulture)}: ")
                    .Add("internationalization responsibilities characteristically incomprehensible ")
                    .Add("\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 ")
                    .Add("\u3053\u3093\u306B\u3061\u306F \u30AB\u30BF\u30AB\u30CA \u65E5\u5E74\u5B57 ")
                    .Add(string.Concat(Enumerable.Repeat("representational ", variant + 3)));
                flow.Add(new ParagraphBlock(paragraph, new BoxStyle { Margin = new(0, 0, 6, 0) }));
                flow.Add(new ImageBlock(i % 3 == 0 ? photo : i % 3 == 1 ? gif : drawing, 60, 30));
            }

            flow.PageDecorator = (canvas, page) => canvas.DrawText(
                $"Page {page.Number.ToString(CultureInfo.InvariantCulture)} of {page.Count.ToString(CultureInfo.InvariantCulture)}", sans, 9, 72, 30);
            var document = flow.Render();
            document.Info.CreationDate = Created;
            return document.ToArray();
        }

        const int Variants = 4;
        var parallel = new byte[Variants * 8][];
        Parallel.For(0, parallel.Length, Parallelism, i => parallel[i] = Render(i % Variants));
        var sequential = Enumerable.Range(0, Variants).Select(Render).ToList();

        for (var i = 0; i < parallel.Length; i++)
        {
            Assert.Equal(sequential[i % Variants], parallel[i]);
        }
    }

    [Fact]
    public void Shaping_with_new_fonts_on_many_threads_at_once_gives_the_same_glyphs()
    {
        var data = Fixtures.Read("NotoSansArabic-Subset.ttf");
        var latin = Fixtures.Read("LiberationSans-Regular.ttf");
        string[] texts = ["\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 office", "fluffy waffles AVA To", "\u0661\u0662\u0663 ffi"];

        static string Glyphs(ShapedText shaped) =>
            string.Join(' ', shaped.Runs.SelectMany(r => r.Glyphs).Select(g => FormattableString.Invariant($"{g.GlyphId}:{g.Advance:R}:{g.OffsetX:R}")));

        var expectedFonts = new[] { PdfFont.Load(data), PdfFont.Load(latin) };
        var expected = texts.Select(t => Glyphs(TextShaper.Shape(t, expectedFonts))).ToList();

        const int Threads = 8;
        for (var round = 0; round < 20; round++)
        {
            // New font objects each round: the threads race to build the character map and the HarfBuzz face.
            var fonts = new[] { PdfFont.Load(data), PdfFont.Load(latin) };
            using var start = new Barrier(Threads);
            var results = new string[Threads][];
            var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait(Ct);
                results[t] = [.. texts.Select(text => Glyphs(TextShaper.Shape(text, fonts)))];
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            foreach (var result in results)
            {
                Assert.Equal(expected, result);
            }
        }
    }

    [Fact]
    public void A_symbol_font_maps_8_bit_codes_to_its_private_use_glyphs()
    {
        var font = (OpenTypePdfFont)PdfFont.Load(Fixtures.Path("RaiCoreTestTT-Symbol.ttf"));

        Assert.True(font.Font.IsSymbolCmap);
        Assert.NotEqual(0, font.Font.GetGlyphId(0xF041));
        Assert.Equal(font.Font.GetGlyphId(0xF041), font.Font.GetGlyphId('A'));
        Assert.False(font.HasGlyph('B'));
    }

    [Fact]
    public void A_symbol_font_used_for_the_first_time_on_many_threads_at_once_maps_every_code()
    {
        var data = Fixtures.Read("RaiCoreTestTT-Symbol.ttf");
        const int Threads = 8;
        for (var round = 0; round < 500; round++)
        {
            // A new font each round: the threads race to build its character map, and each must see the map with
            // its symbol flag (8-bit codes are found at U+F0xx only in a symbol font). The window in which a map could
            // be seen without its flag is short, so this catches such a bug in some runs, not every run.
            var font = PdfFont.Load(data);
            using var start = new Barrier(Threads);
            var found = new bool[Threads];
            var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait(Ct);
                found[t] = font.HasGlyph('A');
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.All(found, Assert.True);
        }
    }

    [Fact]
    public async Task A_registration_racing_with_lookups_is_seen_by_the_lookups_after_it()
    {
        var hyphenator = Hyphenator.FromPatterns(["a1b"]);
        for (var round = 0; round < 100; round++)
        {
            // Every lookup is of a new subtag of the language, so each one resolves (and caches) while the language is
            // being registered; none may keep the answer from before the registration.
            var tag = "x-thread-test-" + round.ToString(CultureInfo.InvariantCulture);
            var registered = 0;
            using var started = new CountdownEvent(3);
            var lookups = Enumerable.Range(0, 3).Select(thread => Task.Run(
                () =>
                {
                    var looked = new List<string>();
                    for (var i = 0; Volatile.Read(ref registered) == 0 || i < 3; i++)
                    {
                        var subtag = FormattableString.Invariant($"{tag}-t{thread}v{i}");
                        Hyphenator.ForLanguage(subtag);
                        looked.Add(subtag);
                        if (i == 0)
                        {
                            started.Signal();
                        }
                    }

                    return looked;
                },
                Ct)).ToArray();

            started.Wait(Ct);
            Thread.SpinWait(round % 20 * 10);
            Hyphenator.Register(tag, hyphenator);
            Volatile.Write(ref registered, 1);
            var subtags = (await Task.WhenAll(lookups)).SelectMany(l => l).ToList();

            try
            {
                Assert.All(subtags, subtag => Assert.Same(hyphenator, Hyphenator.ForLanguage(subtag)));
            }
            finally
            {
                Hyphenator.Register(tag, null);
            }
        }
    }

    /// <summary>A collection shared by all conversions: the bundled fonts plus fonts for Arabic and Japanese.</summary>
    private static FontCollection SharedFonts() => BundledFonts.CreateCollection()
        .AddFile(Fixtures.Path("NotoSansArabic-Subset.ttf"))
        .AddFile(Fixtures.Path("NotoSansJP-Subset.otf"));

    private static byte[] ConvertHtml(string html, FontCollection fonts)
    {
        var pdf = HtmlToPdf.Convert(html, new HtmlConverterOptions { Fonts = fonts, BaseUri = FixturesUri });
        pdf.Info.CreationDate = Created;
        return pdf.ToArray();
    }

    private static List<string> HtmlDocuments()
    {
        const string Style = """
            <style>
              @font-face { font-family: Code; src: url(LiberationMono-Regular.ttf) format("truetype"); }
              body { font-family: "Liberation Serif", serif; }
              p { hyphens: auto; text-align: justify; }
              code { font-family: Code, monospace; }
              .ar { font-family: "Noto Sans Arabic", sans-serif; direction: rtl; }
              .ja { font-family: "Noto Sans JP", sans-serif; }
              td { border: 1px solid #555; padding: 2px 4px; }
              h1 { string-set: title content(); }
              @page { @bottom-center { content: string(title) " " counter(page) "/" counter(pages); } }
            </style>
            """;
        var documents = new List<string>();
        for (var d = 0; d < 4; d++)
        {
            var body = new StringBuilder();
            body.Append(CultureInfo.InvariantCulture, $"<h1>Document {d}</h1>");
            for (var p = 0; p < 4 + (d * 3); p++)
            {
                body.Append(CultureInfo.InvariantCulture, $"""
                    <p lang="{(d % 2 == 0 ? "de" : "en-US")}">Absatz {p}: Donaudampfschifffahrtsgesellschaft und
                    Silbentrennungsalgorithmus, internationalization responsibilities, <b>bold</b> <i>italic</i>
                    <code>code()</code> and more text that wraps across several justified lines of the page.</p>
                    <p class="ar">&#x627;&#x644;&#x633;&#x644;&#x627;&#x645; &#x639;&#x644;&#x64A;&#x643;&#x645; {p}</p>
                    <p class="ja">&#x3053;&#x3093;&#x306B;&#x3061;&#x306F; &#x30AB;&#x30BF;&#x30AB;&#x30CA; {p}</p>
                    """);
            }

            body.Append("<table>");
            for (var r = 0; r < 5 + d; r++)
            {
                body.Append(CultureInfo.InvariantCulture, $"<tr><td>Row {r}</td><td>{r * 17.5:F2}</td><td><img src=\"Images/rgb.jpg\" width=\"20\"></td></tr>");
            }

            body.Append("</table>");
            documents.Add($"<!DOCTYPE html><html><head>{Style}</head><body>{body}</body></html>");
        }

        return documents;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
