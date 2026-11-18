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

## Untrusted files

The work per page is bounded, and malformed content is skipped. To limit memory and time as well, run extraction
inside `ResourceLimits.Enforce`, as for conversions:

[!code-csharp[](../snippets/TextExtraction.cs#untrusted)]

A `TextExtractor` is not thread-safe, like the `PdfReader` it reads: use one per thread. Fonts are decoded once per
extractor and reused by later pages.
