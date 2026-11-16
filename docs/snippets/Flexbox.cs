#pragma warning disable CA1849, CA2007
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Flexbox
{
    public static Task RunAsync()
    {
        #region flex
        var html = """
            <style>
              @page { size: A4; margin: 20mm; }
              html, body { height: 100%; margin: 0; }
              /* A cover page: the title in the middle, the footer at the bottom. */
              .cover { display: flex; flex-direction: column; height: 100%; }
              .cover .title { flex: 1; display: flex; align-items: center; justify-content: center; font-size: 28pt; }
              .cover footer { display: flex; justify-content: space-between; }
              /* Cards in rows that wrap, with gaps. */
              .cards { display: flex; flex-wrap: wrap; gap: 5mm; break-before: page; }
              .card { flex: 1 1 50mm; border: 0.5pt solid #999; border-radius: 2mm; padding: 4mm; }
            </style>
            <section class="cover">
              <div class="title">Annual report 2026</div>
              <footer><span>Example Group</span><span>November 2026</span></footer>
            </section>
            <div class="cards">
              <div class="card">Revenue</div><div class="card">Costs</div><div class="card">Margin</div><div class="card">Outlook</div>
            </div>
            """;
        HtmlToPdf.Convert(html).Save("flexbox.pdf");
        #endregion

        return Task.CompletedTask;
    }
}
