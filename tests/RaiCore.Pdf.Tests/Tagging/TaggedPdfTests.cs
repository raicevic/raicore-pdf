using System.Text;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Tagging;

public class TaggedPdfTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static PdfDocument Tagged(bool pdfUA = false) => new()
    {
        Tagged = true,
        PdfUA = pdfUA,
        Language = "en-US",
        CompressContent = false,
        Info = { Title = "Tagged" },
    };

    [Fact]
    public void Untagged_documents_have_no_structure_and_no_marked_content()
    {
        var doc = new PdfDocument { CompressContent = false };
        var page = doc.AddPage();
        page.Canvas.DrawText("Plain", Sans, 12, 72, 720).BeginArtifact().Rectangle(0, 0, 10, 10).Fill().EndTag();
        page.AddLink(72, 700, 50, 12, new Uri("https://example.com/"));
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.DoesNotContain("BDC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BMC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/StructTreeRoot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/StructParent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Tabs", text, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => doc.StructureRoot);
        Assert.Throws<InvalidOperationException>(() => page.Canvas.BeginTag(Tagged().StructureRoot));
    }

    [Fact]
    public void Tagged_content_belongs_to_its_elements_in_reading_order()
    {
        var doc = Tagged();
        var page = doc.AddPage();
        var heading = doc.StructureRoot.AddChild(PdfStructureTypes.Heading1);
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        page.Canvas
            .BeginTag(heading).DrawText("Title", Sans, 20, 72, 760).EndTag()
            .BeginTag(paragraph).DrawText("First ", Sans, 12, 72, 720).EndTag()
            .BeginArtifact(PdfArtifactKind.Pagination).DrawText("Page 1", Sans, 9, 72, 40).EndTag()
            .BeginTag(paragraph).DrawText("second", Sans, 12, 110, 720).EndTag();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        Assert.Equal("Document(H1 P)", root.ToString());
        Assert.Equal("Title", root.Children[0].Text);
        Assert.Equal("First second", root.Children[1].Text);
        Assert.Equal(2, root.Children[1].Content.Count); // Interrupted by the artifact: two sequences.
    }

    [Fact]
    public void Consecutive_drawing_with_the_same_tag_is_one_sequence_and_untagged_drawing_is_an_artifact()
    {
        var doc = Tagged();
        var page = doc.AddPage();
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        page.Canvas.SetFillColor(PdfColor.FromRgb(200, 200, 200)).Rectangle(60, 700, 200, 40).Fill();
        page.Canvas.BeginTag(paragraph).DrawText("One", Sans, 12, 72, 720).EndTag();
        page.Canvas.BeginTag(paragraph).DrawText("Two", Sans, 12, 120, 720).EndTag();
        var bytes = doc.ToArray();
        var content = Encoding.Latin1.GetString(bytes);

        Assert.Single(TaggedPdfInspector.Inspect(bytes).Children[0].Content);
        Assert.Contains("/Artifact BMC", content, StringComparison.Ordinal);
        Assert.Contains("/P <</MCID 0>> BDC", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_tags_become_consecutive_sequences_of_the_inner_element()
    {
        var doc = Tagged();
        var page = doc.AddPage();
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        var span = paragraph.AddChild(PdfStructureTypes.Span);
        span.Language = "da";
        page.Canvas.BeginTag(paragraph).DrawText("Say ", Sans, 12, 72, 720)
            .BeginTag(span).DrawText("hej", Sans, 12, 100, 720).EndTag()
            .DrawText(" now", Sans, 12, 125, 720).EndTag();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        var p = Assert.Single(root.Children);
        Assert.Equal("P(Span)", p.ToString());
        Assert.Equal("Say  now", p.OwnText);
        Assert.Equal("hej", p.Children[0].Text);
        Assert.Equal("da", p.Children[0].Entry("Lang"));
    }

    [Fact]
    public void Links_are_tagged_with_object_references_and_descriptions()
    {
        var doc = Tagged();
        var page = doc.AddPage();
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        var link = paragraph.AddChild(PdfStructureTypes.Link);
        page.Canvas.BeginTag(paragraph).DrawText("Visit ", Sans, 12, 72, 720)
            .BeginTag(link).DrawText("example", Sans, 12, 108, 720);
        page.Canvas.AddLink(108, 716, 45, 14, new Uri("https://example.com/")).Description = "Example site";
        page.Canvas.EndTag().EndTag();

        // A link added outside a Link element gets one of its own (here at the document level).
        page.AddLink(72, 600, 50, 12, new Uri("https://example.org/"));

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        Assert.Equal("Document(P(Link) Link)", root.ToString());
        var tagged = root.Children[0].Children[0];
        Assert.Equal("example", tagged.Text);
        var annotation = Assert.Single(tagged.Annotations);
        Assert.Equal("Example site", PdfPageTreeText(annotation["Contents"]));

        var own = Assert.Single(root.Children[1].Annotations);
        Assert.Equal("https://example.org/", PdfPageTreeText(own["Contents"]));
    }

    [Fact]
    public void Content_in_transparency_groups_is_marked_in_the_group_form()
    {
        var doc = Tagged();
        var page = doc.AddPage();
        var outer = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        var inner = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        page.Canvas.BeginTag(outer).DrawText("Before", Sans, 12, 72, 740)
            .BeginGroup(0.5).DrawText("Faded", Sans, 12, 72, 720)
            .BeginTag(inner).DrawText("Inner", Sans, 12, 72, 700).EndTag()
            .EndGroup()
            .DrawText("After", Sans, 12, 72, 680).EndTag();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        Assert.Equal("BeforeFadedAfter", root.Children[0].Text);
        Assert.Equal("Inner", root.Children[1].Text);
        Assert.Contains(root.Children[0].Content, c => c.Stream is not null); // "Faded", in the group's form.
        Assert.Contains(root.Children[1].Content, c => c.Stream is not null);
    }

    [Fact]
    public void Element_properties_and_attributes_are_written()
    {
        var doc = Tagged();
        var page = doc.AddPage();
        var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        figure.AlternateText = "A grey square";
        var list = doc.StructureRoot.AddChild(PdfStructureTypes.List);
        list.ListNumbering = PdfListNumbering.DecimalDigits;
        var table = doc.StructureRoot.AddChild(PdfStructureTypes.Table);
        var header = table.AddChild(PdfStructureTypes.TableRow).AddChild(PdfStructureTypes.TableHeaderCell);
        header.Scope = PdfTableScope.Column;
        header.ColumnSpan = 2;
        var empty = doc.StructureRoot.AddChild(PdfStructureTypes.Section); // No content: left out.
        empty.AddChild(PdfStructureTypes.Paragraph);

        page.Canvas.BeginTag(figure).Rectangle(72, 600, 50, 50).Fill().EndTag()
            .BeginTag(list.AddChild(PdfStructureTypes.ListItem)).DrawText("1. Item", Sans, 12, 72, 560).EndTag()
            .BeginTag(header).DrawText("Head", Sans, 12, 72, 520).EndTag();
        var bytes = doc.ToArray();
        var root = TaggedPdfInspector.Inspect(bytes);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Equal("Document(Figure L(LI) Table(TR(TH)))", root.ToString());
        Assert.Equal("A grey square", root.Children[0].Entry("Alt"));
        Assert.Contains("<</O /List/ListNumbering /Decimal>>", text, StringComparison.Ordinal);
        Assert.Contains("<</O /Table/Scope /Column/ColSpan 2>>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfUA_declares_conformance_and_shows_the_title()
    {
        var doc = Tagged(pdfUA: true);
        doc.AddPage().Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("Hello", Sans, 12, 72, 720).EndTag();
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", text, StringComparison.Ordinal);
        Assert.Contains("/DisplayDocTitle true", text, StringComparison.Ordinal);
        Assert.Contains("/Lang (en-US)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfUA_requires_a_title_a_language_and_embedded_fonts()
    {
        PdfDocument Make(Action<PdfDocument> configure, PdfFont font)
        {
            var doc = Tagged(pdfUA: true);
            configure(doc);
            doc.AddPage().Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("Hello", font, 12, 72, 720).EndTag();
            return doc;
        }

        Assert.Contains("title", Assert.Throws<InvalidOperationException>(() => Make(d => d.Info.Title = " ", Sans).ToArray()).Message, StringComparison.Ordinal);
        Assert.Contains("language", Assert.Throws<InvalidOperationException>(() => Make(d => d.Language = null, Sans).ToArray()).Message, StringComparison.Ordinal);
        Assert.Contains("embedded", Assert.Throws<InvalidOperationException>(() => Make(_ => { }, PdfFont.Standard(StandardFont.Helvetica)).ToArray()).Message, StringComparison.Ordinal);
        Assert.Contains("XMP", Assert.Throws<InvalidOperationException>(() => Make(d => d.IncludeXmpMetadata = false, Sans).ToArray()).Message, StringComparison.Ordinal);
        Assert.True(Make(_ => { }, Sans).ToArray().Length > 0);
    }

    [Fact]
    public void Tags_must_be_balanced_and_belong_to_the_document()
    {
        var doc = Tagged();
        var canvas = doc.AddPage().Canvas;
        Assert.Throws<InvalidOperationException>(() => canvas.EndTag());
        Assert.Throws<ArgumentException>(() => canvas.BeginTag(Tagged().StructureRoot));
        Assert.Throws<ArgumentException>(() => doc.StructureRoot.AddChild("Bad Name"));
        Assert.Null(canvas.CurrentElement);
        canvas.BeginArtifact();
        Assert.Null(canvas.CurrentElement);
        canvas.BeginTag(doc.StructureRoot);
        Assert.Same(doc.StructureRoot, canvas.CurrentElement);
    }

    [Fact]
    public void A_tagged_document_validates_as_PdfUA_with_veraPDF()
    {
        var doc = Tagged(pdfUA: true);
        var page = doc.AddPage();
        var root = doc.StructureRoot;
        var heading = root.AddChild(PdfStructureTypes.Heading1);
        var paragraph = root.AddChild(PdfStructureTypes.Paragraph);
        var link = paragraph.AddChild(PdfStructureTypes.Link);
        var figure = root.AddChild(PdfStructureTypes.Figure);
        figure.AlternateText = "A grey square";

        page.Canvas.SetFillColor(PdfColor.FromRgb(240, 240, 240)).Rectangle(0, 0, page.Width, page.Height).Fill()
            .BeginTag(heading).SetFillColor(PdfColor.Black).DrawText("Accessible", Sans, 24, 72, 760).EndTag()
            .BeginTag(paragraph).DrawText("Read ", Sans, 12, 72, 720)
            .BeginTag(link).DrawText("the site", Sans, 12, 102, 720);
        page.Canvas.AddLink(102, 716, 45, 14, new Uri("https://example.com/"));
        page.Canvas.EndTag().DrawText(".", Sans, 12, 148, 720).EndTag()
            .BeginTag(figure).BeginGroup(0.5).SetFillColor(PdfColor.FromRgb(128, 128, 128)).Rectangle(72, 600, 80, 80).Fill().EndGroup().EndTag()
            .BeginArtifact(PdfArtifactKind.Pagination).DrawText("1", Sans, 9, 300, 40).EndTag();

        ExternalTools.AssertVeraPdfCompliant(doc.ToArray(), "ua1");
    }

    private static string PdfPageTreeText(Objects.PdfObject? value) =>
        RaiCore.Pdf.Reading.PdfText.Decode(Assert.IsType<Objects.PdfString>(value).Bytes);
}
