# RaiCore.Pdf.Editing

Edits existing PDF files with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf): delete, insert, move and
rotate pages, and save as often as needed. Managed code, no native dependencies.

```csharp
using RaiCore.Pdf;
using RaiCore.Pdf.Editing;

var editor = PdfEditor.Open("contract.pdf");
editor.Pages.Delete(0);                               // drop the cover page
editor.Pages.Add(PdfReader.Open("appendix.pdf"));      // append another file
editor.Pages.Move(editor.Pages.Count - 1, 0);          // its last page goes first
editor.Pages[0].Rotation = 90;
editor.Save("contract-edited.pdf");
```

- **Pages:** `Delete`, `Insert` and `Add` (pages of other files, any selection and order), `Move`, `Rotation`.
- **Save again and again:** `Save`, `SaveAsync` and `ToArray` can be called any number of times, and editing goes on
  after each save.
- **Nothing left behind:** each save writes only what is still used, so deleted pages' fonts and images leave the
  file, and so do bookmarks, named destinations, links and form fields that pointed at them.
- **Everything else is kept:** outline, links, forms, tagged structure, metadata, attachments.
- **Any producer**, including files written page by page and encrypted files (opened with their password, saved
  without encryption).

More: the [editing guide](https://raicevic.github.io/raicore-pdf/guides/editing.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
