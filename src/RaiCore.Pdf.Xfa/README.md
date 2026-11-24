# RaiCore.Pdf.Xfa

Renders dynamic XFA forms (XML Forms Architecture, from Adobe LiveCycle and AEM Forms Designer) to ordinary PDF pages
with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf), as a viewer with XFA support shows them: the form's
data merged into its template, the subforms laid out and paginated, and the field values drawn as text. The result is a
flattened PDF without XFA that every viewer shows, even when the form's own PDF pages are only a "Please wait..."
placeholder. No Adobe software, no browser, no native dependencies.

```csharp
using RaiCore.Pdf.Xfa;

var pdf = XfaToPdf.Convert("form.pdf");   // a PDF with an XFA form, or an .xdp file
pdf.Save("form-flattened.pdf");

var other = XfaToPdf.Convert("form.pdf", new XfaConverterOptions
{
    Data = File.ReadAllText("data.xml"),   // data merged instead of the form's own
    Diagnostics = d => Console.WriteLine($"{d.Kind}: {d.Message}"),
});
```

- **Data merge:** normal (match once), global, data reference and no bindings; repeated subforms (`occur`) with their
  minimum, maximum and initial counts; exclusion groups.
- **Layout:** positioned and flowed subforms (`tb`, `lr-tb`, `rl-tb`), tables with column widths, automatic columns and
  spans, growable objects, margins, captions, borders and fills, rotation, presence (objects that are not printed are
  left out).
- **Pagination:** page sets with ordered or paginated page areas and their content areas, breaks (`breakBefore`,
  `breakAfter`, `break`), overflow leaders and trailers (repeated table headers), keep rules, each page area's own
  objects.
- **Content:** plain and rich text (an XHTML subset), floating fields, images, lines, rectangles, arcs, check boxes,
  radio buttons, choice lists, comb fields; picture clauses for numbers, dates, times and text, with the form's locales.
- **Untrusted input:** XML read with no DTDs and nothing external; malformed forms throw `XfaFormatException`; packet
  bytes, XML elements and nesting, merged form objects (so repetition is bounded), pages, table cells, image pixels and
  time are bounded by `ResourceLimits`.

Not supported: scripts (FormCalc and JavaScript) are not run, so what they would compute, show or hide is missing (they
are reported to `Diagnostics`); barcodes are drawn as their text; the output has no form fields.

More: the [XFA forms guide](https://raicevic.github.io/raicore-pdf/guides/xfa.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
