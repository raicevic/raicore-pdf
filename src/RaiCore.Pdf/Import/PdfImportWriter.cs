using System.Globalization;
using System.Text;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Import;

/// <summary>
/// Writes what a document takes from read files when it is saved: imported pages (their dictionaries, content,
/// resources and annotations) and pages drawn as form XObjects. One <see cref="PdfObjectCopier"/> per source file
/// makes sure each object of that file is written once, whatever uses it.
/// </summary>
internal sealed class PdfImportWriter(PdfFileWriter writer, bool compress)
{
    /// <summary>Page attributes copied as they are, besides the boxes, rotation, resources and content written explicitly.</summary>
    private static readonly string[] CopiedPageKeys =
        ["BleedBox", "TrimBox", "ArtBox", "BoxColorInfo", "UserUnit", "Group", "Metadata", "Thumb", "Dur", "Trans", "Tabs", "VP"];

    private readonly Dictionary<PdfReader, PdfObjectCopier> _copiers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfReaderPage, PdfReference> _forms = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfReaderPage> _annotated = new(ReferenceEqualityComparer.Instance);

    /// <summary>The copier of a source file, created on first use.</summary>
    internal PdfObjectCopier CopierFor(PdfReader reader)
    {
        if (!_copiers.TryGetValue(reader, out var copier))
        {
            copier = new PdfObjectCopier(reader, writer);
            _copiers[reader] = copier;
        }

        return copier;
    }

    /// <summary>
    /// Writes a page of a read file as a form XObject (once per document) and returns a reference to it. The form's
    /// space is the page as displayed: its matrix moves the crop box to the origin and turns it by the page's
    /// <c>/Rotate</c>, so the form fills (0, 0) to (<see cref="PdfReaderPage.Width"/>, <see cref="PdfReaderPage.Height"/>).
    /// </summary>
    public PdfReference WritePageForm(PdfReaderPage page)
    {
        if (_forms.TryGetValue(page, out var existing))
        {
            return existing;
        }

        var copier = CopierFor(page.Reader);
        var streams = page.ContentStreams;
        PdfStreamObject form;
        if (streams.Count == 1)
        {
            // A single content stream keeps its encoded data and filters.
            var source = streams[0];
            form = new PdfStreamObject(source.Data);
            foreach (var key in new[] { "Filter", "DecodeParms" })
            {
                if (copier.Copy(source.Dictionary[key]) is { } value)
                {
                    form.Dictionary[key] = value;
                }
            }
        }
        else
        {
            var content = page.GetContent();
            form = compress ? PdfStreamObject.CreateCompressed(content) : new PdfStreamObject(content);
        }

        var dictionary = form.Dictionary;
        dictionary["Type"] = new PdfName("XObject");
        dictionary["Subtype"] = new PdfName("Form");
        var box = page.CropBox;
        dictionary["BBox"] = PdfArray.OfNumbers(box.X, box.Y, box.Right, box.Top);
        dictionary["Matrix"] = PdfArray.OfNumbers(FormMatrix(page));
        dictionary["Resources"] = copier.Copy(page.ResourcesObject) ?? new PdfDictionary();
        if (copier.Copy(page.Dictionary["Group"]) is { } group)
        {
            dictionary["Group"] = group;
        }

        var reference = writer.Add(form);
        _forms[page] = reference;
        return reference;
    }

    /// <summary>
    /// The form matrix that shows a page as displayed: the crop box moved to the origin and turned clockwise by
    /// <c>/Rotate</c>. It maps the form's space (the page's user space) to the space of the displayed page.
    /// </summary>
    internal static double[] FormMatrix(PdfReaderPage page)
    {
        var c = page.CropBox;
        return page.Rotate switch
        {
            // (x, y) -> (y - bottom, width - (x - left)): the left edge becomes the top.
            90 => [0, -1, 1, 0, -c.Y, c.Width + c.X],
            180 => [-1, 0, 0, -1, c.X + c.Width, c.Y + c.Height],
            270 => [0, 1, -1, 0, c.Y + c.Height, -c.X],
            _ => [1, 0, 0, 1, -c.X, -c.Y],
        };
    }

    /// <summary>
    /// Builds the dictionary of an imported page: the source page's boxes, rotation, resources, content and other
    /// attributes. When something was drawn on the page (<paramref name="drawing"/> is not empty), the drawing goes in
    /// a form XObject of its own, painted after the original content, so that its resource names cannot clash with
    /// the page's; the original content is wrapped in <c>q</c>/<c>Q</c> so that its graphics state does not leak into it.
    /// </summary>
    public PdfDictionary WritePage(PdfPage page, PdfReference parent, byte[] drawing, PdfDictionary drawingResources)
    {
        var source = page.Source!;
        var copier = CopierFor(source.Reader);
        var media = source.MediaBox;
        var dictionary = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Parent"] = parent,
            ["MediaBox"] = PdfArray.OfNumbers(media.X, media.Y, media.Right, media.Top),
        };

        if (source.CropBox != media)
        {
            var crop = source.CropBox;
            dictionary["CropBox"] = PdfArray.OfNumbers(crop.X, crop.Y, crop.Right, crop.Top);
        }

        if (source.Rotate != 0)
        {
            dictionary["Rotate"] = new PdfInteger(source.Rotate);
        }

        foreach (var key in CopiedPageKeys)
        {
            if (copier.Copy(source.Dictionary[key]) is { } value)
            {
                dictionary[key] = value;
            }
        }

