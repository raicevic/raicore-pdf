# Changelog

All notable changes to RaiCore.Pdf are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Nothing has been released yet: the first release will be 1.0.0, once the
[roadmap milestones](https://github.com/raicevic/raicore-pdf/milestones) are done. Until then, the public API may
change at any time, and this section lists what the packages can do so far.

### RaiCore.Pdf
- PDF writer with compressed streams, document information and the 14 standard fonts.
- Embedded TrueType, OpenType, TTC, WOFF and WOFF2 fonts, subset to the glyphs used, with ToUnicode maps so text
  stays searchable and copyable. Fonts whose license forbids embedding are rejected.
- CFF and CFF2 subsetting: OpenType fonts with PostScript outlines are embedded as a CID-keyed CFF
  (`/FontFile3 /CIDFontType0C`) holding only the used glyphs, which keep their original glyph IDs as CIDs.
  Name-keyed and CID-keyed fonts (each Font DICT with its own Private DICT and subroutines) are supported;
  subroutines are inlined, since that compresses smaller than keeping the called ones. CFF2 (variable) fonts are
  converted to CFF at their default instance. Cross-checked against fontTools on the 137 CFF faces of macOS, Source
  Sans 3 and Noto Sans JP.
- JPEG (embedded without re-encoding, EXIF orientation applied), PNG (all color types and bit depths, interlacing,
  transparency; verified against PngSuite) and GIF images, each stored once per document.
- BMP images: BITMAPCOREHEADER, BITMAPINFOHEADER and V2-V5 headers; 1, 2, 4 and 8-bit palettes (kept indexed),
  16, 24 and 32-bit pixels, BI_RLE8/BI_RLE4 (skipped pixels are transparent), BI_BITFIELDS/BI_ALPHABITFIELDS,
  bottom-up and top-down rows, and 32-bit alpha (ignored when every alpha byte is zero, as browsers do). BMPs with
  embedded JPEG or PNG data are rejected.
- WebP images, decoded in managed code with no dependencies: lossy (VP8) and lossless (VP8L), and the extended
  format with alpha (ALPH chunks, raw or lossless-compressed, with every prediction filter). Lossy decoding matches
  libwebp's output bit for bit, including its chroma upsampling. An animated WebP shows its first frame.
- SVG images drawn as PDF vectors in a form XObject (`PdfImage.Load` detects them, `PdfImage.LoadSvg` takes markup;
  `PdfImage.NaturalWidth`/`NaturalHeight` give the natural size in points). A static SVG 1.1 subset: paths with arcs,
  basic shapes, groups, transforms, nested `svg`, `use`/`symbol`, fills and strokes with dashes, caps, joins and fill
  rules, presentation attributes, `style` attributes and `<style>` sheets with type, class and id selectors,
  `currentColor`, linear and radial gradients (shading patterns), clip paths, `text`/`tspan` in the standard fonts
  and `data:` images. `viewBox` and `preserveAspectRatio` are honored, also when drawn at another aspect ratio.
  Unsupported features are skipped; DTDs are ignored and nothing external is loaded.
- Drawing API: paths, fills, strokes, colors, text and images.
- Document outline (bookmarks): `PdfDocument.Outline` with nested `PdfOutlineItem`s (Unicode titles, a page and
  position to jump to, open or closed), and `PdfDocument.PageMode`, which by default opens the outline panel when
  the document has an outline.
- Links: `PdfPage.AddLink` writes link annotations (borderless) that open a URI (a URI action, written as 7-bit ASCII
  with other characters percent-encoded) or jump to a `PdfDestination` or a named destination.
  `PdfDocument.NamedDestinations` is written as the catalog's `/Names` tree; links may be added before their
  destinations, and links to names missing when the document is saved are left out. `PdfCanvas.AddLink` places links
  in the canvas's current coordinates (it follows `Transform`, `SaveState` and `RestoreState`), and `PdfPage.Links`
  lists them.
- Transparency: `PdfColor` has an `Alpha` (`new PdfColor(r, g, b, alpha)`, `FromRgba`, `WithAlpha`; opaque by
  default), which `SetFillColor` and `SetStrokeColor` apply as the fill or stroke alpha (extended graphics states with
  `/ca` and `/CA`, written only when the alpha changes and stored once per document). `PdfCanvas.BeginGroup(opacity)`
  and `EndGroup()` draw everything in between as an isolated transparency group (a form XObject) composited with one
  opacity, so overlapping shapes do not show through each other; groups nest. Images are drawn opaque whatever the
  fill alpha. Pages that use transparency blend in RGB (a page group). SVG `opacity`, `fill-opacity`,
  `stroke-opacity`, `stop-opacity` and translucent colors use real alpha, and group opacity (`<g opacity>`, or a
  shape with both fill and stroke) is a transparency group.
- Document metadata in XMP: an uncompressed metadata stream (`/Type /Metadata /Subtype /XML`, in an `xpacket` with
  padding) kept in sync with the Info dictionary: `dc:title`, `dc:creator`, `dc:description`, `dc:subject` (the
  keywords split at commas and semicolons), `dc:language`, `xmp:CreateDate`/`ModifyDate`/`MetadataDate` (the same
  moments as the Info dictionary's dates), `xmp:CreatorTool`, `pdf:Producer` and `pdf:Keywords`.
  `PdfDocument.IncludeXmpMetadata = false` leaves it out. `PdfDocumentInfo.ModificationDate` (defaults to the creation
  date) is written as `/ModDate`, and `PdfDocumentInfo.CustomProperties` as custom Info entries (text strings) and in
  Adobe's `pdfx:` XMP namespace, as Acrobat does.
- Document language: `PdfDocument.Language` (a BCP 47 tag) is written as the catalog's `/Lang` and as `dc:language`.
- Encryption (the standard security handler): `PdfDocument.Encryption = new PdfEncryption { ... }` with a user
  password (to open the file), an owner password (full access; random when left out) and `PdfPermissions` (print,
  high-quality print, modify, copy, annotate, fill forms, accessibility extraction, assemble). AES-256 (PDF 2.0,
  revision 6, `/AESV3`, SHA-2 password hashing; written as Adobe extension level 8 of PDF 1.7) by default, AES-128
  (revision 4, `/AESV2`) for old readers. Every string and stream of every object is AES-CBC encrypted with a random
  IV, after compression; `EncryptMetadata = false` leaves the XMP metadata stream readable. With only an owner password
  the file opens without one and the viewer enforces the permissions. Passwords go through a simplified SASLprep and
  are cut to 127 UTF-8 bytes. Verified with qpdf, pypdf and PDFium.
- PDF/A-2b and PDF/A-3b (ISO 19005-2/-3, level B) for archiving: `PdfDocument.PdfAConformance`. The document gets an
  sRGB output intent (a 456-byte ICC v2 profile, CC0), `pdfaid:part`/`conformance` in its XMP and a PDF/A extension
  schema for the `pdfx:` custom properties; links get the Print flag. Saving enforces the rules the library could
  break, with an `InvalidOperationException` that names the problem: every font embedded, no .notdef glyph (text a
  font has no glyph for), no encryption, no CMYK images, no pages imported or drawn from other PDF files, XMP on.
  `PdfDocument.StandardFontSubstitute` embeds a TrueType font in place of the standard fonts (a simple font with
  WinAnsi encoding and a (3,1) `cmap`, so the codes already drawn keep working); `EmbedStandardFonts()` from
  RaiCore.Pdf.Fonts uses Liberation. Validated with veraPDF in CI.
- Attachments: `PdfDocument.Attachments` with `PdfAttachment` (file name, data, MIME type, description, relationship,
  dates). Each is an embedded file (Flate-compressed, `/Subtype` MIME type, `/Params` with size, dates and MD5
  checksum) with a file specification (`/F` and `/UF`, `/Desc`, `/AFRelationship`) in the `/EmbeddedFiles` name tree
  and in the catalog's associated files (`/AF`). PDF/A-2b rejects attachments; PDF/A-3b allows any file.
- Factur-X 1.0 / ZUGFeRD 2.x e-invoices: `document.AttachFacturXInvoice(xml, FacturXProfile.EN16931)` attaches the
  CII invoice XML as `factur-x.xml` (`xrechnung.xml` for XRECHNUNG) with the relationship the profile needs (`Data`
  for MINIMUM and BASIC WL, `Alternative` otherwise), makes the document PDF/A-3b and writes the `fx:` XMP properties
  (document type, file name, version, conformance level) with their PDF/A extension schema. Profiles MINIMUM,
  BASIC WL, BASIC, EN 16931, EXTENDED and XRECHNUNG. Validated with veraPDF in CI.
- Reading existing PDFs: `PdfReader.Open` (file, stream or bytes) parses classic cross-reference tables,
  cross-reference streams, object streams, hybrid files and incremental updates (the newest revision wins), and
  repairs damaged files by scanning for objects when the cross-reference data is missing or wrong
  (`PdfReader.IsRepaired`). Streams decode with Flate and LZW (with PNG and TIFF predictors), ASCIIHex, ASCII85 and
  RunLength; image codecs are passed through untouched. `PdfReader.Pages` lists `PdfReaderPage`s with their inherited
  resources, media and crop boxes and rotation resolved; `GetContent()` returns a page's decoded content. Reference
  loops, `/Prev` loops and page tree loops are cut, nesting depth and decoded stream sizes are limited, and malformed
  input throws `PdfFormatException`.
- Merging: `PdfDocument.ImportPages` appends pages of a read file (all, or chosen ones in any order) and
  `InsertPages` inserts them anywhere; `InsertPage` adds a new page anywhere, e.g. a cover. The reachable objects are
  deep-copied once per source file, so fonts and images shared by pages (also across several imports and
  `DrawPage`) are written once, and streams keep their data and filters. `PdfImportOptions`: annotations are kept
  by default, with links within the file pointed at the imported pages (named destinations made explicit, links to
  pages left out dropped); `KeepOutline` adds the file's bookmarks to `PdfDocument.Outline`. More can be drawn on
  imported pages (e.g. page numbers), in a form XObject of its own over the original content; positions on them are
  measured from the media box.
- `PdfCanvas.DrawPage` draws a page of a read file as a form XObject, at its size or scaled into a rectangle, as it
  is displayed (crop box, `/Rotate`), e.g. a letterhead under generated content. `PdfRectangle` holds page boxes.
- Resource limits for untrusted input (`ResourceLimits`): nesting depth, element and table cell counts, image pixels
  (checked from the header, before decoding), resource, font and decompressed stream sizes, pages and time. Exceeding
  one throws `ResourceLimitExceededException`, naming the limit. The defaults apply everywhere (image decoding, WOFF
  and WOFF2, the PDF reader, layout); `limits.Enforce(cancellationToken)` applies others to the work in its scope, with
  a time limit and cancellation checked between pages, blocks, table rows and lines. Deep nesting stops with that
  exception instead of overflowing the stack. PNG data is inflated only as far as the image needs.
- Fuzzed (#53): the HTML/CSS converter (plain, tagged, PDF/UA and PDF/A output), the image decoders, the font
  parsers (OpenType, TTC, WOFF, WOFF2) and the PDF reader, nightly with libFuzzer and locally with a managed mutation
  fuzzer (`tests/RaiCore.Pdf.Fuzz`). Malformed input now fails only with the documented exceptions: corrupt zlib and
  Brotli data, out-of-range WOFF2 values and short `head` tables throw `FontFormatException`, fonts without a `name`
  table embed, and zlib errors in PDF streams and PNG data no longer escape. Every input fuzzing found is replayed by
  the unit tests.

- Tagged PDF and PDF/UA-1: `PdfDocument.Tagged`/`PdfUA`, structure elements (`PdfDocument.StructureRoot`,
  `PdfStructureElement` with alternate and actual text, language, table scope and spans, list numbering;
  `PdfStructureTypes`), `PdfCanvas.BeginTag`/`BeginArtifact`/`EndTag`. Marked content is written flat and only around
  path and painting operators, with its own MCIDs in each transparency group's form; untagged drawing becomes an
  artifact; links are tagged (`/OBJR`, `/StructParent`, `PdfLink.Description` as `/Contents`); the parent tree,
  `/MarkInfo`, `/Tabs /S`, `/DisplayDocTitle` and `pdfuaid:part` are written. PDF/UA checks the title, language,
  XMP and embedded fonts when saving. Untagged documents are unchanged.
- Writing page by page: `PdfDocument.BeginSave(stream)`, `FlushPages()` (writes the pages so far and frees their
  content) and `EndSave()`. Fonts, page dictionaries, links, outline, named destinations, attachments, the structure
  tree and metadata are written at the end, so links may point at any page; tagged, PDF/UA and PDF/A documents can be
  written page by page too. `Save` writes each page's content as it goes too; object numbers changed
  (fonts and page dictionaries now follow the content).

### RaiCore.Pdf.Fonts
- Bundled Liberation Sans, Serif and Mono (SIL OFL 1.1), metric-compatible with Arial, Times New Roman and Courier New.
- `StandardFontSubstitutes`: the Liberation face for each standard font (`Get`), and `PdfDocument.EmbedStandardFonts()`,
  which embeds them in place of Helvetica, Times and Courier (as PDF/A requires).
- `FontCollection` with CSS font matching, system font discovery and metric-compatible substitutes.
- Thread safety: `PdfFont`, `FontCollection`, `BundledFonts`, `PdfImage`, `TextShaper`, `Hyphenator`, `HtmlToPdf`,
  `RazorToPdf` and `RazorPdfRenderer` are thread-safe; documents, pages, canvases, flow documents and `PdfReader` are
  used by one thread at a time (see "Thread safety" in the README). Font data (the file's bytes, parsed tables,
  character map and HarfBuzz face) is shared by every document and thread; what a document uses of a font is kept per
  document. The bundled fonts are loaded once per process and shared by every collection, so conversions without a
  `FontCollection` no longer read and parse them each time; the faces of a `.ttc` file share one copy of its data.
  `FontCollection.AddFont(Func<PdfFont>, ...)` adds a lazily created font that can be shared between collections.
  Fixed: a font's character map could be seen without its symbol flag by a concurrent first use, and a hyphenation
  `Register` racing with lookups could leave them cached with the old answer.

### RaiCore.Pdf.Layout
- The package includes the Unicode License v3 notice (`licenses/unicode.txt`) for its bidi and line break tables,
  generated from the Unicode Character Database, and its license expression names `Unicode-3.0`. All third-party
  material is listed in `THIRD-PARTY-NOTICES.md`.
- HarfBuzz text shaping (kerning, ligatures, Arabic joining, Indic reordering, mark positioning) with per-character
  font fallback.
- Unicode line breaking (UAX #14, passing the full conformance test) and paragraphs with alignment, justification,
  line heights, indents, soft hyphens and hard breaks; inline images and inline boxes (`Paragraph.AddBox`: any block
  as one item of a line, shrink-to-fit, aligned on its last baseline), leaders (`Paragraph.AddLeader`: a pattern
  repeated to fill the rest of the line, as in a table of contents); `InlineAlignment` positions inline text, images
  and boxes vertically (CSS 2.1 section 10.8); `Paragraph.BeginInline`/`EndInline`/`AddInline` give part of the text an
  inline element's box (`InlineDecoration`).
- Hyphenation: `TextStyle.Hyphens` (`Manual`, the default: only at soft hyphens; `None`: never, not even at soft
  hyphens; `Auto`: also where the hyphenation patterns of `TextStyle.Language` allow), `HyphenateCharacter` (U+2010 by
  default, or the hyphen-minus when the font lacks it) and `HyphenationLimits` (the shortest word, and the fewest
  characters before and after a hyphen; by default 5 and the language's own minimums). A hyphenation point is a break
  opportunity whose hyphen, shaped in the text's font, counts when fitting and justifying the line, and in min-content
  widths. Words with a soft hyphen are hyphenated only there, and the style of a word's first letter decides for the
  whole word. `Hyphenator` implements Liang's algorithm (TeX's) with patterns and exceptions; patterns for en-US,
  en-GB, de, da, nl, fr, es, it, pt, pl, nb and nn are bundled from hyph-utf8 (compressed, loaded on first use; MIT,
  BSD-3-Clause or all-permissive licences, listed in `Text/Hyphenation/Patterns/LICENSES.md` and packed as
  `licenses/hyphenation-patterns.md`), language tags fall back to shorter ones (`de-CH` uses `de`), and
  `Hyphenator.FromPatterns` with `Register` adds other languages.
- Bidirectional text: the Unicode Bidirectional Algorithm (UAX #9, passing the full `BidiTest.txt` and
  `BidiCharacterTest.txt` conformance tests) orders mixed left-to-right and right-to-left text on every line, after
  lines are broken in logical order. `Paragraph.Direction` sets the base direction (left to right, right to left, or
  automatic from the first strong character); `TextAlignment.Start` (now the default) and `End` follow it, and the
  first-line indent and the last line of justified text are on the start side. Embedding, override and isolate
  controls in the text are honored and not drawn; right-to-left runs are shaped right to left (mirroring brackets),
  numbers keep their order. An inline element's box or a link split by reordering gets one part per visual piece,
  with the element's edges where CSS 2.1 section 8.6 puts them (`BeginInline`'s `direction`). Text is still drawn in
  logical order, so extraction reads it as written. `TextShaper.Shape` takes an optional direction.
- Right-to-left tables and flex containers: `TableBlock.RightToLeft` puts the first column (of the cells, spans and
  `ColumnWidths`) on the right, mirroring the grid so collapsed borders and splitting work unchanged;
  `FlexBlock.RightToLeft` starts rows on the right (`RowReverse` then runs left to right) and the cross axis of
  columns on the right (alignment and wrapped lines). In a reversed or right-to-left row, the item margin on the
  main-start side (the right one) now comes first.
- Block layout and pagination: the box model, margin collapsing, orphans and widows, forced and avoided page breaks,
  boxes sliced across pages, page decorators for headers and footers. A paragraph whose orphans and widows leave no
  possible break needs all its lines at the bottom of a page before it starts there.
- A page setup per page: `FlowDocument.PageSetupResolver` chooses each page's size and margins from a `PageContext`
  (page number, `IsFirst`, page name, `PageSide` and whether it is a blank page), and `PageInfo` gives decorators the
  page's own setup, name, side and blankness. `Block.PageName` names the pages a block goes on: a change of name
  between siblings forces a page break, a container starts with its first child's name, and pages created by
  automatic breaks keep the name. `PageBreak.Left` and `PageBreak.Right` start a page on that side, inserting a blank
  page when needed (before the first content they set the first page's side). Blocks laid out on a page with another
  content width get that width through containers without a width of their own (backgrounds follow on every page),
  and pages with another left margin shift their content (links included).
- `height`, `min-height` and `max-height` on every block (`BoxStyle.Height`, `MinHeight`, `MaxHeight`). Content
  taller than a fixed height overflows, as with `overflow: visible`; a fixed-height box that fits on a page is not
  split.
- `min-width` and `max-width` on every block (`BoxStyle.MinWidth`, `MaxWidth`, as border-box widths), applied by the
  layout engine to fixed, filling and shrink-to-fit widths alike (inline boxes, tables, flex items in rows and
  columns) and to intrinsic widths; the minimum wins over the maximum, as in CSS. Images scale to them, keeping their
  aspect ratio unless both a width and a height are given; a table without a width does not get narrower than its
  content. They replace `FlexItem.MinWidth`/`MaxWidth`.
- Percentage sizes (`BoxStyle.Percentages`, `BoxPercentages`, `BoxLength`, `RelativeThickness`): a width, min/max
  width, margins and padding of points plus a percentage of the containing block's width, resolved in layout against
  the width that block actually gets: a flex item after it grew or shrank, a table column, a shrink-to-fit float or
  inline box, an absolute box's containing block. Min-content and max-content widths treat a percentage width as
  auto, and an image with a percentage width or max-width can shrink with its container (CSS Sizing 3 section 5.2).
  `FlexBasis.Percent` also applies to rows.
- Tables: automatic and fixed layout, `colspan`/`rowspan`, separate and collapsed borders. Header rows repeat at the
  top of every page and footer rows (`TableRow.IsFooter`) right below the body; footers taller than a quarter of the
  page are not repeated, as in Chrome. Rows that do not fit are split like browsers do, each cell continuing on the
  next page in its own column with backgrounds and borders sliced; rows joined by row spans may also break between
  them, with the spanning cell continuing alongside. `TableRow.BreakInside` keeps a row whole when it fits on a page.
- Flexbox (`FlexBlock`, `FlexItem`): all directions, wrapping (including `wrap-reverse`), `justify-content`,
  `align-items`/`align-self` (`stretch`, `flex-start`, `flex-end`, `center`, `baseline`), `align-content`, gaps,
  `order`, grow/shrink/basis with min/max sizes and the automatic minimum size, and auto margins. Containers with a
  definite or minimum height flex their columns vertically and wrap into side-by-side columns; a flex item's flexed or
  stretched size is definite for a flex container inside it. Rows taller than the space left on a page are split like
  browsers do, each item continuing in its own column and stretched items running down to the end of the row.
- Border styles (`BorderStyle` on `BorderSide`): solid, dashed, dotted, double, groove, ridge, inset and outset, drawn
  like browsers draw them, with sides of different colors or styles meeting on the corner diagonals. Collapsed table
  borders resolve conflicts as in CSS 2.1 (`hidden` wins, then the wider border, then the style) and keep their style.
- Rounded corners (`BoxStyle.BorderRadius`, `CornerRadii`): circular or elliptical radii in points, percentages of the
  border box or both, with overlapping radii scaled down as CSS specifies. Backgrounds, borders in every style (sides of
  different widths and colors meet on the corner diagonals; dashes and dots run around the curves, and translucent ones
  are painted once where sides meet) and the images of
  image blocks and inline images (`InlineImage.BorderRadius`) follow them. A box split across pages is rounded only at
  its own top and bottom, an inline element only at its first and last line; tables with collapsed borders are not
  rounded. `BoxStyle.BackgroundClip` paints the background over the border, padding or content box.
- Text decorations (`TextStyle.Decorations`): underline, overline and line-through, solid, double, dotted, dashed or
  wavy, with their own color, thickness and underline offset. Each is drawn with the metrics of the font that declared
  it (the `post` and `OS/2` tables; `PdfFont.UnderlinePosition`, `StrikeoutPosition` and their thicknesses) as one
  continuous line across adjacent text.
- Letter and word spacing (`TextStyle.LetterSpacing`, `WordSpacing`): added after every character (a character with
  its marks, or a ligature) and word separator, included in measuring, line breaking and intrinsic widths, and
  combined with justification. Letter spacing turns optional ligatures off and leaves cursive scripts such as Arabic
  unspaced, as browsers do.
- Bookmarks: `Block.Bookmark` makes a block an entry in the PDF outline. Entries nest by level and point at where
  the block starts (where its content starts, if that moved to a later page), also inside flex items and table cells.
- Links: `LinkTarget` (a URI, or a destination id), `Paragraph.BeginLink`/`EndLink`/`AddLink` for links in text (one
  rectangle per line, as high as the line, on the page each line is drawn on; `LineLayout.Links` gives the pieces)
  and `Block.Link` for whole blocks (their border box on every page they continue on). `Block.Id` and
  `Paragraph.AddAnchor` (the top of the line it falls on) become the PDF's named destinations when rendered; the first
  one with an id wins. Links work in flex items, table cells, inline boxes and page decorators.
- Opacity: translucent colors (text, backgrounds, borders, decorations) are painted with real alpha, and
  `BoxStyle.Opacity` draws everything a block paints (background, borders, content and descendants) as one
  transparency group per page it is on, also in flex items, table cells and inline boxes. `InlineImage.Opacity` (and
  the `opacity` argument of `Paragraph.AddImage`) does the same for an image in a line. Translucent dashed and dotted
  borders are painted once where adjacent sides' dashes meet in a corner.
- Overflow (`BoxStyle.ClipOverflow`): a block's content (also its floats and positioned children, which paint in later
  layers) is clipped to its padding box, with the inner rounded corners, on each page of a box split across pages. A
  clipped box of a fixed height that fits on the page is laid out in one piece, so content it hides makes no pages; a
  taller one ends at its height. Drawing scopes now nest clips with opacity groups. `Paragraph.Ellipsis` cuts a line
  too wide for its room at the last grapheme where "…" still fits (CSS `text-overflow: ellipsis`).
- A fixed height (or min-height) that reaches below the page now continues on the next pages, splitting the box,
  instead of running off the page.
- Background layers (`BoxStyle.BackgroundLayers`): images (`ImageBackground`) and gradients (`LinearGradient` at an
  angle or toward a corner, `RadialGradient` with circle or ellipse, extent keywords or radii and a center; both
  repeating if wanted) over the background color, the first on top. Each layer is sized (`BackgroundSize`: auto,
  cover, contain or lengths), positioned from either edge of its origin box (`BackgroundPosition`), repeated
  (`BackgroundRepeat`: repeat, no-repeat, space, round) and painted in its clip box, rounded corners included. A box
  split across pages is painted as if whole. Gradients are PDF shadings painted with `sh` (so they follow relative
  offsets and other transformations); translucent stops get a soft mask. Repeated images are stored once and drawn
  per tile (up to 10,000 tiles per fragment).
- Positioning (`BoxStyle.Position`, `Insets`, `ZIndex`): a relative block is laid out in the flow and drawn moved by
  its insets. An absolute block leaves the flow and is placed in its containing block (the padding box of the nearest
  positioned ancestor, or the page area of the page it is on) once that is laid out: its width from its own, from a
  left and a right inset, or shrink-to-fit; its height from its content or from a top and a bottom inset; auto insets
  keep where it would have been in the flow; percentages are of the containing block. It is laid out as one piece, on
  the page its containing block starts on (or ends on, for a bottom inset). A fixed block is placed in the page area of
  every page. Positioned blocks are painted over the flow and floats, ordered by z-index (a negative one goes under
  the flow), with one stacking order per page.
- Floats (`BoxStyle.Float`, CSS 2.1 section 9.5): a floated block is taken out of the flow and placed as high as it
  can at its place and as far to its side as earlier floats allow (moving down until it fits), without collapsing
  its margins; without a width it shrinks to fit. Text in the same formatting context flows around it: each line is
  broken to the room beside the floats where it is, and moves down past them when its first word does not fit.
  `BoxStyle.Clear` moves a block (also an empty one) below earlier floats, the clearance replacing its top margin.
  Tables, flex containers, images and blocks with `BoxStyle.ContainsFloats` go beside floats or below them; those
  blocks, floats, flex items and table cells contain their own floats (they grow to hold them, and their margins do
  not collapse with their children's). A float that does not fit on the rest of the page moves to the next one with
  what follows it; one that continues on later pages keeps the text there flowing around it. Floats are painted over
  the backgrounds of the blocks around them, as in CSS.

- Tagging: `Block.Tag`, `TableCell.Tag` and `Paragraph.BeginTag`/`EndTag` (`StructureTag`) give layout content its
  structure elements, created in document order when rendering into a tagged document; paragraphs, images and tables
  get default tags, table rows their row groups, and links in text Link elements. Backgrounds, borders, text
  decorations, repeated table header and footer rows and the page decorator are artifacts.
- Faster layout: paragraph layouts (by width), intrinsic widths and short shaped texts are cached for one layout pass
  (never across threads or documents; the output is byte for byte the same), clusters' scripts are looked up once per
  code point, one-character glyph texts are shared, and long tables no longer scan every cell on every page.
  `FlowDocument.RenderTo` flushes each page as soon as it is drawn into a document written page by page.

### RaiCore.Pdf.Html
- HTML and CSS to PDF via AngleSharp: the cascade, inheritance, `!important`, custom properties and print media
  queries, with browser default styles.
- Block and inline formatting, white-space handling, the box model, fonts, colors, text alignment and transforms,
  list markers, images, linked stylesheets and tables (including the legacy table attributes, `thead`, `tfoot` and
  `tr { break-inside: avoid }`).
- SVG in `<img>` (files and `data:` URIs, URL-encoded or base64) and inline `<svg>` elements, sized and placed like
  images by CSS `width`/`height` or the element's attributes; `currentColor` follows the element's CSS `color`.
- `display: flex`, with all the flex properties above.
- Backgrounds, borders, padding and horizontal margins on inline elements: drawn behind the text of each line they
  cover (the font's content area plus padding and border, raised with the element), sliced at line breaks and around
  blocks inside them; horizontal edges take space on the line, vertical ones do not change its height.
- `vertical-align` on inline content (text, images, inline boxes): `sub` and `super` (a fifth and a third of the
  parent's font size, as browsers do; `<sub>`/`<sup>` use them by default), lengths, percentages of the line height,
  `middle`, `text-top`, `text-bottom`, `top` and `bottom`. Nested shifts add up; a block's or table cell's own
  `vertical-align` does not move its text.
- `inline-block`, `inline-flex` and `inline-table` as atomic inline boxes: one unbreakable item of the line with its
  own padding, border, background and width (or shrink-to-fit), aligned on the baseline of its last line of text (the
  first row for tables, the first baseline for flex containers, the bottom margin edge without text).
- `text-decoration` (and its `-line`, `-style`, `-color` and `-thickness` longhands) and `text-underline-offset`;
  `<u>`, `<ins>`, `<s>`, `<del>` and `<strike>` by default. Decorations propagate to all inline content, cannot be
  removed by descendants, skip atomic inline boxes and running elements, and are drawn with the font, size and
  baseline of the element that declared them, as browsers do.
- `letter-spacing` and `word-spacing` (lengths, including negative ones).
- `border-style` and `border-{side}-style` (also through the `border` shorthands), including `hidden` in collapsed tables.
- `border-radius` and its longhands: one to four values, the elliptical `/` form, lengths, percentages and `calc()`, on
  blocks, flex items, table cells, inline elements and images (an `<img>` is clipped to its rounded content box); and
  `background-clip`.
- Generated content: `::before` and `::after` (also the legacy `:before`/`:after`), inserted as the element's first
  and last child in blocks, inline elements, list items, table cells and flex containers. They have their own styles
  (inheriting from their element): inline with an inline box for background, border and padding, `inline-block` as
  atomic inline boxes, or block-level. `content` takes strings with CSS escapes, `attr()`, `open-quote`/`close-quote`
  (with `quotes`; by default double quotes, then single quotes for nested ones), `no-open-quote`/`no-close-quote` and
  `url()` images; `none` and `normal` generate nothing. `<q>` elements get quotes from the browser default styles.
- CSS counters (CSS Lists 3): `counter-reset`, `counter-increment` and `counter-set` with nested scopes (a reset on a
  sibling replaces the counter, one on a descendant nests a new one), computed in document order with `::before` and
  `::after`; `counter()` and `counters()` in every list-style counter style in generated content, `string-set` and
  `bookmark-label`. List items increment the implicit `list-item` counter (`ol`/`ul`/`menu` reset it, honoring
  `start`, `reversed` and `value`), and list markers show it.
- Page-context counters: `counter-reset`, `counter-increment` and `counter-set` in `@page` rules (cascaded like the
  other page properties, so named pages and `:first`/`:left`/`:right`/`:blank` rules apply to the pages they match)
  change the `page` counter (incremented on every page, blank pages included, unless named) and named counters, shown
  in margin boxes with `counter()`.
- Cross-references (CSS GCPM): `target-counter()` and `target-counters()` (the page, or any counter, of the element
  an `attr(href)` or `"#id"` points to), `target-text()` (its text, `::before`/`::after` or first letter) and
  `leader()`, for tables of contents with dotted leaders and right-aligned page numbers. Page references to later
  elements are resolved by laying the document out again until the pages stop changing (at most four layouts).
  `FlowDocument.MapPages` gives the pages (with their names and sides) and the page of each destination.
- `min-width` and `max-width` (also with percentages and `calc()`), applied in layout, so they also limit
  shrink-to-fit boxes (`inline-block`, tables, flex items) and the content inside them; a block narrowed by
  `max-width` is centered by auto margins. Images scale to them, and a declared `width` or `height` (even `auto`, as
  in `img { max-width: 100%; height: auto }`) overrides the `width`/`height` attributes, as in browsers.
- Percentages (also in `calc()`) in `width`, `min-width`, `max-width`, `margin`, `padding` and row `flex-basis`
  resolve against the width the containing block gets in layout, so the children of a flex item that grew or shrank,
  of a table cell, of a shrink-to-fit float or `inline-block` and of an absolute box follow its final width (issue
  #90). A block with a `width` centered by `margin: 0 auto` stays centered in that final width.
- `direction`, `unicode-bidi` (`embed`, `isolate`, `bidi-override`, `isolate-override`, `plaintext`) and the `dir`
  attribute (`ltr`, `rtl`, `auto`), `<bdi>` and `<bdo>`: right-to-left paragraphs start on the right, inline elements
  become the matching bidi embeddings, overrides and isolates, and `text-align: start`/`end` follow the direction.
  Right-to-left tables and flex containers run right to left; a box narrower than its containing block (a table or
  image that shrinks to fit, or a block with a `width`) goes to the containing block's start side, the right when
  that is right to left (CSS 2.1 section 10.3.3). `margin-inline-start`/`-end`, `margin-inline`,
  `padding-inline-start`/`-end` and `padding-inline` follow each element's direction, through the cascade (AngleSharp
  maps them as if everything were left to right, so they are carried in custom properties); lists and `<dd>` are
  indented with them, so right-to-left lists are indented on the right with their markers there.
- `height`, `min-height` and `max-height`; percentage heights resolve against definite ancestor heights, starting
  with the page area, and `vh` units are the page area (to within a pixel).
- `calc()`, `min()`, `max()` and `clamp()` in every length (sizes, margins, padding, `flex-basis`, gaps, `font-size`,
  `line-height`, `text-indent`, `vertical-align`, `letter-spacing`, `word-spacing`, `text-decoration-thickness`,
  `text-underline-offset`, table and `<col>` widths, `@page` size and margins): nested, with
  CSS variables, any mix of units, and percentages resolved against the containing block like plain percentages.
  Invalid expressions fall back to the property's initial value; results are clamped at 0 where lengths cannot be
  negative. Viewport units (`vw`, `vh`, `vmin`, `vmax`) follow the page area set by `@page`.
- CSS Paged Media: `@page` size (lengths, `landscape`/`portrait`, and the ISO A0-A10 and B0-B10, JIS-B4/B5, letter,
  legal and ledger keywords) and margins, `:first`/`:left`/`:right`, margin boxes for headers and footers,
  `counter(page)`/`counter(pages)`, `string-set` and running elements (which can be flex containers).
- Named pages and page-specific geometry: the `page` property (cascaded by specificity) with `@page name` rules, page
  selectors combining a name with `:first`, `:left`, `:right` and `:blank` (also in selector lists), cascaded by
  page selector specificity then source order, give each page its own `size` and margins (e.g. landscape pages for
  wide tables, mirrored margins); margin boxes use each page's size and margins. `break-before`/`break-after: left`,
  `right`, `recto` and `verso` insert blank pages, which `@page :blank` styles.
- Web fonts: `@font-face` with `url()` and `local()` sources, `format()` hints (unsupported formats are not
  downloaded), weight ranges, `font-style` and `unicode-range`, matched like CSS Fonts 4 and loaded on first use. A
  declared family hides installed fonts of the same name; a family that fails to load falls back to the next one.
  `PdfFont.WithUnicodeRanges` restricts a font to code point ranges, embedding it once however many views use it.
- PDF bookmarks from `h1`-`h6`, nested by level, customized with the CSS GCPM properties `bookmark-level`,
  `bookmark-label` (strings, `content()`, `content(first-letter)`, `content(before)`/`content(after)` and `attr()`)
  and `bookmark-state`, cascaded by specificity. `HtmlConverterOptions.CreateBookmarks = false` turns them off.
- Links: `<a href>` to URIs (`http(s):`, `mailto:`, `tel:` and others; relative URLs resolve against `BaseUri`, or
  are kept as written without one; `javascript:` and empty links are dropped) and `href="#id"` to the element with
  that `id` or an `<a name>` (block elements at the top of their box, inline ones at the line they start on). A link
  around blocks is clickable over their boxes; links to ids that do not exist are left out.
- Translucent colors keep their alpha (`rgba()`, `hsla()`, `#rrggbbaa`, `#rgba`, `transparent`) instead of being
  blended with white, and `opacity` makes an element a transparency group (including inline images); a translucent
  CSS `color` reaches `currentColor` in inline SVG.
- Hyphenation: `hyphens` (also `-webkit-hyphens`), `hyphenate-character` (also `-webkit-hyphenate-character`) and
  `hyphenate-limit-chars`, with the language from the nearest `lang` (or `xml:lang`) attribute; the language also
  reaches HarfBuzz for localized glyph forms. Text with `white-space: nowrap` or `pre` is not hyphenated, not even at
  soft hyphens.
- `overflow` (`hidden`, `clip`, `auto`, `scroll`: all clip on paper) and `text-overflow: ellipsis` (in a box that clips).
- `background-image` (`url()`, `linear-gradient()`, `radial-gradient()` and their repeating forms),
  `background-repeat`, `-position` (one to four values), `-size`, `-origin`, `-clip` and multiple layers. The
  `background` shorthand is split into its longhands before AngleSharp.Css parses it (it throws on several layers and
  resets `background-origin` wrongly), gradients toward a corner keep their corner, and `url()` in a linked stylesheet
  is relative to that stylesheet.
- `position` (`relative`, `absolute`, `fixed`; `sticky` is static), `top`/`right`/`bottom`/`left` and `z-index`:
  absolutely positioned elements are block-level and do not float, also spans and images in the middle of text (they
  leave the paragraph without splitting it). A relative element's percentages resolve against its containing block's
  width (and a definite height); an absolute one's against its containing block in layout.
- `float` (`left`, `right`, `inline-start`, `inline-end`) and `clear`: floated elements are block-level, also images,
  inline tables and spans in the middle of text (they go before their paragraph, without splitting it); flex items
  do not float. `display: flow-root` and `overflow` other than `visible`/`clip` start a new formatting context that
  contains its floats, and clearfix rules (`::after { content: ""; display: table; clear: both }`) work.
- Network access is off unless you supply a `ResourceLoader`.
- Document metadata from the HTML: `<title>`, the language from `<html lang>` (or `xml:lang`, or a
  `<meta http-equiv="content-language">` pragma), and `<meta name="author|description|keywords|generator">` as the
  author, subject, keywords and creator. `HtmlConverterOptions.Title`, `Author`, `Subject`, `Keywords`, `Creator` and
  `Language` override them.
- `HtmlConverterOptions.Encryption` encrypts the converted PDF (Razor conversion included).
- `HtmlConverterOptions.Tagged` and `PdfUA`: the structure from HTML semantics (headings, paragraphs, lists with labels
  and bodies, tables with header cells and scopes, figures with `alt` text, sections, quotations, links, `lang`).
  Validated with veraPDF (PDF/UA-1) in CI.
- `HtmlConverterOptions.PdfAConformance` writes PDF/A-2b or PDF/A-3b, with Liberation embedded for SVG text in the
  standard fonts.
- Styles are computed with one AngleSharp style collection instead of one per element, which made styling quadratic in
  the size of the document: a 10,000-row table converts in about 10 s instead of 208 s.
- `HtmlToPdf.ConvertToStream(Async)` writes the PDF to a stream page by page.
- Style sheets with rules AngleSharp cannot parse the selector of (such as `li::marker`) no longer fail the conversion.
- `HtmlConverterOptions.Limits` (resource limits, see RaiCore.Pdf): a document nested too deeply or with too many
  elements, table cells or pages, or that takes too long (parsing included), fails with
  `ResourceLimitExceededException`; an image, stylesheet or web font over a size limit is skipped like a broken one.
  `ConvertAsync` observes its cancellation token throughout. Conversions run on a thread of their own with a large
  stack, so deeply nested documents do not overflow it. `colspan`, `rowspan` and `<col span>` are clamped as HTML
  says (1000, 65534 and 1000).
- Hostile CSS no longer fails a conversion with an exception (found by fuzzing): numbers must be finite and lengths
  are clamped to 1,000,000 points (`flex-grow: 1e3082` looped forever), image sizes to 14,400 points, `@page`
  margins that leave no room are dropped, declarations and rules AngleSharp cannot read are removed and an element
  whose style it cannot compute takes its parent's (it threw `NullReferenceException` on them), `box-shadow` and `text-shadow` (not drawn) are ignored, invalid
  `font-feature-settings` are skipped, and web fonts whose license forbids embedding fall back to other fonts.

### RaiCore.Pdf.Razor
- Razor components to PDF with Blazor's static `HtmlRenderer`: `RazorToPdf` for the simple case, `RazorPdfRenderer`
  for components that inject services, and `AddRazorPdf()` to register it per scope.
- The component's `<html lang>`, `<title>` and `<meta>` elements become the document metadata as in RaiCore.Pdf.Html;
  the creator is `RaiCore.Pdf.Razor` unless the options or a `<meta name="generator">` name another.

### Trimming, Native AOT and containers
- CI publishes the package smoke test trimmed and with Native AOT on Windows, Linux and macOS, runs it
  (`tests/PackageSmoke/publish-and-run.sh`) and fails on trim or AOT warnings other than the known ones in
  `Microsoft.AspNetCore.Components`. HarfBuzz's native library is published for the target runtime.
- CI also runs the package smoke test, trimmed and Native AOT publishing included, in Alpine (musl) and Debian
  containers on x64 and Arm64, and builds the sample Dockerfile in `docs/docker` (an HTML-to-PDF app on Alpine).

### Known limitations
The [roadmap issues](https://github.com/raicevic/raicore-pdf/issues) track these.
- Borders on inline elements are drawn solid, unless they have rounded corners.
- `min()`, `max()` and `clamp()` with percentages in `border-radius` are evaluated against the box's width as far as it
  is known when the styles are read (plain percentages and `calc()` follow the painted box exactly).
- Percentages that are resolved when the styles are read, against the width the containing block is expected to
  have (so they do not follow a flex item that grew or shrank, a table column or a shrink-to-fit box): `min()`,
  `max()` and `clamp()` of a percentage in sizes, margins and padding; the size of an image in text (`<img>` is
  inline unless it is `display: block` or a flex item) and the `width` and `height` of a block image (its
  `max-width` and `min-width` follow); `text-indent`, gaps and `inset`s of relative boxes; a `flex-basis` with a
  percentage and content-box padding or borders; the auto margins of a block limited by `min-width` or `max-width`; the vertical padding of a box with a content-box `height`, `min-height` or `max-height` (which
  includes it); the margins of `inline-block` boxes; and the padding of table cells.
- Generated content: no `::marker`, `::first-line` or `::first-letter`; the legacy `:before`/`:after` count as a class
  in specificity (AngleSharp), not as an element. `counter(page)` in the document's own content (outside margin boxes)
  generates nothing; use `target-counter()`. Document counters are not in scope in margin boxes (they show 0; pass
  them with `string-set`). The `content` values AngleSharp drops (with `target-counter()`, `target-counters()`,
  `target-text()` or `leader()`) are cascaded without `!important`. `target-*()` only follow same-document `#id`
  URLs, and `target-text()` of a `::before`/`::after` further on leaves out quotes. Page references that keep
  changing after four layouts keep the last values. A leader is drawn once on a line that is already full, and only
  in its own font (a pattern needing font fallback is not repeated).
- Ids on table rows and cells are not link destinations (ids inside cells are), and neither are ids inside inline
  boxes (`inline-block`) or in running elements; links inside them work. The same goes for `target-counter(..., page)`. A link's clickable area is the line box
  (rectangles, not rotated with transforms).
- Positioning: z-indexes are not nested (one stacking order per page); absolute blocks do not break across pages; a
  relative or absolute inline element (a span in text) is not moved, and an absolutely positioned element in text
  keeps the static position of its paragraph's start; absolutely positioned children of a flex container are laid out
  as flex items; inside a table cell the containing block of an absolute block without a positioned ancestor in the
  cell is the cell's content.
- Grid (rendered as blocks). `overflow` clips both axes, even when only one is set; `text-overflow` takes only `clip`
  and `ellipsis` (no strings), at the line's end; content hidden in a clipped box taller than a page may still add
  pages at the end of the document.
- Backgrounds: `background-attachment` is ignored (`fixed` acts as `scroll`); color hints in gradients are dropped
  (the colors blend evenly); `image-set()`, `conic-gradient()` and `cross-fade()` are not supported; inline elements
  (a `span` in text) and collapsed table cells paint only their background color. `calc()` with percentages in a
  background position or size uses 0 for the percentage.
- Floats: a float in the middle of a paragraph's text starts at the paragraph's first line, not at the line it is
  on. A line is checked against floats at its strut's height, so a taller line (a large font or image) may overlap a
  float that starts lower down.
- `opacity` on inline elements (other than images and inline boxes) and on table rows and cells is ignored; where
  translucent collapsed table borders cross, they are painted twice.
- `unicode-bidi: isolate-override` in author styles (AngleSharp drops it; `<bdo>` works). The logical border
  properties (`border-inline-start` and the like) and `inset-inline` are left to right; in a right-to-left element a
  logical margin or padding written with `var()` is 0.
- Hyphenation fills lines greedily, like browsers (no Knuth-Plass paragraph optimization, no
  `hyphenate-limit-lines`, `hyphenate-limit-zone` or `hyphenate-limit-last`). Text in `::before`/`::after` has no
  language, so it is not hyphenated automatically. Patterns for languages under the LPPL alone (such as Swedish) or
  the GPL (Czech) are not bundled, and decomposed (NFD) text may miss hyphenation points.
- No synthetic bold or italic: a family without a bold or italic face uses its closest face as is.
- Razor components under trimming and Native AOT: `Microsoft.AspNetCore.Components` reports four IL2072 warnings and
  Microsoft does not support it with Native AOT; the smoke test's components work, but components found only by
  name at run time can be trimmed away.
- In globalization-invariant mode (no ICU, as in the Alpine runtime images), AES-256 passwords are not
  NFKC-normalized: .NET cannot normalize without ICU, so a password that is not already in NFKC (a decomposed `é`,
  a ligature) gives a key that viewers will not derive.
- Pages: no `@page :nth()`. Percentages, `vw`/`vh` and auto margins resolve against the content width of the
  element's named page (that of a right page that is not the first; unnamed pages use the plain `@page` rules), so
  `:first`/`:left`/`:right` pages with another content width do not change them. Content continuing onto a page with
  another content width (after an automatic break) keeps the width it started with, and `page` is ignored inside flex
  items, table cells and inline boxes. Page progression is left to right (`recto` is a right page). Inline elements
  cannot be bookmarks.
- PDF/A: only level B of parts 2 and 3 (no PDF/A-1, no levels A and U), always with the sRGB output intent, so
  CMYK JPEG images are rejected rather than converted. Pages imported from other PDFs are rejected, even when they are
  PDF/A themselves. Text in characters no font has fails under PDF/A instead of showing the missing-glyph box. Symbol
  and ZapfDingbats have no substitute. The nesting limit of 28 `q` levels is not checked.
- Tagging: `lang` on inline elements, abbreviations (`abbr` expansions), `figure`/`figcaption` grouping, description
  lists as lists, table `headers`/`id` associations, form fields and notes are not tagged yet; a `dl` is a sequence of
  paragraphs. Flex items are read in document order, not in their visual order (`order`, `row-reverse`). PDF/UA needs
  embedded fonts (a standard font only with a `StandardFontSubstitute`, such as `EmbedStandardFonts()`), and imported
  pages are not tagged.
- Factur-X / ZUGFeRD: the invoice XML is only checked to be well-formed with a CII root element
  (`rsm:CrossIndustryInvoice`); it is not validated against the profile's XML schema or business rules (use the
  official validators or Mustang for that), nor compared with the visible pages. Attachments are document-level
  associated files only: no file attachment annotations on pages and no portfolios.
- Layout time grows exponentially with the nesting of tables (each level measures and lays out the next more than
  once): 12 nested tables take about a second, 20 more than a minute, so a few hundred bytes of such markup run until
  the `ResourceLimits.Timeout` (5 minutes by default; lower it on servers). Nested `inline-block`s and flex
  containers are linear, and a 10,000-row table takes about 9 s.
- Metadata: custom property names that are not XML names are encoded (`_x0020_` for a space).
  Keywords and the author are plain text (`dc:creator` holds one entry). Language alternatives are written with
  `x-default` only, and the language is not checked against BCP 47. `HtmlToPdf.CreateFlowDocument` returns no
  metadata: set `PdfDocument.Info` and `Language` on the document you render it into. XMP in images and other
  objects is not written.
- Encryption: no RC4 (40/128-bit), no deprecated AES-256 revision 5 and no public-key (certificate) security. The
  SASLprep for AES-256 passwords is simplified: unassigned code points and the bidirectional rule are not checked.
  AES-128 passwords must be printable Latin-1. Encrypted output is not reproducible byte for byte (random keys, salts
  and IVs). Permissions are advisory, as in every PDF: viewers enforce them, but they are not a protection against
  a determined user who can open the file.
- Reading PDFs: encrypted files are rejected (`NotSupportedException`); decryption will follow encryption. Imports
  do not copy the source's interactive form (`/AcroForm`: widgets keep their appearance but are not fields of the
  new document), structure tree (tagged PDF), optional content configuration, named destinations, JavaScript or
  page labels; a page imported twice has its annotations on its first copy only. `DrawPage` draws a page's content
  without its annotations. Decoding is needed only for cross-reference and object streams and for pages drawn with
  several content streams, so the image filters (DCT, JPX, JBIG2, CCITT) are never decoded.
- Thread safety: fonts declared with `@font-face` are loaded per conversion (put fonts shared by many documents in the
  `FontCollection`); a loaded font's data stays in memory as long as the `PdfFont` (and a collection that found it) is
  referenced.
- Variable fonts are embedded at their default instance: CFF2 fonts are not instanced at other axis values (nor are
  TrueType variable fonts), so `font-weight` or `font-variation-settings` cannot select another instance of one
  variable font file. CFF glyphs built with `seac` (accented glyphs from old Type 1 conversions) and charstrings using
  the arithmetic operators make the font embed whole, as do fonts whose license asks not to be subset.
- SVG: no filters, masks, markers, patterns, `spreadMethod` `reflect`/`repeat` (drawn as `pad`), text on a path or
  per-character positions (only the first `x`/`y`/`dx`/`dy`), and no web fonts in SVG text (the standard fonts are
  used). PDF shadings have no alpha: a gradient whose stops differ in opacity takes its most opaque stop's alpha and
  blends the others with white towards it (exact on white only). `opacity` on a `tspan` is ignored. A clip path made of
  several shapes uses the first one's clip rule; text in clip paths is ignored. HTML style sheets do not style
  the inside of inline `<svg>` (its own `<style>` and attributes do).
- Writing page by page saves the memory of the drawn content only: `FlowDocument` and HTML lay out every page before
  drawing the first (page counts, `target-counter()`), so their peak memory is the layout's and hardly changes.
  Pages imported from other PDFs keep what they copy until `EndSave`.
