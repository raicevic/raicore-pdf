#pragma warning disable CA1849, CA2007
using System.Globalization;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Tables
{
    public static Task RunAsync()
    {
        var rows = string.Concat(Enumerable.Range(1, 80).Select(i =>
            $"<tr><td>Item {i}</td><td class='num'>{(i * 37.5m).ToString("N2", CultureInfo.InvariantCulture)}</td></tr>"));

        #region table
        var html = $$"""
            <style>
              @page { size: A4; margin: 20mm; }
              table { width: 100%; border-collapse: collapse; }
              th, td { border: 0.5pt solid #999; padding: 1.5mm 2mm; }
              thead th { background: #e8eef6; }        /* the header row repeats on every page */
              tfoot td { font-weight: bold; }          /* and so does the footer row */
              tr { break-inside: avoid; }              /* rows are not split across pages */
              .num { text-align: right; }
            </style>
            <table>
              <thead><tr><th>Item</th><th class="num">Amount</th></tr></thead>
              <tfoot><tr><td>Carried over</td><td class="num">...</td></tr></tfoot>
              <tbody>{{rows}}</tbody>
            </table>
            """;
        HtmlToPdf.Convert(html).Save("table.pdf");
        #endregion

        #region breaks
        var breaks = """
            <style>
              h1 { break-before: page; }               /* every chapter on a new page */
              h2 { break-after: avoid; }               /* headings stay with their text */
              figure { break-inside: avoid; }          /* figures are not split */
              p { orphans: 3; widows: 3; }             /* at least 3 lines at the bottom and top of a page */
              .appendix { break-before: right; }       /* on a right page, after a blank one if needed */
            </style>
            <h1>One</h1><h2>Section</h2><p>Text.</p>
            <h1>Two</h1><figure>Figure</figure>
            <h1 class="appendix">Appendix</h1>
            """;
        HtmlToPdf.Convert(breaks).Save("breaks.pdf");
        #endregion

        return Task.CompletedTask;
    }
}