        var resources = copier.Copy(source.ResourcesObject);
        var contents = copier.Copy(source.Dictionary["Contents"]);
        if (drawing.Length == 0)
        {
            dictionary["Resources"] = resources ?? new PdfDictionary();
            if (contents is not null)
            {
                dictionary["Contents"] = contents;
            }
        }
        else
        {
            var (merged, xobjects, name) = AddXObject(copier, resources, "RcStamp");
            var content = drawing;
            if (page.Origin != (0, 0))
            {
                var translate = Encoding.ASCII.GetBytes(string.Create(
                    CultureInfo.InvariantCulture, $"1 0 0 1 {PdfNumberFormat.Format(page.Origin.X)} {PdfNumberFormat.Format(page.Origin.Y)} cm\n"));
                content = [.. translate, .. drawing];
            }

            var stamp = compress ? PdfStreamObject.CreateCompressed(content) : new PdfStreamObject(content);
            stamp.Dictionary["Type"] = new PdfName("XObject");
            stamp.Dictionary["Subtype"] = new PdfName("Form");
            stamp.Dictionary["BBox"] = PdfArray.OfNumbers(media.X, media.Y, media.Right, media.Top);
            stamp.Dictionary["Resources"] = drawingResources;
            xobjects[name] = writer.Add(stamp);
            dictionary["Resources"] = merged;

            var parts = new PdfArray(writer.Add(new PdfStreamObject("q\n"u8.ToArray())));
            switch (copier.Target(contents))
            {
                case PdfArray array:
                    foreach (var item in array.Items)
                    {
                        parts.Items.Add(item);
                    }

                    break;
                case PdfStreamObject:
                    parts.Items.Add(contents!);
                    break;
            }

            parts.Items.Add(writer.Add(new PdfStreamObject(Encoding.ASCII.GetBytes($"\nQ\nq /{name} Do Q\n"))));
            dictionary["Contents"] = parts;
        }

        if (page.UsesTransparency && dictionary["Group"] is null)
        {
            dictionary["Group"] = PdfPage.TransparencyGroup(isolated: false);
        }

        return dictionary;
    }

    /// <summary>
    /// A copy of a page's (copied) resource dictionary with room for one more XObject, under a name the page does not
    /// use yet. The dictionaries are copied shallowly: the page's own resources may be shared with other pages.
    /// </summary>
    private static (PdfDictionary Resources, PdfDictionary XObjects, string Name) AddXObject(PdfObjectCopier copier, PdfObject? resources, string baseName)
    {
        var merged = new PdfDictionary();
        if (copier.Target(resources) is PdfDictionary original)
        {
            foreach (var (key, value) in original.Entries)
            {
                merged.AddUnchecked(key, value);
            }
        }

        var xobjects = new PdfDictionary();
        if (copier.Target(merged["XObject"]) is PdfDictionary existing)
        {
            foreach (var (key, value) in existing.Entries)
            {
                xobjects.AddUnchecked(key, value);
            }
        }

        merged["XObject"] = xobjects;
        var name = baseName;
        for (var i = 2; xobjects[name] is not null; i++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{baseName}{i}");
        }

        return (merged, xobjects, name);
    }

    /// <summary>Records the reference an imported page was written with, so that links and other references to it find it.</summary>
    public void RegisterPage(PdfPage page, PdfReference reference)
    {
        if (page.Source is { } source)
        {
            CopierFor(source.Reader).MapPage(source, reference);
        }
    }

    /// <summary>
    /// The <c>/Annots</c> of an imported page: the source page's annotations (when kept), followed by the links added to
    /// the page (<paramref name="links"/>). Links within the source file are pointed at the imported pages; links to
    /// pages that were not imported are dropped. A source page imported more than once has its annotations on its first
    /// copy only, since an annotation belongs to one page.
    /// </summary>
    public PdfArray? WriteAnnotations(PdfPage page, PdfArray? links)
    {
        if (page.Source is not { } source || page.ImportOptions?.KeepAnnotations != true || !_annotated.Add(source)
            || source.Reader.Resolve(source.Dictionary["Annots"]) is not PdfArray annotations)
        {
            return links;
        }

        var reader = source.Reader;
        var copier = CopierFor(reader);
        var result = new PdfArray();
        foreach (var item in annotations.Items)
        {
            if (reader.Resolve(item) is not PdfDictionary annotation)
            {
                continue;
            }

            if (reader.Resolve(annotation["Subtype"]) is PdfName { Value: "Link" } && reader.DestinationOf(annotation) is { } destination)
            {
                var explicitDestination = reader.ResolveDestination(destination);
                if (copier.PageReference(explicitDestination?.Items[0]) is not { } target)
                {
                    continue; // A link to a page that is not in the new document.
                }

                var copy = copier.Copy(item);
                if (copier.Target(copy) is PdfDictionary link)
                {
                    // An explicit destination: names and GoTo actions of the source file mean nothing in the new one.
                    link["Dest"] = new PdfArray([target, .. explicitDestination!.Items.Skip(1).Select(i => copier.Copy(i) ?? PdfNull.Instance)]);
                    link["A"] = null;
                    result.Items.Add(copy!);
                }

                continue;
            }

            if (copier.Copy(item) is { } other)
            {
                result.Items.Add(other);
            }
        }

        foreach (var link in links?.Items ?? [])
        {
            result.Items.Add(link);
        }

        return result.Items.Count > 0 ? result : null;
    }
}
