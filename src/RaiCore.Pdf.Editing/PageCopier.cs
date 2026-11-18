using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>Copies pages of another file into an editor's objects (see <see cref="PdfEditorPages.Insert(int, PdfReader, IEnumerable{int})"/>).</summary>
internal static class PageCopier
{
    public static List<PdfReference> Copy(PdfEditor editor, PdfReader source, List<int> indexes)
    {
        var table = editor.Table;
        var catalog = (source.Trailer["Root"] as PdfReference)?.ObjectNumber ?? 0;

        // The pages' copies are reserved first, so that links between inserted pages point at their copies; references
        // to the other file's catalog, page tree and pages that are not inserted copy to nothing.
        var mapped = new Dictionary<int, PdfReference>();
        var copier = new GraphCopier(source, table, n => n == catalog || source.IsPageTreeObject(n) ? (true, mapped.GetValueOrDefault(n)) : (false, null));
        var plan = new List<(PdfReaderPage Page, PdfReference Copy, bool First)>();
        foreach (var index in indexes)
        {
            var page = source.Pages[index];
            if (page.Reference is { } reference && !mapped.ContainsKey(reference.ObjectNumber))
            {
                mapped[reference.ObjectNumber] = table.Reserve();
                plan.Add((page, mapped[reference.ObjectNumber], true));
            }
            else
            {
                plan.Add((page, table.Reserve(), false));
            }
        }

        var result = new List<PdfReference>();
        foreach (var (page, copy, first) in plan)
        {
            if (first)
            {
                copier.CopyInto(page.Reference!.ObjectNumber, copy);
            }
            else
            {
                // A page inserted again (or one without an object of its own) is a copy of its dictionary that shares
                // its content and resources but has annotations of its own: an annotation belongs to one page.
                var dictionary = (PdfDictionary)copier.Copy(page.Dictionary)!;
                table.Set(copy, dictionary);
                if (table.Array(dictionary["Annots"]) is { } annotations)
                {
                    dictionary["Annots"] = new PdfArray(annotations.Items.Select(a => table.Dictionary(a) is { } annotation
                        ? table.Add(Clone(annotation, copy))
                        : a));
                }
            }

            var pageDictionary = table.Dictionary(copy)!;
            PdfEditor.InheritAttributes(pageDictionary, page, copier);
            pageDictionary["Parent"] = null;
            pageDictionary["StructParents"] = null; // The other file's structure tree is not copied.
            ResolveLinks(pageDictionary, source, copier, table);
            editor.AddPageObject(copy);
            result.Add(copy);
        }

        editor.NoteVersion(source.Version);
        return result;
    }

    private static PdfDictionary Clone(PdfDictionary annotation, PdfReference page)
    {
        var clone = new PdfDictionary();
        foreach (var (key, value) in annotation.Entries)
        {
            clone.AddUnchecked(key, key.Value == "P" ? page : value);
        }

        return clone;
    }

    /// <summary>
    /// Makes the links of an inserted page point at their destinations directly: named destinations of the other file are
    /// looked up there (its names are not copied). Links to pages that were not inserted are dropped.
    /// </summary>
    private static void ResolveLinks(PdfDictionary page, PdfReader source, GraphCopier copier, ObjectTable table)
    {
        if (table.Array(page["Annots"]) is not { } annotations)
        {
            return;
        }

        var kept = new PdfArray();
        foreach (var item in annotations.Items)
        {
            if (table.Dictionary(item) is { } annotation && table.Resolve(annotation["Subtype"]) is PdfName { Value: "Link" })
            {
                var holder = annotation["Dest"] is not null ? annotation
                    : table.Dictionary(annotation["A"]) is { } action && table.Resolve(action["S"]) is PdfName { Value: "GoTo" } ? action
                    : null;
                if (holder is not null)
                {
                    var key = holder == annotation ? "Dest" : "D";
                    if (table.Resolve(holder[key]) is PdfName or PdfString)
                    {
                        holder[key] = copier.Copy(source.ResolveDestination(table.Resolve(holder[key])));
                    }

                    if (table.Array(holder[key]) is not { Items.Count: > 0 } destination || destination.Items[0] is PdfNull)
                    {
                        continue; // Its page was not inserted, or the name is unknown.
                    }
                }
            }

            kept.Items.Add(item);
        }

        page["Annots"] = kept.Items.Count > 0 ? kept : null;
    }
}
