using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html;

/// <summary>
/// HTML (with CSS) converted to layout blocks for a document built with the layout API: rich text stored as HTML,
/// say, placed between the paragraphs of a report or in a table cell.
/// </summary>
/// <remarks>
/// <para>
/// The fragment is converted as the body of an HTML document is, by the same engine as <see cref="HtmlToPdf"/>: text,
/// links, lists, tables, images, flex boxes, floats, bidirectional text and generated content come out as they would
/// in a converted document, starting from <see cref="HtmlFragmentOptions.BaseStyle"/> instead of the HTML defaults.
/// The fragment can be a whole document (with <c>&lt;head&gt;</c> and <c>&lt;style&gt;</c>) or any part of a body.
/// </para>
/// <para>
/// The pages belong to the document the blocks are placed in, so what only pages have is left out and reported in
/// <see cref="Diagnostics"/>: <c>@page</c> rules (page size, margins, margin boxes), running elements
/// (<c>position: running()</c>, which stay in the flow), running strings (<c>string-set</c>), named pages
/// (<c>page</c>) and page numbers of cross-references (<c>target-counter(..., page)</c>, which show 0). Page breaks
/// (<c>break-before</c>, <c>break-after</c>, <c>break-inside</c>) apply as the blocks' <see cref="BoxStyle"/> breaks
/// do. Ids become named destinations of the document the blocks are rendered in, like <see cref="Block.Id"/>.
/// </para>
/// <para>
/// The blocks carry structure tags as in a converted document (headings, paragraphs, lists, tables, figures with
/// their <c>alt</c> text), so they are tagged like one when they are rendered in a tagged document
/// (<see cref="PdfDocument.Tagged"/>). Layout issues (content wider than its box) are reported by the
/// <see cref="FlowDocument.Diagnostics"/> of the document they are rendered in.
/// </para>
/// <para>
/// A fragment's blocks are built for one place in one document: add them once (parse the HTML again to place it
/// again). Parsing is thread-safe; the blocks, like other blocks, are not (see <see cref="FlowDocument"/>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var flow = new FlowDocument(PageSetup.A4);
/// flow.Add(new ParagraphBlock(new Paragraph(body).Add("Customer notes:")));
/// HtmlFragment.Parse(notesHtml, new HtmlFragmentOptions { BaseStyle = body, Width = flow.Setup.ContentWidth }).AddTo(flow);
/// </code>
/// </example>
public sealed class HtmlFragment
{
    private HtmlFragment(IReadOnlyList<Block> blocks, IReadOnlyList<HtmlDiagnostic> diagnostics)
    {
        Blocks = blocks;
        Diagnostics = diagnostics;
    }

    /// <summary>The blocks the fragment's content makes, top to bottom; empty for a fragment with no content.</summary>
    public IReadOnlyList<Block> Blocks { get; }

    /// <summary>
    /// What could not be converted as a browser would (resources that did not load, CSS that is not supported, what
    /// only pages have), each distinct issue once, in the order they first occurred (see <see cref="HtmlDiagnostic"/>).
    /// </summary>
    public IReadOnlyList<HtmlDiagnostic> Diagnostics { get; }

    /// <summary>Converts an HTML fragment to layout blocks.</summary>
    /// <param name="html">The HTML: a part of a body, such as <c>&lt;p&gt;Hello &lt;b&gt;world&lt;/b&gt;&lt;/p&gt;</c>, or a whole document.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ResourceLimitExceededException">The fragment exceeds one of the options' <see cref="HtmlFragmentOptions.Limits"/>.</exception>
    /// <exception cref="ArgumentException">The options' <see cref="HtmlFragmentOptions.Width"/> is not a positive number.</exception>
    public static HtmlFragment Parse(string html, HtmlFragmentOptions? options = null) =>
        ParseAsync(html, options, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Converts an HTML fragment to layout blocks (see <see cref="Parse"/>).</summary>
    /// <remarks>
    /// Like <see cref="HtmlToPdf.ConvertAsync(string, HtmlConverterOptions?, CancellationToken)"/>, the conversion runs
    /// on a thread of its own (with a large stack, for deeply nested HTML) and checks
    /// <paramref name="cancellationToken"/> and the <see cref="ResourceLimits.Timeout"/> while parsing and building.
    /// </remarks>
    /// <param name="html">The HTML.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <exception cref="ResourceLimitExceededException">The fragment exceeds one of the options' <see cref="HtmlFragmentOptions.Limits"/>.</exception>
    /// <exception cref="ArgumentException">The options' <see cref="HtmlFragmentOptions.Width"/> is not a positive number.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<HtmlFragment> ParseAsync(string html, HtmlFragmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlFragmentOptions();
        if (!double.IsFinite(options.Width) || options.Width <= 0)
        {
            throw new ArgumentException("The width must be a positive number of points.", nameof(options));
        }

        var (blocks, diagnostics) = await HtmlToPdf.BuildFragmentAsync(html, options, cancellationToken).ConfigureAwait(false);
        return new HtmlFragment(blocks, diagnostics);
    }

    /// <summary>Appends the blocks to a flow document.</summary>
    /// <returns>The flow document, for chaining.</returns>
    public FlowDocument AddTo(FlowDocument flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        foreach (var block in Blocks)
        {
            flow.Add(block);
        }

        return flow;
    }

    /// <summary>Appends the blocks to a container block. For a table cell, pass <see cref="Blocks"/> to <see cref="TableCell(IEnumerable{Block})">a table cell's constructor</see>.</summary>
    /// <returns>The container, for chaining.</returns>
    public ContainerBlock AddTo(ContainerBlock container)
    {
        ArgumentNullException.ThrowIfNull(container);
        foreach (var block in Blocks)
        {
            container.Add(block);
        }

        return container;
    }
}
