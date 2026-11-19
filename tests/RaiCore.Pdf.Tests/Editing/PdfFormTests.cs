using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Tests.Reading;

namespace RaiCore.Pdf.Tests.Editing;

public class PdfFormTests
{
    private static PdfForm Form(string fixture) => PdfEditor.Open(Fixtures.Read("Forms/" + fixture)).GetForm();

    private static string Describe(PdfFormField field) =>
        $"{field.FullName} {field.Type} [{string.Join("|", field.Values)}]{(field.Options.Count > 0 ? $" of [{string.Join("|", field.Options)}]" : "")}"
        + $"{(field.IsReadOnly ? " read-only" : "")}{(field.IsRequired ? " required" : "")}"
        + string.Concat(field.Widgets.Select(w => $" p{w.PageIndex + 1}@{w.Rectangle.X:0},{w.Rectangle.Y:0},{w.Rectangle.Width:0}x{w.Rectangle.Height:0}{(w.State is null ? "" : "=" + w.State)}{(w.IsHidden ? " hidden" : "")}"));

    [Fact]
    public void Fields_made_by_pymupdf_are_read()
    {
        var form = Form("pymupdf-form.pdf");

        Assert.True(form.HasAcroForm);
        Assert.Equal(PdfXfaKind.None, form.Xfa);
        Assert.Equal(
            [
                "name Text [Ada Lovelace] p1@72,750,228x20",
                "subscribe CheckBox [Yes] p1@72,714,18x18=Yes",
                "country ComboBox [Denmark] of [Denmark|Norway|Sweden] p1@72,652,128x20",
                "colour ListBox [Blue] of [Red|Blue] p1@72,582,128x60",
                "id Text [A-17] read-only required p2@72,750,228x20",
            ],
            form.Fields.Select(Describe));
    }

    [Fact]
    public void Fields_made_by_reportlab_are_read()
    {
        var form = Form("reportlab-form.pdf");

        Assert.Equal(
            [
                "name Text [Grace Hopper] p1@72,700,200x20",
                "subscribe CheckBox [Off] required p1@72,660,18x18=Off",
                "size RadioButton [large] required p1@72,620,18x18=Off p1@100,620,18x18=large",
                "country ComboBox [Norway] of [Denmark|Norway|Sweden] p1@72,580,150x20",
            ],
            form.Fields.Select(Describe));
        Assert.Equal("size (RadioButton) = large", form["size"]!.ToString());
        Assert.Null(form["missing"]);
    }

    [Fact]
    public void Field_trees_inherit_and_name_their_fields()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 9 0 R 11 0 R 12 0 R 13 0 R 15 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [7 0 R 8 0 R 10 0 R 11 0 R 12 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [13 0 R 16 0 R] >>",
            // address (no type) > city, zip: text fields that inherit their type and flags from "address".
            "<< /T (address) /FT /Tx /Ff 1 /Kids [6 0 R 14 0 R] >>",
            "<< /T (city) /Parent 5 0 R /V (Aarhus) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [10 20 110 40] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [110 70 10 50] /F 2 >>", // Corners the other way round.
            // A multi-select list with [export shown] options.
            "<< /T (tags) /FT /Ch /Ff 2097152 /V [(a) (c)] /Opt [[(a) (Alpha)] [(b) (Beta)] (Gamma)] /Kids [10 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 9 0 R /Rect [0 0 50 50] >>",
            "<< /T (signature) /FT /Sig /V << /Type /Sig >> /Type /Annot /Subtype /Widget /Rect [0 0 1 1] >>",
            "<< /T (go) /FT /Btn /Ff 65536 /Type /Annot /Subtype /Widget /Rect [0 0 1 1] >>",
            "<< /T (other) /FT /Tx /V <FEFF00C6> /Type /Annot /Subtype /Widget /Rect [5 5 15 15] >>",
            "<< /T (zip) /Parent 5 0 R /V (8000) /Type /Annot /Subtype /Widget /Rect [1 2 3 4] >>", // On no page.
            "<< /T (group) /FT /Tx /V (inherited) /Kids [16 0 R] >>", // A value its kid inherits.
            "<< /T (item) /Parent 15 0 R /Type /Annot /Subtype /Widget /Rect [0 0 2 2] >>",
        ]);
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(
            [
                "address.city Text [Aarhus] read-only p1@10,20,100x20 p1@10,50,100x20 hidden",
                "tags ListBox [a|c] of [Alpha|Beta|Gamma] p1@0,0,50x50",
                "signature Signature [Signed] p1@0,0,1x1",
                "go PushButton [] p1@0,0,1x1",
                "other Text [\u00C6] p2@5,5,10x10",
                "group.item Text [inherited] p2@0,0,2x2",
            ],
            editor.GetForm().Fields.Select(Describe));

        // After the first page is deleted, only the field on the second page is left, on page 1.
        editor.Pages.Delete(0);
        Assert.Equal(["other Text [\u00C6] p1@5,5,10x10", "group.item Text [inherited] p1@0,0,2x2"], editor.GetForm().Fields.Select(Describe));
    }

    [Theory]
    [InlineData("", PdfXfaKind.Static)]
    [InlineData("/NeedsRendering true", PdfXfaKind.Dynamic)]
    [InlineData("/NeedsRendering false", PdfXfaKind.Static)]
    public void Xfa_forms_are_told_apart(string catalogEntries, PdfXfaKind expected)
    {
        var pdf = TestPdf.Build([
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA [(template) 4 0 R] >> {catalogEntries} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            TestPdf.Stream("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/>"),
        ]);

        var form = PdfEditor.Open(pdf).GetForm();

        Assert.Equal(expected, form.Xfa);
        Assert.True(form.HasAcroForm);
        Assert.Empty(form.Fields);
    }

    [Fact]
    public void Documents_without_a_form_say_so()
    {
        var form = PdfEditor.Open(PdfEditorTests.Document(1)).GetForm();

        Assert.False(form.HasAcroForm);
        Assert.Equal(PdfXfaKind.None, form.Xfa);
        Assert.Empty(form.Fields);
    }
}
