# RaiCore.Pdf.Layout

The format-neutral layout engine behind [RaiCore.Pdf.Html](https://www.nuget.org/packages/RaiCore.Pdf.Html):
text shaping with HarfBuzz (kerning, ligatures, Arabic, Indic) and per-character font fallback, Unicode line breaking
(UAX #14), paragraphs, CSS-like block layout, pagination and tables. Use it directly to build documents in code.

```csharp
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

var fonts = BundledFonts.CreateCollection();
var body = new TextStyle(fonts.Find("serif")!, 11) { LineHeight = LineHeight.Multiple(1.4) };

var flow = new FlowDocument(new PageSetup(PdfPageSize.A4, Thickness.All(56)));
flow.Add(new ParagraphBlock(new Paragraph(body) { Alignment = TextAlignment.Justify }.Add("Text that flows across pages.")));
flow.PageDecorator = (canvas, page) => { /* headers, footers, "Page {page.Number} of {page.Count}" */ };
flow.Render().Save("report.pdf");
```

- Paragraphs: alignment and justification, line heights, indents, soft hyphens, orphans and widows.
- Blocks: margins (with collapsing), borders, padding, backgrounds, forced and avoided page breaks.
- Tables: automatic and fixed layout, spans, header rows repeated on every page, separate or collapsed borders.
- Flexbox (`FlexBlock`): rows and columns, wrapping, grow/shrink/basis, justification, alignment including baselines,
  gaps and auto margins; rows split across pages side by side.

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
