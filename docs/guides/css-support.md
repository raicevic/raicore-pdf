# HTML and CSS support

HTML is parsed and styled with [AngleSharp](https://anglesharp.github.io/), with `print` media, and laid out by
RaiCore.Pdf's own engine. Scripts do not run.

## Support by area

| Area | Supported | Not yet |
|---|---|---|
| Cascade | Inheritance, `!important`, CSS variables, `@media print`, `calc()`, `min()`, `max()`, `clamp()`, browser default styles | `@import`, `@layer` |
| Selectors | CSS selectors as AngleSharp matches them, including `:nth-child()`, `:not()`, `:is()`, `:where()`; `::before` and `::after` | `::first-letter`; `:dir()` sees only an element's own `dir` |
| Box model | Margins (collapsing), borders in every style, `border-radius`, padding, widths and heights with min/max, `box-sizing`, percentages, `overflow` clipping, `opacity`, `box-shadow` (outer and inset, blurred, following the radius; left out in PDF/A-1) | `outline`, `filter` |
| Text | Fonts and web fonts (`@font-face`), color, `text-align` (also justify), `text-indent`, `line-height`, `letter-spacing`, `word-spacing`, `text-transform`, `text-decoration` in every style, `white-space`, `overflow-wrap` (and `word-wrap`), `text-overflow: ellipsis`, `vertical-align`, hyphenation (`hyphens`), bidirectional text, complex scripts | `text-shadow`, `writing-mode` (vertical text) |
| Generated content | `content` with strings, `attr()`, quotes, `url()`, counters (`counter()`, `counters()`), `target-counter()`, `target-text()`, `leader()` | |
| Lists | Markers in the common counter styles, `start`, `reversed`, `value` | `list-style-image` |
| Layout | Block and inline flow, `inline-block`, floats and `clear`, positioning (`relative`, `absolute`, `fixed`), `z-index`, `visibility`, [flexbox](flexbox.md), multi-column layout (`columns`, `column-count`, `column-width`, `column-gap`, `column-rule`, `column-fill`, `column-span`, column breaks; balanced like browsers) | Grid (laid out as blocks), `display: contents`, `position: sticky` (static, as there is no scrolling) |
| Tables | Automatic and fixed layout, `colspan`/`rowspan`, collapsed and separate borders, captions, `<col>` widths, legacy attributes; [header and footer rows on every page](tables.md) | |
| Backgrounds | Colors, images, gradients (linear and radial, repeating), several layers, `background-size`, `-position`, `-repeat`, `-origin`, `-clip`; the root's (or body's) background color fills the page area of every page | `background-attachment`, `background-blend-mode` |
| Images | JPEG, PNG, GIF, BMP, WebP, TIFF and SVG (as vectors), inline `<svg>`, `data:` URIs; see [Images](images.md) | |
| Paged media | `@page` size and margins, margin boxes, page selectors, named pages, page counters, running elements, `string-set`, forced and avoided breaks, orphans and widows; see [Paged media](paged-media.md) | `@page :nth()` |
| Links and bookmarks | Links to URLs and to ids in the document, bookmarks from headings (`bookmark-level`, `-label`, `-state`) | |
| Forms and media | | Form controls as fillable PDF fields, `<video>`, `<audio>`, `<canvas>`, `<iframe>` |

CSS that has no effect in print (`cursor`, `transition`, `animation`, vendor prefixes, Outlook's `mso-*`) is ignored
on purpose.

## Finding out why output differs

`HtmlConverterOptions.Diagnostics` receives what the converter could not render as a browser would: resources that
did not load and why, CSS it does not support (properties, values, selectors, at-rules), and content that does not
fit (lines and boxes wider than their room, lines taller than a page, images scaled down). Each issue comes once per
conversion, with the path of the first element it concerns and how many elements it concerns.

[!code-csharp[](../snippets/CssSupport.cs#diagnostics)]

The output of this example:

```text
Warning UnsupportedValue: display: grid at html > body > div (1x): Grid layout is not supported: the element is laid out as a block, its children one below the other.
Warning UnsupportedProperty: box-shadow at html > body > div (1x): The property box-shadow is not supported: it is ignored.
Warning ResourceNotLoaded: missing.png at html > body > img (1x): The image missing.png is a relative URL and there is no base URI to resolve it against (HtmlConverterOptions.BaseUri).
```

## Differences from browsers

- **No JavaScript.** Content that a script would build is missing; render it on the server (for example with
  [Razor](razor.md)).
- **The paper and the page are the viewport.** As in Chrome's print layout, width and height media queries see the
  paper, `HtmlConverterOptions.Page` (794 CSS pixels wide for A4, whatever size `@page` gives the pages), so a
  tablet-and-up layout such as Bootstrap's `min-width: 768px` applies; `vw`/`vh` and percentages see the page's
  content area as `@page` sets it up (about 643 CSS pixels wide for A4 with 2 cm margins), not a browser window.
  Content wider than the page is not shrunk to fit (Chrome scales it down, by at most a third).
- **Fonts** are the bundled Liberation fonts and your web fonts unless you add installed ones; see [Fonts](fonts.md).
- **Network access is off** unless you supply a `ResourceLoader`; see [Images](images.md#loading-resources).
