using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Tests.Reading;

namespace RaiCore.Pdf.Tests.Editing;

public class PdfEditorTests
{
    /// <summary>
    /// A document of <paramref name="count"/> pages: "Page n" on each, an image of its own on each (64 x 64 noise, so
    /// that it compresses badly and pages differ in size), a bookmark per page and a named destination per page.
    /// </summary>
    private static byte[] Document(int count, Action<PdfDocument, IReadOnlyList<PdfPage>>? more = null)
    {
        var document = new PdfDocument();
        var pages = new List<PdfPage>();
        var random = new Random(count);
        for (var i = 0; i < count; i++)
        {
            var page = document.AddPage(PdfPageSize.A4);
            page.Canvas.DrawText($"Page {i + 1}", StandardFont.Helvetica, 12, 72, 700);
            page.Canvas.DrawImage(Noise(random), 72, 400, 100, 100);
            document.Outline.Add(new PdfOutlineItem($"Page {i + 1}", page));
            document.NamedDestinations[$"page{i + 1}"] = new PdfDestination(page);
            pages.Add(page);
        }

        more?.Invoke(document, pages);
        return document.ToArray();
    }

    private static PdfImage Noise(Random random)
    {
        var pixels = new byte[64 * 64 * 3];
        random.NextBytes(pixels);
        return PdfImage.Load(RaiCore.Pdf.Extraction.Images.PngWriter.Write(64, 64, 3, pixels));
    }

