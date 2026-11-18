# Editing pages

`RaiCore.Pdf.Editing` opens an existing PDF file from any producer, lets you delete, insert, move and rotate its pages,
and saves it, as often as you need.

```sh
dotnet add package RaiCore.Pdf.Editing
```

[!code-csharp[](../snippets/Editing.cs#pages)]

`PdfEditor.Open` takes a path, a stream, bytes or a `PdfReader`, and the password of an encrypted file. `Pages` is the
list of pages, in order:

| Method | What it does |
|---|---|
| `Delete(index)`, `Delete(index, count)` | Removes pages; the pages after them move up. |
| `Insert(index, reader)`, `Insert(index, reader, pageIndexes)` | Inserts all pages of another file, or the ones you list, in that order (a page may be listed twice). |
| `Add(reader)`, `Add(reader, pageIndexes)` | Appends pages of another file. |
| `Move(from, to)` | Moves a page so that it ends up at `to`. |
| `Pages[i].Rotation` | Gets or sets how far the page is turned clockwise: 0, 90, 180 or 270. |

`Pages[i]` also gives the page's `MediaBox`, `CropBox`, `Width` and `Height`.

## Saving

`Save(path)`, `Save(stream)`, `SaveAsync(stream)` and `ToArray()` write a complete new file. They can be called any
number of times, and editing goes on after each save. A file may be saved over the one that was opened.

[!code-csharp[](../snippets/Editing.cs#repeat)]

Each save writes only the objects still in use:

- **Deleted pages leave nothing behind:** their content, and the fonts and images no other page uses, are not written,
  so the file shrinks as pages go.
- **What pointed at them goes too:** bookmarks (an entry with entries left below it stays, without its destination),
  named destinations, links on the remaining pages, and form fields whose widgets were on deleted pages.
- **Everything else is kept:** the outline, links, form fields, tagged structure, metadata, attachments and other
  document-level parts.
- **Page labels** are dropped once pages are deleted, moved or inserted before the original ones, since they would no
  longer fit. Appending pages keeps them.

A saved file is a new file, not an incremental update, so digital signatures become invalid. Encrypted files are saved
without encryption; the editor does not enforce a file's permissions (`PdfReader.Permissions`), so check them before
you change a file you received with restrictions.

## Pages from other files

[!code-csharp[](../snippets/Editing.cs#insert)]

Inserted pages keep their content, resources, annotations, boxes and rotation, and fonts and images they share are
copied once. Links between inserted pages keep working; links to pages of the other file that were not inserted are
dropped. The other file's outline, form fields and tagged structure are not copied.

An editor holds the whole file in memory, like `PdfReader`, and is not thread-safe.
