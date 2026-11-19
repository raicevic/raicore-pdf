using RaiCore.Pdf.Email.Mime;

namespace RaiCore.Pdf.Email;

/// <summary>What a body is made of: HTML, plain text or an image shown inline.</summary>
internal enum BodyKind
{
    Html,
    Text,
    Image,
}

/// <summary>
/// A message read as a mail client shows it: the parts that make up its body, in order; its attachments; and the parts
/// its HTML can refer to (by <c>cid:</c> or by <c>Content-Location</c>).
/// </summary>
internal sealed class MessageContent
{
    public MessageContent(MimeEntity root) => Root = root;

    /// <summary>The message (its top-level part, with the message's header fields).</summary>
    public MimeEntity Root { get; }

    /// <summary>The parts shown as the body, in order.</summary>
    public List<(BodyKind Kind, MimeEntity Part)> Body { get; } = [];

    /// <summary>The attached files, in the order of the message.</summary>
    public List<MimeEntity> Attachments { get; } = [];

    /// <summary>
    /// Whether the message is a web archive (MHTML) rather than an email: it has no recipients, and it is a
    /// <c>multipart/related</c> page, names its page (<c>Snapshot-Content-Location</c>) or says it was saved by a browser.
    /// </summary>
    public bool IsWebArchive { get; set; }
}

/// <summary>
/// Reads a parsed message the way mail clients show it.
/// <list type="bullet">
/// <item><c>multipart/alternative</c>: the last HTML version, else the last plain text one. Other versions are left
/// out, except those that are no text (a calendar invitation, say), which become attachments.</item>
/// <item><c>multipart/related</c>: the root part (the <c>start</c> parameter, else the first part) is the body; the
/// others are what it refers to. One of those is an attachment if it says so (<c>Content-Disposition:
/// attachment</c>), or if it has a file name and the body does not refer to it (except in web archives).</item>
/// <item><c>multipart/mixed</c> and every other multipart: each part in turn. Text and HTML parts without a file name
/// are body; images marked <c>inline</c> are shown where they are (and are attachments too if they have a file name);
/// everything else, attached messages included, is an attachment.</item>
/// </list>
/// </summary>
internal sealed class MessageReader
{
    private readonly MessageContent _content;
    private readonly List<(MimeEntity Part, bool UnlessReferenced)> _attachments = [];

    private MessageReader(MimeEntity root) => _content = new(root);

    public static MessageContent Read(MimeEntity root)
    {
        var reader = new MessageReader(root);
        var content = reader._content;
        content.IsWebArchive = IsWebArchive(root);
        reader.Walk(root);

        // A related part with a file name that the body does not refer to is an attachment: only now is the body known.
        var html = string.Concat(content.Body.Where(b => b.Kind == BodyKind.Html).Select(b => b.Part.Text));
        foreach (var (part, unlessReferenced) in reader._attachments)
        {
            if (!unlessReferenced || (!content.IsWebArchive && part.FileName is not null && !IsReferenced(part, html)))
            {
                content.Attachments.Add(part);
            }
        }

        return content;
    }

    /// <summary>All the leaf parts of a message, outside attached messages, in order.</summary>
    public static IEnumerable<MimeEntity> Leaves(MimeEntity entity)
    {
        var stack = new Stack<MimeEntity>();
        stack.Push(entity);
        while (stack.Count > 0)
        {
            var part = stack.Pop();
            if (part.Children.Count == 0)
            {
                yield return part;
                continue;
            }

            for (var i = part.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(part.Children[i]);
            }
        }
    }

    private static bool IsWebArchive(MimeEntity root) =>
        root.Header("To") is null && root.Header("Cc") is null && root.Header("Bcc") is null
        && (root.ContentType.MediaType == "multipart/related"
            || root.Header("Snapshot-Content-Location") is not null
            || (root.Header("From") ?? string.Empty).Contains("Saved by ", StringComparison.OrdinalIgnoreCase));

