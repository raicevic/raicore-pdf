// A business letter for a window envelope (DL or C5/6, DIN 5008 form B): the recipient's address sits where the
// envelope's window is, 20 mm from the left and 45 mm from the top edge of the page, with fold and punch marks in the
// margin. The page margins are 27 mm (top), 20 mm (right) and 25 mm (left), so positions inside the page area are those
// distances minus the margins.
// Usage: dotnet run --project samples/Letter [output-directory]
using RaiCore.Pdf.Html;

const string Html = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <title>Your quote, our reference 4711</title>
      <style>
        @page { size: A4; margin: 27mm 20mm 25mm 25mm; }
        body { font-family: Arial, sans-serif; font-size: 11pt; line-height: 1.35; }

        /* The address field: 85 x 45 mm, 20 mm from the left and 45 mm from the top edge of the page. */
        .window { position: absolute; left: calc(20mm - 25mm); top: calc(45mm - 27mm); width: 85mm; height: 45mm; }
        .window .return { font-size: 7pt; color: #555; border-bottom: 0.5pt solid #555; margin-bottom: 3mm; }

        /* Fold marks at 105 mm and 210 mm, and the punch mark at 148.5 mm, 5 mm from the left edge of the page. */
        .mark { position: absolute; left: calc(5mm - 25mm); width: 5mm; border-top: 0.5pt solid #000; }
        .fold-1 { top: calc(105mm - 27mm); }
        .fold-2 { top: calc(210mm - 27mm); }
        .punch { top: calc(148.5mm - 27mm); width: 7mm; }

        /* The sender's details beside the window. */
        .sender { position: absolute; left: calc(125mm - 25mm); top: calc(50mm - 27mm); width: 65mm; font-size: 9pt; }

        /* The letter starts below the address field (45 mm + 45 mm from the top edge). */
        .date { margin-top: calc(98mm - 27mm); text-align: right; }
        h1 { font-size: 11pt; margin: 8mm 0 6mm; }
      </style>
    </head>
    <body>
      <div class="mark fold-1"></div>
      <div class="mark punch"></div>
      <div class="mark fold-2"></div>

      <div class="window">
        <div class="return">Example Consulting &middot; Havnegade 1 &middot; 1058 Copenhagen K</div>
        Nordlys Design ApS<br>
        Attn. Ida Jensen<br>
        Vestergade 12<br>
        8000 Aarhus C<br>
        DENMARK
      </div>

      <div class="sender">
        <b>Example Consulting</b><br>
        Havnegade 1<br>
        1058 Copenhagen K<br>
        +45 12 34 56 78<br>
        hello@example.com
      </div>

      <p class="date">Copenhagen, 16 November 2026</p>
      <h1>Your quote, our reference 4711</h1>
      <p>Dear Ida Jensen,</p>
      <p>Thank you for your enquiry. We are pleased to offer the redesign of your web shop as described in the attached
      quote, at a fixed price and with delivery in the first quarter of next year.</p>
      <p>The offer is valid for 30 days. Please let us know if you have any questions; we are happy to go through the
      details in a meeting.</p>
      <p>Kind regards,</p>
      <p style="margin-top: 15mm">Mads Hansen<br>Example Consulting</p>
    </body>
    </html>
    """;

var output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "output").FullName;
var path = Path.Combine(output, "letter.pdf");
HtmlToPdf.Convert(Html).Save(path);
Console.WriteLine($"Wrote {path}");
