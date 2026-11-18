# Text extraction

`RaiCore.Pdf.Extraction` reads the text of existing PDF files, page by page, from any producer. Install it next to
the package you already use:

```sh
dotnet add package RaiCore.Pdf.Extraction
```

[!code-csharp[](../snippets/TextExtraction.cs#pages)]

`ExtractPage(index)` reads one page, `ExtractPages()` all of them as you enumerate, and `ExtractPages(indexes)` the
pages you name, in that order. A page that is only an image, such as a scan, has no text: `HasText` is `false`, so you
can send it to OCR. RaiCore.Pdf does not do OCR itself.

## What you get

In the default **reading order**:

- **Lines and words:** lines go from top to bottom, with words separated by a space. Spaces come from the space
  characters in the file and from gaps between words, since many producers leave the spaces out.
- **Blocks:** paragraphs are separated by an empty line.
- **Columns:** each column is read to its end before the next one starts. Headings and footers that run across the
  columns stay where they are.
- **Tables:** tables are read row by row, with a space between cells.
- **Right-to-left scripts:** Arabic and Hebrew come out in logical order, the order you type them, not the order the
  glyphs sit on the page. Numbers and Latin words inside them keep their own order.
- **Turned and vertical text:** text on a turned page is read as it is displayed. Text running in another direction,
  such as a label turned on its side, follows the ordinary text. Vertical writing (CJK) is read in columns from right
  to left.
- **Clean-up:**
  - ligatures (ﬁ, ﬂ) and Arabic presentation forms become the letters they stand for;
  - text drawn twice for a bold effect is read once;
  - text hidden by clipping or placed outside the page is left out.

Text comes from the fonts' maps to Unicode, which almost every producer writes, or else from the font's encoding and
glyph names, or from an embedded TrueType program. A font that maps nothing to Unicode gives its codes as Latin-1, a
guess that is usually right for symbol-encoded fonts that use ASCII codes. Problems, such as a missing font or a
stream that cannot be decoded, never throw: what they affect is skipped or guessed and listed in `PageText.Diagnostics`.

## Modes and options

[!code-csharp[](../snippets/TextExtraction.cs#modes)]

| Mode | Text | Use it for |
|---|---|---|
| `Reading` (default) | Reading order: columns one after the other, empty lines between paragraphs | Search, indexing, summaries |
| `Layout` | Each line across the whole page, with spaces that keep columns and cells in place, as `pdftotext -layout` | Tables and forms read by position |
| `Raw` | The order the text is drawn in the file | Speed, or files whose drawing order is known |

`IncludeInvisibleText` (default `true`) controls invisible text, the OCR layer of scanned PDFs. `PageSeparator`
(default `\f`) is what `ExtractText()` puts between pages.

## Finding text

`Find` searches every page and returns each match with its page and its place on the page; `FindOnPage` searches one
page.

[!code-csharp[](../snippets/TextExtraction.cs#search)]

- **Plain text** is matched as it is, optionally ignoring case. Whitespace in it matches any whitespace on the page,
  a line break included, so a phrase is found where it wraps. Ligatures on the page match the letters they stand for.
- **Regular expressions** are matched against each page's text in reading order: lines end with `\n`, paragraphs with
  an empty line. A match never spans two pages; empty matches are skipped.
- **What matches:** the page text in reading order, whatever `Mode` says, so text matches across font changes and
  across the pieces a producer draws a line in, but not from one column or table cell into the next. Right-to-left
  text is searched in logical order. Invisible text follows `IncludeInvisibleText`, and hidden text is never found.
- **Where:** `Rectangles` has one rectangle per line of the match, in reading order, and `Bounds` the rectangle around
  them. Rectangles are `PdfRectangle`s in the page's user space: PDF points from the bottom-left corner of the page
  before its `/Rotate`, the coordinates the page's content and annotations use. A rectangle covers the glyphs'
  advance widths by the font's ascent and descent; for turned text, it is the axis-aligned rectangle around them.

To mark the matches, draw over the pages imported into a new document. An imported page's canvas starts at the
corner of its media box, so subtract that corner:

[!code-csharp[](../snippets/TextExtraction.cs#highlight)]

## Untrusted files

The work per page is bounded, and malformed content is skipped. To limit memory and time as well, run extraction
inside `ResourceLimits.Enforce`, as for conversions:

[!code-csharp[](../snippets/TextExtraction.cs#untrusted)]

A `TextExtractor` is not thread-safe, like the `PdfReader` it reads: use one per thread. Fonts are decoded once per
extractor and reused by later pages.
