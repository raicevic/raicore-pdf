using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// A PDF document under construction.
/// </summary>
/// <example>
/// <code>
/// var doc = new PdfDocument();
/// var page = doc.AddPage(PdfPageSize.A4);
/// page.Canvas.DrawText("Hello, PDF", StandardFont.Helvetica, 24, 72, page.Height - 96);
/// doc.Save("hello.pdf");
/// </code>
/// </example>
public sealed partial class PdfDocument
{
    private readonly List<PdfPage> _pages = [];

    /// <summary>The pages in order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Document metadata, written to the Info dictionary and the XMP metadata stream.</summary>
    public PdfDocumentInfo Info { get; } = new();

    /// <summary>Whether content streams are Flate-compressed. Turn off to inspect output by eye.</summary>
    public bool CompressContent { get; set; } = true;

    /// <summary>
    /// The document outline: the top-level bookmarks a PDF viewer shows in its navigation panel, each with nested
    /// entries. Empty by default (no outline is written).
    /// </summary>
    public IList<PdfOutlineItem> Outline { get; } = new List<PdfOutlineItem>();

    /// <summary>
    /// How a viewer opens the document. By default (<see cref="PdfPageMode.Automatic"/>) the outline panel opens when
    /// the document has an outline.
    /// </summary>
    public PdfPageMode PageMode { get; set; } = PdfPageMode.Automatic;

    /// <summary>
    /// Named destinations: places in the document that links refer to by name
    /// (<see cref="PdfPage.AddLink(double, double, double, double, string)"/>), written to the catalog's <c>/Names</c>
    /// tree. Names are compared exactly (ordinal). Links may be added before their destinations; links to names
    /// missing here when the document is saved are left out.
    /// </summary>
    public IDictionary<string, PdfDestination> NamedDestinations { get; } = new Dictionary<string, PdfDestination>(StringComparer.Ordinal);

    /// <summary>Appends a new, empty page.</summary>
    public PdfPage AddPage(PdfPageSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Page dimensions must be positive.");
        }

        var page = new PdfPage(size);
        _pages.Add(page);
        return page;
    }

    /// <summary>Appends a new A4 page.</summary>
    public PdfPage AddPage() => AddPage(PdfPageSize.A4);

    /// <summary>Writes the document to a file.</summary>
    public void Save(string path)
    {
        using var file = File.Create(path);
        Save(file);
    }

    /// <summary>Writes the document to a stream. The stream is left open.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("A PDF document must contain at least one page.");
        }

        var writer = new PdfFileWriter();

        var catalog = new PdfDictionary { ["Type"] = new PdfName("Catalog") };
        var catalogRef = writer.Add(catalog);
        var pageTree = new PdfDictionary { ["Type"] = new PdfName("Pages") };
        var pageTreeRef = writer.Add(pageTree);
        catalog["Pages"] = pageTreeRef;

        // Each font is written once per document and embeds the union of glyphs used on all pages.
        var fontUsage = new Dictionary<PdfFont, FontUsage>(ReferenceEqualityComparer.Instance);
        var fontOrder = new List<PdfFont>();
        foreach (var (font, _, usage) in _pages.SelectMany(p => p.Fonts))
        {
            if (!fontUsage.TryGetValue(font, out var total))
            {
                total = new FontUsage();
                fontUsage[font] = total;
                fontOrder.Add(font);
            }

            total.MergeFrom(usage);
        }

        var fontRefs = new Dictionary<PdfFont, PdfReference>(ReferenceEqualityComparer.Instance);
        foreach (var font in fontOrder)
        {
            fontRefs[font] = font.WriteTo(writer, fontUsage[font]);
        }

        // Each image is written once, however many times and pages it is drawn on.
        var imageRefs = new Dictionary<PdfImage, PdfReference>(ReferenceEqualityComparer.Instance);
        foreach (var (image, _) in _pages.SelectMany(p => p.Images))
        {
            if (!imageRefs.ContainsKey(image))
            {
                imageRefs[image] = image.WriteTo(writer);
            }
        }

        // Each extended graphics state (a pair of fill and stroke alphas) is written once, when first used.
        var extGStateRefs = new Dictionary<(double Fill, double Stroke), PdfReference>();
        PdfReference ExtGState((double Fill, double Stroke) alpha)
        {
            if (!extGStateRefs.TryGetValue(alpha, out var reference))
            {
                reference = writer.Add(PdfPage.ExtGState(alpha));
                extGStateRefs[alpha] = reference;
            }

            return reference;
        }

        var kids = new PdfArray();
        var pageRefs = new Dictionary<PdfPage, PdfReference>(ReferenceEqualityComparer.Instance);
        var pageDictionaries = new List<PdfDictionary>(_pages.Count);
        foreach (var page in _pages)
        {
            var content = page.Canvas.ToBytes();
            var resources = page.WriteResources(writer, CompressContent, (font, _) => fontRefs[font], image => imageRefs[image], ExtGState);
            var contentStream = CompressContent ? PdfStreamObject.CreateCompressed(content) : new PdfStreamObject(content);

            var pageDictionary = new PdfDictionary
            {
                ["Type"] = new PdfName("Page"),
                ["Parent"] = pageTreeRef,
                ["MediaBox"] = PdfArray.OfNumbers(0, 0, page.Width, page.Height),
                ["Resources"] = resources,
                ["Contents"] = writer.Add(contentStream),
            };

            // A page with transparency blends in RGB, so that viewers agree on how translucent colors mix.
            if (page.UsesTransparency)
            {
                pageDictionary["Group"] = PdfPage.TransparencyGroup(isolated: false);
            }

            var pageRef = writer.Add(pageDictionary);
            kids.Items.Add(pageRef);
            pageRefs[page] = pageRef;
            pageDictionaries.Add(pageDictionary);
        }

        // Links may point at any page, so they are written once every page has its object number.
        for (var i = 0; i < _pages.Count; i++)
        {
            if (PdfLinkWriter.WriteAnnotations(writer, _pages[i], pageRefs, NamedDestinations) is { } annotations)
            {
                pageDictionaries[i]["Annots"] = annotations;
            }
        }

        pageTree["Kids"] = kids;
        pageTree["Count"] = new PdfInteger(_pages.Count);

        if (Outline.Count > 0)
        {
            catalog["Outlines"] = PdfOutlineWriter.Write(writer, Outline, pageRefs);
        }

        if (NamedDestinations.Count > 0)
        {
            catalog["Names"] = new PdfDictionary { ["Dests"] = PdfLinkWriter.WriteNames(writer, NamedDestinations, pageRefs) };
        }

        if (PageMode == PdfPageMode.UseOutlines || (PageMode == PdfPageMode.Automatic && Outline.Count > 0))
        {
            catalog["PageMode"] = new PdfName("UseOutlines");
        }

        var infoRef = WriteMetadata(writer, catalog);
        writer.Write(stream, catalogRef, infoRef);
    }

    /// <summary>Returns the document as a byte array.</summary>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();
        Save(buffer);
        return buffer.ToArray();
    }
}
