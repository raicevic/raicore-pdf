using System.Globalization;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>A page in a <see cref="PdfDocument"/>.</summary>
/// <remarks>Not thread-safe; see <see cref="PdfDocument"/>.</remarks>
public sealed class PdfPage
{
    private readonly List<(PdfFont Font, string Name, FontUsage Usage)> _fonts = [];
    private readonly List<(PdfImage Image, string Name)> _images = [];
    private readonly List<PdfLink> _links = [];
    private readonly List<((double Fill, double Stroke) Alpha, string Name)> _extGStates = [];
    private readonly List<GroupForm> _groups = [];
    private readonly List<(PdfReaderPage Page, string Name)> _pageForms = [];

    internal PdfPage(PdfPageSize size)
    {
        Size = size;
        Canvas = new PdfCanvas(this);
    }

    /// <summary>
    /// A page imported from another file. Its size is the source page's media box, and everything placed on it
    /// (drawing, links, destinations) is measured from the media box's bottom-left corner, as on a generated page.
    /// </summary>
    internal PdfPage(PdfReaderPage source, PdfImportOptions options)
        : this(new PdfPageSize(source.MediaBox.Width, source.MediaBox.Height))
    {
        Source = source;
        ImportOptions = options;
        Origin = (source.MediaBox.X, source.MediaBox.Y);
    }

    /// <summary>The page size in points.</summary>
    public PdfPageSize Size { get; }

    /// <summary>The page width in points.</summary>
    public double Width => Size.Width;

    /// <summary>The page height in points.</summary>
    public double Height => Size.Height;

    /// <summary>Draws on the page.</summary>
    public PdfCanvas Canvas { get; }

    /// <summary>The document the page belongs to; <c>null</c> for the drawing surface of an SVG image.</summary>
    internal PdfDocument? Document { get; init; }

    /// <summary>The links on the page (link annotations), in the order they were added.</summary>
    public IReadOnlyList<PdfLink> Links => _links;

    internal IReadOnlyList<(PdfFont Font, string Name, FontUsage Usage)> Fonts => _fonts;

    internal IReadOnlyList<(PdfImage Image, string Name)> Images => _images;

    /// <summary>The page of another file this page was imported from, or <c>null</c> for a generated page.</summary>
    internal PdfReaderPage? Source { get; }

    /// <summary>How the page was imported, for an imported page.</summary>
    internal PdfImportOptions? ImportOptions { get; }

    /// <summary>
    /// Where the page's coordinates start in its PDF user space: (0, 0) for generated pages, the media box's corner for
    /// imported ones. Coordinates written into the file (link rectangles, destinations) are offset by it.
    /// </summary>
    internal (double X, double Y) Origin { get; }

    /// <summary>The extended graphics states (fill and stroke alpha) the page's content uses.</summary>
    internal IReadOnlyList<((double Fill, double Stroke) Alpha, string Name)> ExtGStates => _extGStates;

    /// <summary>The transparency groups drawn on the page (form XObjects), including nested ones.</summary>
    internal IReadOnlyList<GroupForm> Groups => _groups;

    /// <summary>Whether the page uses transparency: alpha, groups, or an SVG image that does.</summary>
    internal bool UsesTransparency =>
        _extGStates.Count > 0 || Canvas.SoftMasks.Count > 0 || _images.Any(entry => entry.Image.UsesTransparency) || _pageForms.Any(entry => entry.Page.UsesTransparency);

    /// <summary>
    /// Makes a rectangle of the page a link that opens <paramref name="uri"/> (a URI action), e.g. a web page or a
    /// <c>mailto:</c> address. Coordinates are PDF points with the origin in the bottom-left corner of the page.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="uri">The URI to open. Relative URIs are written as they are; viewers resolve them against the file's location.</param>
    public PdfLink AddLink(double x, double y, double width, double height, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Add(x, y, width, height, uri, null, null);
    }

