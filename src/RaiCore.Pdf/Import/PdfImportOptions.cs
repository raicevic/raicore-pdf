namespace RaiCore.Pdf;

/// <summary>What to keep of pages imported from another PDF file (<see cref="PdfDocument.ImportPages(PdfReader, PdfImportOptions?)"/>).</summary>
public sealed class PdfImportOptions
{
    /// <summary>The default options: annotations kept, outline not.</summary>
    public static PdfImportOptions Default { get; } = new();

    /// <summary>
    /// Whether to keep the pages' annotations (default <c>true</c>): links, comments, stamps and form field widgets.
    /// Links within the file are pointed at the imported pages; links to pages that are not imported are dropped.
    /// Form fields keep their appearance, but are not part of the document's interactive form.
    /// </summary>
    public bool KeepAnnotations { get; init; } = true;

    /// <summary>
    /// Whether to add the file's outline (bookmarks) to <see cref="PdfDocument.Outline"/> (default <c>false</c>).
    /// Items are appended after the outline items already there, as <see cref="PdfOutlineItem"/>s that can be
    /// rearranged; items that point to pages not imported are left out (their children take their place). Item colors,
    /// styles and actions other than jumps within the file are not kept.
    /// </summary>
    public bool KeepOutline { get; init; }
}
