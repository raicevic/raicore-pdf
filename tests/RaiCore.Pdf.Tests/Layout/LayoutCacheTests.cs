using RaiCore.Pdf.Benchmarks;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// The layout cache (paragraph layouts by width, intrinsic widths, shaped text) must be invisible: every document comes
/// out byte for byte the same with and without it.
/// </summary>
public class LayoutCacheTests
{
    private const string Hebrew = "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD";

    private static readonly DateTimeOffset Date = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> HtmlDocuments() => new(
        // Floats: lines beside them get the room the floats leave (not cached), the rest are cached.
        $"""
        <style>p {"{"} text-align: justify; hyphens: auto {"}"} .f {"{"} float: left; width: 120pt; height: 90pt; background: #ddd; margin: 4pt {"}"}</style>
        <div lang="en"><div class="f"></div><p>{Documents.Text(1, 180)}</p><div class="f" style="float: right"></div><p>{Documents.Text(2, 260)}</p></div>
        """,
        // Tables: automatic and fixed layout, spans, nested tables, header rows repeated across pages.
        $"""
        <style>td, th {"{"} border: 1px solid #888; padding: 3pt {"}"} table {"{"} border-collapse: collapse; width: 100% {"}"}</style>
        <table><thead><tr><th colspan="2">Head</th><th>Third</th></tr></thead><tbody>
        {string.Concat(Enumerable.Range(0, 60).Select(i => $"<tr><td rowspan=\"{1 + (i % 3 == 0 ? 1 : 0)}\">{Documents.Text(i, 3 + (i % 7))}</td><td>{Documents.Text(i + 100, 1 + (i % 4))}</td><td>{(i % 10 == 0 ? $"<table><tr><td>{Documents.Text(i, 2)}</td><td>x</td></tr></table>" : Documents.Text(i + 7, 2))}</td></tr>"))}
        </tbody></table>
        <table style="table-layout: fixed; width: 300pt"><tr><td style="width: 30%">{Documents.Text(5, 30)}</td><td>{Documents.Text(6, 12)}</td></tr></table>
        """,
        // Flex: wrapping rows, columns, grow and shrink, nested containers.
        Documents.FlexHtml(40),
        $"""
        <div style="display: flex; flex-direction: column; height: 400pt; justify-content: space-between">
          <div style="display: flex; gap: 6pt"><p style="flex: 2">{Documents.Text(3, 40)}</p><p style="flex: 1; min-width: 0">{Documents.Text(4, 9)}</p></div>
          <div style="display: flex; flex-wrap: wrap-reverse; align-content: center">{string.Concat(Enumerable.Range(0, 30).Select(i => $"<span style=\"flex: 1 0 {40 + i}pt; border: 1px solid\">{Documents.Text(i, 1 + i % 3)}</span>"))}</div>
        </div>
        """,
        // Inline boxes, letter and word spacing, ellipsis, leaders, links and anchors, generated content.
        $"""
        <style>
          .e {"{"} white-space: nowrap; overflow: hidden; text-overflow: ellipsis; width: 150pt {"}"}
          li::marker {"{"} color: red {"}"} a::after {"{"} content: leader('.') target-counter(attr(href), page) {"}"}
        </style>
        <p style="letter-spacing: 1pt; word-spacing: 3pt">{Documents.Text(8, 50)}</p>
        <p>Before <span style="display: inline-block; width: 80pt; border: 1px solid">{Documents.Text(9, 12)}</span> after {Documents.Text(10, 20)}</p>
        <div class="e">{Documents.Text(11, 30)}</div><div class="e" dir="rtl">{Hebrew} {Hebrew} {Hebrew} {Hebrew} {Hebrew}</div>
        <ol>{string.Concat(Enumerable.Range(1, 12).Select(i => $"<li><a href=\"#s{i}\">Section {i}</a></li>"))}</ol>
        {string.Concat(Enumerable.Range(1, 12).Select(i => $"<h2 id=\"s{i}\">Section {i}</h2><p>{Documents.Text(i * 3, 90)}</p>"))}
        """,
        // Bidi, named pages of another width, page margin boxes.
        $"""
        <style>@page wide {"{"} size: A4 landscape {"}"} @page {"{"} @bottom-center {"{"} content: counter(page) " / " counter(pages) {"}"} {"}"} .w {"{"} page: wide {"}"}</style>
        <p dir="rtl">{Hebrew} abc 123 {Hebrew} ({Hebrew}) {Documents.Text(12, 30)}</p>
        <div class="w"><p>{Documents.Text(13, 400)}</p></div>
        <p dir="auto">{Hebrew} {Documents.Text(14, 200)}</p>
        """,
        Documents.InvoiceHtml(),
        Documents.ReportHtml(chapters: 2),
        Documents.TableHtml(rows: 150));

