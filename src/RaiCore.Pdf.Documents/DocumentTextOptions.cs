namespace RaiCore.Pdf.Documents;

/// <summary>
/// Options for <see cref="DocumentText"/>: how paragraphs, list labels and tables are written, and which parts of the
/// document the text includes. The defaults give the readable text of the document's body: one paragraph per line,
/// list numbers and bullets included, table cells separated by tabs with each row on a line of its own, the text of
/// text boxes after the paragraph they are anchored in; headers and footers, notes, comments, hidden text and deleted
/// text are left out, and fields show their results.
/// </summary>
/// <remarks>Options are immutable once created and can be shared by extractions running at the same time.</remarks>
public sealed class DocumentTextOptions
{
    /// <summary>
    /// What ends each paragraph (and a line, page or column break in one); also ends the last. Defaults to <c>"\n"</c>.
    /// </summary>
    public string ParagraphSeparator { get; init; } = "\n";

    /// <summary>
    /// Whether the numbers and bullets of list paragraphs are written before their text, followed by what the list level
    /// asks for (a tab, a space or nothing), as the document shows them. Defaults to <c>true</c>.
    /// </summary>
    public bool ListLabels { get; init; } = true;

    /// <summary>
    /// What separates the cells of a table row. Defaults to <c>"\t"</c>. Within a cell, paragraphs, line breaks and tabs
    /// are written as spaces, so a row stays on one line; nested tables are flattened the same way.
    /// </summary>
    public string TableCellSeparator { get; init; } = "\t";

    /// <summary>What ends each table row. Defaults to <c>"\n"</c>.</summary>
    public string TableRowSeparator { get; init; } = "\n";

    /// <summary>
    /// Whether headers and footers are included: each section's own (for its first page, when it has a title page;
    /// for the other pages; for even pages, when the document has different even and odd headers), its headers before
    /// its text and its footers after it, each once. Defaults to <c>false</c>.
    /// </summary>
    public bool IncludeHeadersAndFooters { get; init; }

    /// <summary>
    /// Whether footnotes and endnotes are included: their numbers (or custom marks) where they are referred to, and the
    /// notes themselves at the end of the text, footnotes first, each starting with its number. Defaults to
    /// <c>false</c>.
    /// </summary>
    public bool IncludeNotes { get; init; }

    /// <summary>
    /// Whether comments are included: their text at the end of the text (after the notes), in the order of the places
    /// they are anchored. Defaults to <c>false</c>.
    /// </summary>
    public bool IncludeComments { get; init; }

    /// <summary>
    /// Whether the text of text boxes and shapes is included, after the paragraph they are anchored in. Defaults to
    /// <c>true</c>.
    /// </summary>
    public bool IncludeTextBoxes { get; init; } = true;

    /// <summary>Whether hidden text (that a word processor does not show or print) is included. Defaults to <c>false</c>.</summary>
    public bool IncludeHiddenText { get; init; }

    /// <summary>
    /// Whether text deleted by tracked changes is included, where it was. Defaults to <c>false</c>: changes count as
    /// accepted, as in conversion (inserted text is always included).
    /// </summary>
    public bool IncludeDeletedText { get; init; }

    /// <summary>
    /// Whether fields are written as their codes (<c>{ PAGE }</c>, <c>{ HYPERLINK "https://example.com" }</c>) instead of
    /// their results. Defaults to <c>false</c>: the results the document was saved with (page numbers, which depend on
    /// the layout, are written as saved, or not at all). OpenDocument fields have no codes and always show their results.
    /// </summary>
    public bool FieldCodes { get; init; }

    /// <summary>
    /// The limits that bound reading the document: as for the converters (see
    /// <see cref="DocumentConverterOptions.Limits"/>). Defaults to <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;
}
