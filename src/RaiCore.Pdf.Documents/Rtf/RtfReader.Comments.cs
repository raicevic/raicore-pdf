using System.Globalization;
using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>
/// Comments (annotations): <c>{\*\atnid}</c> and <c>{\*\atnauthor}</c> before <c>{\*\annotation}</c> give the next
/// comment's initials and author; in it, <c>{\*\atnref}</c> names the text it is about (between
/// <c>{\*\atrfstart}</c> and <c>{\*\atrfend}</c> with that tag), <c>{\*\atndate}</c> its date and
/// <c>{\*\atnparent}</c> the comment it answers (by its tag).
/// </summary>
internal sealed partial class RtfReader
{
    /// <summary>The most comment ranges kept (each start and end inline waits for the comment that names it).</summary>
    private const int MaxCommentRanges = 65536;

    /// <summary>A comment detail's destination: its control word and text.</summary>
    private sealed class AnnotationPart(string word)
    {
        public string Word { get; } = word;

        public StringBuilder Text { get; } = new();
    }

    /// <summary>Uses a comment detail when its group ends.</summary>
    private void EndAnnotationPart(AnnotationPart part, Group? parent)
    {
        var text = Clean(part.Text.ToString().Trim());
        switch (part.Word)
        {
            case "atnid":
                _commentInitials = text.Length > 0 ? text : null;
                break;
            case "atnauthor":
                _commentAuthor = text.Length > 0 ? text : null;
                break;
            case "atrfstart" or "atrfend" when parent?.Destination == RtfDestination.Text && text.Length > 0:
                if (!_commentRanges.TryGetValue(text, out var range))
                {
                    if (_commentRanges.Count >= MaxCommentRanges)
                    {
                        break;
                    }

                    range = [];
                    _commentRanges[text] = range;
                }

                // The comment's id is set when a comment names the tag (here, if one already has).
                var id = _commentTags.GetValueOrDefault(text) ?? string.Empty;
                WordInline inline = part.Word == "atrfstart" ? new CommentStart(id) : new CommentEnd(id);
                if (range.Count < 4)
                {
                    range.Add(inline);
                    AddInline(inline);
                }

                break;
            case "atnref":
                if (CurrentComment() is (var commentId, _) && text.Length > 0 && _commentTags.TryAdd(text, commentId)
                    && _commentRanges.TryGetValue(text, out var inlines))
                {
                    foreach (var marker in inlines)
                    {
                        switch (marker)
                        {
                            case CommentStart start:
                                start.Id = commentId;
                                break;
                            case CommentEnd end:
                                end.Id = commentId;
                                break;
                        }
                    }
                }

                break;
            case "atndate":
                if (CurrentComment() is (_, var dated) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                {
                    dated.Date = DateTimeOfDttm(unchecked((uint)value));
                }

                break;
            case "atnparent":
                if (CurrentComment() is (_, var reply) && text.Length > 0 && _replies.Count < MaxCommentRanges)
                {
                    _replies.Add((reply, text));
                }

                break;
        }
    }

    /// <summary>The comment whose text is being read, or <c>null</c>.</summary>
    private (string Id, WordComment Comment)? CurrentComment()
    {
        for (var i = _depth - 1; i >= 0; i--)
        {
            if (_groups[i].Story is { Kind: StoryKind.Comment } && _groups[i].Data is string id && _document.Comments.TryGetValue(id, out var comment))
            {
                return (id, comment);
            }
        }

        return null;
    }

    /// <summary>Links replies to the comments they answer (named by their tags), once all comments are read.</summary>
    private void ResolveReplies()
    {
        foreach (var (comment, tag) in _replies)
        {
            if (_commentTags.TryGetValue(tag, out var parent) && !ReferenceEquals(_document.Comments[parent], comment))
            {
                comment.ParentId = parent;
            }
        }
    }

    /// <summary>
    /// A date and time stored as a DTTM ([MS-DOC] 2.9.66: minutes, hours, day, month, years since 1900, day of the week),
    /// or <c>null</c> when it is not a valid date (zero: no date).
    /// </summary>
    internal static DateTime? DateTimeOfDttm(uint dttm)
    {
        var minute = (int)(dttm & 0x3F);
        var hour = (int)((dttm >> 6) & 0x1F);
        var day = (int)((dttm >> 11) & 0x1F);
        var month = (int)((dttm >> 16) & 0xF);
        var year = 1900 + (int)((dttm >> 20) & 0x1FF);
        return dttm != 0 && minute < 60 && hour < 24 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified)
            : null;
    }
}