    [Theory]
    [MemberData(nameof(HtmlDocuments))]
    public void Html_documents_are_the_same_without_the_cache(string html)
    {
        var cached = Html(html, disabled: false);
        var uncached = Html(html, disabled: true);

        Assert.True(cached.AsSpan().SequenceEqual(uncached), "The layout cache changed the output.");
    }

    [Fact]
    public void Flow_documents_are_the_same_without_the_cache()
    {
        byte[] Render(bool disabled)
        {
            LayoutCache.Disabled = disabled;
            try
            {
                var document = Documents.ReportFlow(BundledFonts.CreateCollection(), chapters: 2).Render();
                document.Info.CreationDate = document.Info.ModificationDate = Date;
                return document.ToArray();
            }
            finally
            {
                LayoutCache.Disabled = false;
            }
        }

        Assert.True(Render(false).AsSpan().SequenceEqual(Render(true)), "The layout cache changed the output.");
    }

    [Fact]
    public void A_pass_lays_a_paragraph_out_once_per_width()
    {
        var paragraph = new Paragraph(Style()).Add(Documents.Text(1, 30));
        using (LayoutCache.Enter())
        {
            Assert.Same(paragraph.Layout(200), paragraph.Layout(200));
            Assert.NotSame(paragraph.Layout(200), paragraph.Layout(150));
            Assert.Same(paragraph.Layout(150), paragraph.Layout(150));
        }

        Assert.NotSame(paragraph.Layout(200), paragraph.Layout(200)); // No pass, no cache.
    }

    [Fact]
    public void A_changed_paragraph_is_laid_out_again()
    {
        var paragraph = new Paragraph(Style()).Add("one two");
        using (LayoutCache.Enter())
        {
            var before = paragraph.Layout(200);
            var intrinsic = paragraph.MeasureIntrinsicWidths();
            paragraph.Add(" three four five");

            var after = paragraph.Layout(200);
            Assert.NotSame(before, after);
            Assert.Equal(23, after.Lines.Sum(l => l.End - l.Start));
            Assert.True(paragraph.MeasureIntrinsicWidths().Max > intrinsic.Max);

            paragraph.Alignment = TextAlignment.Center;
            var centered = paragraph.Layout(200);
            Assert.NotSame(after, centered);
            Assert.True(centered.Lines[0].X > after.Lines[0].X);

            foreach (var change in new Action[]
            {
                () => paragraph.Direction = ParagraphDirection.RightToLeft,
                () => paragraph.FirstLineIndent = 10,
                () => paragraph.BreakLongWords = true,
                () => paragraph.Ellipsis = true,
                () => paragraph.BeginLink(LinkTarget.ToUri(new Uri("https://example.com"))).Add("link").EndLink(),
                () => paragraph.AddAnchor("here"),
                () => paragraph.BeginInline(new BoxStyle()).Add("in").EndInline(),
                () => paragraph.AddLeader("."),
                () => paragraph.AddImage(PdfImage.Load(Fixtures.Read("Images/gray.jpg")), 4, 4),
                () => paragraph.AddBox(new ParagraphBlock(new Paragraph(Style()).Add("box"))),
            })
            {
                var previous = paragraph.Layout(200);
                change();
                Assert.NotSame(previous, paragraph.Layout(200));
            }
        }
    }

    [Fact]
    public void Lines_beside_floats_are_not_cached()
    {
        var paragraph = new Paragraph(Style()).Add(Documents.Text(2, 40));
        using (LayoutCache.Enter())
        {
            var narrow = paragraph.Layout(200, (top, height, minWidth) => (top, 50, 150));
            var full = paragraph.Layout(200);

            Assert.NotSame(narrow, full);
            Assert.Equal(50, narrow.Lines[0].X);
            Assert.Equal(0, full.Lines[0].X);
            Assert.NotSame(narrow, paragraph.Layout(200, (top, height, minWidth) => (top, 50, 150)));
        }
    }

