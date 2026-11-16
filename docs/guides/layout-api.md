# The layout API

`RaiCore.Pdf.Layout` lays out documents from code, with the same engine the HTML converter uses: paragraphs that wrap,
hyphenate and justify, boxes with borders and backgrounds, tables, flex rows, images, and pagination across pages.
Use it when the content comes from code rather than HTML, or to add to a document the converter made
(`HtmlToPdf.CreateFlowDocument` returns its laid-out document).

## A multi-page document

A `FlowDocument` stacks blocks like CSS block boxes and breaks them into pages. A page decorator draws headers and
footers on every page.

[!code-csharp[](../snippets/LayoutApi.cs#flow)]

| Block | What it is |
|---|---|
| `ParagraphBlock` | A `Paragraph`: text runs in `TextStyle`s, inline images and boxes, links and anchors |
| `ContainerBlock` | Child blocks in a box with margins, borders, padding and a background |
| `TableBlock` | Rows of cells, with header and footer rows repeated on every page |
| `FlexBlock` | A flex container (rows or columns, wrapping, gaps) |
| `ImageBlock` | An image as a block |
| `SpacerBlock` | Vertical space |

Every block takes a `BoxStyle`: margins (collapsing like CSS), borders, padding, background layers, rounded corners,
opacity, widths in points or percentages, floats, positioning and page breaks (`BreakBefore`, `BreakAfter`,
`BreakInside`). `Block.Bookmark` adds the block to the PDF outline and `Block.Link` makes it clickable.

## A paragraph on its own

A `Paragraph` can also be laid out to a width and drawn anywhere, for example on a page drawn with the canvas:

[!code-csharp[](../snippets/LayoutApi.cs#paragraph)]

Paragraphs break lines with the Unicode line breaking algorithm, order mixed-direction text with the bidi algorithm,
shape text with HarfBuzz, and support left, right, centered and justified text, line heights, first-line indents,
soft hyphens, automatic hyphenation (`Hyphens.Auto` with a `Language`, or your own patterns in
`TextStyle.Hyphenator`), letter and word spacing, decorations, and leaders.

## Thread safety

Fonts (`PdfFont`, `FontCollection`), images and hyphenators are immutable or thread-safe and meant to be shared.
Documents, pages, `FlowDocument` and `Paragraph` are not: build each document on one thread at a time, and different
documents in parallel.
