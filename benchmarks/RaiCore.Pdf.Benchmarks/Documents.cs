using System.Globalization;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Benchmarks;

/// <summary>
/// The documents the benchmarks render. Everything is generated deterministically (no randomness, no clock), so runs
/// on different machines and commits lay out exactly the same content.
/// </summary>
public static class Documents
{
    private static readonly string[] Words =
    [
        "the", "report", "shows", "that", "quarterly", "revenue", "increased", "across", "all", "regions", "while",
        "operating", "costs", "remained", "stable", "customers", "responded", "well", "to", "new", "pricing", "and",
        "delivery", "times", "improved", "significantly", "in", "most", "markets", "management", "expects", "growth",
        "continue", "through", "next", "year", "despite", "uncertainty", "supply", "chains", "investments",
        "infrastructure", "will", "support", "expansion", "of", "production", "capacity", "a", "detailed", "analysis",
    ];

    /// <summary>Deterministic pseudo-text: <paramref name="count"/> words starting at word <paramref name="seed"/>.</summary>
    public static string Text(int seed, int count)
    {
        var builder = new StringBuilder();
        var state = (uint)seed * 2654435761u + 1;
        for (var i = 0; i < count; i++)
        {
            state = state * 1664525u + 1013904223u;
            var word = Words[(state >> 8) % (uint)Words.Length];
            if (i == 0)
            {
                word = char.ToUpperInvariant(word[0]) + word[1..];
            }

            builder.Append(i == 0 ? "" : " ").Append(word);
        }

        return builder.Append('.').ToString();
    }

