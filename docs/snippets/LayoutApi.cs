#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;

namespace Snippets;

internal static class LayoutApi
{
    public static Task RunAsync()
    {
        #region flow
        var fonts = BundledFonts.CreateCollection();
        var body = new TextStyle(fonts.Find("serif")!, 11) { LineHeight = LineHeight.Multiple(1.4) };
        var h1 = new TextStyle(fonts.Find("sans-serif", weight: 700)!, 20);
        var blue = PdfColor.FromRgb(26, 79, 139);

        var flow = new FlowDocument(new PageSetup(PdfPageSize.A4, Thickness.All(56)));
        flow.Add(new ParagraphBlock(
            new Paragraph(h1).Add("Report"),
            new BoxStyle { BreakAfter = PageBreak.Avoid }));
        flow.Add(new ParagraphBlock(new Paragraph(body) { Alignment = TextAlignment.Justify }
            .Add("Mixed ")
            .Add("styles", body with { Color = PdfColor.FromRgb(200, 0, 0) })
            .Add(" in one paragraph, wrapped to the width and split across pages between lines.")));
        var note = new BoxStyle
        {
            Background = PdfColor.FromRgb(232, 238, 246),
            Border = Borders.All(1, blue),
            Padding = Thickness.All(8),
        };
        flow.Add(new ContainerBlock(note, new ParagraphBlock(new Paragraph(body).Add("A note in a box."))));
        flow.PageDecorator = (canvas, page) =>
            canvas.DrawText($"Page {page.Number} of {page.Count}", StandardFont.Helvetica, 8, 56, 30);
        flow.Render().Save("layout.pdf");
        #endregion

        #region paragraph
        var document = new PdfDocument();
        var pdfPage = document.AddPage(PdfPageSize.A4);
        var english = body with { Language = "en-US", Hyphens = Hyphens.Auto };
        var layout = new Paragraph(english) { Alignment = TextAlignment.Justify }
            .Add("A paragraph laid out on its own, hyphenated and justified, then drawn where you like.")
            .Layout(width: 200);
        layout.Draw(pdfPage.Canvas, x: 72, top: pdfPage.Height - 72);
        document.Save("paragraph.pdf");
        #endregion

        #region fragment
        // Rich text stored as HTML, e.g. in a database.
        var notes = "<p>Delivered <b>on time</b>; see <a href=\"https://example.com/track\">tracking</a>.</p><ul><li>2 boxes</li><li>1 pallet</li></ul>";
        var report = new FlowDocument(new PageSetup(PdfPageSize.A4, Thickness.All(56)));
        report.Add(new ParagraphBlock(new Paragraph(h1).Add("Delivery report")));
        var options = new HtmlFragmentOptions
        {
            BaseStyle = body, // The fragment's text starts from the report's body style.
            Css = "p { margin: 0 0 6pt } ul { margin: 0; padding-left: 14pt }",
            Width = report.Setup.ContentWidth,
            Fonts = fonts,
        };
        var fragment = HtmlFragment.Parse(notes, options);
        foreach (var diagnostic in fragment.Diagnostics)
        {
            Console.WriteLine(diagnostic); // CSS or resources that could not be converted.
        }

        fragment.AddTo(report);

        // In a table cell: the same blocks go in the cell's content.
        var cellNotes = HtmlFragment.Parse(notes, new HtmlFragmentOptions { BaseStyle = body, Css = options.Css, Width = 300, Fonts = fonts });
        report.Add(new TableBlock { ColumnWidths = [Length.FromPoints(120), Length.FromPoints(300)] }
            .Add(new TableRow(new TableCell(new Paragraph(body).Add("Notes")), new TableCell(cellNotes.Blocks))));
        report.Render().Save("fragment.pdf");
        #endregion

        return Task.CompletedTask;
    }
}
