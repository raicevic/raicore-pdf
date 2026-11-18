using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Extraction.Images;

namespace RaiCore.Pdf.Extraction;

/// <summary>
/// Lists the images drawn on the pages of a PDF file, with their place and size on the page, and exports their data
/// (see <see cref="ExtractedImage"/>).
/// </summary>
/// <remarks>
/// <para>
/// Image XObjects, inline images and images inside form XObjects are listed, in the order they are drawn. Images
/// that cannot be seen are left out: those hidden by clipping and those outside the page. Images that are part of
/// patterns, Type 3 glyphs or annotations are not listed.
/// </para>
/// <para>
/// Malformed content never throws: what cannot be read is skipped and reported in <see cref="PageImages.Diagnostics"/>.
/// Image data is only read by <see cref="ExtractedImage.GetData"/>. For untrusted files, run extraction inside
/// <see cref="ResourceLimits.Enforce"/>. Like the <see cref="PdfReader"/> it reads, an extractor is not thread-safe.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var extractor = new ImageExtractor(PdfReader.Open("brochure.pdf"));
/// foreach (var page in extractor.ExtractPages())
/// {
///     foreach (var (image, i) in page.Images.Select((image, i) => (image, i)))
///     {
///         File.WriteAllBytes($"page{page.PageIndex + 1}-{i + 1}{image.FileExtension}", image.GetData());
///     }
/// }
/// </code>
/// </example>
public sealed class ImageExtractor
{
    private readonly ContentInterpreter _interpreter;

    /// <summary>An extractor for the pages of <paramref name="reader"/>.</summary>
    public ImageExtractor(PdfReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Reader = reader;
        _interpreter = new ContentInterpreter(reader, new ImageCollector());
    }

    /// <summary>The file read.</summary>
    public PdfReader Reader { get; }

    /// <summary>The images of one page.</summary>
    /// <param name="pageIndex">The page's position in the file, from 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the file.</exception>
    /// <exception cref="ResourceLimitExceededException">A limit of <see cref="ResourceLimits.Enforce"/> was exceeded.</exception>
    public PageImages ExtractPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, Reader.Pages.Count);
        var page = Reader.Pages[pageIndex];
        var collector = new ImageCollector();
        var reported = _interpreter.Diagnostics.Messages.Count;
        _interpreter.Run(page, collector);
        var diagnostics = _interpreter.Diagnostics.Messages.Skip(reported).ToArray();
        var toUser = ContentInterpreter.DisplayMatrix(page).Invert() ?? Matrix.Identity;
        var images = new List<ExtractedImage>();
        foreach (var placement in collector.Placements)
        {
            var bounds = placement.Bounds;
            if (placement.IsClipped || bounds.Right < 0 || bounds.Bottom < 0 || bounds.Left > page.Width || bounds.Top > page.Height)
            {
                continue;
            }

            var image = placement.Inline is { } inline
                ? ImageObject.FromInline(Reader, inline.Dictionary, inline.Data, placement.Resources)
                : ImageObject.FromXObject(Reader, placement.XObject!);
            images.Add(Describe(pageIndex, image, placement, toUser));
        }

        return new PageImages(pageIndex, images, diagnostics);
    }

    /// <summary>The images of every page, in order. Pages are read as the sequence is enumerated.</summary>
    public IEnumerable<PageImages> ExtractPages() => ExtractPages(Enumerable.Range(0, Reader.Pages.Count));

    /// <summary>The images of the pages at <paramref name="pageIndexes"/> (from 0), in the order given.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the file (thrown when it is reached).</exception>
    public IEnumerable<PageImages> ExtractPages(IEnumerable<int> pageIndexes)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        return Iterate(pageIndexes);

        IEnumerable<PageImages> Iterate(IEnumerable<int> indexes)
        {
            foreach (var index in indexes)
            {
                yield return ExtractPage(index);
            }
        }
    }

    /// <summary>An image's placement in user space: the image's unit square mapped by the matrix it was drawn with.</summary>
    private static ExtractedImage Describe(int pageIndex, ImageObject image, ImagePlacement placement, Matrix toUser)
    {
        var m = placement.Transform.Then(toUser);
        var corners = new[] { m.Transform(0, 0), m.Transform(1, 0), m.Transform(1, 1), m.Transform(0, 1) };
        var (left, bottom) = (corners.Min(c => c.X), corners.Min(c => c.Y));
        var bounds = new PdfRectangle(left, bottom, corners.Max(c => c.X) - left, corners.Max(c => c.Y) - bottom);
        var rotation = Math.Atan2(m.B, m.A) * 180 / Math.PI;
        rotation = Math.Abs(rotation) < 1e-9 ? 0 : rotation;
        return new ExtractedImage(
            pageIndex, image, bounds, Math.Sqrt((m.A * m.A) + (m.B * m.B)), Math.Sqrt((m.C * m.C) + (m.D * m.D)), rotation,
            placement.ResourceName, placement.Reference?.ObjectNumber);
    }

    /// <summary>Collects the images drawn on a page.</summary>
    private sealed class ImageCollector : ContentSink
    {
        public List<ImagePlacement> Placements { get; } = [];

        public override void Image(in ImagePlacement image) => Placements.Add(image);
    }
}
