using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Tests.Reading;
using static RaiCore.Pdf.Tests.Editing.PdfEditorTests;

namespace RaiCore.Pdf.Tests.Editing;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PdfOptimizeTests
{
    private static void AssertLooksTheSame(byte[] before, byte[] after)
    {
        var (a, b) = (PdfReader.Open(before), PdfReader.Open(after));
        Assert.Equal(a.Pages.Count, b.Pages.Count);
        for (var i = 0; i < a.Pages.Count; i++)
        {
            using var expected = Pixels.Render(before, i);
            using var actual = Pixels.Render(after, i);
            Assert.Equal(0, Pixels.CountDifferences(expected, actual, tolerance: 0));
        }
    }

    private static PdfDictionary Resources(PdfReader reader, int page) => (PdfDictionary)reader.Resolve(reader.Pages[page].Dictionary["Resources"])!;

    private static string[] Names(PdfReader reader, PdfDictionary resources, string category) =>
        reader.Resolve(resources[category]) is PdfDictionary names ? [.. names.Entries.Select(e => e.Key.Value).Order()] : [];

    [Fact]
    public void A_merge_of_copies_shrinks_and_looks_the_same()
    {
        var source = Document(2);
        var editor = PdfEditor.Open(source);
        for (var i = 0; i < 4; i++)
        {
            editor.Pages.Add(PdfReader.Open(source)); // Each file brings its own copy of the fonts and images.
        }

        var before = editor.ToArray();
        var result = editor.Optimize();
        var after = editor.ToArray();

        Assert.Equal(10, ImageObjects(before));
        Assert.Equal(2, ImageObjects(after));
        Assert.True(result.ObjectsMerged >= 8, result.ToString());
        Assert.True(after.Length < before.Length / 3, $"{before.Length} -> {after.Length} bytes");
        Assert.Equal(Texts(before), Texts(after));
        AssertLooksTheSame(before, after);
        AssertValid(after);
    }

