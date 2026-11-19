namespace RaiCore.Pdf.Editing;

/// <summary>What <see cref="PdfEditor.Optimize"/> does. Everything is on by default.</summary>
public sealed record PdfOptimizationOptions
{
    /// <summary>Everything on.</summary>
    public static PdfOptimizationOptions Default { get; } = new();

    /// <summary>
    /// Remove resources that no content uses: entries of a page's (or form's, pattern's, Type 3 font's) fonts, images and
    /// forms, graphics states, patterns, shadings, color spaces and marked-content properties that its content never
    /// names. Resources shared with something whose content is not read (rare) are kept whole.
    /// </summary>
    public bool RemoveUnusedResources { get; init; } = true;

    /// <summary>
    /// Merge identical objects into one: streams (images, embedded fonts, forms, content) with the same data and
    /// dictionary, and identical fonts, font descriptors, encodings and graphics states. Merged PDFs often carry the same
    /// font or logo once per source file.
    /// </summary>
    public bool MergeDuplicates { get; init; } = true;

    /// <summary>Compress (Flate) streams that are not compressed, where that makes them smaller. XMP metadata stays uncompressed.</summary>
    public bool CompressStreams { get; init; } = true;

    /// <summary>Turn on <see cref="PdfEditor.UseObjectStreams"/> for the saves that follow.</summary>
    public bool UseObjectStreams { get; init; } = true;
}

/// <summary>What <see cref="PdfEditor.Optimize"/> did.</summary>
/// <param name="ResourcesRemoved">How many resource entries were removed because no content used them.</param>
/// <param name="ObjectsMerged">How many objects were merged into an identical one.</param>
/// <param name="StreamsCompressed">How many streams were compressed.</param>
public sealed record PdfOptimizationResult(int ResourcesRemoved, int ObjectsMerged, int StreamsCompressed);
