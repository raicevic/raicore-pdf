# Dynamic XFA forms to PDF

`RaiCore.Pdf.Xfa` renders XFA forms (XML Forms Architecture, the forms Adobe LiveCycle and AEM Forms Designer make) to
ordinary PDF pages, as a viewer with XFA support shows them: the form's data is merged into its template, the subforms
are laid out and paginated into the template's page areas, and the field values are drawn as text. The result is a
flattened PDF without XFA and without form fields, which every viewer, printer and archive shows the same.

```sh
dotnet add package RaiCore.Pdf.Xfa
```

[!code-csharp[](../snippets/Xfa.cs#convert)]

`XfaToPdf.Convert` takes a path, a stream or bytes: a PDF file with an XFA form (dynamic or static), or an XDP file
(the XFA as XML). `ConvertAsync` (on the thread pool, with a cancellation token) and `ConvertToBytes` do the same.

## Dynamic XFA, static XFA, and the AcroForm

A *dynamic* XFA form (`/NeedsRendering true`) has PDF pages that are often only a placeholder: "Please wait... If this
message is not eventually replaced by the proper contents of the document, your PDF viewer may not be able to display
this type of document." The viewer is expected to lay the pages out from the XFA itself, and that is what `XfaToPdf`
does. A *static* XFA form, or a dynamic one that also comes with complete PDF pages and AcroForm fields, can instead
keep its own pages: remove the XFA with `PdfEditor.RemoveXfa` and flatten the AcroForm (see
[XFA forms](editing.md#xfa-forms) in the editing guide). `PdfEditor.GetForm()` tells which kind a document has, and
whether its pages are only a placeholder.

## What is rendered

- **Data:** the `datasets` packet (or the options' `Data`) merged into the template: subforms bind to data groups and
  fields to data values of their name, each data node once (`match="once"`, the default); global fields
  (`match="global"`) take the first value of their name anywhere in the record; data references
  (`match="dataRef" ref="$.Items.Item[*]"`) follow a SOM expression (`$`, `$record`, `$data`, names with indexes and
  `[*]`); fields bound to nothing (`match="none"`, or no data) keep their default value. Unnamed subforms, and
  subforms that find no data group of their name, pass the current data group on to their content.
- **Repetition:** a subform with `occur` gets an instance per data group within its maximum, at least its minimum, and
  its initial count when there is no data. Exclusion groups turn on the button whose value the data has.
- **Layout:** positioned content (`x`, `y`, `anchorType`, `rotate` in steps of 90 degrees), flowed content top to bottom
  (`tb`) and in lines (`lr-tb`, `rl-tb`), tables (`columnWidths`, automatic columns, `colSpan`, cells as tall as
  their row), objects without a width or height growing to fit their content within `minW`/`maxW`/`minH`/`maxH`,
  margins, captions (left, right, top, bottom, with or without a reserve), borders with their edges, corners and
  fills, and presence: hidden objects are left out, invisible ones keep their space, and objects that are not printed
  (`relevant="-print"`, such as buttons for the screen) are left out, as in a printout.
- **Pagination:** the root subform's content flows into the content areas of the page areas: in order
  (`orderedOccurrence`, each page area up to its maximum occurrences) or by page position (`simplexPaginated`,
  `duplexPaginated`: first, rest, last and only pages). A flowed subform that does not fit is split between its
  children, with its overflow trailer at the bottom of the content area and its overflow leader (such as a table's
  header row) at the top of the next; the text of a draw or field that grows (a long remark) is split between its
  lines. Table rows and positioned subforms are kept whole. Breaks (`breakBefore`, `breakAfter` and the older `break`) go to the next content
  area or page area, a named one, or an odd or even page; `keep` keeps objects in one piece and with the next or
  previous one. Each page area's own objects (headers, footers, logos) are drawn on its pages.
- **Content:** plain text and rich text (an XHTML subset: paragraphs, spans, bold, italic, underline, line-through,
  superscript, subscript, line breaks, lists, and the CSS properties XFA uses for fonts, colors, alignment, margins,
  indents, line height and `xfa-spacerun`), floating fields in rich text, images (PNG, JPEG, GIF, BMP, TIFF embedded
  in the template or the data), lines, rectangles and arcs, check boxes and radio buttons with their marks, choice
  lists (the text of the chosen value), comb fields, password fields (masked).
- **Formatting:** display picture clauses for numbers (`num{$z,zz9.99}`, signs, grouping, currency, percent), dates
  (`date{MMMM D, YYYY}`, and the locale's short, medium, long and full patterns), times, date-times and text
  (`text{999-99-9999}`), with `null{}` and `zero{}` alternatives; a value no clause fits is shown as it is. Month and
  day names and number symbols come from the form's `localeSet`, then from .NET's culture data, with US English as
  the default.
- **Fonts:** the TrueType and OpenType fonts the PDF file embeds for its form are used for their typefaces
  (`UseEmbeddedFonts`), then `Fonts` (the bundled Liberation fonts by default: Arial, Times New Roman and Courier
  New have metric-compatible substitutes). Other typefaces, such as Myriad Pro, are drawn with a font of their generic
  family and reported.

## Options

[!code-csharp[](../snippets/Xfa.cs#options)]

| `XfaConverterOptions` | What it does |
|---|---|
| `Fonts` | The fonts the form's typefaces are found in (default the bundled Liberation fonts only). |
| `FallbackFontFamilies` | Families tried for characters the form's fonts lack; Liberation Sans comes last. |
| `UseEmbeddedFonts` | Uses the fonts the PDF file embeds for its form (default `true`). |
| `Data` | XML data merged instead of the form's own: `xfa:datasets`, `xfa:data`, or the data's root element. |
| `Password` | The password of an encrypted PDF file (the empty user password is tried by default). |
| `Title` | The document's title; by default the PDF file's own. |
| `Limits` | Resource limits, below. |
| `Diagnostics` | Receives what is not rendered as a viewer would, below. |

## Diagnostics

Scripts are never run. FormCalc and JavaScript calculations, validations and events (`initialize`, `calculate`,
`layout:ready`, ...) often compute totals, fill page numbers ("Page 1 of 3"), or show, hide and add parts of a form;
what they would have done is missing from the output, and the form's scripts are reported once. Everything else the
converter does not render as a viewer would is reported too, once per issue:

[!code-csharp[](../snippets/Xfa.cs#diagnostics)]

| `XfaDiagnosticKind` | What it reports |
|---|---|
| `ScriptNotRun` | The form's scripts, by kind (calculations, validations, events); they are not run. |
| `UnsupportedContent` | Barcodes (drawn as their text), prototypes that are not references by id within the form, missing overflow leaders and trailers. |
| `ImageNotLoaded` | Linked images (not loaded), images in unknown formats or damaged, and images over `MaxImagePixels`. |
| `Layout` | Objects taller than a whole content area (drawn and cut off), missing break targets, a form without a page set. |
| `Data` | Data values no field is bound to. |
| `FontSubstituted` | Typefaces drawn with a substitute, and embedded fonts that cannot be read. |

## Untrusted forms

XFA forms are untrusted input. Their XML is read with DTDs prohibited and nothing external resolved: linked images
and prototypes in other files are not loaded. The work is bounded by the options' `Limits`:

- `MaxDecompressedBytes`: the XFA packets together, and each PDF stream;
- `MaxElements`: the XML elements of the packets together, and the objects of the merged form, so that repeated
  subforms (`occur`) cannot multiply without bound;
- `MaxNestingDepth`: the nesting of the XML, and so of subforms;
- `MaxPages`, `MaxTableCells` (per table), `MaxImagePixels` (a larger image is left out and reported),
  `MaxFontBytes` (an embedded font) and `Timeout`.

Exceeding a limit throws `ResourceLimitExceededException`. A file without an XFA form, or with malformed XFA, throws
`XfaFormatException`; a PDF file that cannot be read throws `PdfFormatException`.

## Not supported

Scripts (above), and with them anything a form computes or changes when it opens. Barcodes are drawn as their text,
signature fields as their box. The output has no form fields: it is the form as printed. Shading of 3D borders
(`lowered`, `raised`, `etched`, `embossed`) is drawn flat, gradient and pattern fills as one color, and tab stops as
spaces. The `form` packet (the state a viewer saves for instance counts and unbound values) is not read.
