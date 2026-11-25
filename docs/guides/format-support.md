# Format support

What each converter turns into PDF, side by side, for planning which files can go through RaiCore.Pdf. The guides of
the formats have the details: [Word, ODT and RTF](documents.md), [CSV, XLSX and XLS](sheets.md),
[PowerPoint](slides.md), [email and web archives](email.md), [XFA forms](xfa.md).

| Mark | Meaning |
|---|---|
| ✓ | Converted. |
| ◐ | Partly converted: see the note. |
| R | Left out and reported to the options' `Diagnostics`, so you can flag the document or fall back. |
| ✗ | Not converted. |
| - | The format has no such thing. |

## The legacy binary formats

`.doc`, `.xls` and `.ppt` are read into the same model as `.docx`, `.xlsx` and `.pptx`, and drawn by the same
renderer, so text, paragraph, table and page formatting comes out the same way. The differences are in what each
format stores: fewer picture formats in the binary files, tables in `.ppt` that are groups of shapes, Outlook's RTF
bodies. Every converter tells the format from the data, not the file name: `DocxToPdf` converts a `.doc` and
`DocToPdf` a `.docx`, and the same goes for `XlsxToPdf` and `XlsToPdf`. Encrypted files throw `NotSupportedException`
in every format, and so do the formats older than Word 97, Excel 5.0 and PowerPoint 97.

## Word processing

| Feature | DOCX | DOC | RTF | ODT |
|---|---|---|---|---|
| Text formatting: fonts, sizes, bold, italic, underline styles, strikethrough, colors, highlighting, superscript and subscript, capitals, letter spacing | ✓ | ✓ | ✓ | ✓ |
| Paragraphs: alignment, indents, spacing, line spacing, borders, shading, keep with next, keep together, widows and orphans | ✓ | ✓ | ✓ | ✓ |
| Styles and their inheritance | ✓ | ✓ | ✓ | ✓ |
| Lists and multi-level numbering | ✓ | ✓ | ✓ | ✓ |
| Tab stops with leaders | ✓ | ✓ | ✓ | ✓ |
| Tables: merged cells, borders, shading, repeated header rows, nested tables | ✓ | ✓ | ✓ | ✓ |
| Table styles with conditional formats | ✓ | - | - | - |
| Sections: page size, orientation, margins, section starts | ✓ | ✓ | ✓ | ✓ (page styles) |
| Headers and footers: first page, odd and even pages | ✓ | ✓ | ✓ | ✓ |
| Page numbers, page counts and page references computed for the PDF | ✓ | ✓ | ✓ | ✓ |
| Other fields (dates, properties, cross references) | ◐ saved result | ◐ saved result | ◐ saved result | ◐ saved result |
| Hyperlinks and bookmarks | ✓ | ✓ | ✓ | ✓ |
| Headings as PDF bookmarks | ✓ | ✓ | ✓ | ✓ |
| Footnotes and endnotes | ◐ after the section | ◐ after the section | ◐ after the section | ◐ after the section |
| Tracked changes | ◐ as accepted | ◐ as accepted | ◐ as accepted | ◐ as accepted |
| Comments | ✗ | R | R | R |
| Pictures: PNG, JPEG | ✓ | ✓ | ✓ | ✓ |
| Pictures: GIF, BMP, TIFF, WebP | ✓ | ◐ TIFF and bitmaps | ◐ bitmaps | ✓ |
| Pictures: SVG | - | - | - | ✓ |
| Pictures: EMF, WMF (and EMF+) | ✓ | ✓ | ✓ | ✓ |
| Pictures: PICT | R | R | R | R |
| Floating pictures with text wrapping | ✓ | ✓ | ◐ in the text | ✓ |
| Text boxes and shapes | ◐ text after the paragraph, R | ◐ text after the paragraph, R | ◐ in the text, R | ◐ text after the paragraph, R |
| Charts | ✓ newer types too; region maps as their stored picture, R | ◐ preview picture | ◐ object's result | ◐ replacement picture |
| SmartArt | R | ◐ preview picture | ◐ object's result | ◐ replacement picture |
| Embedded objects | ◐ preview picture, R | ◐ preview picture | ◐ object's result, R | ◐ replacement picture |
| Equations | ◐ as plain text, R | ◐ preview picture | ◐ saved result | ◐ replacement picture |
| Several text columns | R | R | R | R |
| Line numbers, page borders, watermarks, drop caps | ✗ | ✗ | ✗ | ✗ |
| Forms and form fields | ◐ their text | ◐ their text | ◐ saved result | R |
| Tagged PDF (`Tagged`): headings, paragraphs, tables, figures | ✓ | ✓ | ✓ | ✓ |
| Linked (external) pictures | R never loaded | ✗ never loaded | ✗ never loaded | R never loaded |

