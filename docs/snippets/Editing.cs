#pragma warning disable CA1849, CA2007
using RaiCore.Pdf;
using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Editing
{
    public static async Task RunAsync()
    {
        // Files to edit: a three-page contract and a one-page appendix.
        HtmlToPdf.Convert("""
            <h1>Cover</h1>
            <h1 style="break-before: page">Terms</h1><p>The terms.</p>
            <h1 style="break-before: page">Signatures</h1>
            """).Save("contract.pdf");
        HtmlToPdf.Convert("<h1>Appendix</h1>").Save("appendix.pdf");

        #region pages
        var editor = PdfEditor.Open("contract.pdf");
        editor.Pages.Delete(0);                                  // drop the cover page
        editor.Pages.Add(PdfReader.Open("appendix.pdf"));        // append every page of another file
        editor.Pages.Move(editor.Pages.Count - 1, 0);            // the appendix goes first
        editor.Pages[0].Rotation = 90;                           // turned a quarter clockwise
        editor.Save("contract-edited.pdf");
        #endregion

        #region repeat
        // Save, look at the result, change more, save again: the editor stays usable after each save.
        var sizes = new List<long>();
        var report = PdfEditor.Open("contract.pdf");
        while (report.Pages.Count > 1)
        {
            sizes.Add(report.ToArray().Length);
            report.Pages.Delete(report.Pages.Count - 1);
        }

        await using (var file = File.Create("contract-first-page.pdf"))
        {
            await report.SaveAsync(file);
        }
        #endregion

        #region insert
        // Pages from several files, in any order; RaiCore documents are read from their bytes.
        var cover = new PdfDocument();
        cover.AddPage(PdfPageSize.A4).Canvas.DrawText("New cover", RaiCore.Pdf.Fonts.StandardFont.Helvetica, 24, 72, 760);
        var merged = PdfEditor.Open("contract.pdf");
        merged.Pages.Delete(0);
        merged.Pages.Insert(0, PdfReader.Open(cover.ToArray()));
        merged.Pages.Insert(2, PdfReader.Open("appendix.pdf"), [0, 0]);  // the appendix twice, after the terms
        merged.Save("contract-merged.pdf");
        #endregion
    }
}
