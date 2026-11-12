using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// A page of a PDF file read with <see cref="PdfReader"/>, with the attributes it inherits from the page tree resolved.
/// Import it into a document with <see cref="PdfDocument.ImportPages(PdfReader, IEnumerable{int}, PdfImportOptions?)"/>,
/// or draw it on another page with <see cref="PdfCanvas.DrawPage(PdfReaderPage, double, double)"/>.
/// </summary>
public sealed class PdfReaderPage
{
    internal PdfReaderPage(
        PdfReader reader, int index, PdfReference? reference, PdfDictionary dictionary, PdfObject? resources,
        PdfRectangle mediaBox, PdfRectangle cropBox, int rotate)
    {
        Reader = reader;
        Index = index;
        Reference = reference;
        Dictionary = dictionary;
        ResourcesObject = resources;
        MediaBox = mediaBox;
        CropBox = cropBox;
        Rotate = rotate;
    }

    /// <summary>The file the page belongs to.</summary>
    public PdfReader Reader { get; }

    /// <summary>The page's position in the file, from 0.</summary>
    public int Index { get; }

    /// <summary>The page dictionary. References in it are to objects of the file; see <see cref="PdfReader.Resolve"/>.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The page's media box: the boundaries of the physical medium, in the page's own coordinates.</summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>The page's crop box: the visible region, within the media box. Defaults to the media box.</summary>
    public PdfRectangle CropBox { get; }

    /// <summary>How many degrees the page is turned clockwise when displayed: 0, 90, 180 or 270.</summary>
    public int Rotate { get; }

    /// <summary>The width of the page as displayed: the crop box, turned by <see cref="Rotate"/>.</summary>
    public double Width => Rotate is 90 or 270 ? CropBox.Height : CropBox.Width;

    /// <summary>The height of the page as displayed: the crop box, turned by <see cref="Rotate"/>.</summary>
    public double Height => Rotate is 90 or 270 ? CropBox.Width : CropBox.Height;

    /// <summary>The page's resource dictionary (fonts, images, ...), inherited from the page tree when the page has none.</summary>
    public PdfDictionary Resources => Reader.Resolve(ResourcesObject) as PdfDictionary ?? new PdfDictionary();

    /// <summary>The reference to the page object, or <c>null</c> for a page written directly in its parent's <c>/Kids</c>.</summary>
    internal PdfReference? Reference { get; }

    /// <summary>The page's <c>/Resources</c> as written (a reference when shared with other pages), or <c>null</c>.</summary>
    internal PdfObject? ResourcesObject { get; }

    /// <summary>Whether the page is a transparency group (has a <c>/Group</c> of subtype <c>/Transparency</c>).</summary>
    internal bool UsesTransparency =>
        Reader.Resolve(Dictionary["Group"]) is PdfDictionary group && Reader.Resolve(group["S"]) is PdfName { Value: "Transparency" };

    /// <summary>The page's content streams, in order.</summary>
    internal IReadOnlyList<PdfStreamObject> ContentStreams => Reader.Resolve(Dictionary["Contents"]) switch
    {
        PdfStreamObject stream => [stream],
        PdfArray array => [.. array.Items.Select(Reader.Resolve).OfType<PdfStreamObject>()],
        _ => [],
    };

    /// <summary>
    /// The page's content: its content streams decoded and joined (with a line break between them), as the PDF
    /// operators that draw the page.
    /// </summary>
    /// <exception cref="PdfFormatException">A content stream uses a filter that cannot be decoded.</exception>
    public byte[] GetContent()
    {
        var streams = ContentStreams;
        if (streams.Count == 1)
        {
            return Reader.Store.Decode(streams[0]);
        }

        using var content = new MemoryStream();
        foreach (var stream in streams)
        {
            var data = Reader.Store.Decode(stream);
            ResourceLimits.CheckDecompressedBytes(content.Length + (long)data.Length);

            content.Write(data);
            content.WriteByte((byte)'\n'); // Content may be split between streams anywhere between tokens.
        }

        return content.ToArray();
    }
}
