using System.Globalization;
using System.Text;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Text;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>
/// Comments (<see cref="DocumentConverterOptions.Comments"/>): the text they are about highlighted, and either a numbered
/// mark at each reference with the comments listed after the document (<see cref="DocumentComments.AtEnd"/>), or a PDF
/// note where each comment's text starts (<see cref="DocumentComments.Annotations"/>). Replies go with the comment they
/// answer. Comments are numbered from the body (and its notes and text boxes) in the order of their references;
/// headers and footers show none.
/// </summary>
internal sealed partial class DocumentRenderer
{
    /// <summary>The most characters of the text a comment is about that its entry quotes.</summary>
    private const int MaxQuotedText = 80;

    /// <summary>The size of a note's icon, in points.</summary>
    private const double NoteSize = 10;

    /// <summary>The prefix of the destinations that place notes (removed from the PDF once the notes are placed).</summary>
    private const string NoteAnchorPrefix = "_comment:";

    /// <summary>The background of the text comments are about (a pale yellow, as word processors show it).</summary>
    private static readonly PdfColor CommentHighlight = PdfColor.FromRgb(255, 242, 171);

    // Per layout pass.
    private readonly List<string> _commentOrder = [];
    private readonly Dictionary<string, int> _commentNumbers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openComments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _commentText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _noteAnchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _anchoredComments = new(StringComparer.Ordinal);
    private RunFormat? _lastRunFormat;
    private bool _inCommentText;

    /// <summary>Whether comments are shown (and so read).</summary>
    private bool ShowsComments => _options.Comments is DocumentComments.AtEnd or DocumentComments.Annotations;

    /// <summary>Whether comment marks, ranges and anchors are handled where the renderer is: the body, its notes and text boxes.</summary>
    private bool InCommentedText => ShowsComments && _page is null && !_inCommentText;

    private void ResetComments()
    {
        _commentOrder.Clear();
        _commentNumbers.Clear();
        _openComments.Clear();
        _commentText.Clear();
        _noteAnchors.Clear();
        _anchoredComments.Clear();
        _lastRunFormat = null;
    }

    /// <summary>
    /// The comment a reply belongs under: the comment it answers (or that one's, for a reply to a reply), or the comment
    /// itself when it answers none that the document has.
    /// </summary>
    private string RootComment(string id)
    {
        var current = id;
        for (var i = 0; i < 16 && _document.Comments.TryGetValue(current, out var comment) && comment.ParentId is { } parent
            && parent != id && _document.Comments.ContainsKey(parent); i++)
        {
            current = parent;
        }

        return current;
    }

    /// <summary>
    /// Handles a comment's inline in <see cref="Flatten"/>: a range's start (a note's anchor) or end, or a reference (a
    /// numbered mark, or a note's anchor when the comment has no range). Returns whether the inline was one.
    /// </summary>
    private bool FlattenComment(WordInline inline, LinkTarget? link, List<Atom> atoms)
    {
        switch (inline)
        {
            case CommentStart start:
                if (InCommentedText && _document.Comments.ContainsKey(start.Id))
                {
                    _openComments.Add(start.Id);
                    AnchorNote(start.Id, start, link, atoms);
                }

                return true;
            case CommentEnd end:
                _openComments.Remove(end.Id);
                return true;
            case CommentReference reference:
                if (InCommentedText && _document.Comments.ContainsKey(reference.Id))
                {
                    Referenced(reference.Id, link, atoms);
                }

                return true;
            case TextRun run when _openComments.Count > 0 && InCommentedText:
                foreach (var id in _openComments)
                {
                    if (!_commentText.TryGetValue(id, out var text))
                    {
                        text = new StringBuilder();
                        _commentText[id] = text;
                    }

                    if (text.Length <= MaxQuotedText)
                    {
                        text.Append(run.Text);
                    }
                }

                _lastRunFormat = run.Format;
                atoms.Add(new Atom(run.Format.Background is null ? new TextRun(run.Text, run.Format with { Background = CommentHighlight }) : run, link));
                return true;
            case TextRun run:
                _lastRunFormat = run.Format;
                return false;
            default:
                return false;
        }
    }

