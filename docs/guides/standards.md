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

## E-invoices (Factur-X and ZUGFeRD)

A Factur-X or ZUGFeRD invoice is a PDF/A-3b that carries the invoice as Cross Industry Invoice (CII) XML. The XML is
checked to be well-formed, not validated against the profile's rules.

[!code-csharp[](../snippets/Standards.cs#facturx)]

`PdfDocument.Attachments` attaches other files too (PDF/A-3b allows any file).

## Passwords and permissions

`HtmlConverterOptions.Encryption` (or `PdfDocument.Encryption`) protects a document with a password, or lets anyone
open it but restricts what they may do. AES-256 is the default; AES-128 is there for old readers.

[!code-csharp[](../snippets/Standards.cs#encryption)]

Permissions are enforced by the viewer. Encrypted output is not byte-for-byte reproducible, since keys are random.
