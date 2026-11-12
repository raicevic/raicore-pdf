using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RaiCore.Pdf;

/// <summary>
/// Limits on the work and memory that input may cause, so that a server converting untrusted HTML, images, fonts or
/// PDF files cannot be crashed or stalled by them. The defaults (<see cref="Default"/>) are generous enough for large
/// legitimate documents (thousands of pages, large photos and fonts) and stop hostile ones
/// (decompression bombs, absurd nesting, endless pagination).
/// </summary>
/// <remarks>
/// <para>
/// HTML conversion applies <c>HtmlConverterOptions.Limits</c>. Everything else (<see cref="PdfImage.Load(byte[])"/>,
/// <see cref="Fonts.PdfFont.Load(byte[], int)"/>, <see cref="PdfReader"/>, <c>FlowDocument.Render</c>) applies <see cref="Default"/>,
/// unless it runs inside <see cref="Enforce"/>, which also sets a time limit and a cancellation token for the work.
/// </para>
/// <para>
/// Exceeding a limit throws <see cref="ResourceLimitExceededException"/>, naming the limit. HTML conversion degrades
/// gracefully instead for limits on single resources (<see cref="MaxResourceBytes"/>, <see cref="MaxImagePixels"/>,
/// <see cref="MaxFontBytes"/>, <see cref="MaxDecompressedBytes"/>): such an image, stylesheet or web font is skipped
/// like one that cannot be loaded.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var limits = ResourceLimits.Default with { MaxPages = 200, Timeout = TimeSpan.FromSeconds(20) };
/// var pdf = await HtmlToPdf.ConvertAsync(html, new HtmlConverterOptions { Limits = limits }, cancellationToken);
///
/// using (limits.Enforce(cancellationToken))
/// {
///     var image = PdfImage.Load(uploadedBytes); // Also limited outside HTML conversion.
/// }
/// </code>
/// </example>
public sealed record ResourceLimits
{
    private static readonly AsyncLocal<LimitScope?> CurrentScopeLocal = new();

    /// <summary>The default limits.</summary>
    public static ResourceLimits Default { get; } = new();

    /// <summary>
    /// The deepest nesting of HTML elements; blocks laid out with <c>FlowDocument</c> may nest twice as deep (an
    /// inline-block is a box in an anonymous paragraph). Defaults to 256. Independently of it, layout stops with this
    /// limit's exception rather than overflowing the stack when the thread's stack runs low. (SVG images have fixed
    /// limits of their own: 256 levels of elements, 48 of them drawn.)
    /// </summary>
    public int MaxNestingDepth { get; init; } = 256;

    /// <summary>The most elements an HTML document may have. Defaults to 1,000,000.</summary>
    public int MaxElements { get; init; } = 1_000_000;

    /// <summary>
    /// The most grid cells (rows times columns, counting the slots spanned cells cover) one table may have.
    /// Defaults to 2,000,000.
    /// </summary>
    public long MaxTableCells { get; init; } = 2_000_000;

    /// <summary>
    /// The most pixels (width times height) a raster image may have, so that a small file cannot decode to gigabytes.
    /// Defaults to 100,000,000 (e.g. 10,000 by 10,000).
    /// </summary>
    public long MaxImagePixels { get; init; } = 100_000_000;

    /// <summary>The largest resource (image, stylesheet or web font) HTML conversion loads, in bytes. Defaults to 64 MB.</summary>
    public long MaxResourceBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>The largest font a WOFF or WOFF2 file may decompress to, in bytes. Defaults to 64 MB.</summary>
    public long MaxFontBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>The most bytes one compressed PDF stream may decode to. Defaults to 256 MB.</summary>
    public long MaxDecompressedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The most pages a laid out document may have. Defaults to 10,000.</summary>
    public int MaxPages { get; init; } = 10_000;

    /// <summary>
    /// How long one HTML conversion (or the work inside <see cref="Enforce"/>) may take: parsing, layout and rendering
    /// check it regularly. Defaults to 5 minutes; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The limits in effect: those of the innermost <see cref="Enforce"/> scope, or <see cref="Default"/>.</summary>
    internal static ResourceLimits Current => CurrentScopeLocal.Value?.Limits ?? Default;

    /// <summary>The innermost <see cref="Enforce"/> scope, if any.</summary>
    internal static LimitScope? CurrentScope => CurrentScopeLocal.Value;

    /// <summary>
    /// Applies these limits to the work that follows on this thread and the asynchronous calls it makes, until the
    /// returned scope is disposed: image decoding, font parsing, PDF reading and layout. Starts the
    /// <see cref="Timeout"/>; layout also stops with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is canceled. Scopes nest; the innermost one applies.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The scope; dispose it to restore the limits in effect before.</returns>
    public IDisposable Enforce(CancellationToken cancellationToken = default)
    {
        Validate();
        var scope = new LimitScope(this, CurrentScopeLocal.Value, cancellationToken);
        CurrentScopeLocal.Value = scope;
        return scope;
    }

    /// <summary>Makes <paramref name="scope"/> the current scope.</summary>
    internal static void SetScope(LimitScope? scope) => CurrentScopeLocal.Value = scope;

    /// <summary>
    /// Throws when the work in the current scope has been canceled or has run out of time. Called regularly by
    /// long-running loops (layout, pagination, line breaking).
    /// </summary>
    internal static void Checkpoint() => CurrentScopeLocal.Value?.Checkpoint();

