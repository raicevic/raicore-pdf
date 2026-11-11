using System.Diagnostics;
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Tests.Reading;

/// <summary>
/// Malformed input: the reader must neither hang nor crash. It either reads what it can or throws
/// <see cref="PdfFormatException"/> (or <see cref="NotSupportedException"/> for encryption). Not a fuzzer (that is a
/// separate issue), but a seeded smoke test of a few thousand damaged files runs too.
/// </summary>
public class PdfReaderRobustnessTests
{
    [Theory]
    [InlineData("")]
    [InlineData("%PDF-1.7")]
    [InlineData("%PDF-1.7\n%%EOF")]
    [InlineData("Hello, this is not a PDF file at all.")]
    [InlineData("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>")] // No pages.
    [InlineData("%PDF-1.4\n1 0 obj << /Type /Pages >> endobj\nstartxref\n999999\n%%EOF")] // No catalog.
    public void Garbage_is_rejected_with_a_clear_exception(string text)
    {
        var exception = Assert.Throws<PdfFormatException>(() => PdfReader.Open(Encoding.Latin1.GetBytes(text)));
        Assert.False(string.IsNullOrEmpty(exception.Message));
    }

    [Fact]
    public void Random_bytes_are_rejected()
    {
        var random = new Random(45);
        for (var i = 0; i < 50; i++)
        {
            var bytes = new byte[random.Next(0, 4000)];
            random.NextBytes(bytes);
            Assert.Throws<PdfFormatException>(() => PdfReader.Open(bytes));
        }
    }