## Spreadsheets

| Feature | XLSX, XLSM | XLS | CSV |
|---|---|---|---|
| Numbers, dates and times in their number formats | ✓ | ✓ | ◐ recognized and aligned |
| Formulas | ◐ saved values | ◐ saved values | - |
| Text, booleans, errors | ✓ | ✓ | ✓ |
| Rich text within a cell | ✓ | ✓ | - |
| Fonts, fills, borders, alignment, wrapping, indents | ✓ | ✓ | - |
| Merged cells | ✓ | ✓ | - |
| Column widths and row heights | ✓ | ✓ | ◐ fitted to the content |
| Hidden rows, columns and sheets left out | ✓ | ✓ | - |
| Right-to-left sheets | ✓ | ✓ | ◐ text aligned by its direction |
| Print area, print titles, orientation, scale, fit to width | ✓ | ✓ | - |
| Header row repeated on every page | ✓ print titles | ✓ print titles | ✓ `HasHeaderRow` |
| Pivot tables | ✓ their cells, `PivotStyleLight15`-`21`; other styles R | ◐ their cells, R | - |
| Charts in a sheet | ✓ newer types too; region maps R | R | - |
| Chart sheets | ✓ on a page of their own | R | - |
| Pictures: PNG, JPEG, GIF, BMP, TIFF, EMF, WMF | ✓ cropped, turned | ✓ bitmaps, metafiles | - |
| Shapes and text boxes | ✓ preset and free-form geometry | ◐ common shape types, R | - |
| SmartArt, ink, form controls | R | R | - |
| Conditional formatting: value, rank, average, duplicate, text and date rules, color scales | ✓ over stored values | ✓ over stored values | - |
| Conditional formatting: formulas, data bars, icon sets | ✓ formulas of common functions; others R | ✓ formulas of common functions; others R | - |
| Comments and notes | ✓ at the end of the sheet, or as PDF notes | ✓ at the end of the sheet, or as PDF notes | - |
| Headers and footers, page breaks set by hand | ✓ | ✓ | - |
| Stacked text, shrink to fit | ✓ | ✓ | - |
| Rotated text | ✓ | ✓ | - |
| External links, data connections | ◐ saved values, never refreshed | ◐ saved values, never refreshed | - |
| Tagged PDF (`Tagged`): tables with header cells | ✓ | ✓ | ✓ |
| Binary `.xlsb` | ✗ `NotSupportedException` | - | - |

## Presentations

