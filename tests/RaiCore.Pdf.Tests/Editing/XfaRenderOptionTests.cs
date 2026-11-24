using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Tests.Reading;
using RaiCore.Pdf.Xfa;

namespace RaiCore.Pdf.Tests.Editing;

/// <summary>
/// <see cref="PdfDynamicXfaHandling.Render"/>: flattening a dynamic XFA form by laying it out with a renderer (here
/// <see cref="XfaToPdf"/>) and replacing the document's pages (#262, #263).
/// </summary>
public class XfaRenderOptionTests
{
    private static PdfEditor Open(string fixture) => PdfEditor.Open(Fixtures.Read("Forms/" + fixture));

    private static readonly PdfFormFlattenOptions Render = new()
    {
        DynamicXfa = PdfDynamicXfaHandling.Render,
        XfaRenderer = bytes => XfaToPdf.Convert(bytes),
    };

    private static string Text(byte[] pdf) => new TextExtractor(PdfReader.Open(pdf)).ExtractText();

    [Fact]
    public void A_placeholder_is_replaced_by_the_rendered_form()
    {
        var editor = Open("xfa-placeholder.pdf");
        var expectedPages = PdfReader.Open(XfaToPdf.Convert(Fixtures.Read("Forms/xfa-placeholder.pdf")).ToArray()).Pages.Count;

        var result = editor.FlattenForm(Render);

        Assert.Equal(new PdfFlattenResult(0, 0, 0), result);
        var pdf = editor.ToArray();
        var reader = PdfReader.Open(pdf);
        Assert.Equal(expectedPages, reader.Pages.Count);
        Assert.Null(reader.Catalog["AcroForm"]);
        Assert.Null(reader.Catalog["NeedsRendering"]);
        var text = Text(pdf);
        Assert.Contains("Ada Lovelace", text);
        Assert.DoesNotContain("Please wait", text);
        var form = PdfEditor.Open(pdf).GetForm();
        Assert.Equal((false, PdfXfaKind.None), (form.HasAcroForm, form.Xfa));
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Fact]
    public void A_dynamic_form_with_an_acroform_loses_its_old_pages_and_widgets()
    {
        var editor = Open("xfa-dynamic.pdf");
        byte[]? seen = null;

        var result = editor.FlattenForm(Render with { XfaRenderer = bytes => XfaToPdf.Convert(seen = bytes) });

        Assert.Equal(new PdfFlattenResult(0, 0, 5), result); // The five widgets went with the replaced pages.
        Assert.Equal(PdfXfaKind.Dynamic, PdfEditor.Open(seen!).GetForm().Xfa); // The renderer got the form as it was.
        var pdf = editor.ToArray();
        var reader = PdfReader.Open(pdf);
        Assert.Single(reader.Pages);
        Assert.Empty(reader.Pages.SelectMany(p => (reader.Resolve(p.Dictionary["Annots"]) as PdfArray)?.Items ?? []));
        Assert.Null(reader.Catalog["AcroForm"]);
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Fact]
    public void Any_renderer_will_do_and_every_widget_counts()
    {
        // One field with two widgets on the placeholder page; the "renderer" makes two pages of its own.
        var editor = PdfEditor.Open(TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /XFA 4 0 R >> /NeedsRendering true >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [6 0 R 7 0 R] >>",
            TestPdf.Stream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"/>"),
            "<< /FT /Tx /T (name) /V (x) /Kids [6 0 R 7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [10 10 100 30] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [10 50 100 70] /P 3 0 R >>",
        ]));
        var rendered = new PdfDocument();
        rendered.AddPage();
        rendered.AddPage();

        var result = editor.FlattenForm(Render with { XfaRenderer = _ => rendered });

        Assert.Equal(new PdfFlattenResult(0, 0, 2), result);
        Assert.Equal(2, PdfReader.Open(editor.ToArray()).Pages.Count);
    }

    [Fact]
    public void Rendering_needs_a_renderer_and_pages_from_it()
    {
        var editor = Open("xfa-placeholder.pdf");
        var before = editor.ToArray();

        var missing = Assert.Throws<InvalidOperationException>(
            () => editor.FlattenForm(new PdfFormFlattenOptions { DynamicXfa = PdfDynamicXfaHandling.Render }));
        Assert.Contains("XfaRenderer", missing.Message);
        Assert.Throws<InvalidOperationException>(() => editor.FlattenForm(Render with { XfaRenderer = _ => new PdfDocument() }));

        Assert.Equal(before, editor.ToArray());
    }

    [Fact]
    public void Static_xfa_is_flattened_without_the_renderer()
    {
        var editor = Open("xfa-static.pdf");
        var called = false;

        var result = editor.FlattenForm(Render with { XfaRenderer = bytes => { called = true; return XfaToPdf.Convert(bytes); } });

        Assert.False(called);
        Assert.Equal(5, result.Drawn);
        Assert.Equal(2, PdfReader.Open(editor.ToArray()).Pages.Count);
    }
}
