# RaiCore.Pdf.Slides

Converts PowerPoint presentations (`.pptx`) to PDF with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf): one
page per slide at the slide's size, drawn with the master's and layout's shapes, theme colors and fonts, shapes, text,
pictures, tables and groups. No presentation program, no browser, no native dependencies.

```csharp
using RaiCore.Pdf.Slides;

var pdf = PptxToPdf.Convert("deck.pptx");
pdf.Save("deck.pdf");

var some = PptxToPdf.Convert("deck.pptx", new PptxConverterOptions { SlideNumbers = [1, 3], Tagged = true });
some.Save("some.pdf");
```

- **Masters, layouts and themes:** placeholders inherit their position, shape and text styles from the layout and
  master; the master's and layout's own shapes and backgrounds are drawn behind the slide's; scheme colors through the
  color map with their transforms, theme fonts, and the style matrix of shapes inserted with a style.
- **Shapes:** more than 80 preset shapes with their adjust values and free forms; solid, gradient, pattern and
  picture fills; lines with dashes and arrowheads; rotation, flips and nested groups; SmartArt from its stored drawing.
- **Text:** insets, anchoring, wrapping, vertical text and autofit; alignment, indents, spacing, line spacing, bullets
  (also Wingdings bullets) and automatic numbering; fonts, sizes, bold, italic, underline, strikethrough, colors,
  superscript and subscript, capitals, character spacing and links.
- **Pictures and tables:** cropped pictures clipped to their shape; tables with merged cells, fills, borders, rows
  growing to fit their text and the table style's header row and bands.
- **Accessible output:** `Tagged` gives each slide a section with headings, paragraphs, tables and figures with
  alternative text.
- **Untrusted input:** parts are read with no DTDs and nothing external; malformed packages throw
  `PresentationFormatException`; decompressed bytes, XML elements and nesting, slides, table cells and time are bounded
  by `ResourceLimits`.

Not supported: charts, EMF and WMF pictures, animations, 3D effects, shadows, WordArt transforms, notes and comments
(reported to `Diagnostics`), and encrypted or PowerPoint 97-2003 (`.ppt`) files.

More: the [PPTX guide](https://raicevic.github.io/raicore-pdf/guides/slides.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
