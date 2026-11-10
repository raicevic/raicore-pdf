# Changelog

All notable changes to RaiCore.Pdf are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/). Before 1.0, minor versions may change the public API.

## [Unreleased]

### Added
- Flexbox in `RaiCore.Pdf.Layout` (`FlexBlock`, `FlexItem`) and `RaiCore.Pdf.Html` (`display: flex` and
  `inline-flex`): all flex directions, `flex-wrap` (including `wrap-reverse`), `justify-content`,
  `align-items`/`align-self` (`stretch`, `flex-start`, `flex-end`, `center`, `baseline`), `gap`, `order`,
  `flex`/`flex-grow`/`flex-shrink`/`flex-basis`, min/max sizes with the automatic minimum size, and auto margins.
- Flex rows taller than the space left on a page are split like browsers do: every item continues on the next
  page in its own column, and stretched items run down to the end of the row.
- Running elements (`position: running()`) can be flex containers, e.g. a header with a logo and an address.

### Known limitations
- A flex container is as tall as its content: `height` on the container, `align-content` and `justify-content` in
  column containers have no effect. `inline-flex` is laid out like `flex`.

## [0.1.0] - Unreleased

The first release: HTML and Razor to PDF with RaiCore.Pdf's own layout engine.

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
  line heights, indents, soft hyphens and hard breaks.
- Block layout and pagination: the box model, margin collapsing, orphans and widows, forced and avoided page breaks,
  boxes sliced across pages, page decorators for headers and footers.
- Tables: automatic and fixed layout, `colspan`/`rowspan`, header rows repeated on every page, separate and collapsed
  borders.

### RaiCore.Pdf.Html
- HTML and CSS to PDF via AngleSharp: the cascade, inheritance, `!important`, custom properties and print media
  queries, with browser default styles.
- Block and inline formatting, white-space handling, the box model, fonts, colors, text alignment and transforms,
  list markers, images, linked stylesheets and tables (including the legacy table attributes).
- CSS Paged Media: `@page` size and margins, `:first`/`:left`/`:right`, margin boxes for headers and footers,
  `counter(page)`/`counter(pages)`, `string-set` and running elements.
- Network access is off unless you supply a `ResourceLoader`.

### RaiCore.Pdf.Razor
- Razor components to PDF with Blazor's static `HtmlRenderer`: `RazorToPdf` for the simple case, `RazorPdfRenderer`
  for components that inject services, and `AddRazorPdf()` to register it per scope.

### Known limitations
- Floats, positioning, flexbox and grid (rendered as blocks), `text-decoration`, inline box backgrounds and borders.
- A single table row is never split across pages; `:first`/`:left`/`:right` cannot change page margins.
- Mixed left-to-right and right-to-left text on one line (the Unicode bidi algorithm).
- No links, bookmarks, tagged PDF or PDF/A yet. WebP, SVG and BMP images are not supported.

[Unreleased]: https://github.com/raicevic/raicore-pdf/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/raicevic/raicore-pdf/releases/tag/v0.1.0
