using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Reading;

/// <summary>
/// Importing pages of existing files into a document (append, insert, select), pages drawn as form XObjects
/// (letterheads), rotated pages, links and outlines, drawing on imported pages, and shared resources written once.
/// Renderings are compared with PDFium.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PdfImportTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfImage Photo = PdfImage.Load(Fixtures.Path("Images/rgb.jpg"));

    [Fact]
    public void Appended_pages_render_like_the_source()
    {
        var sourcePdf = CreateSource().ToArray();
        var source = PdfReader.Open(sourcePdf);
        var doc = new PdfDocument();
        doc.AddPage(PdfPageSize.A4).Canvas.DrawText("Cover", StandardFont.HelveticaBold, 40, 72, 700);

        var imported = doc.ImportPages(source);

        Assert.Equal(3, imported.Count);
        Assert.Equal(4, doc.Pages.Count);
        Assert.Same(imported[0], doc.Pages[1]);
        Assert.Equal(PdfPageSize.Letter, imported[2].Size);
        var pdf = doc.ToArray();
        for (var i = 0; i < 3; i++)
        {
            using var expected = Pixels.Render(sourcePdf, i);
            using var actual = Pixels.Render(pdf, i + 1);
            Pixels.AssertSame(expected, actual);
        }
    }

    [Fact]
    public void Pages_are_inserted_where_asked_in_the_order_asked()
    {
        var sourcePdf = CreateSource().ToArray();
        var source = PdfReader.Open(sourcePdf);
        var doc = new PdfDocument();
        var first = doc.AddPage(PdfPageSize.A5);
        var last = doc.AddPage(PdfPageSize.A5);

        var imported = doc.InsertPages(1, source, [2, 0]);
        var cover = doc.InsertPage(0, PdfPageSize.A4);

        Assert.Equal([cover, first, imported[0], imported[1], last], doc.Pages);
        var pdf = doc.ToArray();
        using (var expected = Pixels.Render(sourcePdf, 2))
        using (var actual = Pixels.Render(pdf, 2))
        {
            Pixels.AssertSame(expected, actual);
        }

        using (var expected = Pixels.Render(sourcePdf, 0))
        using (var actual = Pixels.Render(pdf, 3))
        {
            Pixels.AssertSame(expected, actual);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ImportPages(source, [3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.InsertPages(6, source));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.InsertPage(-1, PdfPageSize.A4));
    }

    [Fact]
    public void Shared_fonts_and_images_are_written_once()
    {
        var source = PdfReader.Open(CreateSource().ToArray());

        var once = new PdfDocument();
        once.ImportPages(source);
        var onceReader = PdfReader.Open(once.ToArray());

        // Three pages share one font and one image. Importing them again, in a second call, and drawing a page as a
        // form adds page objects, not fonts or images.
        var twice = new PdfDocument();
        twice.ImportPages(source);
        twice.ImportPages(source, [1]);
        twice.AddPage(PdfPageSize.A4).Canvas.DrawPage(source.Pages[0], 0, 0);
        var twiceReader = PdfReader.Open(twice.ToArray());

        foreach (var reader in new[] { onceReader, twiceReader })
        {
            Assert.Single(Output.OfType(reader, "Font", "Type0"));
            Assert.Single(Output.OfType(reader, "XObject", "Image"));
            Assert.Single(Output.OfType(reader, "FontDescriptor"));
        }

        // The second import adds one page dictionary; the page drawn as a form adds a generated page (dictionary and
        // content) and the form. Content streams and resources are shared.
        Assert.Equal(Output.Objects(onceReader).Count + 4, Output.Objects(twiceReader).Count);
        Assert.Equal(5, twiceReader.Pages.Count);
    }

    [Fact]
    public void A_letterhead_is_drawn_under_generated_content()
    {
        var letterheadPdf = CreateLetterhead();
        var letterhead = PdfReader.Open(letterheadPdf);
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A4);
        page.Canvas.DrawPage(letterhead.Pages[0], 0, 0);
        page.Canvas.SetFillColor(new PdfColor(1, 0, 0)).Rectangle(200, 400, 100, 100).Fill();
        page.Canvas.DrawText("Dear customer,", Sans, 14, 72, 700);

        var pdf = doc.ToArray();
        using var expected = Pixels.Render(letterheadPdf, 0);
        using var actual = Pixels.Render(pdf, 0);

        // The letterhead's band, logo and footer are where they were; the generated content is on top.
        Pixels.AssertColor(new SKColor(0, 51, 102), Pixels.At(actual, 500, 800));
        Pixels.AssertColor(new SKColor(255, 204, 0), Pixels.At(actual, 30, 800));
        Pixels.AssertColor(new SKColor(0, 51, 102), Pixels.At(actual, 300, 30));
        Pixels.AssertColor(SKColors.Red, Pixels.At(actual, 250, 450));
        var differences = Pixels.CountDifferences(expected, actual);
        Assert.InRange(differences, 100 * 100, (100 * 100) + 3000); // The red square and the text.
    }

    [Fact]
    public void A_page_is_drawn_scaled_into_a_rectangle()
    {
        var letterhead = PdfReader.Open(CreateLetterhead());
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A4);
        var (width, height) = (letterhead.Pages[0].Width / 2, letterhead.Pages[0].Height / 2);
        page.Canvas.DrawPage(letterhead.Pages[0], 100, 100, width, height);

        using var actual = Pixels.Render(doc.ToArray(), 0);

        Pixels.AssertColor(new SKColor(0, 51, 102), Pixels.At(actual, 100 + 250, 100 + 400)); // The band, at half size.
        Pixels.AssertColor(SKColors.White, Pixels.At(actual, 300, 800));
        Pixels.AssertColor(SKColors.White, Pixels.At(actual, 50, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Rotated_pages_import_and_draw_as_displayed(int rotate)
    {
        var sourcePdf = CreateRotated(rotate);
        var source = PdfReader.Open(sourcePdf);
        var page = source.Pages[0];
        Assert.Equal(rotate, page.Rotate);

        var doc = new PdfDocument();
        doc.ImportPages(source);
        doc.AddPage(new PdfPageSize(page.Width, page.Height)).Canvas.DrawPage(page, 0, 0);
        var pdf = doc.ToArray();

        using var expected = Pixels.Render(sourcePdf, 0);
        using var imported = Pixels.Render(pdf, 0);
        using var drawn = Pixels.Render(pdf, 1);
        Pixels.AssertSame(expected, imported);
        Pixels.AssertSame(expected, drawn);

        // The red corner mark is at the bottom left of the unrotated crop box: where it shows depends on the rotation.
        var (x, y) = rotate switch
        {
            90 => (10, (int)page.Height - 10),
            180 => ((int)page.Width - 10, (int)page.Height - 10),
            270 => ((int)page.Width - 10, 10),
            _ => (10, 10),
        };
        Pixels.AssertColor(SKColors.Red, Pixels.At(drawn, x, y));
    }

    [Fact]
    public void Links_are_kept_and_point_at_the_imported_pages()
    {
        var source = PdfReader.Open(CreateSource().ToArray());

        var all = new PdfDocument();
        all.ImportPages(source);
        var output = PdfReader.Open(all.ToArray());
        var links = Annotations(output, 0);
        Assert.Equal(3, links.Count);
        Assert.Same(output.Pages[1], output.PageOf(Dest(output, links[0]).Items[0]));
        Assert.Equal("http://example.com/", Encoding.ASCII.GetString(((PdfString)((PdfDictionary)output.Resolve(links[1]["A"])!)["URI"]!).Bytes));
        Assert.Same(output.Pages[2], output.PageOf(Dest(output, links[2]).Items[0])); // A named destination, made explicit.
        Assert.Equal("/XYZ", Dest(output, links[2]).Items[1].ToString());

        // Links to pages that are not imported are dropped; the web link stays.
        var some = new PdfDocument();
        some.ImportPages(source, [0, 2]);
        var someOutput = PdfReader.Open(some.ToArray());
        links = Annotations(someOutput, 0);
        Assert.Equal(2, links.Count);
        Assert.NotNull(links[0]["A"]);
        Assert.Same(someOutput.Pages[1], someOutput.PageOf(Dest(someOutput, links[1]).Items[0]));

        var none = new PdfDocument();
        none.ImportPages(source, new PdfImportOptions { KeepAnnotations = false });
        Assert.Null(PdfReader.Open(none.ToArray()).Pages[0].Dictionary["Annots"]);
    }

    [Fact]
    public void References_to_pages_never_pull_in_other_pages()
    {
        // A note on page 2 refers to its page (/P) and to page 1 (in a custom entry); page 1 is not imported.
        var source = PdfReader.Open(TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Annots [5 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents (Note) /P 4 0 R /Other 3 0 R /Tree 2 0 R >>",
            TestPdf.Stream("0 0 1 rg 0 0 50 50 re f"),
        ]));
        var doc = new PdfDocument();
        doc.ImportPages(source, [1]);

        var output = PdfReader.Open(doc.ToArray());

        Assert.Single(Output.OfType(output, "Page"));
        Assert.Single(Output.OfType(output, "Pages"));
        var note = Annotations(output, 0).Single();
        Assert.Same(output.Pages[0], output.PageOf(note["P"]));
        Assert.Null(note["Other"]);
        Assert.Null(note["Tree"]);
    }

    [Fact]
    public void The_outline_is_kept_on_request()
    {
        var source = PdfReader.Open(CreateSource().ToArray());

        var doc = new PdfDocument();
        var pages = doc.ImportPages(source, new PdfImportOptions { KeepOutline = true });

        Assert.Equal(["Chapter 1", "Chapter 2 \u00E6\u00F8\u00E5"], doc.Outline.Select(item => item.Title));
        Assert.Same(pages[0], doc.Outline[0].Page);
        Assert.Equal(500, doc.Outline[0].Top);
        Assert.True(doc.Outline[0].IsOpen);
        Assert.Equal("Section 1.1", doc.Outline[0].Children[0].Title);
        Assert.Same(pages[1], doc.Outline[0].Children[0].Page);
        Assert.Same(pages[2], doc.Outline[1].Page);
        Assert.False(doc.Outline[1].IsOpen);

        // The outline is written with destinations in the new document.
        var output = PdfReader.Open(doc.ToArray());
        var outlines = (PdfDictionary)output.Resolve(output.Catalog["Outlines"])!;
        var firstItem = (PdfDictionary)output.Resolve(outlines["First"])!;
        Assert.Same(output.Pages[0], output.PageOf(((PdfArray)firstItem["Dest"]!).Items[0]));

        // Items pointing to pages not imported are left out, and their children move up.
        var some = new PdfDocument();
        some.ImportPages(source, [1, 2], new PdfImportOptions { KeepOutline = true });
        Assert.Equal(["Section 1.1", "Chapter 2 \u00E6\u00F8\u00E5"], some.Outline.Select(item => item.Title));

        var none = new PdfDocument();
        none.ImportPages(source);
        Assert.Empty(none.Outline);
    }

    [Fact]
    public void Drawing_on_an_imported_page_goes_over_its_content()
    {
        var sourcePdf = CreateSource().ToArray();
        var source = PdfReader.Open(sourcePdf);
        var doc = new PdfDocument();
        var page = doc.ImportPages(source, [0])[0];

        // The page's own fonts are F1 and its image Im1, like the names the canvas gives its resources.
        page.Canvas.SetFillColor(new PdfColor(0, 0.6, 0)).Rectangle(300, 20, 80, 40).Fill();
        page.Canvas.DrawImage(Photo, 20, 20, 40, 40);
        page.Canvas.DrawText("Page 1 of 3", StandardFont.Helvetica, 10, 300, 70);
        page.AddLink(300, 20, 80, 40, new Uri("https://example.org/"));

        var pdf = doc.ToArray();
        using var expected = Pixels.Render(sourcePdf, 0);
        using var actual = Pixels.Render(pdf, 0);
        Pixels.AssertColor(new SKColor(0, 153, 0), Pixels.At(actual, 340, 40));
        Pixels.AssertColor(Pixels.At(expected, 100, 450), Pixels.At(actual, 100, 450)); // The original content is untouched.
        Pixels.AssertColor(SKColors.Red, Pixels.At(actual, 100, 450));
        var differences = Pixels.CountDifferences(expected, actual);
        Assert.InRange(differences, (80 * 40) + (40 * 40), (80 * 40) + (40 * 40) + 1500);

        var output = PdfReader.Open(pdf);
        Assert.Equal(2, Annotations(output, 0).Count); // The web link of the source (the others lead to pages left out), and the new one.
    }

    [Fact]
    public void Positions_on_an_imported_page_are_measured_from_its_media_box()
    {
        var source = PdfReader.Open(TestPdf.Pages("/MediaBox [100 200 400 500]", ("", "0 0 1 rg 100 200 300 300 re f")));
        var doc = new PdfDocument();
        var page = doc.ImportPages(source)[0];
        Assert.Equal(new PdfPageSize(300, 300), page.Size);

        page.Canvas.SetFillColor(new PdfColor(1, 0, 0)).Rectangle(0, 0, 50, 50).Fill();
        page.AddLink(0, 0, 50, 50, new PdfDestination(page, top: 300));
        doc.Outline.Add(new PdfOutlineItem("Top", page, 300));

        var pdf = doc.ToArray();
        using var bitmap = Pixels.Render(pdf, 0);
        Pixels.AssertColor(SKColors.Red, Pixels.At(bitmap, 10, 10));
        Pixels.AssertColor(SKColors.Blue, Pixels.At(bitmap, 100, 100));

        var output = PdfReader.Open(pdf);
        var link = Annotations(output, 0)[0];
        Assert.Equal("[100 200 150 250]", link["Rect"]!.ToString());
        Assert.Equal("500", Dest(output, link).Items[3].ToString());
    }

    [Fact]
    public void Pages_of_every_file_form_import()
    {
        foreach (var name in new[] { "xref-stream.pdf", "incremental.pdf", "broken-xref.pdf", "hybrid.pdf" })
        {
            var sourcePdf = Fixtures.Read("Reader/" + name);
            var doc = new PdfDocument();
            var pages = doc.ImportPages(PdfReader.Open(sourcePdf));
            var pdf = doc.ToArray();

            Assert.False(PdfReader.Open(pdf).IsRepaired);
            for (var i = 0; i < pages.Count; i++)
            {
                using var expected = Pixels.Render(sourcePdf, i);
                using var actual = Pixels.Render(pdf, i);
                Pixels.AssertSame(expected, actual);
            }
        }
    }

    [Fact]
    public void Merged_output_passes_qpdf_check()
    {
        var source = PdfReader.Open(CreateSource().ToArray());
        var doc = new PdfDocument();
        doc.AddPage(PdfPageSize.A4).Canvas.DrawPage(PdfReader.Open(CreateLetterhead()).Pages[0], 0, 0);
        doc.ImportPages(source, new PdfImportOptions { KeepOutline = true });
        doc.ImportPages(PdfReader.Open(Fixtures.Path("Reader/xref-stream.pdf")));
        doc.ImportPages(PdfReader.Open(Fixtures.Path("Reader/broken-xref.pdf")));
        doc.ImportPages(PdfReader.Open(CreateRotated(90)));
        doc.Pages[1].Canvas.DrawText("Stamped", StandardFont.Helvetica, 10, 20, 20);

        ExternalTools.AssertQpdfClean(doc.ToArray());
    }

    /// <summary>
    /// Three pages sharing one embedded font and one JPEG; page 1 links to page 2, to a web page and to a named
    /// destination on page 3; an outline with a nested and a closed item.
    /// </summary>
    internal static PdfDocument CreateSource()
    {
        var doc = new PdfDocument();
        doc.Info.CreationDate = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var pages = new[] { doc.AddPage(PdfPageSize.A5), doc.AddPage(PdfPageSize.A5), doc.AddPage(PdfPageSize.Letter) };
        for (var i = 0; i < pages.Length; i++)
        {
            var canvas = pages[i].Canvas;
            canvas.DrawText($"Imported page {i + 1}", Sans, 24, 40, pages[i].Height - 80);
            canvas.DrawImage(Photo, 40, 200 + (i * 20), 160, 120);
            canvas.SetFillColor(i == 1 ? new PdfColor(0, 0, 1) : new PdfColor(1, 0, 0)).Rectangle(60, 420, 120, 60).Fill();
        }

        pages[0].AddLink(40, 500, 200, 30, new PdfDestination(pages[1], 300));
        pages[0].AddLink(40, 140, 200, 30, new Uri("http://example.com/"));
        pages[0].AddLink(40, 100, 200, 30, "chapter2");
        doc.NamedDestinations["chapter2"] = new PdfDestination(pages[2], 700);

        var chapter = new PdfOutlineItem("Chapter 1", pages[0], 500);
        chapter.Children.Add(new PdfOutlineItem("Section 1.1", pages[1], 300));
        var closed = new PdfOutlineItem("Chapter 2 \u00E6\u00F8\u00E5", pages[2], 700) { IsOpen = false };
        closed.Children.Add(new PdfOutlineItem("Section 2.1", pages[2], 400));
        doc.Outline.Add(chapter);
        doc.Outline.Add(closed);
        return doc;
    }

    /// <summary>An A4 letterhead: a dark blue band with a yellow logo square at the top, and a footer rule.</summary>
    internal static byte[] CreateLetterhead()
    {
        var doc = new PdfDocument();
        var canvas = doc.AddPage(PdfPageSize.A4).Canvas;
        var navy = PdfColor.FromRgb(0, 51, 102);
        canvas.SetFillColor(navy).Rectangle(0, 770, 595.28, 71.89).Fill();
        canvas.SetFillColor(PdfColor.FromRgb(255, 204, 0)).Rectangle(20, 785, 40, 40).Fill();
        canvas.SetFillColor(PdfColor.White).DrawText("ACME Letterheads Ltd.", Sans, 22, 80, 795);
        canvas.SetFillColor(navy).Rectangle(40, 25, 515, 10).Fill();
        canvas.DrawText("ACME Ltd. - 1 Main Street - 1234 Town", Sans, 8, 40, 45);
        return doc.ToArray();
    }

    /// <summary>
    /// A page turned by <paramref name="rotate"/> degrees whose media and crop boxes do not start at the origin, with a
    /// red mark at the crop box's bottom-left corner, a blue one at its top right and an arrow pointing up.
    /// </summary>
    internal static byte[] CreateRotated(int rotate) => TestPdf.Pages(
        string.Empty,
        ($"/MediaBox [50 100 450 400] /CropBox [60 110 440 390] /Rotate {rotate}",
            "0.9 0.9 0.9 rg 60 110 380 280 re f 1 0 0 rg 60 110 20 20 re f 0 0 1 rg 420 370 20 20 re f "
            + "0 0.5 0 rg 240 150 m 280 150 l 280 300 l 320 300 l 260 360 l 200 300 l 240 300 l f"));

    private static List<PdfDictionary> Annotations(PdfReader reader, int page) =>
        reader.Resolve(reader.Pages[page].Dictionary["Annots"]) is PdfArray array
            ? [.. array.Items.Select(reader.Resolve).OfType<PdfDictionary>()]
            : [];

    private static PdfArray Dest(PdfReader reader, PdfDictionary link) => (PdfArray)reader.Resolve(link["Dest"])!;
}
