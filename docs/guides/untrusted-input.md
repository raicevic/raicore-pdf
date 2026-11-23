# Untrusted input

Every converter and reader in RaiCore.Pdf is built to take files from strangers: uploads, email attachments,
documents from archives. This page sums up what each one may load besides the file you give it, and what bounds its
work. The guide of each format has the details.

## External resources

Documents can reference content outside themselves: linked images, style sheets, linked workbooks, data
connections, remote images in email. The rule is the same everywhere: **nothing outside the input is read unless you
supply a loader that reads it**, and only the HTML-based converters take a loader at all.

| Input | Converter | External references in the format | What RaiCore.Pdf does |
|---|---|---|---|
| HTML | `HtmlToPdf`, `HtmlFragment` | `<img>`, `<link rel="stylesheet">`, CSS `url()` (backgrounds, `@font-face`), SVG `<image>` | Goes to `HtmlConverterOptions.ResourceLoader`. The default reads `file:` URIs (no network); `ResourceLoaders.None` loads nothing. `data:` URIs are part of the document and always work; SVG images and CSS `@import` are never followed. |
| Razor | `RazorToPdf` | as HTML | as HTML (the same options) |
| EML, MSG, MHT | `EmailToPdf` | `cid:` and `Content-Location` parts, remote images, `file:` URLs | Parts of the message are shown; `file:` URLs never load; other URLs go to `Html.ResourceLoader`, which with the default or `ResourceLoaders.None` reaches no network. |
| DOCX | `DocxToPdf` | external image and object relationships (`TargetMode="External"`), `INCLUDEPICTURE`/`INCLUDETEXT`/`LINK` fields, attached template, `altChunk`, sub-documents | Never followed. Linked images are left out and reported (`ImageNotLoaded`); fields show the result stored in the document; `altChunk` is left out and reported. |
| DOC | `DocToPdf` | linked pictures and objects, `INCLUDEPICTURE` fields | Never followed; the stored picture or field result is shown when there is one. |
| RTF | `RtfToPdf` | `INCLUDEPICTURE`/`INCLUDETEXT` fields, linked objects | Never followed; the field result stored in the document is shown. |
| ODT | `OdtToPdf` | linked images (`xlink:href` outside the package), linked sections | Never followed; linked images are left out and reported (`ImageNotLoaded`). |
| XLSX, XLS | `XlsxToPdf`, `XlsToPdf` | external workbook links, data connections, query tables, web queries, DDE and OLE links | Never followed and never refreshed; formulas show the values stored with them. |
| CSV | `CsvToPdf` | none | - |
| PPTX, PPT | `PptxToPdf`, `PptToPdf` | linked pictures, media and OLE objects | Never followed; linked pictures are left out and reported. |
| PDF | `PdfReader`, `PdfEditor`, `TextExtractor`, `ImageExtractor` | external streams (`/F`), remote go-to actions, URI links | Never followed; links are kept as links. |

The Office and email packages contain no code that opens files, sockets or processes other than reading the input
path and writing the output path you name; a test checks their compiled code for it.

### HTML from strangers

Use `ResourceLoaders.None`, so that a document can embed images as `data:` URIs but can't name a local file:

[!code-csharp[](../snippets/Images.cs#untrusted)]

To allow more, write a loader that checks each request (host, path, kind) and returns `null` for the rest; see
[Loading resources](images.md#loading-resources). The default loader reads any local file a `file:` URL names
(the files the process can read), which is right for your own templates and wrong for uploads.

## Limits

Every converter takes `ResourceLimits` (`Limits` on its options; `ResourceLimits.Enforce` around readers and
extractors). They bound the work and memory an input can cause:

| Limit | Bounds |
|---|---|
| `MaxDecompressedBytes` | decompressed size of ZIP parts and PDF streams together (ZIP bombs, deflate bombs) |
| `MaxElements` | XML and HTML elements read |
| `MaxNestingDepth` | nesting of elements, tables, groups, MIME parts |
| `MaxTableCells` | cells of a table or sheet |
| `MaxImagePixels` | pixels of each decoded image |
| `MaxResourceBytes` | bytes of each resource a loader returns |
| `MaxFontBytes` | bytes of each font program (web fonts, fonts embedded in documents) |
| `MaxPages` | pages written |
| `Timeout` | time for the whole conversion, including loading |

Exceeding one throws `ResourceLimitExceededException` naming it. `ResourceLimits.Default` is meant for untrusted
input; raise a limit only for inputs you trust.

## Errors

Malformed input throws the format's exception, all of them `PdfException`s: `PdfFormatException`,
`FontFormatException`, `ImageFormatException`, `DocumentFormatException`, `SpreadsheetFormatException`,
`PresentationFormatException`, `MessageFormatException`. Encrypted Office files throw `NotSupportedException`.
Anything else (a crash, a hang, an exception the API does not document) is a bug: please report it as described in
[SECURITY.md](https://github.com/raicevic/raicore-pdf/blob/main/SECURITY.md).

## Running conversions in isolation

The limits keep a conversion inside its budget, but a service that converts uploads should still run conversions
with the least privilege it can: a process or container without network access and with a read-only file system
apart from its working directory, and a memory limit. See the `samples/Docker` and `samples/Worker` samples.
