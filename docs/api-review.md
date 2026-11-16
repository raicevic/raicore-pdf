# Public API review before 1.0

This is the review [#60](https://github.com/raicevic/raicore-pdf/issues/60) asks for. It covers every public type of
the five packages as of `main` on 2026-11-16 (166 types), and lists what should change before the API is locked by
[#61](https://github.com/raicevic/raicore-pdf/issues/61).

Every public type and member was listed by reflection and read against these questions: naming, nullability,
immutability (`init` or mutable), options objects, async and cancellation, exceptions, consistency between packages,
types that should be internal, and APIs that should be marked `[Experimental]`.

| Package | Public types | Namespaces |
| --- | --- | --- |
| RaiCore.Pdf | 53 | `RaiCore.Pdf`, `.Fonts`, `.Fonts.OpenType`, `.Images`, `.Objects` |
| RaiCore.Pdf.Fonts | 2 | `RaiCore.Pdf.Fonts` |
| RaiCore.Pdf.Layout | 101 | `RaiCore.Pdf.Layout`, `.Layout.Blocks`, `.Layout.Text` |
| RaiCore.Pdf.Html | 7 | `RaiCore.Pdf.Html` |
| RaiCore.Pdf.Razor | 3 | `RaiCore.Pdf.Razor` |

## What is already in good shape

- **Nullability** is enabled everywhere, and the annotations match the behaviour that was checked (`Find` and
  `ForLanguage` return `null` when nothing matches; the resource loader returns `null` for "not loaded").
- **XML documentation** covers every public member: the build has `GenerateDocumentationFile` and treats warnings,
  including missing comments, as errors.
- **Options objects** (`HtmlConverterOptions`, `PdfEncryption`, `PdfImportOptions`, `ResourceLimits`,
  `ShapingOptions`) are `init`-only, and the diagnostics are records passed to a callback, so new properties can be
  added later without breaking anyone.
- **Async:** the HTML and Razor entry points have `Async` variants with a `CancellationToken`. The synchronous
  `HtmlToPdf.Convert` runs the conversion on its own thread, so it does not deadlock in a synchronization context.
- **Units** are PDF points (1/72 inch) everywhere, and drawing uses PDF's bottom-left origin, including
  `ParagraphLayout.Draw`, whose `top` is a PDF y coordinate. Only `FlowDocument.DrawBlocks` measured `top` from the
  top of the page (corrected in part 3, see section 4).

## Proposed changes

Changes marked **B** break callers; all of them should land before 1.0. Changes marked **A** only add API and could
also come later. **Decision** marks the points that needed the maintainer's choice. All five recommendations were
accepted on 2026-11-16.

### 1. One length type in the layout API (B, decision)

*Done in part 4.* Notes from doing it: a table column width with both points and a percentage counts as a percentage
column (as before, when a width was one or the other); `PageSetup.Margins` percentages are of the page's width (left,
right) or height (top, bottom); the layout reads lengths only after resolving them (debug builds throw otherwise),
which moved two estimates the HTML builder used to store into the layout: an inline box's percentage margins and the
top margin of a first child collapsing through its parent.

The layout API has six structs for "points, a percentage, or both":

| Type | Shape | Used by |
| --- | --- | --- |
| `BoxLength` | `Points` + `Percent` | `BoxPercentages`, `RelativeThickness`, `InlineImageSizing` |
| `Inset` | `Value` + `IsPercent` | `BoxInsets`, `BackgroundPosition`, `BackgroundSize`, `GradientStop`, `RadialGradient` |
| `TableWidth` | `Value` + `IsPercent` | `TableBlock.ColumnWidths`, `TableCell.Width` |
| `RadiusLength` | `Points` + `Percent` | `CornerRadius` |
| `FlexBasis` | auto, content, points or percent | `FlexItem.Basis` |
| `LineHeight` | normal, multiple or points | `TextStyle.LineHeight` |

On top of that, `BoxStyle` holds widths, margins and padding twice: as points (`Width`, `MinWidth`, `MaxWidth`,
`Margin`, `Padding`) and again as percentages in a separate `BoxStyle.Percentages` record (`BoxPercentages`, with
`RelativeThickness`). `InlineImageSizing` mixes `BoxLength?` widths with a `double?` height.

**Proposal:** one `Length` struct (points plus a percentage, as `BoxLength` is today, which also covers
`calc(10pt + 5%)`), with `Length.Points(...)`, `Length.Percent(...)` and an implicit conversion from `double`
(points), so `Width = 120` keeps working.
- `BoxStyle.Width`, `MinWidth` and `MaxWidth` become `Length?`.
- `Margin` and `Padding` become a `Thickness` of `Length`s.
- `BoxPercentages` and `RelativeThickness` go away.
- `Inset`, `TableWidth`, `RadiusLength` and `BoxLength` become `Length`.
- `FlexBasis` and `LineHeight` stay, because they have keywords (`auto`, `content`, `normal`) that a length
  cannot express.

**Recommendation:** do it. It is the largest change in this review, but it is exactly the kind #60 names ("sizing
properties split"), and it can't be fixed after 1.0 without breaking every layout user.

### 2. Namespaces (B, decision)

- **Layout:** `FlowDocument`, `PageSetup`, `PageInfo` and the diagnostics live in `RaiCore.Pdf.Layout.Blocks`, while
  `Paragraph` and `TextStyle` live in `RaiCore.Pdf.Layout`. Every layout user needs both namespaces, and usually
  `RaiCore.Pdf.Layout.Text` too.
  **Recommendation:** merge `RaiCore.Pdf.Layout.Blocks` into `RaiCore.Pdf.Layout`. Keep `RaiCore.Pdf.Layout.Text`
  for the low-level text APIs (shaping, line breaking, hyphenation).
- **Core:** `FontFormatException` is the only public type in `RaiCore.Pdf.Fonts.OpenType`, and `ImageFormatException`
  the only one in `RaiCore.Pdf.Images`. Move them to `RaiCore.Pdf.Fonts` and `RaiCore.Pdf`.
- **Name clashes with common .NET types:**
  - `RaiCore.Pdf.ImageFormat` clashes with `System.Drawing.Imaging.ImageFormat`; rename it to `PdfImageFormat`.
  - `RaiCore.Pdf.Html.Resources` clashes with the `Resources` class that `.resx` files generate in many projects;
    see section 6.
  - The layout names `Thickness`, `TextAlignment` and `VerticalAlignment` also exist in WPF, MAUI and Avalonia, but
    only collide when both namespaces are imported. Leave them.

### 3. Exceptions (B, decision)

There are five exception types:
- `PdfFormatException`
- `PdfPasswordException`
- `ResourceLimitExceededException`
- `FontFormatException`
- `ImageFormatException`

They share no base class, so callers can't catch "anything RaiCore.Pdf rejected" without listing them all.

**Recommendation:** add a base class `PdfException : Exception` and derive all five from it. Besides the namespace
moves in section 2, the names stay.

### 4. Direction, alignment and other consistency fixes (B)

- **Direction:** `FlexBlock.RightToLeft` and `TableBlock.RightToLeft` are `bool`, `Paragraph.Direction` is a
  `ParagraphDirection` (`LeftToRight`, `RightToLeft`, `Auto`), and `InlineDecoration.Direction` is a `TextDirection`.
  Use `Direction` properties everywhere: `ParagraphDirection` on paragraphs and `TextDirection` on blocks.
  *Done in part 3:* `FlexBlock`'s main axis property, which was called `Direction`, is now `FlexDirection` (as in CSS),
  and `TextDirection` moved from `RaiCore.Pdf.Layout.Text` to `RaiCore.Pdf.Layout`.
- **Block alignment:** `ImageBlock.Alignment` and `TableBlock.Alignment` use `TextAlignment`, whose `Justify` means
  nothing for a block. Give them a `BlockAlignment` enum. *Done in part 3* with `Left`, `Center` and
  `Right` only: the layout never resolved `Start` and `End` for blocks.
- **`TableBlock.ColumnWidths`** is a mutable `IList` while everything else on blocks is `init`. Make it an
  `IReadOnlyList<Length?>` with `init`.
- **`TableRow.Background`** is a `PdfColor?`, while `TableCell` has a full `BoxStyle`. Give rows a `BoxStyle Style`
  too (backgrounds, borders in the separated model), or document why rows only take a colour.
- **`Paragraph`** is configured with settable properties (`Alignment`, `Direction`, `FirstLineIndent`,
  `BreakLongWords`, `Ellipsis`), unlike blocks and styles, which use `init`. Make them `init`. The `Add...` methods
  stay: they build the content.
- **`LinearGradient`:**
  - The corner constructor `LinearGradient(bool right, bool bottom, ...)` and the `ValueTuple<int, int>? Corner`
    property are hard to read. Replace them with a `GradientCorner` enum
    (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`) and a `LinearGradient.ToCorner(...)` factory.
- **`FlowDocument.DrawBlocks(canvas, pageHeight, ...)`** asks for the page height because it measures `top` from the
  top of the page, unlike `ParagraphLayout.Draw` and the canvas. *Done in part 3:* `DrawBlocks(canvas, blocks, x,
  top, width)` takes `top` as a PDF y coordinate.
- **`PageInfo`** repeats `Name`, `Side` and `IsBlank` from `PageContext` and also exposes `Context`. Keep `Number`,
  `Count`, `Setup`, `Markers` and `Context`, and drop the copies.
- **`PdfAttachment`:** the constructor parameter is `mimeType`, the property `MediaType`. Rename the parameter to
  `mediaType`.
- **Destinations:**
  - `PdfDestination(page, top, left)` puts `top` before `left`. That is PDF's `/XYZ` order, but the API uses x before
    y everywhere else.
  - `PdfOutlineItem` repeats `Page`, `Top` and `Left` instead of holding a `PdfDestination`.
  - Change `PdfDestination` to `(page, left, top)` and give `PdfOutlineItem` a `Destination` property, keeping a
    `(title, page, top)` convenience constructor.
- **Link targets:** the layout API's `LinkTarget.Destination` is the core's `PdfLink.DestinationName`. Rename it to
  `DestinationName`.

### 5. Internal or experimental (B, decision)

**Should be internal** (they describe how a paragraph stores its content or how a line was laid out, and users never
create them):
- `ParagraphSpan`, `InlineDecoration`, `InlineLink`, `InlineAnchor`, `InlineTag`;
- `Paragraph.Spans`, `Decorations`, `Links`, `Anchors`, `Tags`;
- `ResourceLimits.Enforce`: it is the converter's machinery, and RaiCore.Pdf.Html can reach it through the existing
  `InternalsVisibleTo`.

**The line-layout output stays public but read-only:** `LineLayout`, `LineFragment` and the `Inline*Fragment` types
are useful for measuring and custom drawing. The `Inline*Fragment` types are records with public constructors today.
Make them sealed classes without public constructors.

**Should be `[Experimental]`** (public, but allowed to change after 1.0; callers must opt in by suppressing the
diagnostic ID, e.g. `RCPDF001`):
- **Low-level PDF objects:** the `RaiCore.Pdf.Objects` namespace, `PdfReader.Trailer`, `Catalog` and `Resolve`, and
  `PdfReaderPage.Dictionary`, `Resources` and `GetContent`. Locking the object model now would freeze its mutability:
  `PdfStreamObject.Data` and `PdfArray.Items` are mutable, and `PdfDictionary` is the only unsealed object type.
- **Text shaping:** `TextShaper`, `ShapedText`, `ShapedRun`, `ShapingOptions.Features`, `ShapedTextCanvasExtensions`
  and `LineBreaker`. The HarfBuzz-free shaper (an `ITextShaper` abstraction) is likely to change exactly this surface.
- **`PdfCanvas.DrawGlyphs` and `PositionedGlyph`**, for the same reason.

**Recommendation:** all three lists as above.

*Done in part 5,* with one correction: `ResourceLimits.Enforce` stays public. Its documentation shows it limiting
`PdfImage.Load`, `PdfReader` and `FlowDocument.Render` outside HTML conversion, which nothing else can do. The
diagnostic IDs are `RCPDF001` (PDF objects) and `RCPDF002` (text shaping and glyphs), explained in
`docs/experimental.md`. RaiCore.Pdf.Layout now grants `InternalsVisibleTo` to RaiCore.Pdf.Html, which reads paragraph
content (see the note for #62 below).

### 6. Resource loading and global state (B, decision)

- **`ResourceLoader`** is a delegate `byte[]? ResourceLoader(Uri uri)`. Changing a delegate's signature later breaks
  every loader, so it can't grow (no cancellation token, no hint whether a stylesheet, image or font is requested, no
  async). **Recommendation:**
  - Change it to `byte[]? ResourceLoader(ResourceRequest request)`. `ResourceRequest` is a record with `Uri`, `Kind`
    and `CancellationToken`, and can gain properties later.
  - Move the static `Resources.Default` and `Resources.DecodeDataUri` onto a `ResourceLoaders` class. That also
    removes the clash with `.resx` classes from section 2.
  - An async loader can then be added later without breaking anyone.
- **`Hyphenator.Register`** changes a process-wide registry. That is the only global, mutable state in the public API,
  and it lets two conversions in the same process influence each other. **Recommendation:**
  - Replace it with an `IReadOnlyDictionary<string, Hyphenator>? Hyphenators` setting on `HtmlConverterOptions`, with
    the same on `TextStyle` or `FlowDocument` for the layout API.
  - The bundled patterns stay the default.

### 7. Missing counterparts (A)

These add API only:
- **`PdfDocument.SaveAsync(Stream, CancellationToken)`.** ASP.NET Core forbids synchronous writes to the response
  body by default, so `Save(Response.Body)` throws there today.
- **`HtmlToPdf.ConvertToBytesAsync`.**
- **`RazorToPdf.ConvertToStreamAsync`**, and on `RazorPdfRenderer` a `CreateFlowDocumentAsync(Type, ...)` overload
  (the `Type` overload exists for `ConvertAsync` and `RenderHtmlAsync` only).
- **`PdfPageSize.FromInches`**, next to `FromMillimeters`.

### 8. Left as they are

Reviewed with no change proposed:
- **Core:**
  - `PdfDocument` (mutable by design: it is the document being built), `PdfDocumentInfo`, `PdfPage`, `PdfCanvas`
    (fluent, bottom-left origin; its `AddLink` applies the current transform, unlike `PdfPage.AddLink`, so the two
    are not duplicates);
  - `PdfColor`, `PdfPageSize`, `PdfRectangle`;
  - `PdfImage`, `PdfEncryption`, `PdfPermissions`, `PdfEncryptionAlgorithm`, `PdfImportOptions`, `PdfReader` (reads
    files into memory, so it holds no handle and needs no `IDisposable`);
  - `PdfLink`, `PdfStructureElement`, `PdfStructureTypes`, `PdfTableScope`, `PdfListNumbering`, `PdfArtifactKind`;
  - `PdfAConformance`, `FacturX`, `FacturXProfile`, `PdfAttachmentRelationship`, `PdfPageMode`, `LineCap`,
    `LineJoin`, `ResourceLimits` (apart from `Enforce`);
  - `FontCollection`, `PdfFont`, `StandardFont`, `UnicodeRange`.
- **RaiCore.Pdf.Fonts:** `BundledFonts`, `StandardFontSubstitutes`.
- **Layout:**
  - Blocks: `Block`, `ContainerBlock`, `ParagraphBlock`, `ImageBlock`, `SpacerBlock`, `TableBlock`, `TableCell`,
    `FlexBlock`, `FlexItem`;
  - Pages: `FlowDocument`, `PageSetup`, `PageContext`, `PageMap`, `Bookmark`, `StructureTag`, `LayoutDiagnostic`;
  - Borders and boxes: `Borders`, `BorderSide`, `Thickness`, `CornerRadii`, `CornerRadius`;
  - Backgrounds: `BackgroundLayer`, `BackgroundImage`, `ImageBackground`, `Gradient`, `RadialGradient`,
    `GradientStop`;
  - Paragraphs and text: `ParagraphLayout`, `TextStyle`, `TextDecoration`, `InlineAlignment`, `InlineBox`,
    `InlineImage`, `HyphenationLimits`, `Hyphenator` (apart from `Register`), `LineBreakOpportunity`;
  - Every enum not named above.

  The changes above touch some of these, for example the length types inside them.
- **Html:** `HtmlToPdf`, `HtmlConverterOptions`, `HtmlDiagnostic`, `HtmlDiagnosticKind`, `HtmlDiagnosticSeverity`.
- **Razor:** `RazorPdfRenderer`, `RazorToPdf`, `RazorPdfServiceCollectionExtensions`.

## Note for #62 (independent package versions)

RaiCore.Pdf grants `InternalsVisibleTo` to RaiCore.Pdf.Layout and RaiCore.Pdf.Html, and RaiCore.Pdf.Html grants it to
RaiCore.Pdf.Razor. A package that uses another package's internals only works with the exact version it was built
against. So either those dependencies are pinned to exact versions (`[1.0.0]`), or the internals they use become
public (possibly `[Experimental]`) before the packages are versioned independently.

## Plan

One pull request per group, in this order, each with its CHANGELOG entry and tests:

1. Core:
   - the exception base class and namespace moves (section 3);
   - `PdfImageFormat`;
   - `PdfAttachment`, `PdfDestination` and `PdfOutlineItem` (section 4);
   - `SaveAsync` and `FromInches` (section 7).
2. Layout namespace merge (section 2).
3. Layout consistency fixes (section 4).
4. One length type (section 1).
5. Internal and `[Experimental]` (section 5).
6. Html and Razor:
   - `ResourceRequest` and `ResourceLoaders`, hyphenators as an option (section 6);
   - the async counterparts (section 7).

After that, #61 records the result in `PublicAPI.Shipped.txt`, so later changes show up in review.
