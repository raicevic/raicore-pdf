using System.Text;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// Reads the comments (notes) of a worksheet: in BIFF8 each NOTE record names its cell, author and the drawing object
/// (OBJ, of the note type) whose text (TXO) holds the comment's text ([MS-XLS] 2.4.179); in BIFF5 the NOTE record holds
/// the text itself, a long one going on in NOTE records of row 0xFFFF.
/// </summary>
internal sealed class XlsNotes(int biff, Encoding? codePage)
{
    /// <summary>The most comments a sheet keeps.</summary>
    private const int MaxNotes = 100_000;

    private const ushort Obj = 0x005D;
    private const ushort Txo = 0x01B6;
    private const ushort Note = 0x001C;

    private readonly Dictionary<int, string> _texts = [];
    private readonly List<(int Row, int Column, int Object, string Text, string? Author)> _notes = [];
    private int? _object;

    /// <summary>The comments read: their cell, text and author.</summary>
    public IEnumerable<(int Row, int Column, string Text, string? Author)> Comments => _notes
        .Select(n => (n.Row, n.Column, (biff == 8 ? _texts.GetValueOrDefault(n.Object, string.Empty) : n.Text).TrimEnd(), n.Author))
        .Where(n => n.Item3.Length > 0);

    /// <summary>The note objects of the comments shown on the sheet (BIFF8 NOTE records' fShow).</summary>
    public HashSet<int> Shown { get; } = [];

    /// <summary>Reads a record that belongs to a comment (OBJ, TXO, NOTE); others are ignored.</summary>
    public void Read(ushort id, ReadOnlySpan<byte> data, IReadOnlyList<int> breaks)
    {
        switch (id)
        {
            case Obj:
                // The object's common data (FtCmo) first: its type (0x19 for a note) and id.
                _object = data.Length >= 8 && BiffReader.U16(data, 0) == 0x15 && BiffReader.U16(data, 4) == 0x19 ? BiffReader.U16(data, 6) : null;
                break;
            case Txo when _object is { } owner && _texts.Count < MaxNotes:
                _texts[owner] = XlsDrawingRecords.ReadText(data, breaks).Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                _object = null;
                break;
            case Note when _notes.Count < MaxNotes && data.Length >= 6:
                if (biff == 8)
                {
                    if (data.Length >= 8)
                    {
                        var position = 8;
                        var author = BiffReader.ReadString(data, breaks, ref position, 2, null);
                        _notes.Add((BiffReader.U16(data, 0), BiffReader.U16(data, 2), BiffReader.U16(data, 6), string.Empty, string.IsNullOrWhiteSpace(author) ? null : author));
                        if ((BiffReader.U16(data, 4) & 0x0002) != 0)
                        {
                            Shown.Add(BiffReader.U16(data, 6));
                        }
                    }
                }
                else
                {
                    // BIFF5: the row, the column, the text's length and the text (in the code page); row 0xFFFF goes on with the last.
                    var length = Math.Min(BiffReader.U16(data, 4), data.Length - 6);
                    var text = (codePage ?? Encoding.Latin1).GetString(data.Slice(6, length)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                    if (BiffReader.U16(data, 0) == 0xFFFF)
                    {
                        if (_notes.Count > 0 && _notes[^1].Text.Length < 32_768)
                        {
                            _notes[^1] = _notes[^1] with { Text = _notes[^1].Text + text };
                        }
                    }
                    else
                    {
                        _notes.Add((BiffReader.U16(data, 0), BiffReader.U16(data, 2), 0, text, null));
                    }
                }

                break;
        }
    }
}
