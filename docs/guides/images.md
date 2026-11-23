# Images

JPEG, PNG, GIF, BMP, WebP, TIFF and SVG images work in `<img>`, CSS backgrounds and generated content, and inline `<svg>`
elements are drawn too. SVG is drawn as PDF vector graphics, so logos and charts stay sharp at any zoom. Each image is
stored once per document, however often it appears.

[!code-csharp[](../snippets/Images.cs#html-images)]

- JPEGs are embedded without re-encoding (EXIF orientation is applied); PNGs of every color type and bit depth, BMP
  and WebP (lossy and lossless, with alpha) are decoded in managed code.
- TIFF: bilevel, grayscale, palette, RGB and CMYK images with 1 to 16 bits per sample, in strips or tiles,
  uncompressed or with PackBits, LZW or Deflate (with the predictor), with alpha, in either byte order and as BigTIFF.
  JPEG-compressed TIFFs are embedded without re-encoding (their strips joined into one JPEG), and CCITT Group 3 and 4
  fax images as they are. The Orientation tag is applied, and a TIFF's resolution sets its natural size, so a scan at
  300 dpi comes out at the size of the paper and a fax keeps its proportions. An `<img>` shows the first page of a
  multi-page TIFF. Not supported (the image is skipped with a diagnostic saying why): floating-point samples, YCbCr or
  Lab without JPEG, old-style JPEG, fax images stored in several strips, and JPEG data in several tiles.
- `max-width: 100%; height: auto` scales images keeping their aspect ratio, and `border-radius` rounds them.
  `object-fit` is not supported; to crop an image to a box, make it the box's background with
  `background-size: cover` (or `contain`).
- Inline `<svg>` uses `currentColor` from the CSS `color`. The SVG subset covers what logos, icons and charts use:
  paths, shapes, groups, transforms, `use` and `symbol`, fills, strokes, gradients, clip paths, basic text, opacity and
  embedded `data:` images. Filters, masks, markers, patterns and scripts are skipped.
- Embedded color profiles are ignored: images are treated as sRGB.

## Loading resources

Images, stylesheets and fonts are loaded by the `ResourceLoader` in `HtmlConverterOptions`. The default one reads
`data:` URIs and `file:` URIs only, so network access is off unless you pass a loader that does it. Relative URLs
resolve against `HtmlConverterOptions.BaseUri`.

[!code-csharp[](../snippets/Images.cs#loader)]

The loader gets the resolved `Uri`, what the resource is for (`Kind`: style sheet, image or font) and a
`CancellationToken` that is canceled when the conversion is canceled or runs out of time. Resources larger than
`ResourceLimits.MaxResourceBytes` are skipped. `data:` URIs never reach the loader: they are part of the document.

For untrusted HTML, allow only what the document should reach: the default loader reads any local file a `file:` URL
names. `ResourceLoaders.None` loads nothing, so only `data:` URIs work (see [Untrusted input](untrusted-input.md)):

[!code-csharp[](../snippets/Images.cs#untrusted)]

## Drawing images in code

[!code-csharp[](../snippets/Images.cs#draw-images)]

`PdfImage.Load` reads any of the formats from a file, stream or bytes, and also WMF and EMF pictures (with their
EMF+ records), drawn as vector graphics with their text in the standard PDF fonts (the Office converters draw them
with the document's fonts; see the [Word documents guide](documents.md#wmf-and-emf-pictures)); in the layout API,
`ImageBlock` places an image as a block and `Paragraph.AddImage` in a line of text. For a multi-page TIFF, `PdfImage.GetPageCount` gives the number of
pages and `PdfImage.Load(data, pageIndex)` loads one of them:

[!code-csharp[](../snippets/Images.cs#tiff-pages)]
