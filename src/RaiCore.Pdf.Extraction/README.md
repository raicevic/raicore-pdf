# RaiCore.Pdf.Extraction

Reads what is on the pages of existing PDF files for [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf): the
text with its position, and the images with their placement.

> **Status:** in development. This version contains the engine that reads page content: fonts decoded to Unicode
> (ToUnicode maps, the standard encodings, glyph names, composite fonts with their CMaps, Type 3 fonts), the text and
> graphics state, form XObjects and inline images, with page rotation and crop boxes taken into account. The public
> API (text extraction page by page, text search with positions, and image extraction) follows; see the
> [roadmap](https://github.com/raicevic/raicore-pdf/milestone/8).

Like the rest of RaiCore.Pdf, it is made for untrusted input: malformed content is skipped and reported, never
thrown, and the work per page is bounded by the [resource limits](https://raicevic.github.io/raicore-pdf/).

The glyph name table is the Adobe Glyph List, under the BSD-3-Clause license (included in the package under
`licenses/`); the code is Apache-2.0.

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