    /// <summary>A one-page invoice: header, addresses, a 15-line items table, totals and a page footer.</summary>
    public static string InvoiceHtml()
    {
        var rows = new StringBuilder();
        var total = 0m;
        for (var i = 1; i <= 15; i++)
        {
            var quantity = i % 4 + 1;
            var price = 12.5m * i;
            total += quantity * price;
            rows.Append(CultureInfo.InvariantCulture, $"<tr><td>{i}</td><td>{Text(i, 5)}</td><td class=\"n\">{quantity}</td><td class=\"n\">{price:N2}</td><td class=\"n\">{quantity * price:N2}</td></tr>\n");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <title>Invoice 2026-0042</title>
              <style>
                @page { size: A4; margin: 20mm; @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; } }
                body { font-family: "Liberation Sans", sans-serif; font-size: 10pt; color: #222; }
                header { display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 2px solid #1a4f8b; padding-bottom: 8pt; }
                h1 { color: #1a4f8b; font-size: 22pt; margin: 0; }
                .addresses { display: flex; gap: 24pt; margin: 16pt 0; }
                .addresses div { flex: 1; background: #f3f6fa; border-radius: 4pt; padding: 8pt; }
                table { width: 100%; border-collapse: collapse; }
                th, td { border-bottom: 1px solid #ccc; padding: 4pt 6pt; }
                th { background: #e8eef6; text-align: left; }
                .n { text-align: right; }
                .totals { margin-left: auto; width: 40%; margin-top: 12pt; }
                .totals td { border: none; }
                .grand td { font-weight: bold; border-top: 2px solid #1a4f8b; }
                footer { margin-top: 24pt; font-size: 8pt; color: #666; }
              </style>
            </head>
            <body>
              <header>
                <div><h1>Invoice</h1><p>No. 2026-0042<br>Date: 11 November 2026<br>Due: 11 December 2026</p></div>
                <div style="text-align: right"><strong>Example Supplies Ltd.</strong><br>1 Market Street<br>1000 Example City</div>
              </header>
              <section class="addresses">
                <div><strong>Bill to</strong><br>Customer Corporation<br>22 Harbour Road<br>2000 Port Town</div>
                <div><strong>Ship to</strong><br>Customer Corporation, Warehouse 3<br>5 Industrial Park<br>2100 Port Town</div>
              </section>
              <table>
                <thead><tr><th>#</th><th>Description</th><th class="n">Qty</th><th class="n">Unit price</th><th class="n">Amount</th></tr></thead>
                <tbody>
            {{rows}}
                </tbody>
              </table>
              <table class="totals">
                <tr><td>Subtotal</td><td class="n">{{total.ToString("N2", CultureInfo.InvariantCulture)}}</td></tr>
                <tr><td>VAT 25%</td><td class="n">{{(total * 0.25m).ToString("N2", CultureInfo.InvariantCulture)}}</td></tr>
                <tr class="grand"><td>Total</td><td class="n">{{(total * 1.25m).ToString("N2", CultureInfo.InvariantCulture)}}</td></tr>
              </table>
              <footer>{{Text(99, 40)}}</footer>
            </body>
            </html>
            """;
    }

    /// <summary>A 10,000-row table with a repeated header row (about 250 pages).</summary>
    public static string TableHtml(int rows = 10_000)
    {
        var body = new StringBuilder(rows * 120);
        for (var i = 1; i <= rows; i++)
        {
            body.Append(CultureInfo.InvariantCulture, $"<tr><td>{i}</td><td>{Words[i % Words.Length]} {Words[i * 7 % Words.Length]}</td><td>{2020 + i % 7}-{i % 12 + 1:D2}-{i % 28 + 1:D2}</td><td class=\"n\">{i * 13.37:N2}</td></tr>\n");
        }

        return $$"""
            <!DOCTYPE html>
            <html><head><style>
              @page { size: A4; margin: 15mm; @bottom-center { content: counter(page) " / " counter(pages); font-size: 8pt; } }
              body { font-family: "Liberation Sans", sans-serif; font-size: 9pt; }
              table { width: 100%; border-collapse: collapse; }
              th, td { border: 1px solid #bbb; padding: 2pt 4pt; }
              th { background: #dde; }
              .n { text-align: right; }
            </style></head>
            <body>
              <h1>Ledger</h1>
              <table>
                <thead><tr><th>#</th><th>Account</th><th>Date</th><th class="n">Amount</th></tr></thead>
                <tbody>
            {{body}}
                </tbody>
              </table>
            </body></html>
            """;
    }

    /// <summary>A flex-heavy page set: a dashboard of wrapped cards, each a nested flex row and column.</summary>
    public static string FlexHtml(int cards = 240)
    {
        var body = new StringBuilder();
        for (var i = 0; i < cards; i++)
        {
            body.Append(CultureInfo.InvariantCulture, $$"""
                <div class="card">
                  <div class="head"><span class="badge">{{i + 1}}</span><span class="title">{{Text(i, 3)}}</span><span class="value">{{i * 37 % 1000}}</span></div>
                  <div class="body"><p>{{Text(i + 1000, 12 + i % 9)}}</p><div class="bars"><div style="flex: {{i % 5 + 1}}"></div><div style="flex: {{(i + 2) % 5 + 1}}"></div><div style="flex: 1"></div></div></div>
                </div>

                """);
        }

        return $$"""
            <!DOCTYPE html>
            <html><head><style>
              @page { size: A4; margin: 12mm; }
              body { font-family: "Liberation Sans", sans-serif; font-size: 8pt; }
              .grid { display: flex; flex-wrap: wrap; gap: 6pt; }
              .card { flex: 1 1 150pt; display: flex; flex-direction: column; border: 1px solid #99a; border-radius: 4pt; padding: 4pt; }
              .head { display: flex; align-items: center; gap: 4pt; }
              .badge { background: #1a4f8b; color: white; border-radius: 3pt; padding: 1pt 3pt; }
              .title { flex: 1; font-weight: bold; }
              .value { color: #1a4f8b; }
              .body { display: flex; flex-direction: column; gap: 3pt; }
              .body p { margin: 2pt 0; }
              .bars { display: flex; gap: 2pt; height: 6pt; }
              .bars div { background: #8ab; }
            </style></head>
            <body><h1>Dashboard</h1><div class="grid">
            {{body}}
            </div></body></html>
            """;
    }

    /// <summary>A long report in HTML: chapters of headings and paragraphs (about 100 pages).</summary>
    public static string ReportHtml(int chapters = 25)
    {
        var body = new StringBuilder();
        for (var c = 1; c <= chapters; c++)
        {
            body.Append(CultureInfo.InvariantCulture, $"<h1 id=\"c{c}\">Chapter {c}</h1>\n");
            for (var s = 1; s <= 4; s++)
            {
                body.Append(CultureInfo.InvariantCulture, $"<h2>Section {c}.{s}</h2>\n");
                for (var p = 0; p < 5; p++)
                {
                    body.Append(CultureInfo.InvariantCulture, $"<p>{Text(c * 1000 + s * 100 + p, 60 + (p * 17 % 50))}</p>\n");
                }
            }
        }

        return $$"""
            <!DOCTYPE html>
            <html><head><title>Annual report</title><style>
              @page { size: A4; margin: 20mm; @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; } }
              body { font-family: "Liberation Serif", serif; font-size: 11pt; line-height: 1.4; }
              h1 { break-before: page; font-family: "Liberation Sans", sans-serif; color: #1a4f8b; }
              h2 { font-family: "Liberation Sans", sans-serif; font-size: 13pt; }
              p { text-align: justify; hyphens: auto; }
            </style></head>
            <body lang="en">
            {{body}}
            </body></html>
            """;
    }

    /// <summary>
    /// A long report built with the layout API (about 100 pages): bookmarked headings, justified paragraphs and a
    /// "page X of Y" footer.
    /// </summary>
    public static FlowDocument ReportFlow(FontCollection fonts, int chapters = 34)
    {
        var serif = fonts.Find("Liberation Serif")!;
        var sans = fonts.Find("Liberation Sans", 700)!;
        var body = new TextStyle(serif, 11) { LineHeight = LineHeight.Multiple(1.4) };
        var h1 = new TextStyle(sans, 20) { Color = PdfColor.FromRgb(26, 79, 139) };
        var h2 = new TextStyle(sans, 13);
        var footer = new TextStyle(serif, 8);

        var flow = new FlowDocument(PageSetup.A4);
        for (var c = 1; c <= chapters; c++)
        {
            flow.Add(new ParagraphBlock(
                new Paragraph(h1).Add(string.Create(CultureInfo.InvariantCulture, $"Chapter {c}")),
                new BoxStyle { BreakBefore = c > 1 ? PageBreak.Page : PageBreak.Auto, BreakAfter = PageBreak.Avoid, Margin = new(0, 0, 12, 0) })
            {
                Bookmark = new Bookmark(string.Create(CultureInfo.InvariantCulture, $"Chapter {c}"), 1),
            });
            for (var s = 1; s <= 4; s++)
            {
                flow.Add(new ParagraphBlock(
                    new Paragraph(h2).Add(string.Create(CultureInfo.InvariantCulture, $"Section {c}.{s}")),
                    new BoxStyle { BreakAfter = PageBreak.Avoid, Margin = new(10, 0, 6, 0) })
                {
                    Bookmark = new Bookmark(string.Create(CultureInfo.InvariantCulture, $"Section {c}.{s}"), 2),
                });
                for (var p = 0; p < 5; p++)
                {
                    var paragraph = new Paragraph(body) { Alignment = TextAlignment.Justify }
                        .Add(Text(c * 1000 + s * 100 + p, 60 + (p * 17 % 50)));
                    flow.Add(new ParagraphBlock(paragraph, new BoxStyle { Margin = new(0, 0, 8, 0) }));
                }
            }
        }

        flow.PageDecorator = (canvas, page) =>
        {
            var text = new Paragraph(footer) { Alignment = TextAlignment.Center }
                .Add(string.Create(CultureInfo.InvariantCulture, $"Page {page.Number} of {page.Count}"));
            text.Layout(page.Setup.ContentWidth).Draw(canvas, page.Setup.Margins.Left, 40);
        };
        return flow;
    }
}
