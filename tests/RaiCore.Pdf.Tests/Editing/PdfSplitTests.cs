using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Fonts;
using static RaiCore.Pdf.Tests.Editing.PdfEditorTests;

namespace RaiCore.Pdf.Tests.Editing;

public class PdfSplitTests
{
    /// <summary>The size of the file with the pages <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> - 1 of <paramref name="pdf"/>.</summary>
    private static long SizeOf(byte[] pdf, int first, int count)
    {
        var editor = PdfEditor.Open(pdf);
        editor.Pages.Delete(first + count, editor.Pages.Count - first - count);
        editor.Pages.Delete(0, first);
        return editor.ToArray().Length;
    }

    [Fact]
    public void Parts_stay_below_the_limit_hold_as_many_pages_as_fit_and_add_up_to_the_document()
    {
        var pdf = Document(12); // Every page has an image of its own (64 x 64 noise, about 12 KB).
        var limit = SizeOf(pdf, 0, 3) + 100;
        var editor = PdfEditor.Open(pdf);

        var parts = editor.SplitBySize(limit);

        Assert.True(parts.Count is >= 3 and <= 6, $"{parts.Count} parts");
        Assert.Equal(Enumerable.Range(0, parts.Count).Select(i => parts.Take(i).Sum(p => p.PageCount)), parts.Select(p => p.FirstPageIndex));
        Assert.Equal(Texts(pdf), parts.SelectMany(p => Texts(p.Data.ToArray())));
        foreach (var part in parts)
        {
            Assert.False(part.ExceedsLimit);
            Assert.InRange(part.Length, 1, limit);
            Assert.Equal(part.PageCount, ImageObjects(part.Data.ToArray()));
            AssertValid(part.Data.ToArray());
            if (part.FirstPageIndex + part.PageCount < 12)
            {
                // One more page would not have fitted.
                Assert.True(SizeOf(pdf, part.FirstPageIndex, part.PageCount + 1) > limit, part.ToString());
            }
        }
    }

    [Fact]
    public void Shared_images_and_fonts_count_once_per_part()
    {
        var pdf = Document(1, (document, _) =>
        {
            var logo = Noise(new Random(7));
            document.Pages[0].Canvas.DrawImage(logo, 300, 400, 50, 50);
            for (var i = 2; i <= 10; i++)
            {
                var page = document.AddPage(PdfPageSize.A4);
                page.Canvas.DrawText($"Page {i}", StandardFont.Helvetica, 12, 72, 700).DrawImage(logo, 300, 400, 50, 50);
            }
        });
        var single = SizeOf(pdf, 1, 1);

        var parts = PdfEditor.Open(pdf).SplitBySize(single * 2);

        // A page adds its text but not the logo again: most pages fit in one part.
        Assert.True(parts.Max(p => p.PageCount) >= 8, string.Join("; ", parts));
    }

    [Fact]
    public void A_page_larger_than_the_limit_is_a_part_of_its_own()
    {
        var pdf = Document(1, (document, _) =>
        {
            for (var i = 2; i <= 5; i++)
            {
                var page = document.AddPage(PdfPageSize.A4);
                page.Canvas.DrawText($"Page {i}", StandardFont.Helvetica, 12, 72, 700);
                if (i == 3)
                {
                    for (var j = 0; j < 8; j++)
                    {
                        page.Canvas.DrawImage(Noise(new Random(j)), 20 + (60 * j), 100, 50, 50); // About 100 KB.
                    }
                }
            }
        });
        var limit = SizeOf(pdf, 0, 2) + 100;

        var parts = PdfEditor.Open(pdf).SplitBySize(limit);

        Assert.Equal(["1-2", "3-3 over", "4-5"], parts.Select(p => $"{p.FirstPageIndex + 1}-{p.FirstPageIndex + p.PageCount}{(p.ExceedsLimit ? " over" : "")}"));
        Assert.True(parts[1].Length > limit);
        Assert.Equal(["Page 3"], Texts(parts[1].Data.ToArray()));
    }

    [Fact]
    public void Splitting_leaves_the_document_as_it_was_and_parts_keep_their_own_bookmarks()
    {
        // A chapter on page 1 with a section on page 3: writing the part with page 3 drops the chapter's destination
        // there, which must not change the document.
        var pdf = Document(6, (document, pages) =>
        {
            var chapter = new PdfOutlineItem("Chapter", pages[0]);
            chapter.Children.Add(new PdfOutlineItem("Section", pages[2]));
            document.Outline.Add(chapter);
        });
        var editor = PdfEditor.Open(pdf);
        editor.Pages.Delete(5);
        var before = editor.ToArray();

        var parts = editor.SplitBySize(SizeOf(pdf, 0, 2) + 100);

        Assert.Equal(before, editor.ToArray());
        Assert.Equal(["Page 1 -> 1", "Page 2 -> 2", "Page 3 -> 3", "Page 4 -> 4", "Page 5 -> 5", "Chapter (1) -> 1", "  Section -> 3", "[7]"], Titles(PdfReader.Open(before)));
        var third = parts.Single(p => p.FirstPageIndex <= 2 && p.FirstPageIndex + p.PageCount > 2);
        Assert.NotEqual(0, third.FirstPageIndex);
        var titles = Titles(PdfReader.Open(third.Data.ToArray()));
        Assert.Contains("Page 3 -> " + (3 - third.FirstPageIndex), titles);
        Assert.Contains("Chapter (1)", titles); // Its page is in another part: no destination, but its section stays.
        Assert.Contains("  Section -> " + (3 - third.FirstPageIndex), titles);
        Assert.DoesNotContain("Page 1 -> 1", titles);
        Assert.Equal(5, parts.Sum(p => p.PageCount));
    }

    [Fact]
    public void Parts_are_saved_and_described()
    {
        var part = Assert.Single(PdfEditor.Open(Document(2)).SplitBySize(long.MaxValue));

        Assert.Equal((0, 2, false), (part.FirstPageIndex, part.PageCount, part.ExceedsLimit));
        using var stream = new MemoryStream();
        part.Save(stream);
        Assert.Equal(part.Data.ToArray(), stream.ToArray());
        var path = Path.Combine(Path.GetTempPath(), $"raicore-split-{Guid.NewGuid():N}.pdf");
        try
        {
            part.Save(path);
            Assert.Equal(part.Data.ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal($"Pages 1-2, {part.Length} bytes", part.ToString());
        var over = PdfEditor.Open(Document(1)).SplitBySize(10)[0];
        Assert.Equal($"Pages 1-1, {over.Length} bytes (over the limit)", over.ToString());
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var editor = PdfEditor.Open(Document(2));

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SplitBySize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SplitBySize(-1));
        Assert.Throws<ArgumentNullException>(() => editor.SplitBySize(long.MaxValue)[0].Save((Stream)null!));
        editor.Pages.Delete(0, 2);
        Assert.Throws<InvalidOperationException>(() => editor.SplitBySize(1000));
    }
}
