using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// Comments ([MS-DOC] 2.9.8 ATRDPre10 in PlcfandRef): each comment's author (an index into GrpXstAtnOwners), initials
/// and the tag of the bookmark that marks the text it is about (SttbfAtnBkmk with PlcfAtnBkf and PlcfAtnBkl); Word 2002
/// and later add its date and the comment it answers (ATRDPost10 in AtrdExtra).
/// </summary>
internal sealed partial class DocReader
{
    /// <summary>The size of an ATRDPre10 record.</summary>
    private const int AtrdSize = 30;

    /// <summary>The size of an ATRDPost10 record.</summary>
    private const int AtrdExtraSize = 18;

    /// <summary>The comments whose text starts at a character position.</summary>
    private readonly Dictionary<int, List<string>> _commentStarts = [];

    /// <summary>The comments whose text ends before a character position.</summary>
    private readonly Dictionary<int, List<string>> _commentEnds = [];

    /// <summary>Reads the comments' details and ranges (their text is read with the other stories).</summary>
    private void ReadComments()
    {
        var plc = _file.TableData(4);
        var count = plc.Length >= 4 ? (plc.Length - 4) / (4 + AtrdSize) : 0;
        if (count == 0)
        {
            return;
        }

        var owners = ReadOwners(_file.TableData(36));
        var ranges = ReadCommentRanges();
        var extra = _file.TableData(112);
        var data = plc[(4 * (count + 1))..];
        for (var i = 0; i < count; i++)
        {
            var atrd = data.Slice(i * AtrdSize, AtrdSize);
            var id = "c" + i.ToString(CultureInfo.InvariantCulture);
            var initialsLength = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(atrd), 9);
            var initials = Encoding.Unicode.GetString(atrd.Slice(2, 2 * initialsLength)).Trim('\0', ' ');
            var owner = BinaryPrimitives.ReadUInt16LittleEndian(atrd[20..]);
            var comment = new WordComment
            {
                Initials = initials.Length > 0 ? DocFile.WithoutLoneSurrogates(initials) : null,
                Author = owner < owners.Count && owners[owner].Length > 0 ? owners[owner] : null,
            };

            if (extra.Length >= (i + 1) * AtrdExtraSize)
            {
                var post = extra.Slice(i * AtrdExtraSize, AtrdExtraSize);
                comment.Date = Rtf.RtfReader.DateTimeOfDttm(BinaryPrimitives.ReadUInt32LittleEndian(post));
                var depth = BinaryPrimitives.ReadInt32LittleEndian(post[6..]);
                var parent = i + (long)BinaryPrimitives.ReadInt32LittleEndian(post[10..]);
                if (depth > 0 && parent >= 0 && parent < count && parent != i)
                {
                    comment.ParentId = "c" + parent.ToString(CultureInfo.InvariantCulture);
                }
            }

            _document.Comments[id] = comment;
            var tag = BinaryPrimitives.ReadInt32LittleEndian(atrd[26..]);
            if (tag != -1 && ranges.TryGetValue(tag, out var range))
            {
                Add(_commentStarts, range.Start, id);
                Add(_commentEnds, range.End, id);
            }
        }

        static void Add(Dictionary<int, List<string>> positions, int cp, string id)
        {
            if (!positions.TryGetValue(cp, out var ids))
            {
                ids = [];
                positions[cp] = ids;
            }

            ids.Add(id);
        }
    }

    /// <summary>The authors of comments (GrpXstAtnOwners: strings, each its length and its UTF-16 characters).</summary>
    internal static List<string> ReadOwners(ReadOnlySpan<byte> data)
    {
        var owners = new List<string>();
        var position = 0;
        while (position + 2 <= data.Length && owners.Count < ushort.MaxValue)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
            position += 2;
            if (2 * length > data.Length - position)
            {
                break;
            }

            owners.Add(DocFile.WithoutLoneSurrogates(Encoding.Unicode.GetString(data.Slice(position, 2 * length)).Trim('\0', ' ')));
            position += 2 * length;
        }

        return owners;
    }

    /// <summary>
    /// The text the comments are about, by their bookmarks' tags: the tags in SttbfAtnBkmk (each entry's ATNBE), the
    /// starts in PlcfAtnBkf (with the index of their end) and the ends in PlcfAtnBkl.
    /// </summary>
    private Dictionary<int, (int Start, int End)> ReadCommentRanges()
    {
        var ranges = new Dictionary<int, (int, int)>();
        var sttb = _file.TableData(37);
        if (sttb.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(sttb) != 0xFFFF)
        {
            return ranges;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(sttb[2..]);
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(sttb[4..]);
        var starts = Positions(_file.TableData(42), 4, out var startCount);
        var bkf = _file.TableData(42);
        var ends = Positions(_file.TableData(43), 0, out var endCount);
        var position = 6;
        for (var i = 0; i < count && i < startCount && position + 2 <= sttb.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(sttb[position..]);
            position += 2 + (2 * length);
            if (extra < 6 || position + extra > sttb.Length)
            {
                break;
            }

            var tag = BinaryPrimitives.ReadInt32LittleEndian(sttb[(position + 2)..]);
            position += extra;
            var end = BinaryPrimitives.ReadUInt16LittleEndian(bkf[((4 * (startCount + 1)) + (4 * i))..]);
            if (end < endCount && ends[end] >= starts[i])
            {
                ranges.TryAdd(tag, (starts[i], ends[end]));
            }
        }

        return ranges;
    }

    /// <summary>Marks where comments' text starts and ends at a character position of the main text.</summary>
    private void CommentRanges(Story story, int cp)
    {
        if (story.Kind != DocStory.Main || (_commentStarts.Count == 0 && _commentEnds.Count == 0))
        {
            return;
        }

        if (_commentEnds.TryGetValue(cp, out var ended))
        {
            foreach (var id in ended)
            {
                AddInline(story, new CommentEnd(id));
            }
        }

        if (_commentStarts.TryGetValue(cp, out var started))
        {
            foreach (var id in started)
            {
                AddInline(story, new CommentStart(id));
            }
        }
    }
}
