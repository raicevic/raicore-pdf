using System.Globalization;
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
public sealed class PdfDocument
{
    private readonly List<PdfPage> _pages = [];

    /// <summary>The pages in order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Document metadata written to the Info dictionary.</summary>
    public PdfDocumentInfo Info { get; } = new();

    /// <summary>Whether content streams are Flate-compressed. Turn off to inspect output by eye.</summary>
    public bool CompressContent { get; set; } = true;

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

        var kids = new PdfArray();
        foreach (var page in _pages)
        {
            var fontResources = new PdfDictionary();
            foreach (var (font, name, _) in page.Fonts)
            {
                fontResources[name] = fontRefs[font];
            }

            var resources = new PdfDictionary();
            if (fontResources.Count > 0)
            {
                resources["Font"] = fontResources;
            }

            var content = page.Canvas.ToBytes();
            var contentStream = CompressContent ? PdfStreamObject.CreateCompressed(content) : new PdfStreamObject(content);

            kids.Items.Add(writer.Add(new PdfDictionary
            {
                ["Type"] = new PdfName("Page"),
                ["Parent"] = pageTreeRef,
                ["MediaBox"] = PdfArray.OfNumbers(0, 0, page.Width, page.Height),
                ["Resources"] = resources,
                ["Contents"] = writer.Add(contentStream),
            }));
        }

        pageTree["Kids"] = kids;
        pageTree["Count"] = new PdfInteger(_pages.Count);

        var infoRef = writer.Add(Info.ToDictionary());
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

/// <summary>Document metadata (the PDF Info dictionary).</summary>
public sealed class PdfDocumentInfo
{
    /// <summary>Document title.</summary>
    public string? Title { get; set; }

    /// <summary>Author.</summary>
    public string? Author { get; set; }

    /// <summary>Subject.</summary>
    public string? Subject { get; set; }

    /// <summary>Keywords.</summary>
    public string? Keywords { get; set; }

    /// <summary>The application that created the original content.</summary>
    public string? Creator { get; set; }

    /// <summary>The library that produced the PDF.</summary>
    public string Producer { get; set; } = "RaiCore.Pdf";

    /// <summary>Creation date. Defaults to the time the document is saved; set it for reproducible output.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    internal PdfDictionary ToDictionary()
    {
        var dict = new PdfDictionary();
        AddText(dict, "Title", Title);
        AddText(dict, "Author", Author);
        AddText(dict, "Subject", Subject);
        AddText(dict, "Keywords", Keywords);
        AddText(dict, "Creator", Creator);
        AddText(dict, "Producer", Producer);
        dict["CreationDate"] = new PdfString(System.Text.Encoding.ASCII.GetBytes(FormatDate(CreationDate ?? DateTimeOffset.Now)));
        return dict;
    }

    /// <summary>Formats a date as a PDF date string, e.g. <c>D:20261102194100+01'00'</c>.</summary>
    internal static string FormatDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{abs.Hours:D2}'{abs.Minutes:D2}'");
    }

    private static void AddText(PdfDictionary dict, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            dict[key] = PdfString.FromText(value);
        }
    }
}
