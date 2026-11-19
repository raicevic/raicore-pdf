# PDF/A, accessibility and security

## Accessible PDF (tagged PDF and PDF/UA)

`HtmlConverterOptions.Tagged` writes a tagged PDF: the logical structure that screen readers, reflowing viewers and
accessibility checkers use, taken from the HTML semantics. `PdfUA` also makes it PDF/UA-1, which needs a title
(`<title>` or `Title`), a language (`<html lang>` or `Language`) and `alt` text on images.

[!code-csharp[](../snippets/Standards.cs#pdfua)]

- Headings, paragraphs, block quotes, sections, lists (with labels and bodies), tables (with header cells and their
  scope), images and inline SVG as figures with their alternative text, links with their text, and `lang` on blocks.
- Backgrounds, borders, decorations, page headers and footers and repeated table headers are artifacts, which screen
  readers skip. Reading order is document order.
- CI validates the output with [veraPDF](https://verapdf.org).

## PDF/A

`HtmlConverterOptions.PdfAConformance` writes PDF/A-2b or PDF/A-3b for long-term archiving. The conversion throws
`InvalidOperationException` when the document breaks a rule, for example text that no font has a glyph for, a CMYK
JPEG or encryption.

[!code-csharp[](../snippets/Standards.cs#pdfa)]

### Level A (PDF/A-2a and PDF/A-3a)

`PdfA2A` and `PdfA3A` keep everything level B requires and add the document's logical structure, for archives that
ask for accessible documents:

[!code-csharp[](../snippets/Standards.cs#pdfa-level-a)]

- The document is tagged as with `Tagged` (headings, paragraphs, lists, tables, figures, links; decorations as
  artifacts), and every glyph maps to Unicode. A glyph with no text of its own (the second glyph of a cluster in
  Indic or Arabic text, whose text the run's actual text gives) maps to U+FFFD.
- It needs a language (`<html lang>` or `Language`) that is a BCP 47 tag such as `en` or `de-CH`, and so must every
  `lang` in the document.
- Images need `alt` text (an SVG its `<title>` or `aria-label`); `alt=""` marks an image as decorative, which makes it
  an artifact instead of a figure.

What happens when the content cannot meet level A follows the rest of PDF/A: saving throws an
`InvalidOperationException` that names the problem (an image without `alt` text, a missing or malformed language),
rather than writing a file that claims a conformance it does not have. There is no diagnostic-only mode.

Drawing with `PdfDocument` directly, set `PdfAConformance` before drawing: that is what tags the content, and a page
drawn on before is rejected. Draw content between `BeginTag` and `EndTag`, use only the standard structure types
(`PdfStructureTypes`; no role map is written for custom ones), give figures and formulas `AlternateText`, and draw
Private Use Area characters (icon fonts) with `DrawGlyphs` and its `actualText`, since PDF/A level A requires actual
text for them. The `fi` ligature and the like need nothing: their glyphs map to their characters.

PDF/A-2a and PDF/UA-1 can be combined (`PdfUA = true` as well); CI validates both with veraPDF.

## E-invoices (Factur-X and ZUGFeRD)

A Factur-X or ZUGFeRD invoice is a PDF/A-3b that carries the invoice as Cross Industry Invoice (CII) XML. The XML is
checked to be well-formed, not validated against the profile's rules.

[!code-csharp[](../snippets/Standards.cs#facturx)]

`PdfDocument.Attachments` attaches other files too (PDF/A-3 allows any file). An invoice keeps PDF/A-3a when the
document has it; otherwise it becomes PDF/A-3b.

## Passwords and permissions

`HtmlConverterOptions.Encryption` (or `PdfDocument.Encryption`) protects a document with a password, or lets anyone
open it but restricts what they may do. AES-256 is the default; AES-128 is there for old readers.

[!code-csharp[](../snippets/Standards.cs#encryption)]

Permissions are enforced by the viewer. Encrypted output is not byte-for-byte reproducible, since keys are random.