    private static bool IsReferenced(MimeEntity part, string html) =>
        (part.ContentId is { Length: > 0 } id && html.Contains("cid:" + id, StringComparison.OrdinalIgnoreCase))
        || (part.ContentLocation is { Length: > 0 } location
            && html.Contains(location.Split('/', '\\')[^1] is { Length: > 0 } name ? name : location, StringComparison.OrdinalIgnoreCase));

    private static bool IsMultipart(MimeEntity part) => part.ContentType.IsMultipart && part.Children.Count > 0;

    private void Walk(MimeEntity part)
    {
        if (!IsMultipart(part))
        {
            WalkPart(part);
            return;
        }

        switch (part.ContentType.MediaType)
        {
            case "multipart/alternative":
                WalkAlternative(part);
                break;
            case "multipart/related":
                WalkRelated(part);
                break;
            default:
                foreach (var child in part.Children)
                {
                    WalkPart(child);
                }

                break;
        }
    }

    /// <summary>A part of a <c>multipart/mixed</c> (or of an unknown multipart, or the message itself).</summary>
    private void WalkPart(MimeEntity part)
    {
        var type = part.ContentType;
        if (IsMultipart(part))
        {
            Walk(part);
        }
        else if (part.IsAttachment || type.MediaType is "message/rfc822" or "message/global")
        {
            AddAttachment(part);
        }
        else if ((type.MediaType is "text/html" or "text/plain" || type.IsMultipart) && part.FileName is null)
        {
            AddBody(part); // A multipart without parts (no boundary) shows as text, as mail clients do.
        }
        else if (type.Type == "image" && part.IsInline)
        {
            _content.Body.Add((BodyKind.Image, part));
            if (part.FileName is not null)
            {
                AddAttachment(part);
            }
        }
        else
        {
            AddAttachment(part);
        }
    }

    private void WalkAlternative(MimeEntity part)
    {
        MimeEntity? best = null;
        var bestScore = 0;
        foreach (var child in part.Children)
        {
            var score = Score(child);
            if (score > 0 && score >= bestScore)
            {
                best = child;
                bestScore = score;
            }
        }

        if (best is not null)
        {
            if (IsMultipart(best))
            {
                Walk(best);
            }
            else
            {
                AddBody(best);
            }
        }

        foreach (var child in part.Children)
        {
            if (child != best && !IsMultipart(child) && (child.ContentType.Type != "text" || child.Is("text/calendar")))
            {
                AddAttachment(child); // A calendar invitation, say: data the text versions only describe.
            }
        }
    }

    private void WalkRelated(MimeEntity part)
    {
        var start = part.ContentType.Parameter("start") is { Length: > 0 } id ? MimeHeaders.Unbracket(id) : null;
        var root = (start is null ? null : part.Children.Find(c => c.ContentId == start)) ?? part.Children[0];
        if (IsMultipart(root))
        {
            Walk(root);
        }
        else if (root.ContentType.Type == "text" || root.ContentType.IsMultipart)
        {
            AddBody(root);
        }
        else
        {
            WalkPart(root);
        }

        foreach (var child in part.Children)
        {
            if (child == root)
            {
                continue;
            }

            if (IsMultipart(child))
            {
                Walk(child);
            }
            else if (child.IsAttachment || child.ContentType.MediaType is "message/rfc822" or "message/global")
            {
                AddAttachment(child);
            }
            else
            {
                _attachments.Add((child, true));
            }
        }
    }

    /// <summary>How good a version of an alternative is: 2 for HTML (or a multipart with HTML), 1 for plain text, 0 for anything else.</summary>
    private static int Score(MimeEntity part)
    {
        if (!IsMultipart(part))
        {
            return part.Is("text/html") ? 2 : part.Is("text/plain") ? 1 : 0;
        }

        var score = 0;
        foreach (var leaf in Leaves(part))
        {
            if (!leaf.IsAttachment)
            {
                score = Math.Max(score, Score(leaf));
            }
        }

        return score;
    }

    private void AddBody(MimeEntity part) => _content.Body.Add((part.Is("text/html") ? BodyKind.Html : BodyKind.Text, part));

    private void AddAttachment(MimeEntity part) => _attachments.Add((part, false));
}
