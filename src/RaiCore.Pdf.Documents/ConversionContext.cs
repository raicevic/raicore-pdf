using System.Diagnostics;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// What a conversion shares between its reader and its renderer: the limits, a checkpoint for time and cancellation,
/// and the diagnostics (each distinct one reported once).
/// </summary>
internal sealed class ConversionContext
{
    private readonly Action<DocumentDiagnostic>? _diagnostics;
    private readonly HashSet<DocumentDiagnostic> _reported = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationToken _cancellationToken;

    public ConversionContext(ResourceLimits limits, Action<DocumentDiagnostic>? diagnostics, CancellationToken cancellationToken)
    {
        Limits = limits;
        _diagnostics = diagnostics;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The limits.</summary>
    public ResourceLimits Limits { get; }

    /// <summary>
    /// Throws when the conversion is canceled or has taken longer than <see cref="ResourceLimits.Timeout"/>. Called
    /// regularly outside the layout engine (which checks the limits itself).
    /// </summary>
    public void Checkpoint()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (Limits.Timeout != Timeout.InfiniteTimeSpan && _clock.Elapsed > Limits.Timeout)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.Timeout), (long)Limits.Timeout.TotalMilliseconds,
                $"The conversion took longer than ResourceLimits.Timeout ({Limits.Timeout}).");
        }
    }

    /// <summary>Reports an issue, once.</summary>
    public void Report(DocumentDiagnosticKind kind, string message)
    {
        if (_diagnostics is { } diagnostics && _reported.Add(new DocumentDiagnostic(kind, message)))
        {
            diagnostics(new DocumentDiagnostic(kind, message));
        }
    }
}
