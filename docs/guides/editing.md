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

## Forms

`GetForm()` reads the document's interactive form as it is now:

[!code-csharp[](../snippets/Editing.cs#form)]

- **`HasAcroForm`** tells whether the document has a form at all, and **`Xfa`** whether it has an XFA form: `Static`
  (the PDF pages and their fields are what viewers without XFA show) or `Dynamic` (`/NeedsRendering`: the pages are laid
  out from the XFA data, and the PDF pages are usually only a placeholder).
- **`Fields`** lists the fields with a value, in the order of the field tree: `FullName` (the names of the field and its
  parents joined by periods), `Type` (text, check box, radio buttons, push button, list box, combo box, signature),
  `Value` and `Values` (several for multi-select lists), `Options` of list and combo boxes as shown, `IsReadOnly`,
  `IsRequired`, and `Widgets`: where the field is shown, with the page index, rectangle (user space), whether it is
  hidden, and the on/off state of check boxes and radio buttons.
- Inherited attributes (type, flags, value, options) are followed down the field tree. Fields whose widgets are all on
  deleted pages are not listed. `form["address.city"]` finds a field by its full name.

### Flattening a form

`FlattenForm()` burns the form into the pages: each field's widgets are drawn into the page content exactly as they
look, and the widgets, the fields and the form are removed, so the values stay visible but can no longer be changed.

[!code-csharp[](../snippets/Editing.cs#flatten)]

- A widget is drawn with its normal appearance, in its current state for check boxes and radio buttons, at its
  rectangle (the appearance's box and matrix are followed). The page's own content is wrapped so that a graphics state
  it leaves changed cannot move the drawing.
- Where a widget has no appearance, or the form asks viewers to make new ones (`/NeedAppearances`), one is made: text
  fields (single-line, multiline, comb, password, alignment, automatic size) and choice fields in the font and color of
  their default appearance, measured with the standard font of the same family, with their background and border;
  check boxes and radio buttons with their check character. Text is written in WinAnsi, so other characters show as `?`.
- Hidden widgets are removed without being drawn. Other annotations (links, notes) stay: see below.
- A static XFA form goes with the form; a dynamic XFA form throws `NotSupportedException`, since its pages are laid out
  by the viewer.

## Flattening annotations

`FlattenAnnotations()` burns comments and markup into the pages: highlights, underlines, ink, shapes, free text, stamps
and notes are drawn into the page content as they look when printed, then removed, so they can no longer be changed or
deleted.

[!code-csharp[](../snippets/Editing.cs#flatten-annotations)]

- An annotation is drawn with its normal appearance (in its current state) at its rectangle, like form widgets above.
- Only what prints is drawn: hidden annotations, ones marked not to be shown on screen and ones not printed are
  removed without being drawn, as is one without an appearance (viewers make some themselves, such as the icon of a
  note without one; flattening does not make them).
- A note's pop-up window goes with its note.
- Links are kept by default so that they still work (`KeepLinks = false` removes them too), and `Types` limits
  flattening to some annotation types by their PDF names (`Highlight`, `Ink`, `FreeText`, `Stamp`, `Square`, `Text`,
  ...). Form fields are never touched: flatten them with `FlattenForm()`.

## Making files smaller

Each save already leaves out every object nothing uses. `Optimize()` goes further, without changing how the pages look:

[!code-csharp[](../snippets/Editing.cs#optimize)]

- **Unused resources:** fonts, images, forms, graphics states, patterns, shadings, color spaces and marked-content
  properties that a page's content (or a form's, pattern's or Type 3 font's) never names are removed, so what only
  they used leaves the file. Resources shared with something whose content is not read are kept whole.
- **Duplicates:** identical streams (images, embedded fonts, forms, content) and identical fonts, font descriptors,
  encodings and graphics states become one object. Files merged from several PDFs often carry the same font or logo
  once per source file.
- **Compression:** streams without compression are compressed where that makes them smaller (XMP metadata stays
  readable).
- **Object streams:** later saves write the small objects in compressed object streams with a cross-reference stream
  (PDF 1.5; older files are written as 1.5). `UseObjectStreams` turns this on or off on its own.

`PdfOptimizationOptions` turns each step off, and the result says how many resources were removed, objects merged and
streams compressed. The changes stay in the document for later saves. Images are not recompressed, and fonts are not
subset again.

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
