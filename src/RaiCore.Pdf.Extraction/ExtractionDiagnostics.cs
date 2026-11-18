namespace RaiCore.Pdf.Extraction;

/// <summary>
/// Problems met while reading page content: things that were skipped or guessed, never thrown. Each message is kept
/// once, and at most <see cref="MaxMessages"/> are kept, so hostile content cannot grow the list without bound.
/// </summary>
internal sealed class ExtractionDiagnostics
{
    /// <summary>The most messages kept.</summary>
    internal const int MaxMessages = 100;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _messages = [];

    /// <summary>The messages, in the order they were first reported.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>Records a message, unless it was reported before or the list is full.</summary>
    public void Report(string message)
    {
        if (_messages.Count < MaxMessages && _seen.Add(message))
        {
            _messages.Add(message);
        }
    }
}