    /// <summary>
    /// Makes a rectangle of the page a link that jumps to a named destination of the document (see
    /// <see cref="PdfDocument.NamedDestinations"/>). The destination may be added later, before the document is saved;
    /// a link to a name the document does not define when it is saved is left out.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="destinationName">The name of the destination.</param>
    public PdfLink AddLink(double x, double y, double width, double height, string destinationName)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationName);
        return Add(x, y, width, height, null, destinationName, null);
    }

    /// <summary>Makes a rectangle of the page a link that jumps to a place in the document.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="destination">Where to jump to. Its page must belong to the same document.</param>
    public PdfLink AddLink(double x, double y, double width, double height, PdfDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Add(x, y, width, height, null, null, destination);
    }

    private PdfLink Add(double x, double y, double width, double height, Uri? uri, string? name, PdfDestination? destination)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException("The link rectangle must have finite coordinates.");
        }

        // A rectangle with a negative width or height extends to the left or below its corner.
        var link = new PdfLink(Math.Min(x, x + width), Math.Min(y, y + height), Math.Abs(width), Math.Abs(height), uri, name, destination);
        _links.Add(link);
        if (Document is { Tagged: true } document)
        {
            // PDF/UA: every link annotation belongs to a Link element, with the link's text as marked content beside it.
            var element = Canvas.CurrentElement ?? document.StructureRoot;
            if (element.Type != PdfStructureTypes.Link)
            {
                element = element.AddChild(PdfStructureTypes.Link);
            }

            link.StructureElement = element;
            element.AddContent(new AnnotationKid(this, link));
        }

        return link;
    }

    /// <summary>Returns the page-local resource name (e.g. <c>Im1</c>) for an image, registering it on first use.</summary>
    internal string UseImage(PdfImage image)
    {
        foreach (var entry in _images)
        {
            if (ReferenceEquals(entry.Image, image))
            {
                return entry.Name;
            }
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"Im{_images.Count + 1}");
        _images.Add((image, name));
        return name;
    }

    /// <summary>Returns the page-local resource name (e.g. <c>Pg1</c>) for a page of another file drawn as a form XObject.</summary>
    internal string UsePageForm(PdfReaderPage page)
    {
        foreach (var entry in _pageForms)
        {
            if (ReferenceEquals(entry.Page, page))
            {
                return entry.Name;
            }
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"Pg{_pageForms.Count + 1}");
        _pageForms.Add((page, name));
        return name;
    }

    /// <summary>Returns the page-local resource name (e.g. <c>GS1</c>) for an extended graphics state with these alphas.</summary>
    internal string UseExtGState(double fill, double stroke)
    {
        foreach (var entry in _extGStates)
        {
            if (entry.Alpha == (fill, stroke))
            {
                return entry.Name;
            }
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"GS{_extGStates.Count + 1}");
        _extGStates.Add(((fill, stroke), name));
        return name;
    }

    /// <summary>Registers a transparency group's content and returns its page-local resource name (e.g. <c>Fm1</c>).</summary>
    /// <param name="content">The group's content stream.</param>
    /// <param name="resources">The names of the resources its content uses.</param>
    /// <param name="matrix">The form matrix, which maps the group's space (the page's) to the space it is drawn in.</param>
    /// <param name="marks">The group's marked content (tagged documents).</param>
    internal string AddGroup(byte[] content, IReadOnlySet<string> resources, Svg.SvgMatrix matrix, MarkedContentStream? marks = null)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"Fm{_groups.Count + 1}");
        _groups.Add(new GroupForm(name, content, resources, matrix, marks));
        return name;
    }

    /// <summary>
    /// Writes the transparency groups and returns the resource dictionary of the page's content stream. Each content
    /// stream (the page's and every group's) gets only the resources it uses; the callbacks supply the references of
    /// resources written elsewhere, and are called at most once per resource. <paramref name="pageForm"/> writes pages of
    /// other files drawn with <see cref="PdfCanvas.DrawPage(PdfReaderPage, double, double)"/>.
    /// </summary>
    internal PdfDictionary WriteResources(
        PdfFileWriter writer, bool compress, Func<PdfFont, FontUsage, PdfReference> font, Func<PdfImage, PdfReference> image,
        Func<(double Fill, double Stroke), PdfReference> extGState, Func<PdfReaderPage, PdfReference>? pageForm = null,
        Action<GroupForm, PdfDictionary, PdfReference>? groupWritten = null)
    {
        var references = new Dictionary<string, PdfReference>(StringComparer.Ordinal);
        PdfReference Resolve(string name, Func<PdfReference> write)
        {
            if (!references.TryGetValue(name, out var reference))
            {
                reference = write();
                references[name] = reference;
            }

            return reference;
        }

        // Group forms are added first, so that any content (also another group's) can refer to them.
        var forms = new List<PdfStreamObject>(_groups.Count);
        foreach (var group in _groups)
        {
            var form = compress ? PdfStreamObject.CreateCompressed(group.Content) : new PdfStreamObject(group.Content);
            var dictionary = form.Dictionary;
            dictionary["Type"] = new PdfName("XObject");
            dictionary["Subtype"] = new PdfName("Form");
            dictionary["BBox"] = PdfArray.OfNumbers(0, 0, Width, Height);
            if (!group.Matrix.IsIdentity)
            {
                var m = group.Matrix;
                dictionary["Matrix"] = PdfArray.OfNumbers(m.A, m.B, m.C, m.D, m.E, m.F);
            }

            dictionary["Group"] = TransparencyGroup(isolated: true);
            references[group.Name] = writer.Add(form);
            forms.Add(form);
            groupWritten?.Invoke(group, dictionary, references[group.Name]);
        }

        PdfDictionary Build(IReadOnlySet<string> used)
        {
            var resources = new PdfDictionary();
            var fonts = new PdfDictionary();
            foreach (var (f, name, usage) in _fonts.Where(entry => used.Contains(entry.Name)))
            {
                fonts[name] = Resolve(name, () => font(f, usage));
            }

            var xobjects = new PdfDictionary();
            foreach (var (i, name) in _images.Where(entry => used.Contains(entry.Name)))
            {
                xobjects[name] = Resolve(name, () => image(i));
            }

            foreach (var group in _groups.Where(group => used.Contains(group.Name)))
            {
                xobjects[group.Name] = references[group.Name];
            }

            foreach (var (p, name) in _pageForms.Where(entry => used.Contains(entry.Name)))
            {
                xobjects[name] = Resolve(name, () => (pageForm ?? throw new InvalidOperationException("Pages of other files cannot be drawn here."))(p));
            }

            var states = new PdfDictionary();
            foreach (var (alpha, name) in _extGStates.Where(entry => used.Contains(entry.Name)))
            {
                states[name] = Resolve(name, () => extGState(alpha));
            }

            foreach (var (alpha, name) in Canvas.SoftMasks.Where(entry => used.Contains(entry.Name)))
            {
                states[name] = Resolve(name, () => writer.Add(SoftMask(writer, alpha, compress)));
            }

            var patterns = new PdfDictionary();
            foreach (var (pattern, name) in Canvas.Patterns.Where(entry => used.Contains(entry.Name)))
            {
                patterns[name] = Resolve(name, () => writer.Add(pattern));
            }

            var shadings = new PdfDictionary();
            foreach (var (shading, name) in Canvas.Shadings.Where(entry => used.Contains(entry.Name)))
            {
                shadings[name] = Resolve(name, () => writer.Add(shading));
            }

            foreach (var (key, dictionary) in new[] { ("Font", fonts), ("XObject", xobjects), ("ExtGState", states), ("Pattern", patterns), ("Shading", shadings) })
            {
                if (dictionary.Count > 0)
                {
                    resources[key] = dictionary;
                }
            }

            return resources;
        }

        for (var i = 0; i < _groups.Count; i++)
        {
            forms[i].Dictionary["Resources"] = Build(_groups[i].Resources);
        }

        return Build(Canvas.PageResources);
    }

    /// <summary>
    /// An extended graphics state whose soft mask is the luminosity of a gray shading: painted through it, a gradient
    /// gets the alpha the shading carries. The mask's group covers far more than any page, as the shading fills it.
    /// </summary>
    private static PdfDictionary SoftMask(PdfFileWriter writer, PdfDictionary alphaShading, bool compress)
    {
        var content = "/A sh"u8.ToArray();
        var form = compress ? PdfStreamObject.CreateCompressed(content) : new PdfStreamObject(content);
        form.Dictionary["Type"] = new PdfName("XObject");
        form.Dictionary["Subtype"] = new PdfName("Form");
        form.Dictionary["BBox"] = PdfArray.OfNumbers(-20000, -20000, 20000, 20000);
        form.Dictionary["Group"] = new PdfDictionary
        {
            ["Type"] = new PdfName("Group"),
            ["S"] = new PdfName("Transparency"),
            ["CS"] = new PdfName("DeviceGray"),
        };
        form.Dictionary["Resources"] = new PdfDictionary { ["Shading"] = new PdfDictionary { ["A"] = writer.Add(alphaShading) } };
        return new PdfDictionary
        {
            ["Type"] = new PdfName("ExtGState"),
            ["SMask"] = new PdfDictionary
            {
                ["Type"] = new PdfName("Mask"),
                ["S"] = new PdfName("Luminosity"),
                ["G"] = writer.Add(form),
            },
        };
    }

    /// <summary>A transparency group dictionary in the RGB blending space (for pages, and for groups when isolated).</summary>
    internal static PdfDictionary TransparencyGroup(bool isolated)
    {
        var group = new PdfDictionary
        {
            ["Type"] = new PdfName("Group"),
            ["S"] = new PdfName("Transparency"),
            ["CS"] = new PdfName("DeviceRGB"),
        };
        if (isolated)
        {
            group["I"] = PdfBoolean.True;
        }

        return group;
    }

    /// <summary>An extended graphics state that sets the fill (<c>/ca</c>) and stroke (<c>/CA</c>) alpha.</summary>
    internal static PdfDictionary ExtGState((double Fill, double Stroke) alpha) => new()
    {
        ["Type"] = new PdfName("ExtGState"),
        ["CA"] = new PdfReal(alpha.Stroke),
        ["ca"] = new PdfReal(alpha.Fill),
    };

    /// <summary>Returns the page-local resource name (e.g. <c>F1</c>) for a font and the record of glyphs used from it.</summary>
    internal (string Name, FontUsage Usage) UseFont(PdfFont font)
    {
        font = font.EmbeddedFont; // Views of a font share its embedding.
        foreach (var entry in _fonts)
        {
            if (ReferenceEquals(entry.Font, font))
            {
                return (entry.Name, entry.Usage);
            }
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"F{_fonts.Count + 1}");
        var usage = new FontUsage();
        _fonts.Add((font, name, usage));
        return (name, usage);
    }
}

