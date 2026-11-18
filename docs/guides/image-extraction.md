# Image extraction

`ImageExtractor` in `RaiCore.Pdf.Extraction` lists the images on each page of an existing PDF, with where they are
and how large they are shown, and exports their data as image files.

```sh
dotnet add package RaiCore.Pdf.Extraction
```

[!code-csharp[](../snippets/ImageExtraction.cs#list)]

`ExtractPage(index)` reads one page, `ExtractPages()` all of them as you enumerate, and `ExtractPages(indexes)` the
pages you name. Each `PageImages` has the page's `Images` in drawing order (back to front) and `Diagnostics` for
problems such as a missing image object, which never throw.

## What is listed

- **Image XObjects, inline images** (`BI` ... `EI`) and images inside form XObjects, once per placement: an image drawn
  three times is listed three times, with the same `ObjectNumber`.
- **Hidden images are left out:** images clipped away and images outside the page. Images that only appear in
  patterns, Type 3 glyphs or annotations are not listed.
- **Placement:** `Bounds` is the rectangle the image covers, a `PdfRectangle` in the page's user space (PDF points
  from the bottom-left corner of the page before its `/Rotate`, as for text search). `Width` and `Height` are the
  displayed size, the lengths of the image's edges, and `Rotation` how far it is turned, in degrees counterclockwise.
- **Properties:** `PixelWidth` and `PixelHeight`, `ColorSpace` (`DeviceRGB`, `ICCBased`, `Indexed`, ...),
  `BitsPerComponent`, `HasMask` (a soft mask or mask), `IsStencilMask` and `IsInline`.

## Exporting

[!code-csharp[](../snippets/ImageExtraction.cs#export)]

`GetData()` reads the image's data on each call and gives a file whose kind is `Format` (`FileExtension` has the
usual extension):

| Image in the PDF | `Format` | What you get |
|---|---|---|
| JPEG (`/DCTDecode`) | `Jpeg` | The JPEG data as it is, not re-encoded. `/Decode` is not applied. |
| Flate, LZW, run-length or uncompressed, in gray, RGB, CMYK, indexed or ICC-based colors | `Png` | A PNG in gray or RGB, with the soft mask, stencil mask or color key mask as alpha. CMYK is converted without a profile, and ICC profiles are not applied. |
| Stencil mask (`/ImageMask`) | `Png` | Black where the mask paints, transparent elsewhere. |
| CCITT fax (`/CCITTFaxDecode`) | `Tiff` | A TIFF holding the fax data as it is. |
| JPEG 2000 (`/JPXDecode`) | `Jpeg2000` | The JPEG 2000 data as it is. |
| JBIG2 (`/JBIG2Decode`) | `Jbig2` | The embedded JBIG2 segments, without `/JBIG2Globals`. |
| `Separation`, `DeviceN` or `Lab` colors | `Raw` | The uncompressed samples. |
| An unknown filter | `Raw` | The data as stored. |

Data that ends early is padded, as viewers draw it. Data too damaged to read throws `PdfFormatException`, and an
image larger than `ResourceLimits.MaxDecompressedBytes` throws `ResourceLimitExceededException`. For untrusted files,
run extraction inside `ResourceLimits.Enforce`, as in the [text extraction guide](text-extraction.md#untrusted-files).
