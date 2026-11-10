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

- Paragraphs: alignment and justification, line heights, indents, soft hyphens, orphans and widows; inline images
  and inline boxes (any block as one item of a line, like CSS `inline-block`).
- Blocks: margins (with collapsing), borders in every CSS style, rounded corners (circular, elliptical or percentage
  radii), padding, backgrounds, widths and heights with min/max limits, forced and avoided page breaks, and opacity
  (`BoxStyle.Opacity`, drawn as a transparency group on every page the block is on).
- Tables: automatic and fixed layout, spans, header and footer rows repeated on every page, rows split across pages,
  separate or collapsed borders.
- Flexbox (`FlexBlock`): rows and columns, wrapping, grow/shrink/basis, justification, alignment including baselines,
  `align-content`, gaps, auto margins and containers with a fixed or minimum height; rows split across pages side by
  side.
- Bookmarks: `Block.Bookmark` adds a block to the PDF outline, nested by level.
- Links: in text (`Paragraph.AddLink`, `BeginLink`/`EndLink`) and on whole blocks (`Block.Link`), to URIs or to
  blocks and paragraph anchors by id (`Block.Id`, `Paragraph.AddAnchor`).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
