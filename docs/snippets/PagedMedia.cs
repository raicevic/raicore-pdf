#pragma warning disable CA1849, CA2007
using System.Text;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class PagedMedia
{
    public static Task RunAsync()
    {
        var chapters = new StringBuilder();
        for (var i = 1; i <= 3; i++)
        {
            chapters.Append($"<h1 id='ch{i}'>Chapter {i}</h1>").Append(string.Concat(Enumerable.Repeat("<p>Some text of the chapter.</p>", 30)));
        }

        #region headers-footers
        var css = """
            @page {
              size: A4;
              margin: 25mm 20mm;
              @top-left { content: element(letterhead); }
              @top-right { content: string(chapter); font-size: 8pt; color: #555; }
              @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; }
            }
            /* No running title on the first page. */
            @page :first { @top-right { content: none; } }
            /* Taken out of the flow and repeated on every page. */
            .letterhead { position: running(letterhead); }
            h1 { string-set: chapter content(); }
            /* Margin boxes inherit from the root element. */
            html { font-family: Arial, sans-serif; }
            """;
        var html = $"""
            <style>{css}</style>
            <div class="letterhead"><b>Example Consulting</b></div>
            {chapters}
            """;
        HtmlToPdf.Convert(html).Save("headers-footers.pdf");
        #endregion

        #region named-pages
        var landscape = """
            <style>
              @page { size: A4; margin: 20mm; }
              @page wide { size: A4 landscape; }
              .wide { page: wide; }                             /* on landscape pages of its own */
              h2 { break-before: page; }
            </style>
            <h1>Report</h1>
            <h2>Figures</h2>
            <div class="wide"><table border="1"><tr><td>A wide table</td></tr></table></div>
            <h2>Conclusion</h2>
            """;
        HtmlToPdf.Convert(landscape).Save("named-pages.pdf");
        #endregion

        #region toc
        var toc = $$"""
            <style>
              nav a { color: inherit; text-decoration: none; }
              nav a::after { content: leader(".") target-counter(attr(href), page); }
              h1 { break-before: page; }
            </style>
            <nav>
              <p><a href="#ch1">Chapter 1</a></p>
              <p><a href="#ch2">Chapter 2</a></p>
              <p><a href="#ch3">Chapter 3</a></p>
            </nav>
            {{chapters}}
            """;
        HtmlToPdf.Convert(toc).Save("toc.pdf");
        #endregion

        #region bookmarks
        var bookmarks = """
            <style>
              h1.subtitle { bookmark-level: none; }                                   /* not in the outline */
              .chapter {
                bookmark-level: 1;
                bookmark-label: "Chapter: " attr(data-title);
              }
              h2 { bookmark-state: closed; }                                          /* collapsed */
            </style>
            <h1>Report</h1><h1 class="subtitle">2026</h1>
            <section class="chapter" data-title="Results"><h2>Sales</h2><h2>Costs</h2></section>
            """;
        HtmlToPdf.Convert(bookmarks).Save("bookmarks.pdf");
        #endregion

        return Task.CompletedTask;
    }
}
