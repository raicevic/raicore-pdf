# Images

JPEG, PNG, GIF, BMP, WebP and SVG images work in `<img>`, CSS backgrounds and generated content, and inline `<svg>`
elements are drawn too. SVG is drawn as PDF vector graphics, so logos and charts stay sharp at any zoom. Each image is
stored once per document, however often it appears.

[!code-csharp[](../snippets/Images.cs#html-images)]

- JPEGs are embedded without re-encoding (EXIF orientation is applied); PNGs of every color type and bit depth, BMP
  and WebP (lossy and lossless, with alpha) are decoded in managed code.
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
`ResourceLimits.MaxResourceBytes` are skipped. For untrusted HTML, allow only what the document should reach: the
default loader reads any local file a `file:` URL names.

## Drawing images in code

[!code-csharp[](../snippets/Images.cs#draw-images)]

`PdfImage.Load` reads any of the formats from a file, stream or bytes; in the layout API, `ImageBlock` places an image
as a block and `Paragraph.AddImage` in a line of text.
