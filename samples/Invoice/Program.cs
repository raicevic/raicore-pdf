// An invoice from your own data: an HTML template filled in C#, converted to PDF.
// Usage: dotnet run --project samples/Invoice [output-directory]
using System.Globalization;
using System.Net;
using RaiCore.Pdf.Html;

var invoice = new Invoice(
    Number: "2026-0042",
    Date: new DateOnly(2026, 11, 16),
    Customer: ["Nordlys Design ApS", "Vestergade 12", "8000 Aarhus C", "Denmark"],
    Lines: [.. Enumerable.Range(1, 40).Select(i => new InvoiceLine($"Consulting, week {i}", 7.5m, 950m + i % 3 * 50m))]);

var output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "output").FullName;
var path = Path.Combine(output, $"invoice-{invoice.Number}.pdf");

// Convert straight to a file: pages are written as soon as they are laid out.
await using (var file = File.Create(path))
{
    await HtmlToPdf.ConvertToStreamAsync(InvoiceHtml(invoice), file, new HtmlConverterOptions { Language = "en-GB" });
}

Console.WriteLine($"Wrote {path}");

static string InvoiceHtml(Invoice invoice)
{
    var culture = CultureInfo.GetCultureInfo("da-DK");
    string Money(decimal amount) => WebUtility.HtmlEncode(amount.ToString("N2", culture) + " kr.");
    string Text(string text) => WebUtility.HtmlEncode(text);

    var net = invoice.Lines.Sum(l => l.Total);
    var vat = Math.Round(net * 0.25m, 2);
    var rows = string.Concat(invoice.Lines.Select(l => $"""
        <tr><td>{Text(l.Description)}</td><td class="num">{l.Hours.ToString("0.0", culture)}</td>
            <td class="num">{Money(l.Rate)}</td><td class="num">{Money(l.Total)}</td></tr>
        """));

    return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <title>Invoice {{Text(invoice.Number)}}</title>
          <meta name="author" content="Example Consulting">
          <style>
            @page {
              size: A4;
              margin: 20mm 18mm 25mm;
              @bottom-left { content: "Example Consulting \00B7  CVR 12345678"; font-size: 8pt; color: #666; }
              @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; color: #666; }
            }
            /* On html rather than body: the page's margin boxes (header, footer) inherit from the root element. */
            html { font-family: Arial, sans-serif; }
            body { font-size: 10pt; color: #222; }
            header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 12mm; }
            .logo { font-size: 20pt; font-weight: bold; color: #1a4f8b; }
            .meta { text-align: right; }
            address { font-style: normal; }
            h1 { font-size: 16pt; margin: 10mm 0 4mm; }
            table { width: 100%; border-collapse: collapse; }
            thead th { background: #1a4f8b; color: white; text-align: left; padding: 2mm; }
            td { padding: 1.5mm 2mm; border-bottom: 0.5pt solid #ccc; }
            tbody tr:nth-child(even) td { background: #f3f6fa; }
            tr { break-inside: avoid; }
            .num { text-align: right; white-space: nowrap; }
            .totals { width: 70mm; margin: 6mm 0 0 auto; break-inside: avoid; }
            .totals td { border: none; }
            .totals .grand td { font-weight: bold; border-top: 1pt solid #222; }
          </style>
        </head>
        <body>
          <header>
            <div class="logo">Example Consulting</div>
            <div class="meta">Invoice {{Text(invoice.Number)}}<br>{{invoice.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}}</div>
          </header>
          <address>{{string.Join("<br>", invoice.Customer.Select(Text))}}</address>
          <h1>Invoice {{Text(invoice.Number)}}</h1>
          <table>
            <thead><tr><th>Description</th><th class="num">Hours</th><th class="num">Rate</th><th class="num">Amount</th></tr></thead>
            <tbody>{{rows}}</tbody>
          </table>
          <table class="totals">
            <tr><td>Net</td><td class="num">{{Money(net)}}</td></tr>
            <tr><td>VAT 25%</td><td class="num">{{Money(vat)}}</td></tr>
            <tr class="grand"><td>Total</td><td class="num">{{Money(net + vat)}}</td></tr>
          </table>
          <p>Payment within 14 days. Thank you for your business.</p>
        </body>
        </html>
        """;
}

internal sealed record Invoice(string Number, DateOnly Date, IReadOnlyList<string> Customer, IReadOnlyList<InvoiceLine> Lines);

internal sealed record InvoiceLine(string Description, decimal Hours, decimal Rate)
{
    public decimal Total => Hours * Rate;
}
