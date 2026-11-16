// A multi-page report: a title page, a table of contents with page numbers and dot leaders, running headers and
// footers, chapters on new pages, tables and bookmarks.
// Usage: dotnet run --project samples/Report [output-directory]
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Html;

string[] regions = ["North", "East", "South", "West"];
string[] chapters = ["Summary", "Sales by region", "Costs", "Customers", "Outlook"];

var html = new StringBuilder("""
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <title>Annual report 2026</title>
      <style>
        @page {
          size: A4;
          margin: 25mm 20mm;
          @top-left { content: string(chapter); font-size: 8pt; color: #555; }
          @top-right { content: "Annual report 2026"; font-size: 8pt; color: #555; }
          @bottom-center { content: counter(page) " / " counter(pages); font-size: 8pt; }
        }
        /* The title page has no header or footer. */
        @page title { @top-left { content: none; } @top-right { content: none; } @bottom-center { content: none; } }
        /* On html rather than body: the page's margin boxes (header, footer) inherit from the root element. */
        html { font-family: "Times New Roman", serif; }
        body { font-size: 11pt; line-height: 1.4; }
        .title { page: title; text-align: center; padding-top: 80mm; }
        .title h1 { font-size: 32pt; margin: 0; }
        h1, h2 { font-family: Arial, sans-serif; color: #1a4f8b; }
        h2 { break-before: page; string-set: chapter content(text); }
        nav h2 { string-set: none; }
        nav ol { list-style: none; padding: 0; }
        nav li { margin: 2mm 0; }
        nav a { color: inherit; text-decoration: none; }
        /* The page number of the chapter a link points to, after a row of dots. */
        nav a::after { content: leader(".") target-counter(attr(href), page); }
        table { width: 100%; border-collapse: collapse; margin: 4mm 0; }
        th, td { border: 0.5pt solid #999; padding: 1.5mm 2mm; }
        th { background: #e8eef6; text-align: left; }
        td.num { text-align: right; }
        p { text-align: justify; hyphens: auto; }
      </style>
    </head>
    <body>
      <section class="title"><h1>Annual report 2026</h1><p>Example Group</p></section>
      <nav><h2>Contents</h2><ol>
    """);

for (var i = 0; i < chapters.Length; i++)
{
    html.Append(CultureInfo.InvariantCulture, $"""<li><a href="#chapter-{i + 1}">{i + 1}. {chapters[i]}</a></li>""");
}

html.Append("</ol></nav>");

for (var i = 0; i < chapters.Length; i++)
{
    html.Append(CultureInfo.InvariantCulture, $"""<h2 id="chapter-{i + 1}">{i + 1}. {chapters[i]}</h2>""");
    for (var p = 0; p < 6 + i; p++)
    {
        html.Append("""
            <p>This year the group grew in every region. Revenue rose faster than costs, so the operating margin
            improved for the third year running, while investments in new products and in the organisation
            continued as planned. The table below breaks the figures down by quarter.</p>
            """);
    }

    html.Append("<table><thead><tr><th>Region</th><th>Q1</th><th>Q2</th><th>Q3</th><th>Q4</th></tr></thead><tbody>");
    foreach (var region in regions)
    {
        html.Append(CultureInfo.InvariantCulture, $"<tr><td>{region}</td>");
        for (var q = 1; q <= 4; q++)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<td class="num">{(100 + i * 10 + q * 7 + region.Length * 3) * 1000:N0}</td>""");
        }

        html.Append("</tr>");
    }

    html.Append("</tbody></table>");
}

html.Append("</body></html>");

var output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "output").FullName;
var path = Path.Combine(output, "report.pdf");

// Headings become bookmarks (the PDF outline) by default.
var pdf = await HtmlToPdf.ConvertAsync(html.ToString());
pdf.Save(path);
Console.WriteLine($"Wrote {path} ({pdf.Pages.Count} pages)");