    /// <summary>A comment's first reference: its number, and its mark or its note's anchor.</summary>
    private void Referenced(string id, LinkTarget? link, List<Atom> atoms)
    {
        var root = RootComment(id);
        if (root != id)
        {
            // A reply is shown with the comment it answers; that one is anchored here if it has not been yet.
            if (!_commentNumbers.ContainsKey(root))
            {
                Referenced(root, link, atoms);
            }

            return;
        }

        if (_commentNumbers.ContainsKey(id))
        {
            return; // Marked (or anchored) once, at its first reference.
        }

        var number = _commentOrder.Count + 1;
        _commentNumbers[id] = number;
        _commentOrder.Add(id);

        if (_options.Comments == DocumentComments.Annotations)
        {
            AnchorNote(id, null, link, atoms);
            return;
        }

        var format = (_lastRunFormat ?? RunFormat.Default) with
        {
            Position = VerticalPosition.Superscript,
            Background = null,
            Underline = UnderlineStyle.None,
            Strike = false,
            Hidden = false,
            Deleted = false,
        };
        atoms.Add(new Atom(new TextRun(CommentLabel(id, number), format), LinkTarget.ToDestination(CommentDestination(number))));
    }

    /// <summary>Ends a paragraph in the text of the comments whose ranges are open: their quotes run on with a space.</summary>
    private void EndCommentedParagraph()
    {
        foreach (var id in _openComments)
        {
            if (_commentText.TryGetValue(id, out var text) && text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
            }
        }
    }

    /// <summary>Where a comment's note goes: the start of the text it is about, or its reference (once per comment).</summary>
    private void AnchorNote(string id, CommentStart? start, LinkTarget? link, List<Atom> atoms)
    {
        if (_options.Comments != DocumentComments.Annotations)
        {
            return;
        }

        var root = RootComment(id);
        if (!_anchoredComments.Add(root))
        {
            return;
        }

        var name = NoteAnchorPrefix + _noteAnchors.Count.ToString(CultureInfo.InvariantCulture);
        _noteAnchors[name] = root;
        atoms.Add(new Atom(start ?? new CommentStart(root), link, name));
    }

    /// <summary>A comment's label: its author's initials (if known) and its number in brackets, as Word prints it.</summary>
    private string CommentLabel(string id, int number) =>
        "[" + (_document.Comments[id].Initials ?? string.Empty) + number.ToString(CultureInfo.InvariantCulture) + "]";

    private static string CommentDestination(int number) => "_cm" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The replies to a comment (and to its replies), in the document's order.</summary>
    private List<(string Id, WordComment Comment)> Replies(string id) =>
        [.. _document.Comments.Where(c => c.Key != id && c.Value.ParentId is not null && RootComment(c.Key) == id).Select(c => (c.Key, c.Value))];

    /// <summary>
    /// The comments listed after the document (<see cref="DocumentComments.AtEnd"/>): on a page of their own under a
    /// heading, each with its label, author and date, the text it is about, its text and its replies.
    /// </summary>
    private List<Block> CommentsAtEnd(double width)
    {
        var blocks = new List<Block>();
        if (_options.Comments != DocumentComments.AtEnd || _commentOrder.Count == 0)
        {
            return blocks;
        }

        var heading = new WordParagraph
        {
            Format = ParagraphFormat.Default with { PageBreakBefore = true, OutlineLevel = 0, SpaceAfter = 12, KeepWithNext = true },
            MarkFormat = RunFormat.Default with { FontSize = 14, Bold = true },
        };
        heading.Inlines.Add(new TextRun("Comments", heading.MarkFormat));
        blocks.AddRange(RenderBlocks([heading], width, inTable: false));

        _inCommentText = true;
        _depth++;
        try
        {
            foreach (var id in _commentOrder.ToList())
            {
                var comment = _document.Comments[id];
                var number = _commentNumbers[id];
                var entry = new ContainerBlock(new BoxStyle { Margin = new Thickness(0, 0, 10, 0) }) { Id = CommentDestination(number) };
                foreach (var block in CommentHeader(CommentLabel(id, number), comment, width, reply: false))
                {
                    entry.Add(block);
                }

                if (_commentText.TryGetValue(id, out var quoted) && Shortened(quoted.ToString()) is { Length: > 0 } about)
                {
                    var quote = new WordParagraph
                    {
                        Format = ParagraphFormat.Default with { IndentStart = 18, SpaceAfter = 3 },
                        MarkFormat = RunFormat.Default with { Italic = true, Color = PdfColor.FromRgb(89, 89, 89) },
                    };
                    quote.Inlines.Add(new TextRun("On “" + about + "”", quote.MarkFormat));
                    foreach (var block in RenderBlocks([quote], width, inTable: false))
                    {
                        entry.Add(block);
                    }
                }

                entry.Add(CommentBody(comment, width));
                foreach (var (_, reply) in Replies(id))
                {
                    foreach (var block in CommentHeader("Reply", reply, width, reply: true))
                    {
                        entry.Add(block);
                    }

                    entry.Add(CommentBody(reply, width));
                }

                blocks.Add(entry);
            }
        }
        finally
        {
            _depth--;
            _inCommentText = false;
        }

        return blocks;
    }