    [Fact]
    public void Reference_loops_resolve_to_nothing()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Loop 4 0 R /Self 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 2 0 R 3 0 R] /Count 1 >>", // The tree contains itself, and a page twice.
            "<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>",
            "5 0 R",
            "4 0 R",
            "<< /Me 6 0 R >>",
            "<< /Length 7 0 R >>\nstream\nq Q\nendstream", // A stream whose length is itself.
        ]);

        var reader = PdfReader.Open(pdf);

        Assert.Single(reader.Pages);
        Assert.Null(reader.Resolve(reader.Catalog["Loop"]));
        var self = (PdfDictionary)reader.Resolve(reader.Catalog["Self"])!;
        Assert.Same(self, reader.Resolve(self["Me"]));
        Assert.Equal("q Q", TestPdf.Text(reader.Pages[0].GetContent()));

        // Importing copies the self-reference once and saves.
        var doc = new PdfDocument();
        doc.ImportPages(reader);
        Assert.Single(PdfReader.Open(doc.ToArray()).Pages);
    }

    [Fact]
    public void A_prev_loop_ends()
    {
        var pdf = TestPdf.Text(TestPdf.Pages(string.Empty, ("", "q Q")));
        var xref = pdf.IndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        pdf = pdf.Replace("/Root 1 0 R", string.Create(CultureInfo.InvariantCulture, $"/Root 1 0 R /Prev {xref}"), StringComparison.Ordinal);

        var reader = PdfReader.Open(Encoding.Latin1.GetBytes(pdf));

        Assert.False(reader.IsRepaired);
        Assert.Single(reader.Pages);
    }

    [Fact]
    public void Deep_nesting_is_limited()
    {
        var deep = new string('[', 100_000) + new string(']', 100_000);
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Deep 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R >>",
            deep,
        ]);

        var reader = PdfReader.Open(pdf);

        Assert.Single(reader.Pages);
        Assert.Null(reader.Resolve(reader.Catalog["Deep"])); // Too deep to parse: the object is unusable, the rest is fine.
    }

    [Fact]
    public void Deep_nesting_in_the_catalog_is_a_format_error()
    {
        var deep = new string('<', 50_000).Replace("<<", "<< /A ", StringComparison.Ordinal);
        var pdf = TestPdf.Build(["<< /Type /Catalog /Pages 2 0 R /X " + deep + " >>", "<< /Type /Pages /Kids [] /Count 0 >>"]);

        Assert.Throws<PdfFormatException>(() => PdfReader.Open(pdf));
    }

    [Fact]
    public void Lying_lengths_and_counts_are_survived()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Stm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 999999999 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            "<< /Length 99999999 >>\nstream\n1 0 0 rg\nendstream",
            "<< /Type /ObjStm /N 2000000000 /First 99999 /Length 3 >>\nstream\n1 0\nendstream",
            "<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>",
            "<< /Length 3 >>\nstream\n0 0 1 rg 5 5 10 10 re f\nendstream", // Too short: endstream does not follow.
        ]);

        var reader = PdfReader.Open(pdf);

        Assert.Equal("1 0 0 rg", TestPdf.Text(reader.Pages[0].GetContent()));
        Assert.Equal("0 0 1 rg 5 5 10 10 re f", TestPdf.Text(reader.Pages[1].GetContent()));
    }

    [Fact]
    public void A_huge_cross_reference_subsection_triggers_the_repair()
    {
        var pdf = TestPdf.Text(TestPdf.Pages(string.Empty, ("", "q Q")));
        pdf = pdf.Replace("xref\n0 5\n", "xref\n0 2147483000\n", StringComparison.Ordinal);

        var reader = PdfReader.Open(Encoding.Latin1.GetBytes(pdf));

        Assert.True(reader.IsRepaired);
        Assert.Single(reader.Pages);
    }

    /// <summary>
    /// Damages valid files a few thousand ways (flipped bytes, cut files, duplicated and deleted ranges) and reads,
    /// imports and saves each one: only <see cref="PdfFormatException"/> and <see cref="NotSupportedException"/> may
    /// come out, and no file may take long.
    /// </summary>
    [Fact]
    public void Damaged_files_never_crash_or_hang()
    {
        var sources = new[]
        {
            LibraryFile(),
            Fixtures.Read("Reader/xref-stream.pdf"),
            Fixtures.Read("Reader/incremental.pdf"),
            Fixtures.Read("Reader/broken-xref.pdf"),
        };

        var random = new Random(53);
        var opened = 0;
        var saved = 0;
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 2000; i++)
        {
            var source = sources[i % sources.Length];
            var damaged = Damage(source, random);
            var watch = Stopwatch.StartNew();
            try
            {
                var reader = PdfReader.Open(damaged);
                opened++;
                foreach (var page in reader.Pages)
                {
                    _ = page.Resources;
                    try
                    {
                        _ = page.GetContent();
                    }
                    catch (PdfFormatException)
                    {
                    }
                }

                var doc = new PdfDocument();
                doc.ImportPages(reader, new PdfImportOptions { KeepOutline = true });
                doc.AddPage().Canvas.DrawPage(reader.Pages[0], 0, 0);
                _ = doc.ToArray();
                saved++;
            }
            catch (Exception e) when (e is PdfFormatException or NotSupportedException)
            {
            }
            catch (Exception e)
            {
                Assert.Fail($"Damaged file {i} (seed 53) threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }

            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
        }

        Assert.True(opened > 500 && saved > 500, $"Only {opened} damaged files could be opened, and {saved} imported and saved.");
        Assert.True(slowest < TimeSpan.FromSeconds(5), $"The slowest damaged file took {slowest}.");
    }

    private static byte[] LibraryFile()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A5);
        page.Canvas.DrawText("Damaged", PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 20, 40, 500);
        var second = doc.AddPage(PdfPageSize.A5);
        page.AddLink(40, 490, 100, 30, new PdfDestination(second, 300));
        doc.Outline.Add(new PdfOutlineItem("Second", second, 300));
        return doc.ToArray();
    }

    private static byte[] Damage(byte[] source, Random random)
    {
        var data = new List<byte>(source);
        switch (random.Next(5))
        {
            case 0: // Flip a few bytes.
                for (var n = random.Next(1, 8); n > 0; n--)
                {
                    data[random.Next(data.Count)] = (byte)random.Next(256);
                }

                break;
            case 1: // Cut the file short.
                var cut = random.Next(data.Count);
                data.RemoveRange(cut, data.Count - cut);
                break;
            case 2: // Delete a range.
                var start = random.Next(data.Count);
                data.RemoveRange(start, Math.Min(data.Count - start, random.Next(1, 200)));
                break;
            case 3: // Duplicate a range.
                var from = random.Next(data.Count);
                data.InsertRange(random.Next(data.Count), data.GetRange(from, Math.Min(data.Count - from, random.Next(1, 200))));
                break;
            default: // Replace digits, which hits offsets, lengths, counts and object numbers.
                for (var n = random.Next(1, 6); n > 0; n--)
                {
                    var at = random.Next(data.Count);
                    if (data[at] is >= (byte)'0' and <= (byte)'9')
                    {
                        data[at] = (byte)('0' + random.Next(10));
                    }
                }

                break;
        }

        return [.. data];
    }
}
