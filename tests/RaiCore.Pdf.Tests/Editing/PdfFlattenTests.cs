using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Tests.Reading;
using static RaiCore.Pdf.Tests.Editing.PdfEditorTests;

namespace RaiCore.Pdf.Tests.Editing;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PdfFlattenTests
{
    /// <summary>
    /// How many pixels differ between the original as a viewer shows it (fields drawn) and the flattened file without
    /// annotations, both ways, beyond anti-aliasing: PDFium draws text of form fields and of page content with slightly
    /// different edges.
    /// </summary>
    private static int[] Differences(byte[] before, byte[] after, bool formFill = true, bool afterWithAnnotations = false)
    {
        var count = PdfReader.Open(before).Pages.Count;
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            using var expected = Pixels.RenderWithAnnotations(before, i, 100, formFill);
            using var actual = afterWithAnnotations ? Pixels.RenderWithAnnotations(after, i, 100) : Pixels.Render(after, i, 100);
            result[i] = Pixels.CountDifferencesBeyondEdges(expected, actual) + Pixels.CountDifferencesBeyondEdges(actual, expected);
        }

        return result;
    }

    [Theory]
    [InlineData("pymupdf-form.pdf")]
    [InlineData("reportlab-form.pdf")]
    public void Fields_look_the_same_after_flattening_and_are_gone(string fixture)
    {
        var before = Fixtures.Read("Forms/" + fixture);
        var editor = PdfEditor.Open(before);
        var fields = editor.GetForm().Fields.Sum(f => f.Widgets.Count);

        var result = editor.FlattenForm();
        var after = editor.ToArray();

        Assert.Equal(fields, result.Drawn + result.Removed);
        Assert.Equal(0, result.Generated);
        Assert.All(Differences(before, after), d => Assert.Equal(0, d));
        var form = PdfEditor.Open(after).GetForm();
        Assert.False(form.HasAcroForm);
        Assert.Empty(form.Fields);
        Assert.All(PdfReader.Open(after).Pages, p => Assert.Null(p.Dictionary["Annots"]));
        AssertValid(after);
    }

    /// <summary>The decoded content of the appearances drawn into the first page.</summary>
    private static string[] AppearanceStreams(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        var forms = (PdfDictionary)reader.Resolve(((PdfDictionary)reader.Resolve(reader.Pages[0].Dictionary["Resources"])!)["XObject"])!;
        return [.. forms.Entries.Where(e => e.Key.Value.StartsWith("Flat", StringComparison.Ordinal))
            .Select(e => System.Text.Encoding.Latin1.GetString(Filters.StreamDecoder.Decode((PdfStreamObject)reader.Resolve(e.Value)!, reader.Resolve)))];
    }

    /// <summary>A one-page form (300 x 400) with the catalog's form entries, the page's content and annotations, and more objects from 5.</summary>
    private static byte[] FormPdf(string formEntries, string annots, string content = "", params string[] objects) => TestPdf.Build([
        $"<< /Type /Catalog /Pages 2 0 R /AcroForm << {formEntries} >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
        $"<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [{annots}] >>",
        TestPdf.Stream(content),
        .. objects,
    ]);

    private const string Helv = "/DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >> /DA (/Helv 0 Tf 0 g)";

    [Fact]
    public void Appearances_are_made_where_there_are_none_or_the_form_asks_for_new_ones()
    {
        var pdf = FormPdf(
            $"/Fields [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R 16 0 R 17 0 R 18 0 R] /NeedAppearances true {Helv}",
            "5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R 16 0 R 17 0 R 18 0 R",
            "",
            "<< /FT /Tx /T (centered) /V (Middle) /Q 1 /DA (/Helv 12 Tf 0 0 1 rg) /Type /Annot /Subtype /Widget /Rect [10 360 290 380] /MK << /BG [0.9] /BC [0 0 0] >> >>",
            "<< /FT /Tx /T (notes) /Ff 4096 /V (one two three four five six seven eight nine ten eleven twelve) /DA (/Helv 10 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 300 110 350] >>",
            "<< /FT /Tx /T (code) /Ff 16777216 /MaxLen 4 /V (AB12) /Type /Annot /Subtype /Widget /Rect [10 270 90 290] >>",
            "<< /FT /Tx /T (pin) /Ff 8192 /V (1234) /Type /Annot /Subtype /Widget /Rect [10 240 90 260] >>",
            "<< /FT /Ch /T (country) /Ff 131072 /V (Norway) /Opt [(Denmark) (Norway)] /Type /Annot /Subtype /Widget /Rect [10 210 150 230] >>",
            "<< /FT /Ch /T (colours) /Ff 2097152 /V [(r) (b)] /Opt [[(r) (Red)] [(g) (Green)] [(b) (Blue)]] /DA (/Helv 10 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 130 110 200] >>",
            "<< /FT /Btn /T (agree) /V /Yes /AS /Yes /Type /Annot /Subtype /Widget /Rect [10 100 25 115] >>",
            "<< /FT /Btn /T (size) /Ff 32768 /V /L /AS /L /Type /Annot /Subtype /Widget /Rect [40 100 55 115] >>",
            // An appearance made by the producer: kept for buttons even with /NeedAppearances.
            "<< /FT /Btn /T (made) /V /Yes /AS /Yes /Type /Annot /Subtype /Widget /Rect [70 100 85 115] /AP << /N << /Yes 14 0 R /Off 15 0 R >> >> >>",
            TestPdf.Stream("0 0 1 rg 0 0 15 15 re f", "/Type /XObject /Subtype /Form /BBox [0 0 15 15]"),
            TestPdf.Stream("", "/Type /XObject /Subtype /Form /BBox [0 0 15 15]"),
            "<< /FT /Btn /T (off) /V /Off /AS /Off /Type /Annot /Subtype /Widget /Rect [100 100 115 115] >>",
            // A push button without an appearance has nothing to show.
            "<< /FT /Btn /T (go) /Ff 65536 /Type /Annot /Subtype /Widget /Rect [130 100 145 115] >>",
            // A text field's appearance is made again when the form asks for new ones.
            "<< /FT /Tx /T (stale) /V (Fresh) /Type /Annot /Subtype /Widget /Rect [150 100 290 115] /AP << /N 19 0 R >> >>",
            TestPdf.Stream("BT /Helv 10 Tf 2 2 Td (Stale) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 140 15] /Resources << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"));
        var editor = PdfEditor.Open(pdf);

        var result = editor.FlattenForm();
        var after = editor.ToArray();

        Assert.Equal(new PdfFlattenResult(11, 10, 1), result);
        var text = Texts(after)[0];
        foreach (var expected in new[] { "Middle", "one two three", "twelve", "A", "B", "1", "2", "****", "Norway", "Red", "Green", "Blue" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("1234", text, StringComparison.Ordinal); // The password is starred.
        Assert.Contains("Fresh", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale", text, StringComparison.Ordinal);
        var drawings = AppearanceStreams(after);
        Assert.Single(drawings, d => d.Contains("(4) Tj", StringComparison.Ordinal)); // A check for the box that is on, none for the one that is off,
        Assert.Single(drawings, d => d.Contains("(l) Tj", StringComparison.Ordinal)); // a dot for the radio button (in ZapfDingbats).
        using var page = Pixels.Render(after, 0, 72);
        Assert.Equal(new SkiaSharp.SKColor(0, 0, 255), Pixels.At(page, 77, 107)); // The producer's blue box, unchanged.
        Assert.NotEqual(SkiaSharp.SKColors.White, Pixels.At(page, 17, 107)); // A check drawn in the check box.
        Assert.Equal(new SkiaSharp.SKColor(230, 230, 230), Pixels.At(page, 280, 362)); // The centered field's background.
        Assert.False(PdfEditor.Open(after).GetForm().HasAcroForm);
        AssertValid(after);
    }

    [Fact]
    public void Made_text_appearances_are_aligned_and_sized_like_viewers_make_them()
    {
        const string fonts = "/DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> "
            + "/Cour << /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >> >> >> /DA (/Helv 0 Tf 0 g)";
        var pdf = FormPdf(
            $"/Fields [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 11 0 R 12 0 R 13 0 R] {fonts}",
            "5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 12 0 R 13 0 R",
            "",
            "<< /FT /Tx /T (centered) /V (Middle) /Q 1 /DA (/Helv 12 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 360 290 380] >>",
            "<< /FT /Tx /T (right) /V (Right) /Q 2 /DA (/Helv 12 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 330 290 350] >>",
            "<< /FT /Tx /T (code) /Ff 16777216 /MaxLen 4 /V (WXYZ) /Type /Annot /Subtype /Widget /Rect [10 300 90 320] >>",
            "<< /FT /Tx /T (long) /V (A long value that must shrink to fit) /Type /Annot /Subtype /Widget /Rect [10 270 90 290] >>",
            // Measured with Courier's widths, not Helvetica's.
            "<< /FT /Tx /T (mono) /V (iiii) /Q 1 /DA (/Cour 12 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 240 290 260] >>",
            // A widget of a field: its own alignment wins over the field's, the rest is the field's.
            "<< /Parent 11 0 R /Q 2 /Type /Annot /Subtype /Widget /Rect [10 210 290 230] >>",
            "<< /FT /Tx /T (parent) /V (Kid) /Q 0 /DA (/Helv 12 Tf 0 g) /Kids [10 0 R] >>",
            // A list box scrolled to its second option, with the third chosen (by the name it shows).
            "<< /FT /Ch /T (list) /Opt [[(a) (Alpha)] [(b) (Beta)] [(g) (Gamma)]] /TI 1 /V (Gamma) /DA (/Helv 10 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 130 110 200] >>",
            "<< /FT /Tx /T (framed) /V () /BS << /W 4 >> /MK << /BC [1 0 0] >> /Type /Annot /Subtype /Widget /Rect [150 130 250 170] >>");
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(new PdfFlattenResult(8, 8, 0), editor.FlattenForm());
        var after = editor.ToArray();

        var extractor = new TextExtractor(PdfReader.Open(after));
        PdfRectangle Find(string text) => Assert.Single(extractor.FindOnPage(0, text)).Bounds;
        Assert.Equal(150, Find("Middle").X + (Find("Middle").Width / 2), 0.5);
        Assert.Equal(288, Find("Right").Right, 0.5);
        Assert.Equal(288, Find("Kid").Right, 0.5);
        Assert.Equal(150, Find("iiii").X + (Find("iiii").Width / 2), 0.5);
        foreach (var (c, center) in new[] { ("W", 20), ("X", 40), ("Y", 60), ("Z", 80) })
        {
            Assert.Equal(center, Find(c).X + (Find(c).Width / 2), 0.5); // One letter in the middle of each comb cell.
        }

        var shrunk = Find("A long value that must shrink to fit");
        Assert.InRange(shrunk.X, 11.5, 12.5);
        Assert.InRange(shrunk.Right, 80, 88.5);
        var text = Texts(after)[0];
        Assert.DoesNotContain("Alpha", text, StringComparison.Ordinal); // Scrolled out of the list.
        Assert.Contains("Beta", text, StringComparison.Ordinal);
        using var page = Pixels.Render(after, 0, 72);
        Assert.Equal(new SkiaSharp.SKColor(153, 191, 219), Pixels.At(page, 100, 178)); // Gamma's row is highlighted,
        Assert.Equal(SkiaSharp.SKColors.White, Pixels.At(page, 100, 190)); // Beta's is not.
        Assert.Equal(new SkiaSharp.SKColor(255, 0, 0), Pixels.At(page, 153, 150)); // A 4-point border.
        Assert.Equal(SkiaSharp.SKColors.White, Pixels.At(page, 156, 150));
        AssertValid(after);
    }

    [Fact]
    public void Appearances_are_fitted_to_the_widget_rectangle()
    {
        // The appearance's box starts at (10, 10) and is half the rectangle's size; its dictionary lacks /Type and /Subtype.
        var pdf = FormPdf(
            "/Fields [5 0 R]",
            "5 0 R",
            "",
            "<< /FT /Tx /T (box) /V (x) /Type /Annot /Subtype /Widget /Rect [100 100 200 120] /AP << /N 6 0 R >> >>",
            TestPdf.Stream("1 0 0 rg 10 10 50 10 re f", "/BBox [10 10 60 20]"));
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(new PdfFlattenResult(1, 0, 0), editor.FlattenForm());
        using var page = Pixels.Render(editor.ToArray(), 0, 72);
        var red = new SkiaSharp.SKColor(255, 0, 0);
        Assert.Equal((red, red, red), (Pixels.At(page, 101, 101), Pixels.At(page, 150, 110), Pixels.At(page, 198, 118)));
        Assert.Equal((SkiaSharp.SKColors.White, SkiaSharp.SKColors.White), (Pixels.At(page, 98, 110), Pixels.At(page, 202, 110)));
    }

    [Fact]
    public void Content_that_leaves_its_state_changed_does_not_move_the_fields()
    {
        // The page's content scales everything by 2 and never restores the state; the field has an appearance with a matrix.
        var pdf = FormPdf(
            "/Fields [5 0 R]",
            "5 0 R 7 0 R",
            "2 0 0 2 0 0 cm 1 0 0 rg 0 0 10 10 re f",
            "<< /FT /Tx /T (turned) /V (x) /Type /Annot /Subtype /Widget /Rect [100 100 120 160] /AP << /N 6 0 R >> >>",
            TestPdf.Stream("0 0.6 0 rg 0 0 60 20 re f 0 0 1 rg 0 0 10 20 re f", "/Type /XObject /Subtype /Form /BBox [0 0 60 20] /Matrix [0 1 -1 0 20 0]"),
            "<< /Type /Annot /Subtype /Text /Rect [200 200 220 220] /Contents (A note stays) >>");
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(new PdfFlattenResult(1, 0, 0), editor.FlattenForm());
        var after = editor.ToArray();

        Assert.All(Differences(pdf, after, afterWithAnnotations: true), d => Assert.Equal(0, d)); // The note is drawn in both.
        using var page = Pixels.Render(after, 0, 72);
        Assert.Equal(new SkiaSharp.SKColor(0, 0, 255), Pixels.At(page, 110, 103)); // The blue end, turned to the bottom.
        Assert.Equal(new SkiaSharp.SKColor(0, 153, 0), Pixels.At(page, 110, 150));
        var annotation = Assert.Single(((PdfArray)PdfReader.Open(after).Resolve(PdfReader.Open(after).Pages[0].Dictionary["Annots"])!).Items);
        Assert.Equal("Text", ((PdfName)((PdfDictionary)PdfReader.Open(after).Resolve(annotation)!)["Subtype"]!).Value);
    }

    [Fact]
    public void Hidden_widgets_are_removed_without_being_drawn()
    {
        var pdf = FormPdf(
            $"/Fields [5 0 R] {Helv}",
            "5 0 R",
            "",
            "<< /FT /Tx /T (secret) /V (Hidden value) /F 2 /Type /Annot /Subtype /Widget /Rect [10 10 200 30] >>");
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(new PdfFlattenResult(0, 0, 1), editor.FlattenForm());
        Assert.DoesNotContain("Hidden", Texts(editor.ToArray())[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Static_xfa_goes_with_the_form_and_dynamic_xfa_is_refused(bool dynamic)
    {
        var pdf = TestPdf.Build([
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA 4 0 R >> {(dynamic ? "/NeedsRendering true" : "")} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            TestPdf.Stream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"/>"),
        ]);
        var editor = PdfEditor.Open(pdf);

        if (dynamic)
        {
            Assert.Throws<NotSupportedException>(() => editor.FlattenForm());
            Assert.Equal(PdfXfaKind.Dynamic, editor.GetForm().Xfa);
        }
        else
        {
            Assert.Equal(new PdfFlattenResult(0, 0, 0), editor.FlattenForm());
            var form = PdfEditor.Open(editor.ToArray()).GetForm();
            Assert.Equal((false, PdfXfaKind.None), (form.HasAcroForm, form.Xfa));
        }
    }

    [Fact]
    public void Documents_without_a_form_are_left_alone()
    {
        var editor = PdfEditor.Open(Document(1));
        var before = editor.ToArray();

        Assert.Equal(new PdfFlattenResult(0, 0, 0), editor.FlattenForm());
        Assert.Equal(before, editor.ToArray());
    }

    [Fact]
    public void A_default_appearance_with_an_empty_font_name_uses_helvetica()
    {
        // Found by fuzzing: "/ 10 Tf" named the font with an empty name, which no resource dictionary can hold.
        var pdf = FormPdf(
            "/Fields [5 0 R] /NeedAppearances true",
            "5 0 R",
            "",
            "<< /FT /Tx /T (name) /V (Plain) /DA (/ 10 Tf 0 g) /Type /Annot /Subtype /Widget /Rect [10 10 200 30] >>");
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(new PdfFlattenResult(1, 1, 0), editor.FlattenForm());
        var after = editor.ToArray();
        Assert.Contains("Plain", Texts(after)[0], StringComparison.Ordinal);
        Assert.Contains("/Helv 10 Tf", Assert.Single(AppearanceStreams(after)), StringComparison.Ordinal);
    }
}