    [Fact]
    public void Shaping_is_cached_by_text_fonts_features_language_and_direction()
    {
        var fonts = BundledFonts.CreateCollection();
        var sans = fonts.Find("Liberation Sans")!;
        var serif = fonts.Find("Liberation Serif")!;
        using (LayoutCache.Enter())
        {
            var shaped = TextShaper.Shape("office", [sans]);
            Assert.Same(shaped, TextShaper.Shape(new string("office".ToCharArray()), [sans], ShapingOptions.Default));
            Assert.NotSame(shaped, TextShaper.Shape("office", [serif]));
            Assert.NotSame(shaped, TextShaper.Shape("office", [sans, serif]));
            Assert.NotSame(shaped, TextShaper.Shape("offices", [sans]));
            Assert.NotSame(shaped, TextShaper.Shape("office", [sans], new ShapingOptions { Features = ["-liga"] }));
            Assert.NotSame(shaped, TextShaper.Shape("office", [sans], new ShapingOptions { Language = "tr" }));
            Assert.NotSame(shaped, TextShaper.Shape("office", [sans], null, TextDirection.LeftToRight));
            Assert.NotSame(
                TextShaper.Shape("office", [sans], null, TextDirection.LeftToRight),
                TextShaper.Shape("office", [sans], null, TextDirection.RightToLeft));
            Assert.Same(
                TextShaper.Shape("office", [sans], new ShapingOptions { Features = ["-liga"] }),
                TextShaper.Shape("office", [sans], new ShapingOptions { Features = ["-liga"] }));
            Assert.NotSame(
                TextShaper.Shape("office", [sans], new ShapingOptions { Features = ["-liga"] }),
                TextShaper.Shape("office", [sans], new ShapingOptions { Features = ["-kern"] }));

            // Long texts (whole spans and lines) are not kept.
            var text = Documents.Text(3, 20);
            Assert.True(text.Length > 64);
            Assert.NotSame(TextShaper.Shape(text, [sans]), TextShaper.Shape(text, [sans]));
            Assert.Same(TextShaper.Shape(text[..64], [sans]), TextShaper.Shape(text[..64], [sans]));
        }

        Assert.NotSame(TextShaper.Shape("office", [sans]), TextShaper.Shape("office", [sans]));
    }

    [Fact]
    public void Shape_keys_compare_every_font_of_the_list()
    {
        var fonts = BundledFonts.CreateCollection();
        var sans = fonts.Find("Liberation Sans")!;
        var serif = fonts.Find("Liberation Serif")!;
        LayoutCache.ShapeKey Key(params PdfFont[] list) => new("office", list, ShapingOptions.Default, null);

        Assert.True(Key(sans, serif).Equals(Key(sans, serif))); // Equal lists, different instances.
        Assert.False(Key(sans, serif).Equals(Key(sans, sans)));
        Assert.False(Key(sans).Equals(Key(serif)));
        Assert.False(Key(sans).Equals(Key(sans, serif)));
    }

    [Fact]
    public void Passes_on_different_threads_have_their_own_caches()
    {
        using (LayoutCache.Enter())
        {
            var outer = LayoutCache.Current;
            Assert.NotNull(outer);
            using (LayoutCache.Enter())
            {
                Assert.Same(outer, LayoutCache.Current); // Nested: the outer pass's cache.
            }

            Assert.Same(outer, LayoutCache.Current);
            LayoutCache? other = null;
            var thread = new Thread(() =>
            {
                using (LayoutCache.Enter())
                {
                    other = LayoutCache.Current;
                }
            });
            thread.Start();
            thread.Join();
            Assert.NotNull(other);
            Assert.NotSame(outer, other);
        }

        Assert.Null(LayoutCache.Current);
    }

    private static TextStyle Style() => new(BundledFonts.CreateCollection().Find("Liberation Sans")!, 11);

    private static byte[] Html(string html, bool disabled)
    {
        LayoutCache.Disabled = disabled;
        try
        {
            var document = HtmlToPdf.Convert(html);
            document.Info.CreationDate = document.Info.ModificationDate = Date;
            return document.ToArray();
        }
        finally
        {
            LayoutCache.Disabled = false;
        }
    }
}
