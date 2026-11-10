# Changelog

All notable changes to RaiCore.Pdf are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Nothing has been released yet: the first release will be 1.0.0, once the
[roadmap milestones](https://github.com/raicevic/raicore-pdf/milestones) are done. Until then, the public API may
change at any time, and this section lists what the packages can do so far.

### RaiCore.Pdf
- PDF writer with compressed streams, document information and the 14 standard fonts.
- Embedded TrueType, OpenType (CFF embedded whole), TTC, WOFF and WOFF2 fonts, subset to the glyphs used, with
  ToUnicode maps so text stays searchable and copyable. Fonts whose license forbids embedding are rejected.
- JPEG (embedded without re-encoding, EXIF orientation applied), PNG (all color types and bit depths, interlacing,
  transparency; verified against PngSuite) and GIF images, each stored once per document.
- Drawing API: paths, fills, strokes, colors, text and images.

### RaiCore.Pdf.Fonts
- Bundled Liberation Sans, Serif and Mono (SIL OFL 1.1), metric-compatible with Arial, Times New Roman and Courier New.
- `FontCollection` with CSS font matching, system font discovery and metric-compatible substitutes.

### RaiCore.Pdf.Layout
- HarfBuzz text shaping (kerning, ligatures, Arabic joining, Indic reordering, mark positioning) with per-character
  font fallback.
- Unicode line breaking (UAX #14, passing the full conformance test) and paragraphs with alignment, justification,
  line heights, indents, soft hyphens and hard breaks; inline images and inline boxes (`Paragraph.AddBox`: any block
  as one item of a line, shrink-to-fit, aligned on its last baseline); `InlineAlignment` positions inline text, images
  and boxes vertically (CSS 2.1 section 10.8); `Paragraph.BeginInline`/`EndInline`/`AddInline` give part of the text an
  inline element's box (`InlineDecoration`).
- Block layout and pagination: the box model, margin collapsing, orphans and widows, forced and avoided page breaks,
  boxes sliced across pages, page decorators for headers and footers. A paragraph whose orphans and widows leave no
  possible break needs all its lines at the bottom of a page before it starts there.
- `height`, `min-height` and `max-height` on every block (`BoxStyle.Height`, `MinHeight`, `MaxHeight`). Content
  taller than a fixed height overflows, as with `overflow: visible`; a fixed-height box that fits on a page is not
  split.
- Tables: automatic and fixed layout, `colspan`/`rowspan`, separate and collapsed borders. Header rows repeat at the
  top of every page and footer rows (`TableRow.IsFooter`) right below the body; footers taller than a quarter of the
  page are not repeated, as in Chrome. Rows that do not fit are split like browsers do, each cell continuing on the
  next page in its own column with backgrounds and borders sliced; rows joined by row spans may also break between
  them, with the spanning cell continuing alongside. `TableRow.BreakInside` keeps a row whole when it fits on a page.
- Flexbox (`FlexBlock`, `FlexItem`): all directions, wrapping (including `wrap-reverse`), `justify-content`,
  `align-items`/`align-self` (`stretch`, `flex-start`, `flex-end`, `center`, `baseline`), `align-content`, gaps,
  `order`, grow/shrink/basis with min/max sizes and the automatic minimum size, and auto margins. Containers with a
  definite or minimum height flex their columns vertically and wrap into side-by-side columns; a flex item's flexed or
  stretched size is definite for a flex container inside it. Rows taller than the space left on a page are split like
  browsers do, each item continuing in its own column and stretched items running down to the end of the row.

### RaiCore.Pdf.Html
- HTML and CSS to PDF via AngleSharp: the cascade, inheritance, `!important`, custom properties and print media
  queries, with browser default styles.
- Block and inline formatting, white-space handling, the box model, fonts, colors, text alignment and transforms,
  list markers, images, linked stylesheets and tables (including the legacy table attributes, `thead`, `tfoot` and
  `tr { break-inside: avoid }`).
- `display: flex`, with all the flex properties above.
- Backgrounds, borders, padding and horizontal margins on inline elements: drawn behind the text of each line they
  cover (the font's content area plus padding and border, raised with the element), sliced at line breaks and around
  blocks inside them; horizontal edges take space on the line, vertical ones do not change its height.
- `vertical-align` on inline content (text, images, inline boxes): `sub` and `super` (a fifth and a third of the
  parent's font size, as browsers do; `<sub>`/`<sup>` use them by default), lengths, percentages of the line height,
  `middle`, `text-top`, `text-bottom`, `top` and `bottom`. Nested shifts add up; a block's or table cell's own
  `vertical-align` does not move its text.
- `inline-block`, `inline-flex` and `inline-table` as atomic inline boxes: one unbreakable item of the line with its
  own padding, border, background and width (or shrink-to-fit), aligned on the baseline of its last line of text (the
  first row for tables, the first baseline for flex containers, the bottom margin edge without text).
- `height`, `min-height` and `max-height`; percentage heights resolve against definite ancestor heights, starting
  with the page area, and `vh` units are the page area (to within a pixel).
- CSS Paged Media: `@page` size and margins, `:first`/`:left`/`:right`, margin boxes for headers and footers,
  `counter(page)`/`counter(pages)`, `string-set` and running elements (which can be flex containers).
- Web fonts: `@font-face` with `url()` and `local()` sources, `format()` hints (unsupported formats are not
  downloaded), weight ranges, `font-style` and `unicode-range`, matched like CSS Fonts 4 and loaded on first use. A
  declared family hides installed fonts of the same name; a family that fails to load falls back to the next one.
  `PdfFont.WithUnicodeRanges` restricts a font to code point ranges, embedding it once however many views use it.
- Network access is off unless you supply a `ResourceLoader`.

### RaiCore.Pdf.Razor
- Razor components to PDF with Blazor's static `HtmlRenderer`: `RazorToPdf` for the simple case, `RazorPdfRenderer`
  for components that inject services, and `AddRazorPdf()` to register it per scope.

### Known limitations
The [roadmap issues](https://github.com/raicevic/raicore-pdf/issues) track these.
- No `::before`/`::after`, links, bookmarks or `text-decoration`. Borders on inline elements are drawn solid.
- Floats, positioning and grid (rendered as blocks); `calc()`, `overflow: hidden`, transparency (blended with white),
  border styles other than solid, rounded corners and background images.
- Mixed left-to-right and right-to-left text on one line (the Unicode bidi algorithm); no hyphenation.
- No synthetic bold or italic: a family without a bold or italic face uses its closest face as is.
- `:first`/`:left`/`:right` cannot change page margins; no named pages.
- No tagged PDF, PDF/A or encryption. WebP, SVG and BMP images are not supported.
