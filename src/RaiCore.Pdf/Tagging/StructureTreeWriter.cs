using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// Writes the structure tree of a tagged document (ISO 32000-1, section 14.7): the structure elements, the parent tree
/// that maps marked content and annotations back to their elements, and the catalog entries. Pages, transparency group
/// forms and link annotations are registered as they are written; <see cref="Write"/> then links everything up.
/// </summary>
internal sealed class StructureTreeWriter
{
    private readonly PdfFileWriter _writer;
    private readonly PdfStructureElement _root;
    private readonly Dictionary<PdfStructureElement, (PdfDictionary Dictionary, PdfReference Reference)> _elements =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The parent tree's entries: a stream's MCIDs → elements, or an annotation → its element.</summary>
    private readonly List<(int Key, PdfObject Value)> _parentTree = [];

    /// <summary>The form XObject of each transparency group whose content is marked (page streams are not here).</summary>
    private readonly Dictionary<MarkedContentStream, PdfReference> _forms = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<PdfLink, PdfReference> _annotations = new(ReferenceEqualityComparer.Instance);

    private int _nextKey;

    /// <summary>Allocates an object for every element that has content (empty elements are left out).</summary>
    public StructureTreeWriter(PdfFileWriter writer, PdfStructureElement root)
    {
        _writer = writer;
        _root = root;
        var hasContent = new Dictionary<PdfStructureElement, bool>(ReferenceEqualityComparer.Instance);
        bool HasContent(PdfStructureElement element)
        {
            if (!hasContent.TryGetValue(element, out var result))
            {
                result = element.Kids.Any(kid => kid is not ElementKid child || HasContent(child.Element));
                hasContent[element] = result;
            }

            return result;
        }

        var pending = new Stack<PdfStructureElement>([root]);
        while (pending.TryPop(out var element))
        {
            if (element == root || HasContent(element))
            {
                var dictionary = new PdfDictionary { ["Type"] = new PdfName("StructElem"), ["S"] = new PdfName(element.Type) };
                _elements[element] = (dictionary, writer.Add(dictionary));
                foreach (var child in element.Children)
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Gives a page its parent tree entry for the marked content of its content stream, and tab order.</summary>
    public void RegisterPage(PdfPage page, PdfDictionary dictionary)
    {
        // PDF/UA: annotations are visited in the order of the structure tree.
        dictionary["Tabs"] = new PdfName("S");
        if (ParentTreeArray(page.Canvas.PageMarks) is { } key)
        {
            dictionary["StructParents"] = new PdfInteger(key);
        }
    }

    /// <summary>Gives a transparency group's form its parent tree entry, if its content is marked.</summary>
    public void RegisterGroup(GroupForm group, PdfDictionary form, PdfReference reference)
    {
        if (group.Marks is { } marks && ParentTreeArray(marks) is { } key)
        {
            form["StructParents"] = new PdfInteger(key);
            _forms[marks] = reference;
        }
    }

    /// <summary>Gives a link annotation its parent tree entry and a description, as PDF/UA requires.</summary>
    public void RegisterAnnotation(PdfLink link, PdfDictionary annotation, PdfReference reference)
    {
        if (link.StructureElement is not { } element || !_elements.TryGetValue(element, out var entry))
        {
            return;
        }

        var key = _nextKey++;
        annotation["StructParent"] = new PdfInteger(key);
        _parentTree.Add((key, entry.Reference));
        _annotations[link] = reference;
        if (annotation["Contents"] is null)
        {
            var description = link.Uri is { } uri ? PdfLinkWriter.EncodeUri(uri) : "Link to " + (link.DestinationName ?? "another page");
            annotation["Contents"] = PdfString.FromText(description);
        }
    }

    /// <summary>Writes the elements, the parent tree and the structure tree root, and marks the catalog as tagged.</summary>
    public void Write(PdfDictionary catalog, IReadOnlyDictionary<PdfPage, PdfReference> pages)
    {
        var treeRoot = new PdfDictionary { ["Type"] = new PdfName("StructTreeRoot") };
        var treeRootReference = _writer.Add(treeRoot);
        treeRoot["K"] = _elements[_root].Reference;

        foreach (var (element, (dictionary, _)) in _elements)
        {
            dictionary["P"] = element.Parent is { } parent ? _elements[parent].Reference : treeRootReference;
            var kids = new PdfArray();
            foreach (var kid in element.Kids)
            {
                if (Kid(kid, pages) is { } written)
                {
                    kids.Items.Add(written);
                }
            }

            dictionary["K"] = kids;
            AddText(dictionary, "T", element.Title);
            AddText(dictionary, "Lang", element.Language?.Trim());
            AddText(dictionary, "Alt", element.AlternateText);
            AddText(dictionary, "ActualText", element.ActualText);
            AddText(dictionary, "E", element.Expansion);
            if (Attributes(element) is { } attributes)
            {
                dictionary["A"] = attributes;
            }
        }

        var nums = new PdfArray();
        foreach (var (key, value) in _parentTree.OrderBy(entry => entry.Key))
        {
            nums.Items.Add(new PdfInteger(key));
            nums.Items.Add(value);
        }

        treeRoot["ParentTree"] = _writer.Add(new PdfDictionary { ["Nums"] = nums });
        treeRoot["ParentTreeNextKey"] = new PdfInteger(_nextKey);
        catalog["StructTreeRoot"] = treeRootReference;
        catalog["MarkInfo"] = new PdfDictionary { ["Marked"] = PdfBoolean.True };
    }

    /// <summary>Adds a parent tree entry mapping a stream's MCIDs to their elements; returns its key, or <c>null</c> if unmarked.</summary>
    private int? ParentTreeArray(MarkedContentStream marks)
    {
        if (marks.Owners.Count == 0)
        {
            return null;
        }

        var key = _nextKey++;
        _parentTree.Add((key, new PdfArray([.. marks.Owners.Select(owner => (PdfObject)_elements[owner].Reference)])));
        return key;
    }

    /// <summary>A kid as written in <c>/K</c>: a child element, a marked-content reference or an object reference.</summary>
    private PdfObject? Kid(StructureKid kid, IReadOnlyDictionary<PdfPage, PdfReference> pages) => kid switch
    {
        ElementKid child => _elements.TryGetValue(child.Element, out var entry) ? entry.Reference : null,
        ContentKid content when pages.TryGetValue(content.Page, out var page) => MarkedContentReference(content, page),
        AnnotationKid annotation when _annotations.TryGetValue(annotation.Link, out var reference) && pages.TryGetValue(annotation.Page, out var page) =>
            new PdfDictionary { ["Type"] = new PdfName("OBJR"), ["Pg"] = page, ["Obj"] = reference },
        _ => null,
    };

    private PdfDictionary MarkedContentReference(ContentKid content, PdfReference page)
    {
        var reference = new PdfDictionary { ["Type"] = new PdfName("MCR"), ["Pg"] = page, ["MCID"] = new PdfInteger(content.Mcid) };
        if (_forms.TryGetValue(content.Stream, out var form))
        {
            reference["Stm"] = form;
        }

        return reference;
    }

    /// <summary>The element's attributes (<c>/A</c>): the Table and List attributes that are set.</summary>
    private static PdfObject? Attributes(PdfStructureElement element)
    {
        var table = new PdfDictionary { ["O"] = new PdfName("Table") };
        if (element.Scope is { } scope)
        {
            table["Scope"] = new PdfName(scope.ToString());
        }

        if (element.RowSpan > 1)
        {
            table["RowSpan"] = new PdfInteger(element.RowSpan);
        }

        if (element.ColumnSpan > 1)
        {
            table["ColSpan"] = new PdfInteger(element.ColumnSpan);
        }

        var attributes = new List<PdfObject>();
        if (table.Count > 1)
        {
            attributes.Add(table);
        }

        if (element.ListNumbering is { } numbering)
        {
            attributes.Add(new PdfDictionary { ["O"] = new PdfName("List"), ["ListNumbering"] = new PdfName(numbering == PdfListNumbering.DecimalDigits ? "Decimal" : numbering.ToString()) });
        }

        return attributes.Count switch
        {
            0 => null,
            1 => attributes[0],
            _ => new PdfArray(attributes),
        };
    }

    private static void AddText(PdfDictionary dictionary, string key, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            dictionary[key] = PdfString.FromText(text);
        }
    }
}