/// <summary>A page size in PDF points (1/72 inch).</summary>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>ISO A4 portrait (210 × 297 mm).</summary>
    public static PdfPageSize A4 { get; } = FromMillimeters(210, 297);

    /// <summary>ISO A3 portrait (297 × 420 mm).</summary>
    public static PdfPageSize A3 { get; } = FromMillimeters(297, 420);

    /// <summary>ISO A5 portrait (148 × 210 mm).</summary>
    public static PdfPageSize A5 { get; } = FromMillimeters(148, 210);

    /// <summary>US Letter portrait (8.5 × 11 in).</summary>
    public static PdfPageSize Letter { get; } = new(612, 792);

    /// <summary>US Legal portrait (8.5 × 14 in).</summary>
    public static PdfPageSize Legal { get; } = new(612, 1008);

    /// <summary>The same size rotated to landscape.</summary>
    public PdfPageSize Landscape => Width >= Height ? this : new(Height, Width);

    /// <summary>Creates a size from millimeters.</summary>
    public static PdfPageSize FromMillimeters(double width, double height) => new(width * 72 / 25.4, height * 72 / 25.4);

    // The generated record ToString would print Landscape, which is itself a PdfPageSize: infinite recursion.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Width = {Width}, Height = {Height}");
        return true;
    }
}

/// <summary>A transparency group drawn on a page: a form XObject's content, the resources it uses and its matrix.</summary>
internal sealed record GroupForm(string Name, byte[] Content, IReadOnlySet<string> Resources, Svg.SvgMatrix Matrix, MarkedContentStream? Marks = null);
