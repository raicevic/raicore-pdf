# RaiCore.Pdf.Extraction

Reads the text of existing PDF files, page by page, for [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf).
It works on files from any producer, in managed code with no native dependencies.

```csharp
using RaiCore.Pdf;
using RaiCore.Pdf.Extraction;

var extractor = new TextExtractor(PdfReader.Open("report.pdf"));
foreach (var page in extractor.ExtractPages())
{
    Console.WriteLine(page.HasText ? page.Text : $"Page {page.PageIndex + 1} is a scan");
}
```

- **Reading order:**
  - lines from top to bottom, with spaces between words even where the file has none;
  - columns read one after the other, tables row by row;
  - Arabic and Hebrew in logical order;
  - turned pages and vertical writing handled.
- **Other modes:** `Layout` keeps columns and table cells in place on each line, as `pdftotext -layout` does. `Raw` follows
  the order the text is drawn in.
- **Fonts:** ToUnicode maps, the standard encodings, glyph names, composite fonts with their CMaps, Type 3 fonts, and
  embedded TrueType programs.
- **Clean output:**
  - ligatures and presentation forms become letters;
  - text drawn twice for a bold effect is read once;
  - hidden text (clipped away or outside the page) is left out.
- **Scans:** `HasText` tells you which pages have no text, so you can send them to OCR. Invisible text, such as an
  existing OCR layer, is included unless you turn it off.
- **Made for untrusted input:** malformed content is skipped and reported in `PageText.Diagnostics`, never thrown,
  and the work per page is bounded by the [resource limits](https://raicevic.github.io/raicore-pdf/).

More: the [text extraction guide](https://raicevic.github.io/raicore-pdf/guides/text-extraction.html). Text search with
positions and image extraction are coming next: see the [roadmap](https://github.com/raicevic/raicore-pdf/milestone/8).

The glyph name table is the Adobe Glyph List (BSD-3-Clause), and the presentation form table comes from the Unicode
Character Database (Unicode License v3); both licenses are included in the package under `licenses/`. The code is
Apache-2.0.

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