| Feature | PPTX | PPT |
|---|---|---|
| Masters, layouts, placeholders and backgrounds | ✓ | ✓ masters and title masters |
| Theme colors and fonts | ✓ | ◐ color schemes |
| Preset shapes | ✓ more than 80, others as rectangles (R) | ◐ the common ones, others as rectangles (R) |
| Free forms, groups, rotation, flips | ✓ | ✓ |
| Fills: solid, gradient, picture; patterns as their average color | ✓ gradients in fine bands | ✓ |
| Lines with dashes and arrowheads | ✓ | ✓ |
| Text: alignment, indents, spacing, bullets, links, vertical text | ✓ also shrink on overflow | ✓ |
| Automatic numbering | ✓ | ✗ |
| Tab stops | ✗ a few spaces | ✗ a few spaces |
| Pictures: PNG, JPEG, TIFF, bitmaps | ✓ | ✓ |
| Pictures: GIF, WebP, SVG | ✓ | - |
| Pictures: EMF, WMF (and EMF+) | ✓ | ✓ |
| Pictures: PICT | R | R |
| Tables with table styles | ✓ | ◐ drawn as their shapes |
| SmartArt | ◐ stored drawing, else R | - |
| Charts | ✓ newer types too; surface charts R, region maps as their stored picture | ◐ stored picture |
| Embedded objects | ◐ stored picture | ◐ stored picture |
| Video and audio | ◐ poster picture | ✗ |
| Slide numbers, footers, dates | ✓ | ✓ (automatic dates R) |
| Hidden slides (`IncludeHiddenSlides`) | ✓ | ✓ |
| Shadows, glow, reflections, soft edges, 3D effects | ✗ | ✗ |
| WordArt | ◐ without transforms | ◐ as plain text, R |
| Animations and transitions | ✗ shapes drawn as stored | ✗ shapes drawn as stored |
| Notes pages and comments | ✗ | ✗ |
| Tagged PDF (`Tagged`): titles, text, tables, figures | ✓ | ✓ |

## Email and web archives

| Feature | EML | MSG | MHT, MHTML |
|---|---|---|---|
| Header block (from, to, cc, date, subject, attachments) | ✓ | ✓ | ◐ only with `HeaderFields` |
| HTML body with style sheets | ✓ | ✓ also HTML inside compressed RTF | ✓ the page |
| Plain text body (`format=flowed`, links) | ✓ | ✓ | - |
| RTF body without HTML | - | ◐ its text | - |
| Inline images (`cid:`, `Content-Location`) | ✓ | ✓ | ✓ |
| Charsets and encoded header fields | ✓ | ✓ Unicode or code page | ✓ |
| Attachments embedded in the PDF (`EmbedAttachments`) | ✓ | ✓ | ✓ |
| Attachments appended as pages: images, text, PDF, HTML, messages (`AppendAttachments`) | ✓ | ✓ | ✓ |
| Attached messages, recursively | ✓ | ✓ | - |
| Original message embedded (`EmbedOriginal`) | ✓ | ✓ | ✓ |
| Remote images | ◐ only through your `ResourceLoader` | ◐ only through your `ResourceLoader` | ◐ only through your `ResourceLoader` |
| TNEF (`winmail.dat`) | ◐ embedded as it is | - | - |
| S/MIME and PGP encrypted messages | ◐ their parts as attachments | ◐ their parts as attachments | - |
| Calendar items, contacts | ◐ invitations as attachments | ◐ their message properties | - |
| OLE objects attached to messages | - | ◐ the file inside (packaged files, Office documents, PDF), else the object's picture; others R | - |
| Tagged PDF, PDF/A (`Html` options) | ✓ | ✓ | ✓ |

## XFA forms

`XfaToPdf` renders the XFA form of a PDF file (dynamic or static, its packets as one stream or an array) or an XDP
file to flattened PDF pages.

| Feature | Dynamic XFA (PDF, XDP) |
|---|---|
| Data merge: normal, global, data reference and no bindings; repeated subforms; exclusion groups | ✓ |
| Positioned and flowed layout (`tb`, `lr-tb`, `rl-tb`), tables, growable objects, rotation | ✓ |
| Page areas and content areas, breaks, overflow leaders and trailers, keep rules | ✓ |
| Text, rich text, floating fields, images, lines, rectangles, arcs | ✓ |
| Check boxes, radio buttons, choice lists, comb and password fields | ✓ |
| Picture clauses (numbers, dates, times, text) and locales | ✓ |
| Captions, margins, borders, fills | ◐ 3D borders flat, gradients as one color |
| Fonts embedded in the PDF for the form | ✓ TrueType and OpenType |
| Scripts (FormCalc, JavaScript): calculations, events, page numbers | R not run |
| Barcodes | ◐ as their text, R |
| Signature fields | ◐ their box |
| Linked images and prototypes in other files | R |
| Form fields in the output | ✗ flattened |

Planned work on these gaps is tracked in the
[issues](https://github.com/raicevic/raicore-pdf/issues?q=is%3Aopen+label%3A%22area%3A+formats%22).
