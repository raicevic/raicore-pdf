namespace RaiCore.Pdf.Editing;

/// <summary>A part of a document split by <see cref="PdfEditor.SplitBySize"/>: consecutive pages, as a PDF file of their own.</summary>
public sealed class PdfSplitPart
{
    private readonly byte[] _data;

    internal PdfSplitPart(int firstPageIndex, int pageCount, byte[] data, bool exceedsLimit)
    {
        FirstPageIndex = firstPageIndex;
        PageCount = pageCount;
        _data = data;
        ExceedsLimit = exceedsLimit;
    }

    /// <summary>The index of the part's first page in the document that was split, from 0.</summary>
    public int FirstPageIndex { get; }

    /// <summary>How many pages the part has.</summary>
    public int PageCount { get; }

    /// <summary>
    /// Whether the part is larger than the limit: a single page that is larger on its own, which is a part by itself.
    /// </summary>
    public bool ExceedsLimit { get; }

    /// <summary>The part's PDF file.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>The size of the part's PDF file in bytes.</summary>
    public long Length => _data.Length;

    /// <summary>Writes the part's PDF file.</summary>
    public void Save(string path) => File.WriteAllBytes(path, _data);

    /// <summary>Writes the part's PDF file to a stream, which is left open.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(_data);
    }

    /// <summary>The part's pages and size.</summary>
    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Pages {FirstPageIndex + 1}-{FirstPageIndex + PageCount}, {Length} bytes{(ExceedsLimit ? " (over the limit)" : "")}");
}
