# Experimental APIs

A few public APIs are marked `[Experimental]`. They work and are tested, but they may change in a minor version after
1.0, unlike the rest of the API. Using one makes the compiler report its diagnostic ID as an error, so that the choice
to depend on it is explicit. To opt in, suppress the ID in the project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);RCPDF002</NoWarn>
</PropertyGroup>
```

or around the code that uses it:

```csharp
#pragma warning disable RCPDF002
var shaped = TextShaper.Shape("Total: سلام", [latinFont, arabicFont]);
#pragma warning restore RCPDF002
```

## RCPDF001

The low-level PDF object model:
- the types in `RaiCore.Pdf.Objects` (`PdfObject`, `PdfDictionary`, `PdfArray`, `PdfName`, `PdfStreamObject` and the
  others);
- what `PdfReader` exposes of a file through them: `PdfReader.Trailer`, `Catalog` and `Resolve`, and
  `PdfReaderPage.Dictionary`, `Resources` and `GetContent`.

The object model may get immutable views, other collection types and stream decoding before it is locked. Importing
and drawing pages (`PdfDocument.ImportPages`, `PdfCanvas.DrawPage`) and the rest of `PdfReader` are not experimental.

## RCPDF002

Text shaping and positioned glyphs:
- `TextShaper`, `ShapedText`, `ShapedRun`, `ShapedTextCanvasExtensions` and `ShapingOptions.Features`;
- `LineBreaker` and `LineBreakOpportunity`;
- `PdfCanvas.DrawGlyphs` and `PositionedGlyph`.

A shaper abstraction (so that layout can run without the native HarfBuzz library) is likely to change these types.
Paragraphs, text styles and `PdfCanvas.DrawText` are not experimental.
