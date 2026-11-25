# Paged media: pages, headers, footers and page numbers

Page size, margins, headers, footers and page numbers come from CSS Paged Media and Generated Content for Paged Media
(GCPM), as when a browser prints, so the same stylesheet describes the document everywhere.

## Headers, footers and page numbers

[!code-csharp[](../snippets/PagedMedia.cs#headers-footers)]

- **Page sizes:** lengths (`size: 210mm 297mm`), `landscape` and `portrait`, and the names `A0`-`A10`, `B0`-`B10`,
  `JIS-B4`, `JIS-B5`, `letter`, `legal` and `ledger`. Without an `@page` rule, `HtmlConverterOptions.Page` applies
  (A4 with 2 cm margins).
- **Margin boxes:** `@top-left`, `@top-center`, `@top-right`, the same at the bottom, and the corners, with font
  (also the `font` shorthand), line height, color, alignment and `vertical-align`. They inherit from the root element,
  so set the document's font on `html`.
- **Content:** strings, `counter(page)` and `counter(pages)` in any counter style, `string()` (with `first`, `start`,
  `last` or `first-except`) for text the document sets with `string-set`, and `element()` for running elements, which
  can be any HTML: a logo, a table, several lines.
- **Page selectors:** `:first`, `:left`, `:right`, `:blank` and page names, combined (`@page chapter:first`) and in
  lists. Each page gets its own size, margins and margin boxes, for example mirrored margins on left and right pages.
- **Page counters:** `counter-reset`, `counter-increment` and `counter-set` in `@page` rules change the page counter,
  e.g. `@page :first { counter-set: page 5 }`.

## Named pages

An element with `page: name` goes on pages of that name, which can have another size, orientation or margins. A
change of page name starts a new page.

[!code-csharp[](../snippets/PagedMedia.cs#named-pages)]

## Page breaks

`break-before` and `break-after` (`page`, `left`, `right`, `recto`, `verso`, `avoid`), `break-inside: avoid`,
`orphans` and `widows`; the legacy `page-break-*` properties work too. A break to a left or right page inserts a blank
page when needed, styled with `@page :blank`. See [Tables and pagination](tables.md#page-breaks).

## Tables of contents and cross-references

`target-counter()` gives the page number of the element a link points to, `target-text()` its text, and `leader()`
fills the line up to the page number. Documents that use them are laid out more than once (until the page numbers
stop changing), so they take two to three times as long to convert.

[!code-csharp[](../snippets/PagedMedia.cs#toc)]

## Bookmarks

Headings become PDF bookmarks, the outline in a viewer's navigation panel, nested by level.
`HtmlConverterOptions.CreateBookmarks = false` turns them off; the GCPM properties customize them:

[!code-csharp[](../snippets/PagedMedia.cs#bookmarks)]

- `bookmark-level`: `none` or a level (`h1`-`h6` default to 1-6).
- `bookmark-label`: strings, `content()` (the element's text, the default), `content(before)`, `content(after)`,
  `content(first-letter)` and `attr()`.
- `bookmark-state`: `open` (the default) or `closed`.

## Links

`<a href>` makes text clickable: web pages, `mailto:`, `tel:` and other URIs (relative URLs resolve against
`HtmlConverterOptions.BaseUri`), and `href="#id"` jumps to any element with that `id`, on whatever page it lands.
