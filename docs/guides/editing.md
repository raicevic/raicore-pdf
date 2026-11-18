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

## Splitting by size

`SplitBySize(maxBytes)` cuts the document into consecutive parts whose files are each at most `maxBytes` long, with as
many pages in each as fit, for example for a mail server or an archive that limits file sizes. The document itself is
not changed.

[!code-csharp[](../snippets/Editing.cs#split)]

- **Each part is a complete PDF file**, written as a save of the document with only the part's pages would be: fonts
  and images its pages share are in it once, and bookmarks, named destinations and links to pages in other parts are
  left out. Page labels are kept in the first part only.
- **A page larger than the limit on its own** is a part by itself, with `ExceedsLimit` set.
- **Fonts are not subset again:** a part carries the whole embedded font of the original file, even if its pages use a
  few of the glyphs, so a part of one page is never smaller than the fonts and the images shared by all pages.
- Sizes are measured by writing the parts: splitting writes each page a few times, and the parts are held in memory.

An editor holds the whole file in memory, like `PdfReader`, and is not thread-safe.