    /// <summary>A comment's header line: its label (or "Reply") in bold, its author and its date.</summary>
    private List<Block> CommentHeader(string label, WordComment comment, double width, bool reply)
    {
        var paragraph = new WordParagraph
        {
            Format = ParagraphFormat.Default with { IndentStart = reply ? 18 : 0, SpaceBefore = reply ? 4 : 0, SpaceAfter = 2, KeepWithNext = true },
            MarkFormat = RunFormat.Default,
        };
        paragraph.Inlines.Add(new TextRun(label, RunFormat.Default with { Bold = true }));
        if (comment.Author is { } author)
        {
            paragraph.Inlines.Add(new TextRun("  " + author, RunFormat.Default));
        }

        if (comment.Date is { } date)
        {
            paragraph.Inlines.Add(new TextRun("  " + date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), RunFormat.Default with { Color = PdfColor.FromRgb(89, 89, 89) }));
        }

        return RenderBlocks([paragraph], width, inTable: false);
    }

    /// <summary>A comment's own text, indented under its header.</summary>
    private ContainerBlock CommentBody(WordComment comment, double width)
    {
        var body = new ContainerBlock(new BoxStyle { Margin = new Thickness(0, 0, 0, 18) });
        foreach (var block in RenderBlocks(comment.Blocks, Math.Max(1, width - 18), inTable: false))
        {
            body.Add(block);
        }

        return body;
    }

    /// <summary>Text on one line, at most <see cref="MaxQuotedText"/> characters (cut at a word, with an ellipsis).</summary>
    internal static string Shortened(string text)
    {
        var line = string.Join(' ', text.Split((char[])[' ', '\t', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= MaxQuotedText)
        {
            return line;
        }

        var cut = line.LastIndexOf(' ', MaxQuotedText);
        cut = cut > MaxQuotedText / 2 ? cut : MaxQuotedText;
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return line[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// The notes of <see cref="DocumentComments.Annotations"/>, added as the layout places their anchors: at the start of
    /// the text each is about (its icon's top left corner there), its text and its replies' with their authors.
    /// </summary>
    private Action<string, PdfDestination> PlaceNotes()
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        return (name, destination) =>
        {
            if (!_noteAnchors.TryGetValue(name, out var id) || !placed.Add(id))
            {
                return;
            }

            var page = destination.Page;
            var x = Math.Clamp(destination.Left ?? 0, 0, Math.Max(0, page.Width - NoteSize));
            var y = Math.Clamp((destination.Top ?? page.Height) - NoteSize, 0, Math.Max(0, page.Height - NoteSize));
            var comment = _document.Comments[id];
            page.AddNote(x, y, NoteSize, NoteSize, NoteText(id, comment)).Author = comment.Author;
        };
    }

    /// <summary>A note's text: the comment's, then each reply's with its author.</summary>
    private string NoteText(string id, WordComment comment)
    {
        var text = new StringBuilder(PlainTextWriter.TextOf(comment.Blocks, _context));
        foreach (var (_, reply) in Replies(id))
        {
            text.Append("\n\nReply");
            if (reply.Author is { } author)
            {
                text.Append(" from ").Append(author);
            }

            text.Append(":\n").Append(PlainTextWriter.TextOf(reply.Blocks, _context));
        }

        return text.ToString();
    }

    /// <summary>Removes the destinations that only placed notes.</summary>
    private void RemoveNoteAnchors(PdfDocument pdf)
    {
        foreach (var name in _noteAnchors.Keys)
        {
            pdf.NamedDestinations.Remove(name);
        }
    }
}
