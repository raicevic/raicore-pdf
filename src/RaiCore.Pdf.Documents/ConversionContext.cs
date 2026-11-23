using System.Diagnostics;

namespace RaiCore.Pdf.Documents;

/// <summary>What the readers keep in the model beyond what a conversion shows (see <see cref="ConversionContext.Extras"/>).</summary>
[Flags]
internal enum ReadExtras
{
    /// <summary>Nothing more.</summary>
    None = 0,

    /// <summary>Hidden text, as runs whose format is <see cref="Model.RunFormat.Hidden"/>.</summary>
    HiddenText = 1,

    /// <summary>Deleted text of tracked changes, as runs whose format is <see cref="Model.RunFormat.Deleted"/>.</summary>
    DeletedText = 2,

    /// <summary>Field instructions, as <see cref="Model.FieldStart"/> and <see cref="Model.FieldEnd"/> around each field's result.</summary>
    FieldCodes = 4,

    /// <summary>Comments (<see cref="Model.WordDocument.Comments"/>) and where they are (<see cref="Model.CommentReference"/>).</summary>
    Comments = 8,

    /// <summary>No images: their data is not read (text extraction has no use for it).</summary>
    NoImages = 16,
}

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
    /// What the readers keep in the model beyond what a conversion shows (for text extraction); none for conversions,
    /// whose renderer never sees these.
    /// </summary>
    public ReadExtras Extras { get; init; }

    /// <summary>Whether <see cref="Extras"/> has any of the given ones.</summary>
    public bool Has(ReadExtras extras) => (Extras & extras) != 0;

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