    [Fact]
    public void Resources_no_content_uses_are_removed()
    {
        var image = TestPdf.Stream(new string('x', 4000), "/Type /XObject /Subtype /Image /Width 40 /Height 100 /ColorSpace /DeviceGray /BitsPerComponent 8");
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            // Page 3 has its own resources; pages 4 and 5 share the inherited ones (an object of their own), and use
            // different fonts of them.
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 300 400] /Resources 12 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Resources << /Font << /F1 {font} /F2 {font} >> /XObject << /Im1 7 0 R /Im2 8 0 R /Fm1 9 0 R >> /ExtGState << /GS1 << /ca 0.5 >> /GS2 << /CA 0.5 >> >> /ColorSpace << /CS1 [/Indexed /DeviceRGB 0 <ff0000>] /CS2 /DeviceRGB /CS3 /DeviceRGB >> /Properties << /P1 << /Foo 1 >> /P2 << /Foo 2 >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 10 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 11 0 R >>",
            TestPdf.Stream("q /GS1 gs /Span /P1 BDC 40 0 0 100 10 10 cm /Im1 Do EMC Q /Fm1 Do BI /W 1 /H 1 /CS /CS1 /BPC 8 ID \u0000 EI /CS3 cs"),
            image,
            image,
            // A form without resources of its own uses the page's (font F2).
            TestPdf.Stream("BT /F2 12 Tf 10 300 Td (Form) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 300 400]"),
            TestPdf.Stream("BT /F1 12 Tf 10 300 Td (Four) Tj ET"),
            TestPdf.Stream("BT /F2 12 Tf 10 300 Td (Five) Tj ET"),
            $"<< /Font << /F1 {font} /F2 {font} /F9 {font} >> >>",
        ]);
        var editor = PdfEditor.Open(pdf);
        var before = editor.ToArray();

        var result = editor.Optimize(new PdfOptimizationOptions { MergeDuplicates = false });
        var after = editor.ToArray();
        var reader = PdfReader.Open(after);

        var first = Resources(reader, 0);
        Assert.Equal(["F2"], Names(reader, first, "Font"));
        Assert.Equal(["Fm1", "Im1"], Names(reader, first, "XObject"));
        Assert.Equal(["GS1"], Names(reader, first, "ExtGState"));
        Assert.Equal(["CS1", "CS3"], Names(reader, first, "ColorSpace"));
        Assert.Equal(["P1"], Names(reader, first, "Properties"));
        Assert.Equal(["F1", "F2"], Names(reader, Resources(reader, 1), "Font"));
        Assert.Same(Resources(reader, 1), Resources(reader, 2));
        Assert.Equal(6, result.ResourcesRemoved); // F1 of page 3, Im2, GS2, CS2, P2, and the shared F9.
        Assert.Equal(1, ImageObjects(after));
        Assert.Equal(["Form", "Four", "Five"], Texts(after));
        AssertLooksTheSame(before, after);
    }

    [Fact]
    public void Resources_shared_with_something_unread_are_kept_whole()
    {
        // Page 1's resources are also an annotation's (not read). Pages 2 and 3 share their font dictionary but have
        // resources of their own; a dictionary page 2 has as XObjects (none used) is an annotation's fonts on page 3.
        var helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R 8 0 R] /Count 3 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources 5 0 R /Annots [<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Resources 5 0 R >>] >>",
            TestPdf.Stream("BT /F1 12 Tf 10 300 Td (One) Tj ET"),
            $"<< /Font << /F1 {helvetica} /F2 {helvetica} >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font 10 0 R /XObject 11 0 R >> >>",
            TestPdf.Stream("BT /F2 12 Tf 10 300 Td (Three) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /Contents 7 0 R /Resources << /Font 10 0 R >> /Annots [<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Resources << /Font 11 0 R >> >>] >>",
            "null",
            $"<< /F1 {helvetica} /F2 {helvetica} /F3 {helvetica} >>",
            $"<< /F1 {helvetica} /F2 {helvetica} >>",
        ]);
        var editor = PdfEditor.Open(pdf);

        var result = editor.Optimize();
        var reader = PdfReader.Open(editor.ToArray());

        Assert.Equal(["F1", "F2"], Names(reader, Resources(reader, 0), "Font"));
        Assert.Equal(["F1", "F2"], Names(reader, Resources(reader, 1), "Font")); // F1 for page 2, F2 for page 3.
        Assert.Equal(["F1", "F2"], Names(reader, Resources(reader, 1), "XObject")); // Also the annotation's fonts.
        Assert.Equal(1, result.ResourcesRemoved); // Only F3.
    }

    /// <summary>
    /// Checks a file's cross-reference stream entry by entry: each object's entry leads to it, in the file or in an object
    /// stream, and the stream's own entry to itself (the core's reader would repair a wrong one silently).
    /// </summary>
    private static void AssertCrossReferenceStream(byte[] pdf)
    {
        var text = TestPdf.Text(pdf);
        var start = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        var reader = PdfReader.Open(pdf);
        var xrefNumber = int.Parse(text[start..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        var xref = (PdfStreamObject)reader.Store.GetObject(xrefNumber)!;
        var widths = ((PdfArray)xref.Dictionary["W"]!).Items.Select(i => (int)((PdfInteger)i).Value).ToArray();
        var rows = reader.Store.Decode(xref);
        var size = (int)((PdfInteger)xref.Dictionary["Size"]!).Value;
        Assert.Equal(size * widths.Sum(), rows.Length);
        long Field(int row, int field)
        {
            var at = (row * widths.Sum()) + widths.Take(field).Sum();
            return rows.AsSpan(at, widths[field]).ToArray().Aggregate(0L, (value, b) => (value << 8) | b);
        }

        Assert.Equal((0L, 0xFFFFL), (Field(0, 0), Field(0, 2)));
        Assert.Equal((1L, (long)start), (Field(xrefNumber, 0), Field(xrefNumber, 1)));
        for (var number = 1; number < size; number++)
        {
            if (Field(number, 0) == 1)
            {
                Assert.StartsWith($"{number} 0 obj", text[(int)Field(number, 1)..], StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(2, Field(number, 0));
                var objects = (PdfStreamObject)reader.Store.GetObject((int)Field(number, 1))!;
                var header = System.Text.Encoding.ASCII.GetString(reader.Store.Decode(objects)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(number.ToString(System.Globalization.CultureInfo.InvariantCulture), header[2 * (int)Field(number, 2)]);
            }
        }
    }

    [Fact]
    public void Fonts_merge_once_their_embedded_programs_have()
    {
        var font = RaiCore.Pdf.Fonts.PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawText("Embedded", font, 14, 72, 700);
        var source = document.ToArray();
        var editor = PdfEditor.Open(source);
        editor.Pages.Add(PdfReader.Open(source));
        editor.Pages.Add(PdfReader.Open(source));

        editor.Optimize();
        var saved = editor.ToArray();

        // The font file, its descriptor, the CID font, its widths' owner and the composite font each merge to one, in rounds.
        var reader = PdfReader.Open(saved);
        var fonts = reader.Store.ObjectNumbers.Select(reader.Store.GetObject).OfType<PdfDictionary>().Count(d => d["Type"] is PdfName { Value: "Font" });
        var programs = reader.Store.ObjectNumbers.Select(reader.Store.GetObject).OfType<PdfDictionary>().Count(d => d["FontFile2"] is not null);
        Assert.Equal((2, 1), (fonts, programs));
        Assert.Equal(["Embedded", "Embedded", "Embedded"], Texts(saved));
        AssertCrossReferenceStream(saved);
    }

    [Fact]
    public void Uncompressed_streams_are_compressed_and_written_in_object_streams()
    {
        var text = string.Concat(Enumerable.Repeat("BT /F1 12 Tf 10 300 Td (Hello) Tj ET ", 50)) + " q 100 0 0 100 0 0 cm /Im1 Do Q";
        var noise = new byte[300];
        new Random(3).NextBytes(noise);
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /XObject << /Im1 6 0 R >> >> >>",
            TestPdf.Stream(text),
            TestPdf.Stream(new string(' ', 2000), "/Type /Metadata /Subtype /XML"),
            TestPdf.Stream(System.Text.Encoding.Latin1.GetString(noise), "/Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8"), // Does not compress.
        ]).AsSpan().ToArray();
        var before = PdfEditor.Open(pdf).ToArray();
        var editor = PdfEditor.Open(pdf);
        Assert.False(editor.UseObjectStreams);

        var result = editor.Optimize();
        var after = editor.ToArray();

        Assert.Equal(1, result.StreamsCompressed);
        Assert.True(editor.UseObjectStreams);
        var file = TestPdf.Text(after);
        Assert.StartsWith("%PDF-1.7", file, StringComparison.Ordinal);
        Assert.Contains("/Type /ObjStm", file, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", file, StringComparison.Ordinal);
        Assert.DoesNotContain("\nxref\n", file, StringComparison.Ordinal);
        Assert.DoesNotContain("trailer", file, StringComparison.Ordinal);
        var reader = PdfReader.Open(after);
        Assert.False(reader.IsRepaired);
        var metadata = (PdfStreamObject)reader.Resolve(reader.Catalog["Metadata"])!;
        Assert.Null(metadata.Dictionary["Filter"]);
        Assert.Equal(Texts(before), Texts(after));
        Assert.True(before.Length - after.Length > 1500, $"{before.Length} -> {after.Length} bytes"); // The 1,900-byte content shrinks; the metadata does not.
        AssertLooksTheSame(before, after);
        AssertCrossReferenceStream(after);
        AssertValid(after);
    }

    [Fact]
    public void Object_streams_raise_old_files_to_pdf_1_5_and_can_be_chosen_alone()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /Version /1.3 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
        ]);
        var editor = PdfEditor.Open(TestPdf.Text(pdf).Replace("%PDF-1.7", "%PDF-1.3", StringComparison.Ordinal).Select(c => (byte)c).ToArray());
        Assert.StartsWith("%PDF-1.3", TestPdf.Text(editor.ToArray()), StringComparison.Ordinal);

        editor.UseObjectStreams = true;
        var saved = editor.ToArray();

        Assert.StartsWith("%PDF-1.5", TestPdf.Text(saved), StringComparison.Ordinal);
        Assert.Single(PdfReader.Open(saved).Pages);
        AssertCrossReferenceStream(saved);
        AssertValid(saved);
    }

    [Fact]
    public void Options_choose_what_is_done()
    {
        var source = Document(1);
        var editor = PdfEditor.Open(TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F9 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>", // Unused.
        ]));
        editor.Pages.Add(PdfReader.Open(source));
        editor.Pages.Add(PdfReader.Open(source));
        var before = editor.ToArray();

        var nothing = editor.Optimize(new PdfOptimizationOptions
        {
            RemoveUnusedResources = false,
            MergeDuplicates = false,
            CompressStreams = false,
            UseObjectStreams = false,
        });

        Assert.Equal(new PdfOptimizationResult(0, 0, 0), nothing);
        Assert.False(editor.UseObjectStreams);
        Assert.Equal(before, editor.ToArray());
        Assert.True(PdfOptimizationOptions.Default.MergeDuplicates);
        Assert.Throws<InvalidOperationException>(() =>
        {
            editor.Pages.Delete(0, editor.Pages.Count);
            editor.Optimize();
        });
    }
}
