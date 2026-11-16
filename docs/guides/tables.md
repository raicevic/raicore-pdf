# Tables and pagination

## Tables across pages

Header rows (`<thead>`) repeat at the top of every page a table continues on, and footer rows (`<tfoot>`) at the
bottom. A row that does not fit is split like browsers split it, each cell continuing on the next page below the
repeated header; `break-inside: avoid` keeps rows whole.

[!code-csharp[](../snippets/Tables.cs#table)]

- Automatic and fixed (`table-layout: fixed`) column widths, `<col>` widths, `colspan` and `rowspan`.
- `border-collapse: collapse` resolves conflicting borders by width and style as in CSS 2.1; `separate` with
  `border-spacing`.
- Captions, `vertical-align` in cells, and the legacy `border`, `cellpadding`, `cellspacing` and `width` attributes
  of e-mail and older HTML.
- Right-to-left tables put their first column on the right.

## Page breaks

[!code-csharp[](../snippets/Tables.cs#breaks)]

- Forced breaks: `break-before` and `break-after` with `page`, `left`, `right`, `recto` or `verso` (the side-specific
  ones insert a blank page when needed). A forced break on an element's first child counts once, before the element.
- Avoided breaks: `break-after: avoid` keeps a heading with what follows, `break-inside: avoid` keeps a box, figure or
  row together (unless it is taller than a page).
- Paragraphs split between lines, with `orphans` and `widows` (2 by default).
- Boxes split across pages are sliced: the top border on the first page, the bottom border on the last, and each
  part shows its slice of the background.
- The legacy `page-break-before`, `page-break-after` and `page-break-inside` work as their `break-*` equivalents.
