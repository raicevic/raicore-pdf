# The layout API

`RaiCore.Pdf.Layout` lays out documents from code, with the same engine the HTML converter uses: paragraphs that wrap,
hyphenate and justify, boxes with borders and backgrounds, tables, flex rows, images, and pagination across pages.
Use it when the content comes from code rather than HTML, or to add to a document the converter made
(`HtmlToPdf.CreateFlowDocument` returns its laid-out document). HTML fragments can go in it too (see
[HTML fragments](#html-fragments)).

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

## HTML fragments

Rich text often arrives as HTML: notes stored in a database, a description from a CMS. `HtmlFragment.Parse` (in
`RaiCore.Pdf.Html`) converts such a fragment into layout blocks with the HTML converter's engine, to place between
the blocks of a document built in code or in a table cell:

[!code-csharp[](../snippets/LayoutApi.cs#fragment)]

- **Base style:** `HtmlFragmentOptions.BaseStyle` is the `TextStyle` the fragment starts from: its font family (the
  families of `TextStyle.Fonts`), size, color, line height, letter and word spacing, language and hyphenation are the
  fragment root's, which its elements inherit unless the fragment's CSS says otherwise. Relative sizes follow it (an
  `h1` is `2em` of it), and bold and italic text uses the faces of the same family in `Fonts`. Without a base style
  the fragment starts from the HTML defaults (16 px serif), as a converted document does.
- **CSS:** `HtmlFragmentOptions.Css` is a style sheet for the fragment, applied before its own `<style>` elements and
  `style` attributes, which win where they conflict.
- **Width:** `vw` and the percentages the converter resolves while it builds blocks (such as `text-indent`) are of
  `HtmlFragmentOptions.Width`: set it to the content width of the page or cell. Text wraps to the width the blocks get
  when they are laid out.
- **Resources:** images, linked style sheets and `@font-face` fonts load through `ResourceLoader` relative to
  `BaseUri`, within `Limits`, as in a conversion. `ParseAsync` takes a `CancellationToken`.
- **Pages:** the pages are the document's, so `@page` rules, running elements (`position: running()`), running strings
  (`string-set`), named pages (`page`) and the page numbers of `target-counter()` are left out and reported in
  `HtmlFragment.Diagnostics`, with resources that did not load and unsupported CSS. Page breaks (`break-before`,
  `break-after`, `break-inside`) apply as `BoxStyle` breaks do. Headings become bookmarks only with
  `CreateBookmarks = true`.
- **Tagged PDF:** the blocks carry the same structure tags as in a converted document (headings, paragraphs, lists,
  tables, figures with `alt` text, links), so a tagged document (`PdfDocument.Tagged`) tags them the same way.

A fragment renders as the same HTML does in a converted document of the same width: the words land at the same
places, moved by what comes before them. Its blocks belong to one place in one document; parse the HTML again to
place it again.

## Thread safety

Fonts (`PdfFont`, `FontCollection`), images and hyphenators are immutable or thread-safe and meant to be shared.
Documents, pages, `FlowDocument` and `Paragraph` are not: build each document on one thread at a time, and different
documents in parallel.
