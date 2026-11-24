using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Tests.Reading;

namespace RaiCore.Pdf.Tests.Editing;

/// <summary>
/// Removing XFA forms (#262) from the fixtures made by Fixtures/Forms/make-xfa.py: a static XFA form, a dynamic one with
/// a full AcroForm behind it, and a dynamic one whose only PDF page is the "Please wait..." placeholder.
/// </summary>
public class XfaRemovalTests
{
    private static PdfEditor Open(string fixture) => PdfEditor.Open(Fixtures.Read("Forms/" + fixture));

    /// <summary>The catalog and AcroForm entries that matter here, as read back from the saved file.</summary>
    private static (bool Xfa, bool NeedsRendering, int Fields) Entries(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        var catalog = reader.Catalog;
        var form = reader.Resolve(catalog["AcroForm"]) as PdfDictionary;
        return (form?["XFA"] is not null, catalog["NeedsRendering"] is not null,
            (reader.Resolve(form?["Fields"]) as PdfArray)?.Items.Count ?? 0);
    }

    [Theory]
    [InlineData("xfa-static.pdf", PdfXfaKind.Static, false)]
    [InlineData("xfa-dynamic.pdf", PdfXfaKind.Dynamic, false)]
    [InlineData("xfa-placeholder.pdf", PdfXfaKind.Dynamic, true)]
    [InlineData("pymupdf-form.pdf", PdfXfaKind.None, false)]
    public void Placeholder_pages_are_told_apart(string fixture, PdfXfaKind kind, bool placeholder)
    {
        var form = Open(fixture).GetForm();

        Assert.Equal((kind, placeholder), (form.Xfa, form.IsXfaPlaceholder));
    }

    [Theory]
    [InlineData("xfa-static.pdf")]
    [InlineData("xfa-dynamic.pdf")]
    public void Removing_xfa_keeps_the_acroform_and_its_values(string fixture)
    {
        var editor = Open(fixture);
        var fieldsBefore = editor.GetForm().Fields.Select(f => $"{f.FullName}={f.Value}").ToList();

        Assert.True(editor.RemoveXfa());
        var pdf = editor.ToArray();

        Assert.Equal((false, false, 5), Entries(pdf));
        var form = PdfEditor.Open(pdf).GetForm();
        Assert.Equal((true, PdfXfaKind.None, false), (form.HasAcroForm, form.Xfa, form.IsXfaPlaceholder));
        Assert.Equal(fieldsBefore, form.Fields.Select(f => $"{f.FullName}={f.Value}"));
        Assert.Contains("name=Ada Lovelace", fieldsBefore);
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Fact]
    public void A_placeholder_is_refused_unless_forced()
    {
        var editor = Open("xfa-placeholder.pdf");

        var error = Assert.Throws<InvalidOperationException>(() => editor.RemoveXfa());
        Assert.Contains("placeholder", error.Message);
        Assert.Equal(PdfXfaKind.Dynamic, editor.GetForm().Xfa);

        Assert.True(editor.RemoveXfa(force: true));
        var pdf = editor.ToArray();
        Assert.Equal((false, false, 0), Entries(pdf));
        Assert.Contains("Please wait", new TextExtractor(PdfReader.Open(pdf)).ExtractText());
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Fact]
    public void Documents_without_xfa_are_left_alone()
    {
        var editor = Open("pymupdf-form.pdf");
        var before = editor.ToArray();

        Assert.False(editor.RemoveXfa());
        Assert.Equal(before, editor.ToArray());
        Assert.False(PdfEditor.Open(PdfEditorTests.Document(1)).RemoveXfa());
    }

    [Fact]
    public void A_stray_needs_rendering_without_xfa_goes_too()
    {
        var editor = PdfEditor.Open(TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /NeedsRendering true >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R >>",
        ]));

        Assert.False(editor.RemoveXfa());
        Assert.Equal((false, false, 0), Entries(editor.ToArray()));
    }

    [Fact]
    public void Flattening_can_remove_dynamic_xfa_first()
    {
        var editor = Open("xfa-dynamic.pdf");
        Assert.Throws<NotSupportedException>(() => editor.FlattenForm(PdfFormFlattenOptions.Default));

        var result = editor.FlattenForm(new PdfFormFlattenOptions { DynamicXfa = PdfDynamicXfaHandling.RemoveXfa });

        Assert.Equal(5, result.Drawn);
        var pdf = editor.ToArray();
        var form = PdfEditor.Open(pdf).GetForm();
        Assert.Equal((false, PdfXfaKind.None), (form.HasAcroForm, form.Xfa));
        Assert.Equal((false, false, 0), Entries(pdf));
        Assert.Contains("Ada Lovelace", new TextExtractor(PdfReader.Open(pdf)).ExtractText());
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Fact]
    public void Flattening_a_placeholder_refuses_even_when_asked_to_remove_the_xfa()
    {
        var editor = Open("xfa-placeholder.pdf");

        Assert.Throws<InvalidOperationException>(
            () => editor.FlattenForm(new PdfFormFlattenOptions { DynamicXfa = PdfDynamicXfaHandling.RemoveXfa }));
        Assert.Equal(PdfXfaKind.Dynamic, editor.GetForm().Xfa);
        Assert.Throws<ArgumentNullException>(() => editor.FlattenForm(null!));
    }

    [Fact]
    public void Static_xfa_is_flattened_whatever_the_option()
    {
        var editor = Open("xfa-static.pdf");

        var result = editor.FlattenForm(PdfFormFlattenOptions.Default);

        Assert.Equal(5, result.Drawn);
        Assert.Equal((false, false, 0), Entries(editor.ToArray()));
    }
}
