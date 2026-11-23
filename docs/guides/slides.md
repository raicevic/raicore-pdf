# PPTX to PDF

`RaiCore.Pdf.Slides` converts PowerPoint presentations to PDF: each slide becomes one page of the slide's size, drawn
with its master's and layout's shapes, its background, theme colors and fonts, shapes, text, pictures, tables and
groups. No presentation program, no browser, no native dependencies.

```sh
dotnet add package RaiCore.Pdf.Slides
```

[!code-csharp[](../snippets/Slides.cs#convert)]

`PptxToPdf.Convert` takes a path, a stream or bytes; `ConvertAsync` (on the thread pool, with a cancellation token)
and `ConvertToBytes` do the same. It reads `.pptx`, `.pptm`, `.ppsx` and `.potx` files, also in strict Office Open
XML.

## Options

[!code-csharp[](../snippets/Slides.cs#options)]

| `PptxConverterOptions` | What it does |
|---|---|
| `Fonts` | The fonts the presentation's fonts are found in (default the bundled Liberation fonts only). |
| `FallbackFontFamilies` | Families tried for characters the presentation's fonts lack; Liberation Sans comes last. |
| `IncludeHiddenSlides` | Converts hidden slides too (default `false`, as in a slide show). |
| `SlideNumbers` | The slides to convert, numbered from 1 (hidden slides counted, and converted when named). |
| `Title` | The document's title; by default the presentation's own. |
| `Tagged` | A tagged (accessible) PDF, below (default `false`). |
| `Limits` | The `ResourceLimits` of the conversion, below. |
| `Diagnostics` | Receives what was not drawn as a presentation program would, below. |

The returned `PdfDocument` can be changed before it is saved: metadata, `Language`, `PdfUA`, `PdfAConformance`,
encryption, attachments.

## What is drawn

- **Masters and layouts:** the master's shapes, then the layout's (unless the layout or slide hides them), then the
  slide's. Placeholders take their position, shape, fill and text styles from the layout's placeholder with the same
  index (or type) and from the master's of the same kind; their prompt text is not drawn. Backgrounds come from the
  slide, its layout or its master, including the theme's background styles.
- **Theme:** scheme colors through the master's color map and overrides, with their transforms (luminance, tint,
  shade, saturation, hue, alpha); the theme's major and minor fonts; the style matrix that shapes inserted with a
  style refer to (fill, line and text color).
- **Shapes:** more than 80 preset shapes (rectangles, rounded and snipped rectangles, ellipses, triangles,
  polygons, stars, arrows, chevrons, callouts, brackets, flowchart shapes, connectors and lines) with their adjust
  values, and free forms with their guide formulas; other presets are drawn as rectangles and reported. Solid,
  gradient (linear and radial, drawn in fine bands) and picture fills; patterns as their average color. Lines with
  their width, color, dashes, caps, joins and arrowheads (triangle, arrow, stealth, diamond, oval). Rotation, flips and
  groups of any depth.
- **Text:** insets, top, middle or bottom anchoring, wrapping (or not), vertical text, and autofit: text in a frame set
  to shrink on overflow is made as small as the file says, and smaller still if it does not fit with the fonts at hand.
  Paragraphs keep their alignment, indents, spacing before and after, line spacing, bullets (characters, Wingdings and
  Symbol bullets as their Unicode look-alikes, automatic numbers in every scheme) and levels; runs their font, size,
  bold, italic, underline, strikethrough, color, superscript and subscript, capitals and character spacing. Links on
  text and shapes become PDF links; slide number fields show the slide's number.
- **Pictures:** PNG, JPEG, GIF, BMP, TIFF, WebP and SVG, cropped, clipped to their shape, translucent, with their
  border; video and audio as their poster picture.
- **Tables:** column widths, rows that grow to fit their text, merged cells, cell margins and anchoring, cell fills and
  borders, and the table style (the presentation's own, or PowerPoint's default Medium Style 2) with its header row,
  total row, first and last column and banded rows.
- **SmartArt** from the drawing PowerPoint stores with it, and embedded objects from their picture.

Text is laid out with the fonts at hand: add the presentation's fonts (Calibri, Aptos, ...) to `Fonts` for the same
line breaks as PowerPoint. Without them, text is set in a metric-compatible substitute where there is one (Carlito for
Calibri, Caladea for Cambria, Liberation for Arial, Times New Roman and Courier New) or in the generic family the font
belongs to.

[!code-csharp[](../snippets/Slides.cs#fonts)]

## Not supported

Charts, SmartArt without a stored drawing, EMF and WMF pictures, embedded objects without a picture, animations and
transitions, 3D effects, shadows, glow, reflections and soft edges, WordArt transforms, multiple text columns, tab
stops (a tab is a few spaces), notes pages and comments. Password-protected (encrypted) presentations throw
`NotSupportedException`, and so do PowerPoint 97-2003 (`.ppt`) files given to `PptxToPdf`: convert those with
`PptToPdf`, below.

What is not drawn is reported to `Diagnostics`, once per slide and issue:

[!code-csharp[](../snippets/Slides.cs#diagnostics)]

## PowerPoint 97-2003 (.ppt)

`PptToPdf` converts presentations in the binary format of PowerPoint 97 to 2003 (`.ppt`, `.pps`, `.pot`), which
PowerPoint still opens and writes. It has the same overloads as `PptxToPdf` and takes the same `PptxConverterOptions`.

```csharp
var pdf = PptToPdf.Convert("old-deck.ppt", new PptxConverterOptions { IncludeHiddenSlides = true });
pdf.Save("old-deck.pdf");
```

- **The file:** the current edit and its history (a file saved many times with "fast save" keeps its old versions;
  the newest wins), recovered from what remains when the history is damaged.
- **Masters:** the slide's master (and title master) with its shapes, background and color scheme, unless the slide
  has its own; the master's text styles by text type (title, body, other) and indent level, under the document's
  defaults. The master's date, footer and slide number placeholders show as the presentation's header and footer
  settings say (an automatic date is not drawn, and reported).
- **Shapes:** the common preset shapes (rectangles, rounded rectangles, ellipses, triangles, arrows, stars, flowchart
  shapes, lines and connectors) with their adjust values, free forms from their vertices, groups (tables are groups of
  shapes in this format, and are drawn as such), rotation and flips; solid, gradient, pattern (as an average color),
  picture and texture fills; lines with dashes and arrowheads. Other shapes are drawn as rectangles, and reported.
- **Text:** insets, anchoring, wrapping and vertical text; paragraphs with alignment, indents and rulers, spacing, line
  spacing and bullets (symbol fonts' bullets as Unicode); runs with font, size, bold, italic, underline, color,
  superscript and subscript and links. WordArt is drawn as plain text, and reported.
- **Pictures:** PNG, JPEG, TIFF and device-independent bitmaps, cropped; embedded objects (charts, workbooks) as the
  picture PowerPoint stores with them. EMF, WMF and PICT pictures are not drawn, and reported.

Not supported: automatic numbering, automatic dates, shadows, 3D effects, animations and transitions, sounds and
movies, notes pages and comments. Password-protected (encrypted) presentations, and PowerPoint 95 and older files,
throw `NotSupportedException`; data that is not a PowerPoint 97-2003 presentation (or whose document cannot be found)
throws `PresentationFormatException`. Every length, offset and count in the file is checked; `MaxElements` counts its
records, `MaxNestingDepth` bounds the nesting of groups and `MaxDecompressedBytes` the streams and pictures read.

## Accessibility

With `Tagged`, each slide is a section of the structure tree with its content in the order of its shapes: titles as
headings (`H1`), other text as paragraphs (`P`), tables with their rows and cells, and pictures with alternative text
as figures. Lines, fills, pictures without alternative text and the master's and layout's shapes are artifacts. For PDF/UA, also set `PdfUA`, a title
and `Language` on the returned document.

## Untrusted data

Presentations are read with no DTDs and nothing external resolved (linked pictures are not loaded). `Limits` bounds
the conversion: `MaxDecompressedBytes` the bytes of all the package's parts together, `MaxElements` their XML
elements, `MaxNestingDepth` the nesting of their XML (and so of groups), `MaxPages` the slides converted,
`MaxTableCells` the cells of each table, and `Timeout` the time. Exceeding one throws `ResourceLimitExceededException`
naming it; a picture larger than `MaxImagePixels` is left out and reported. A malformed package (not a ZIP file, parts
that are not well-formed XML, no presentation part) throws `PresentationFormatException`.
