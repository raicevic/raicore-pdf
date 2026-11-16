using System.Text;
using AngleSharp.Dom;

namespace RaiCore.Pdf.Html.Diagnostics;

/// <summary>
/// Collects the diagnostics of one conversion: each distinct issue (kind and subject) once, with the path of the first
/// element it occurred at and how many elements it concerns, delivered in the order they first occurred. Without a
/// callback it does nothing. Not thread-safe: a conversion runs on one thread.
/// </summary>
internal sealed class DiagnosticCollector(Action<HtmlDiagnostic>? callback)
{
    private readonly Dictionary<(HtmlDiagnosticKind, string), Entry> _entries = [];
    private readonly List<Entry> _order = [];

    /// <summary>A collector that reports nothing.</summary>
    public static DiagnosticCollector None { get; } = new(null);

    /// <summary>Whether anything is collected (there is a callback).</summary>
    public bool IsEnabled => callback is not null;

    /// <summary>
    /// Records an issue. The same kind and subject again only counts the element (an element seen before, as when a
    /// document is laid out more than once, is not counted twice).
    /// </summary>
    public void Report(HtmlDiagnosticKind kind, HtmlDiagnosticSeverity severity, string subject, string message, IElement? element = null)
    {
        if (callback is null)
        {
            return;
        }

        if (!_entries.TryGetValue((kind, subject), out var entry))
        {
            entry = new Entry(kind, severity, subject, message, element is null ? null : ElementPath(element));
            _entries[(kind, subject)] = entry;
            _order.Add(entry);
        }

        if (element is not null)
        {
            entry.Elements.Add(element);
        }

        if (_live)
        {
            Deliver();
        }
    }

    /// <summary>Whether issues are delivered as they are reported (after <see cref="DeliverAndGoLive"/>).</summary>
    private bool _live;

    /// <summary>
    /// Delivers what was collected, then delivers each later issue as soon as it is reported (for a laid-out document
    /// that is rendered later, outside the conversion).
    /// </summary>
    public void DeliverAndGoLive()
    {
        Deliver();
        _live = true;
    }

    /// <summary>Calls the callback for each issue, in the order they first occurred, and forgets them.</summary>
    public void Deliver()
    {
        if (callback is null)
        {
            return;
        }

        var entries = _order.ToList();
        _order.Clear();
        _entries.Clear();
        foreach (var entry in entries)
        {
            callback(new HtmlDiagnostic(entry.Kind, entry.Severity, entry.Subject, entry.Message, entry.Path, Math.Max(1, entry.Elements.Count)));
        }
    }

    /// <summary>
    /// A CSS-like path to an element: each ancestor's tag, with its id, up to two classes and, among siblings of the
    /// same tag, its position (<c>html &gt; body &gt; div.card:nth-of-type(2) &gt; img#logo</c>).
    /// </summary>
    internal static string ElementPath(IElement element)
    {
        var parts = new List<string>();
        for (var current = element; current is not null; current = current.ParentElement)
        {
            var part = new StringBuilder(current.LocalName);
            if (current.Id is { Length: > 0 } id)
            {
                part.Append('#').Append(id);
            }

            foreach (var name in current.ClassList.Take(2))
            {
                part.Append('.').Append(name);
            }

            if (current.ParentElement is { } parent && current.Id is not { Length: > 0 })
            {
                var sameTag = parent.Children.Where(c => c.LocalName == current.LocalName).ToList();
                if (sameTag.Count > 1)
                {
                    part.Append(":nth-of-type(").Append(sameTag.IndexOf(current) + 1).Append(')');
                }
            }

            parts.Add(part.ToString());
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private sealed class Entry(HtmlDiagnosticKind kind, HtmlDiagnosticSeverity severity, string subject, string message, string? path)
    {
        public HtmlDiagnosticKind Kind => kind;

        public HtmlDiagnosticSeverity Severity => severity;

        public string Subject => subject;

        public string Message => message;

        public string? Path => path;

        public HashSet<IElement> Elements { get; } = new(ReferenceEqualityComparer.Instance);
    }
}