    /// <summary>Throws if an image of <paramref name="width"/> by <paramref name="height"/> pixels exceeds <see cref="MaxImagePixels"/>.</summary>
    internal static void CheckImagePixels(long width, long height)
    {
        var limit = Current.MaxImagePixels;
        if (width * height > limit)
        {
            throw new ResourceLimitExceededException(nameof(MaxImagePixels), limit, $"The image has {width} x {height} pixels, more than the limit of {limit}.");
        }
    }

    /// <summary>Throws if a font of <paramref name="bytes"/> exceeds <see cref="MaxFontBytes"/>.</summary>
    internal static void CheckFontBytes(long bytes)
    {
        var limit = Current.MaxFontBytes;
        if (bytes > limit)
        {
            throw new ResourceLimitExceededException(nameof(MaxFontBytes), limit, $"The font decompresses to {bytes} bytes, more than the limit of {limit}.");
        }
    }

    /// <summary>Throws if <paramref name="bytes"/> of decoded data exceed <see cref="MaxDecompressedBytes"/>.</summary>
    internal static void CheckDecompressedBytes(long bytes)
    {
        var limit = Current.MaxDecompressedBytes;
        if (bytes > limit)
        {
            throw new ResourceLimitExceededException(nameof(MaxDecompressedBytes), limit, $"The data decompresses to more than the limit of {limit} bytes.");
        }
    }

    /// <summary>
    /// How many nesting levels the stack is checked from (see <see cref="EnterNesting"/>). Below it no content can come
    /// near the end of a thread's stack, so the check, which can be wrong, is not consulted at all.
    /// </summary>
    internal const int StackCheckDepth = 64;

    /// <summary>The nesting depth of the recursive layout on this thread (see <see cref="EnterNesting"/>).</summary>
    [ThreadStatic]
    private static int t_nesting;

    /// <summary>
    /// Replaces the runtime's stack check, for tests: on this thread and the work it starts (the HTML conversion thread).
    /// </summary>
    internal static readonly AsyncLocal<Func<bool>?> StackProbe = new();

    /// <summary>
    /// Enters one level of recursive layout, whose depth follows the input's nesting; dispose the result to leave it.
    /// Deeper than <see cref="StackCheckDepth"/> levels, throws the <see cref="MaxNestingDepth"/> exception when the
    /// stack is too deep to go on safely (a stack overflow cannot be caught and ends the process).
    /// </summary>
    /// <remarks>
    /// The runtime's check (<see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/>) is only consulted past that
    /// depth: on the main thread of musl-based Linux (Alpine) it can report a nearly full stack for shallow content, as
    /// musl reports only the part of the main thread's stack that is mapped so far.
    /// </remarks>
    internal static NestingLevel EnterNesting()
    {
        var depth = ++t_nesting;
        if (depth > StackCheckDepth && !(StackProbe.Value ?? RuntimeHelpers.TryEnsureSufficientExecutionStack)())
        {
            t_nesting--;
            throw new ResourceLimitExceededException(nameof(MaxNestingDepth), Current.MaxNestingDepth, "The content is nested too deeply for the stack space available.");
        }

        return default;
    }

    /// <summary>The current nesting depth on this thread, for tests.</summary>
    internal static int NestingDepth => t_nesting;

    /// <summary>One level of recursive layout (see <see cref="EnterNesting"/>); disposing it leaves the level.</summary>
    internal readonly struct NestingLevel : IDisposable
    {
        public void Dispose() => t_nesting--;
    }

    private void Validate()
    {
        if (MaxNestingDepth < 1 || MaxElements < 1 || MaxTableCells < 1 || MaxImagePixels < 1 || MaxResourceBytes < 1
            || MaxFontBytes < 1 || MaxDecompressedBytes < 1 || MaxPages < 1)
        {
            throw new ArgumentException("Resource limits must be positive.");
        }

        if (Timeout <= TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException("The timeout must be positive or infinite.");
        }
    }
}

/// <summary>An <see cref="ResourceLimits.Enforce"/> scope: the limits, the cancellation token and the deadline.</summary>
internal sealed class LimitScope : IDisposable
{
    private readonly LimitScope? _previous;
    private readonly long _deadline;
    private bool _disposed;

    public LimitScope(ResourceLimits limits, LimitScope? previous, CancellationToken cancellationToken)
    {
        Limits = limits;
        _previous = previous;
        CancellationToken = cancellationToken;
        _deadline = limits.Timeout == System.Threading.Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : Stopwatch.GetTimestamp() + (long)Math.Min(limits.Timeout.TotalSeconds * Stopwatch.Frequency, long.MaxValue / 2);
    }

    public ResourceLimits Limits { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>How long is left until the deadline; <see cref="Timeout.InfiniteTimeSpan"/> if there is none.</summary>
    public TimeSpan Remaining => _deadline == long.MaxValue
        ? Timeout.InfiniteTimeSpan
        : TimeSpan.FromSeconds(Math.Max(0, _deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

    /// <summary>Throws <see cref="OperationCanceledException"/> when canceled, or the timeout's exception when past the deadline.</summary>
    public void Checkpoint()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetTimestamp() > _deadline)
        {
            throw TimeoutExceeded();
        }
    }

    /// <summary>The exception for running out of time.</summary>
    public ResourceLimitExceededException TimeoutExceeded() =>
        new(nameof(ResourceLimits.Timeout), (long)Limits.Timeout.TotalMilliseconds, $"The work took longer than the limit of {Limits.Timeout}.");

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Only while this scope is still the current one, so disposing scopes out of order does no harm.
            if (ReferenceEquals(ResourceLimits.CurrentScope, this))
            {
                ResourceLimits.SetScope(_previous);
            }
        }
    }
}