    /// <summary>Checks a file with qpdf where it is installed (CI); <see cref="Saved_files_pass_qpdf"/> shows when it is not.</summary>
    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    private static string[] Texts(byte[] pdf) =>
        [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static string[] Titles(PdfReader reader)
    {
        var titles = new List<string>();
        void Walk(PdfObject? first, string indent)
        {
            PdfDictionary? previous = null;
            for (var item = reader.Resolve(first) as PdfDictionary; item is not null; previous = item, item = reader.Resolve(item["Next"]) as PdfDictionary)
            {
                Assert.Same(previous, reader.Resolve(item["Prev"]));
                var title = RaiCore.Pdf.Reading.PdfText.Decode(((PdfString)reader.Resolve(item["Title"])!).Bytes);
                var count = reader.Resolve(item["Count"]) is PdfInteger n ? $" ({n.Value})" : "";
                var target = reader.Resolve(item["Dest"]) switch
                {
                    PdfArray { Items.Count: > 0 } dest when reader.Pages.FirstOrDefault(p => p.Reference?.ObjectNumber == (dest.Items[0] as PdfReference)?.ObjectNumber) is { } page => $" -> {page.Index + 1}",
                    null => "",
                    _ => " -> nowhere",
                };
                titles.Add(indent + title + count + target);
                Walk(item["First"], indent + "  ");
            }
        }

        var outlines = reader.Resolve(reader.Catalog["Outlines"]) as PdfDictionary;
        Walk(outlines?["First"], "");
        if (reader.Resolve(outlines?["Count"]) is PdfInteger count)
        {
            titles.Add($"[{count.Value}]");
        }

        return [.. titles];
    }

    /// <summary>The image XObjects in a file, among all its objects.</summary>
    private static int ImageObjects(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        return reader.Store.ObjectNumbers.Count(n => reader.Store.GetObject(n) is PdfStreamObject s && s.Dictionary["Subtype"] is PdfName { Value: "Image" });
    }

    [Fact]
    public void A_file_saves_as_it_was_and_saves_again()
    {
        var source = Document(3);
        var editor = PdfEditor.Open(source);

        var first = editor.ToArray();
        var second = editor.ToArray();

        Assert.Equal(first, second);
        Assert.Equal(["Page 1", "Page 2", "Page 3"], Texts(first));
        Assert.Equal(["Page 1 -> 1", "Page 2 -> 2", "Page 3 -> 3", "[3]"], Titles(PdfReader.Open(first)));
        Assert.Equal(3, ImageObjects(first));
        AssertValid(first);
    }

    [Fact]
    public void Deleting_pages_one_by_one_shrinks_the_file_and_drops_their_resources()
    {
        var editor = PdfEditor.Open(Document(6));
        var size = editor.ToArray().Length;

        for (var left = 5; left >= 1; left--)
        {
            editor.Pages.Delete(0);
            var saved = editor.ToArray();

            Assert.True(saved.Length < size, $"{saved.Length} bytes with {left} pages, {size} before.");
            Assert.Equal(left, ImageObjects(saved));
            Assert.Equal(Enumerable.Range(7 - left, left).Select(i => $"Page {i}"), Texts(saved));
            AssertValid(saved);
            size = saved.Length;
        }

        Assert.Throws<InvalidOperationException>(() =>
        {
            editor.Pages.Delete(0);
            editor.ToArray();
        });
    }

    [Fact]
    public void Pages_are_inserted_from_other_files_moved_and_deleted()
    {
        var editor = PdfEditor.Open(Document(3));
        var other = PdfReader.Open(Document(2, (d, p) =>
        {
            foreach (var (page, i) in p.Select((page, i) => (page, i)))
            {
                page.Canvas.DrawText($"Other {i + 1}", StandardFont.Helvetica, 12, 72, 600);
            }
        }));

        editor.Pages.Insert(1, other);
        editor.Pages.Add(other, [1, 1]);
        editor.Pages.Move(0, 6);
        editor.Pages.Delete(1, 2);

        Assert.Equal(5, editor.Pages.Count);
        var texts = Texts(editor.ToArray());
        // Inserted: P1 O1 O2 P2 P3; added: ... O2 O2; P1 moved to the end; O2 and P2 deleted.
        Assert.Equal(["Page 1\n\nOther 1", "Page 3", "Page 2\n\nOther 2", "Page 2\n\nOther 2", "Page 1"], texts);
        Assert.Equal(5, editor.Pages.Count(p => p.Width > 590));
        AssertValid(editor.ToArray());
    }

    [Fact]
    public void Bookmarks_names_and_links_to_deleted_pages_are_dropped()
    {
        var source = Document(4, (document, pages) =>
        {
            // A chapter (closed) with entries for pages 2 and 3, and links from page 1 to every page by name and directly.
            var chapter = new PdfOutlineItem("Chapter", pages[1]) { IsOpen = false };
            chapter.Children.Add(new PdfOutlineItem("Section 2", pages[1]));
            chapter.Children.Add(new PdfOutlineItem("Section 3", pages[2]));
            document.Outline.Add(chapter);
            for (var i = 0; i < 4; i++)
            {
                pages[0].AddLink(72 + (20 * i), 100, 10, 10, $"page{i + 1}");
                pages[0].AddLink(72 + (20 * i), 150, 10, 10, new PdfDestination(pages[i]));
            }
        });
        var editor = PdfEditor.Open(source);

        editor.Pages.Delete(1);
        var reader = PdfReader.Open(editor.ToArray());

        Assert.Equal(["Page 1 -> 1", "Page 3 -> 2", "Page 4 -> 3", "Chapter (-1)", "  Section 3 -> 2", "[4]"], Titles(reader));
        var links = reader.Pages[0].Dictionary["Annots"] is { } annots ? ((PdfArray)reader.Resolve(annots)!).Items.Count : 0;
        Assert.Equal(6, links);
        var names = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Catalog["Names"])!)["Dests"])!;
        var keys = ((PdfArray)reader.Resolve(names["Names"])!).Items.Where((_, i) => i % 2 == 0).Select(k => RaiCore.Pdf.Reading.PdfText.Decode(((PdfString)k).Bytes));
        Assert.Equal(["page1", "page3", "page4"], keys);
        AssertValid(editor.ToArray());

        // The rest of the chapter goes too: it had no destination left and no entries.
        editor.Pages.Delete(1);
        Assert.Equal(["Page 1 -> 1", "Page 4 -> 2", "[2]"], Titles(PdfReader.Open(editor.ToArray())));
    }

    [Fact]
    public void Inserted_pages_bring_only_what_they_use()
    {
        var tagged = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /StructParents 0 /Annots [5 0 R] /Resources << /XObject << /Im1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im2 7 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /P 3 0 R /Contents (note) >>",
            TestPdf.Stream("x", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8"),
            TestPdf.Stream("y", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8"),
        ]);
        var editor = PdfEditor.Open(Document(1));

        // The first page, twice: the other page's image stays out, and each copy has a note of its own.
        editor.Pages.Add(PdfReader.Open(tagged), [0, 0]);
        var saved = editor.ToArray();
        var reader = PdfReader.Open(saved);

        Assert.Equal(2, ImageObjects(saved));
        var notes = reader.Pages.Skip(1).Select(p => (PdfReference)((PdfArray)reader.Resolve(p.Dictionary["Annots"])!).Items[0]).ToList();
        Assert.NotEqual(notes[0].ObjectNumber, notes[1].ObjectNumber);
        for (var i = 0; i < 2; i++)
        {
            var note = (PdfDictionary)reader.Resolve(notes[i])!;
            Assert.Equal(reader.Pages[i + 1].Reference!.ObjectNumber, ((PdfReference)note["P"]!).ObjectNumber);
            Assert.Null(reader.Pages[i + 1].Dictionary["StructParents"]);
        }
    }

    [Fact]
    public void Links_between_inserted_pages_keep_working()
    {
        var other = Document(3, (_, pages) =>
        {
            pages[0].AddLink(72, 100, 10, 10, "page2");
            pages[0].AddLink(72, 150, 10, 10, new PdfDestination(pages[2]));
        });
        var editor = PdfEditor.Open(Document(1));

        editor.Pages.Add(PdfReader.Open(other), [0, 1]);
        var reader = PdfReader.Open(editor.ToArray());

        var annots = (PdfArray)reader.Resolve(reader.Pages[1].Dictionary["Annots"])!;
        var link = Assert.Single(annots.Items);
        var destination = (PdfArray)reader.Resolve(((PdfDictionary)reader.Resolve(link)!)["Dest"])!;
        Assert.Equal(reader.Pages[2].Reference!.ObjectNumber, ((PdfReference)destination.Items[0]).ObjectNumber);
    }

    [Fact]
    public void Pages_turn_and_keep_attributes_inherited_from_the_page_tree()
    {
        // Both pages inherit their box, rotation and font from the page tree node.
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 6 0 R] /Count 3 /MediaBox [0 0 300 400] /Rotate 90 /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Rotate 0 >>",
            TestPdf.Stream("BT /F1 12 Tf 20 20 Td (Hello) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Rotate -90 >>",
        ]);
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(90, editor.Pages[0].Rotation);
        Assert.Equal(0, editor.Pages[1].Rotation);
        Assert.Equal(270, editor.Pages[2].Rotation);
        Assert.Equal(new PdfRectangle(0, 0, 300, 400), editor.Pages[0].MediaBox);
        Assert.Equal((400, 300), (editor.Pages[0].Width, editor.Pages[0].Height));
        editor.Pages[1].Rotation = -90;
        editor.Pages[0].Rotation = 360;
        Assert.Throws<ArgumentException>(() => editor.Pages[0].Rotation = 45);
        var reader = PdfReader.Open(editor.ToArray());

        Assert.Equal([0, 270, 270], reader.Pages.Select(p => p.Rotate));
        Assert.Equal(270L, ((PdfInteger)reader.Pages[1].Dictionary["Rotate"]!).Value); // Written normalized.
        Assert.All(reader.Pages, p => Assert.Equal(new PdfRectangle(0, 0, 300, 400), p.MediaBox));
        Assert.All(reader.Pages, p => Assert.NotNull(reader.Resolve(p.Dictionary["Resources"]))); // Inherited, written in.
        Assert.Equal(["Hello", "Hello", "Hello"], Texts(editor.ToArray()));
    }

    [Fact]
    public void Form_fields_on_deleted_pages_are_dropped()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 9 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 7 0 R 9 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [8 0 R 9 0 R] >>",
            "<< /FT /Tx /T (name) /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /P 3 0 R >>",
            "<< /FT /Btn /T (choice) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] >>",
            "<< /FT /Tx /T (shared) /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // Listed by both pages.
        ]);
        var editor = PdfEditor.Open(pdf);

        editor.Pages.Delete(0);
        var reader = PdfReader.Open(editor.ToArray());

        var fields = (PdfArray)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Catalog["AcroForm"])!)["Fields"])!;
        Assert.Equal(2, fields.Items.Count);
        var choice = (PdfDictionary)reader.Resolve(fields.Items[0])!;
        Assert.Single(((PdfArray)reader.Resolve(choice["Kids"])!).Items);

        editor.Pages.Delete(0, 0);
        editor.Pages.Insert(0, PdfReader.Open(Document(1)));
        editor.Pages.Delete(1);
        reader = PdfReader.Open(editor.ToArray());
        Assert.Empty(((PdfArray)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Catalog["AcroForm"])!)["Fields"])!).Items);
    }

    [Fact]
    public void Name_trees_and_old_style_destinations_lose_deleted_pages()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> /Dests << /old1 [3 0 R /Fit] /old2 [4 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Annots [8 0 R 9 0 R] >>",
            "<< /Kids [6 0 R 7 0 R] >>",
            "<< /Limits [(a) (b)] /Names [(a) [3 0 R /Fit] (b) [4 0 R /Fit]] >>",
            "<< /Limits [(c) (d)] /Names [(c) [3 0 R /Fit] (d) [3 0 R /Fit]] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest /old1 >>", // By name, to the deleted page.
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest /old2 >>",
        ]);
        var editor = PdfEditor.Open(pdf);

        editor.Pages.Delete(0);
        var reader = PdfReader.Open(editor.ToArray());

        var tree = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Catalog["Names"])!)["Dests"])!;
        var kid = (PdfDictionary)reader.Resolve(Assert.Single(((PdfArray)reader.Resolve(tree["Kids"])!).Items))!;
        string Text(PdfObject? o) => RaiCore.Pdf.Reading.PdfText.Decode(((PdfString)reader.Resolve(o)!).Bytes);
        Assert.Equal(["b", "b"], ((PdfArray)reader.Resolve(kid["Limits"])!).Items.Select(Text));
        Assert.Equal(["old2"], ((PdfDictionary)reader.Resolve(reader.Catalog["Dests"])!).Entries.Select(e => e.Key.Value));
        var link = (PdfDictionary)reader.Resolve(Assert.Single(((PdfArray)reader.Resolve(reader.Pages[0].Dictionary["Annots"])!).Items))!;
        Assert.Equal("old2", ((PdfName)link["Dest"]!).Value);
    }

    [Fact]
    public void The_file_has_the_newest_version_of_its_parts()
    {
        var newer = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
        ]);
        var older = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Version /1.3 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
        ]);
        var editor = PdfEditor.Open(Document(1));
        Assert.StartsWith("%PDF-1.7", TestPdf.Text(editor.ToArray()), StringComparison.Ordinal);

        editor.Pages.Add(PdfReader.Open(newer));
        editor.Pages.Add(PdfReader.Open(older));
        Assert.StartsWith("%PDF-2.0", TestPdf.Text(editor.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Page_labels_are_kept_until_they_no_longer_fit()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /r >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
        ]);
        var editor = PdfEditor.Open(pdf);

        editor.Pages.Add(PdfReader.Open(Document(1)));
        Assert.NotNull(PdfReader.Open(editor.ToArray()).Catalog["PageLabels"]);

        editor.Pages.Move(1, 0);
        Assert.Null(PdfReader.Open(editor.ToArray()).Catalog["PageLabels"]);
    }

    [Fact]
    public void References_to_deleted_pages_elsewhere_become_null()
    {
        // The structure tree points at both pages; the deleted one must not come back through it.
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im1 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /P /Pg 3 0 R /K [<< /Type /MCR /Pg 4 0 R /MCID 0 >>] >>",
            TestPdf.Stream("x", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8"),
        ]);
        var editor = PdfEditor.Open(pdf);

        editor.Pages.Delete(0);
        var saved = editor.ToArray();
        var reader = PdfReader.Open(saved);

        Assert.Single(reader.Pages);
        Assert.Equal(0, ImageObjects(saved));
        var element = (PdfDictionary)reader.Resolve(((PdfArray)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Catalog["StructTreeRoot"])!)["K"])!).Items[0])!;
        Assert.Null(element["Pg"]);
        AssertValid(saved);
    }

    [Fact]
    public void Files_written_page_by_page_and_encrypted_files_can_be_edited()
    {
        var document = new PdfDocument();
        using var streamed = new MemoryStream();
        document.BeginSave(streamed);
        for (var i = 0; i < 3; i++)
        {
            document.AddPage(PdfPageSize.A5).Canvas.DrawText($"Streamed {i + 1}", StandardFont.Helvetica, 12, 50, 300);
            document.FlushPages();
        }

        document.EndSave();
        var editor = PdfEditor.Open(streamed.ToArray());
        editor.Pages.Move(2, 0);
        Assert.Equal(["Streamed 3", "Streamed 1", "Streamed 2"], Texts(editor.ToArray()));

        var secret = new PdfDocument { Encryption = new PdfEncryption { UserPassword = "user", OwnerPassword = "owner" } };
        secret.AddPage(PdfPageSize.A4).Canvas.DrawText("Secret", StandardFont.Helvetica, 12, 72, 700);
        var opened = PdfEditor.Open(secret.ToArray(), "user");
        var saved = opened.ToArray();
        Assert.False(PdfReader.Open(saved).IsEncrypted);
        Assert.Equal(["Secret"], Texts(saved));
    }

    [Fact]
    public async Task Saves_go_to_files_streams_and_async_streams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raicore-editor-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Document(2));
            var editor = PdfEditor.Open(path);
            editor.Pages.Delete(1);
            editor.Save(path); // Over the file that was opened.
            Assert.Equal(["Page 1"], Texts(File.ReadAllBytes(path)));

            using var stream = new MemoryStream();
            await editor.SaveAsync(stream, TestContext.Current.CancellationToken);
            Assert.Equal(File.ReadAllBytes(path), stream.ToArray());
            using var sync = new MemoryStream();
            editor.Save(sync);
            Assert.Equal(stream.ToArray(), sync.ToArray());
            using (var file = File.OpenRead(path))
            {
                Assert.Single(PdfEditor.Open(file).Pages);
            }

            Assert.Single(PdfEditor.Open(PdfReader.Open(path)).Pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Saved_files_pass_qpdf()
    {
        var editor = PdfEditor.Open(Document(3));
        editor.Pages.Delete(1);
        editor.Pages.Add(PdfReader.Open(Document(1)));

        ExternalTools.AssertQpdfClean(editor.ToArray());
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var editor = PdfEditor.Open(Document(2));
        var other = PdfReader.Open(Document(1));

        Assert.Throws<ArgumentNullException>(() => PdfEditor.Open((PdfReader)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Pages[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Pages.Delete(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Pages.Delete(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Pages.Move(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Pages.Insert(3, other));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Pages.Insert(0, other, [1]));
        Assert.Throws<ArgumentNullException>(() => editor.Pages.Insert(0, null!));
        Assert.Throws<ArgumentNullException>(() => editor.Save((Stream)null!));
        Assert.Equal(2, editor.Pages.Count);
        Assert.Equal(2, editor.Pages.ToList().Count);
    }
}
