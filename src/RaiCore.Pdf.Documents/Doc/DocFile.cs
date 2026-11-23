using System.Buffers.Binary;
using System.Text;
using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>The stories of a Word document, in the order their text follows each other ([MS-DOC] 2.3).</summary>
internal enum DocStory
{
    Main,
    Footnotes,
    Headers,
    Macros,
    Comments,
    Endnotes,
    TextBoxes,
    HeaderTextBoxes,
}

/// <summary>A piece of the document's text ([MS-DOC] 2.9.177 Pcd): a range of characters stored at a place in the WordDocument stream.</summary>
/// <param name="CpStart">The first character position.</param>
/// <param name="CpEnd">The character position after the last.</param>
/// <param name="Fc">The offset of the first character's bytes in the WordDocument stream.</param>
/// <param name="Compressed">Whether characters are one byte (code page 1252) rather than two (UTF-16).</param>
/// <param name="Prm">The property modifier of the piece (fast-saved documents).</param>
internal readonly record struct DocPiece(int CpStart, int CpEnd, int Fc, bool Compressed, ushort Prm)
{
    /// <summary>The offset of a character's bytes.</summary>
    public long FcOf(int cp) => Fc + ((long)(cp - CpStart) * (Compressed ? 1 : 2));
}

/// <summary>A run of a formatted disk page: a range of the WordDocument stream and its properties' bytes.</summary>
/// <param name="FcStart">The first byte the properties are for.</param>
/// <param name="FcEnd">The byte after the last.</param>
/// <param name="Offset">Where the properties are in the WordDocument stream (or -1 for none).</param>
/// <param name="Length">Their length.</param>
internal readonly record struct DocFkpRun(long FcStart, long FcEnd, int Offset, int Length);

/// <summary>
/// The binary structure of a Word 97-2003 document ([MS-DOC]): the compound file's WordDocument, table and data
/// streams, the file information block (FIB), the piece table with the document's text, and the formatted disk pages
/// of character and paragraph properties. Everything read from the file is checked against the stream it points into.
/// </summary>
internal sealed class DocFile
{
    /// <summary>The <c>wIdent</c> of a Word binary document.</summary>
    private const ushort WordIdent = 0xA5EC;

    /// <summary>The FIB version of Word 97; earlier versions (Word 6 and Word 95) are a different format.</summary>
    private const int Word97 = 0x00C1;

    private const int FkpSize = 512;

    private readonly ConversionContext _context;
    private readonly int[] _ccp = new int[8];
    private readonly uint[] _fcLcb;
    private readonly List<byte[]> _prcs = [];

    private DocFile(CompoundFile compound, ConversionContext context)
    {
        _context = context;
        var wordEntry = compound.Root["WordDocument"]
            ?? throw new PdfFormatException("The compound file has no WordDocument stream: it is not a Word 97-2003 document.");
        if (wordEntry.Kind != CfbEntryKind.Stream)
        {
            throw new PdfFormatException("The WordDocument entry is not a stream.");
        }

        Word = compound.Read(wordEntry);
        var fib = Word.AsSpan();
        var ident = fib.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(fib) : 0;
        if (ident != WordIdent && fib.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(fib[2..]) is >= 101 and < Word97)
        {
            throw new NotSupportedException(
                $"The document is in the Word 6 or Word 95 format (version {BinaryPrimitives.ReadUInt16LittleEndian(fib[2..])}), which is not supported; only Word 97-2003 documents are.");
        }

        if (fib.Length < 154 || ident != WordIdent)
        {
            throw new PdfFormatException("The WordDocument stream does not start with a Word file information block.");
        }

        Nfib = BinaryPrimitives.ReadUInt16LittleEndian(fib[2..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(fib[10..]);
        if ((flags & 0x0100) != 0)
        {
            throw new NotSupportedException("The Word document is password-protected (encrypted), which is not supported.");
        }

        if (Nfib < Word97)
        {
            throw new NotSupportedException(
                $"The document is in the Word 6 or Word 95 format (version {Nfib}), which is not supported; only Word 97-2003 documents are.");
        }

        // FibRgLw97: the character counts of the stories.
        var csw = BinaryPrimitives.ReadUInt16LittleEndian(fib[32..]);
        var lwStart = 34 + (2 * csw);
        if (lwStart + 2 > fib.Length)
        {
            throw new PdfFormatException("The file information block is truncated.");
        }

        var cslw = BinaryPrimitives.ReadUInt16LittleEndian(fib[lwStart..]);
        var lw = lwStart + 2;
        if (cslw < 11 || lw + (4 * cslw) + 2 > fib.Length)
        {
            throw new PdfFormatException("The file information block is truncated.");
        }

        for (var i = 0; i < _ccp.Length; i++)
        {
            _ccp[i] = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(fib[(lw + 12 + (4 * i))..]));
        }

        // FibRgFcLcb: pairs of offsets and sizes in the table stream.
        var blob = lw + (4 * cslw);
        var pairs = BinaryPrimitives.ReadUInt16LittleEndian(fib[blob..]);
        var available = Math.Min(pairs, (fib.Length - blob - 2) / 8);
        _fcLcb = new uint[2 * available];
        for (var i = 0; i < _fcLcb.Length; i++)
        {
            _fcLcb[i] = BinaryPrimitives.ReadUInt32LittleEndian(fib[(blob + 2 + (4 * i))..]);
        }

        var tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        var tableEntry = compound.Root[tableName];
        if (tableEntry is not { Kind: CfbEntryKind.Stream })
        {
            throw new PdfFormatException($"The Word document has no {tableName} stream.");
        }

        Table = compound.Read(tableEntry);
        Data = compound.Root["Data"] is { Kind: CfbEntryKind.Stream } data ? compound.Read(data) : [];
        if (compound.Root["\u0005SummaryInformation"] is { Kind: CfbEntryKind.Stream } summary)
        {
            try
            {
                var properties = PropertySetStream.Summary(compound.Read(summary));
                Title = properties.TryGetValue(PropertySetStream.Title, out var title) ? title as string : null;
                Author = properties.TryGetValue(PropertySetStream.Author, out var author) ? author as string : null;
            }
            catch (PdfFormatException)
            {
                // Damaged properties: the document has no title.
            }
        }

        Pieces = ReadPieces();
        Text = ReadText();
        CharacterRuns = ReadFkps(12, paragraphs: false);
        ParagraphRuns = ReadFkps(13, paragraphs: true);
    }

    /// <summary>The FIB version (<c>nFib</c>).</summary>
    public int Nfib { get; }

    /// <summary>The WordDocument stream.</summary>
    public byte[] Word { get; }

    /// <summary>The table stream (0Table or 1Table).</summary>
    public byte[] Table { get; }

    /// <summary>The Data stream (pictures), or empty.</summary>
    public byte[] Data { get; }

    /// <summary>The title of the summary information, or <c>null</c>.</summary>
    public string? Title { get; }

    /// <summary>The author of the summary information, or <c>null</c>.</summary>
    public string? Author { get; }

    /// <summary>The pieces of the text, by character position.</summary>
    public DocPiece[] Pieces { get; }

    /// <summary>The text of all stories.</summary>
    public char[] Text { get; }

    /// <summary>The runs of character properties, by stream offset.</summary>
    public DocFkpRun[] CharacterRuns { get; }

    /// <summary>The runs of paragraph properties, by stream offset.</summary>
    public DocFkpRun[] ParagraphRuns { get; }

    /// <summary>Opens a Word 97-2003 document.</summary>
    /// <exception cref="PdfFormatException">The data is not a Word 97-2003 document or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted or in the Word 6/95 format.</exception>
    public static DocFile Open(byte[] data, ConversionContext context)
    {
        if (!CompoundFile.IsCompoundFile(data))
        {
            if (data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xDB, 0xA5]) || data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x9B, 0xA5]))
            {
                throw new NotSupportedException("The document is in the Word for Windows 1 or 2 format, which is not supported; only Word 97-2003 documents are.");
            }

            throw new PdfFormatException(data.AsSpan().StartsWith(@"{\rtf"u8)
                ? "The data is an RTF document, not a Word 97-2003 document; use RtfToPdf."
                : "The data is not a Word 97-2003 document (a compound file).");
        }

        return new DocFile(CompoundFile.Open(data, context.Limits), context);
    }

    /// <summary>Whether data is a compound file with a WordDocument stream (rather than, say, an encrypted package).</summary>
    public static bool IsWordDocument(byte[] data, ConversionContext context)
    {
        if (!CompoundFile.IsCompoundFile(data))
        {
            return false;
        }

        try
        {
            return CompoundFile.Open(data, context.Limits).Root["WordDocument"] is not null;
        }
        catch (PdfFormatException)
        {
            return false;
        }
    }

    /// <summary>The character count of a story.</summary>
    public int StoryLength(DocStory story) => _ccp[(int)story];

    /// <summary>The character position where a story starts.</summary>
    public int StoryStart(DocStory story)
    {
        long start = 0;
        for (var i = 0; i < (int)story; i++)
        {
            start += _ccp[i];
        }

        return (int)Math.Min(start, int.MaxValue);
    }

    /// <summary>A structure of the table stream by its index in FibRgFcLcb ([MS-DOC] 2.5.6), or empty when absent or outside the stream.</summary>
    public ReadOnlySpan<byte> TableData(int index)
    {
        if (2 * index + 1 >= _fcLcb.Length)
        {
            return [];
        }

        var fc = _fcLcb[2 * index];
        var lcb = _fcLcb[(2 * index) + 1];
        if (lcb == 0 || fc >= Table.Length)
        {
            return [];
        }

        return Table.AsSpan((int)fc, (int)Math.Min(lcb, (uint)(Table.Length - (int)fc)));
    }

    /// <summary>The offset of a structure in FibRgFcLcb (for structures in the WordDocument stream).</summary>
    public (uint Fc, uint Lcb) FcLcb(int index) => 2 * index + 1 < _fcLcb.Length ? (_fcLcb[2 * index], _fcLcb[(2 * index) + 1]) : (0, 0);

    /// <summary>The property modifiers of a piece's <c>Prm</c> when it points at a <c>Prc</c>, or empty.</summary>
    public ReadOnlySpan<byte> PieceModifiers(ushort prm)
    {
        if ((prm & 1) == 0)
        {
            return []; // A single modifier by its Prm0 index: rare, and left out.
        }

        var index = prm >> 1;
        return index < _prcs.Count ? _prcs[index] : [];
    }

    /// <summary>The index of the piece holding a character position, or -1.</summary>
    public int PieceAt(int cp)
    {
        int low = 0, high = Pieces.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (cp < Pieces[middle].CpStart)
            {
                high = middle - 1;
            }
            else if (cp >= Pieces[middle].CpEnd)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    /// <summary>The index of the run that covers a stream offset, or -1.</summary>
    public static int RunAt(DocFkpRun[] runs, long fc)
    {
        int low = 0, high = runs.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (runs[middle].FcStart <= fc)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 && fc < runs[found].FcEnd ? found : -1;
    }

    /// <summary>The bytes of a run's properties (empty for none).</summary>
    public ReadOnlySpan<byte> RunBytes(DocFkpRun run) => run.Offset < 0 ? [] : Word.AsSpan(run.Offset, run.Length);

    /// <summary>The CLX ([MS-DOC] 2.9.38): the property modifiers of fast-saved pieces, then the piece table.</summary>
    private DocPiece[] ReadPieces()
    {
        var clx = TableData(33);
        var position = 0;
        while (position < clx.Length && clx[position] == 0x01)
        {
            if (position + 3 > clx.Length)
            {
                throw new PdfFormatException("The piece table is truncated.");
            }

            var size = BinaryPrimitives.ReadInt16LittleEndian(clx[(position + 1)..]);
            if (size < 0 || position + 3 + size > clx.Length)
            {
                throw new PdfFormatException("A property modifier list of the piece table is outside the table stream.");
            }

            _prcs.Add(clx.Slice(position + 3, size).ToArray());
            position += 3 + size;
        }

        if (position + 5 > clx.Length || clx[position] != 0x02)
        {
            throw new PdfFormatException("The Word document has no piece table (CLX).");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(clx[(position + 1)..]);
        var plc = clx[(position + 5)..];
        if (length < plc.Length)
        {
            plc = plc[..(int)length];
        }

        // PlcPcd: n + 1 character positions, then n 8-byte piece descriptors.
        var count = (plc.Length - 4) / 12;
        if (count <= 0)
        {
            throw new PdfFormatException("The piece table is empty.");
        }

        var total = 0L;
        foreach (var ccp in _ccp)
        {
            total += ccp;
        }

        // A story other than the main one is followed by one more paragraph mark. Each character takes a byte of the
        // stream at least, which bounds the text of a damaged piece table.
        var limit = (int)Math.Min(Math.Min(total + 1, _context.Limits.MaxDecompressedBytes / 2), Math.Min(Word.Length + 16L, Array.MaxLength));
        var pieces = new List<DocPiece>(count);
        for (var i = 0; i < count; i++)
        {
            var start = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * i)..]);
            var end = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * (i + 1))..]);
            var descriptor = plc.Slice((4 * (count + 1)) + (8 * i), 8);
            var fcCompressed = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[2..]);
            var prm = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[6..]);
            var compressed = (fcCompressed & 0x40000000) != 0;
            var fc = fcCompressed & 0x3FFFFFFF;
            start = Math.Max(start, pieces.Count > 0 ? pieces[^1].CpEnd : 0);
            end = Math.Min(end, limit);
            if (end <= start)
            {
                continue;
            }

            pieces.Add(new DocPiece(start, end, (int)(compressed ? fc / 2 : fc), compressed, prm));
        }

        if (pieces.Count == 0)
        {
            throw new PdfFormatException("The piece table has no text.");
        }

        return [.. pieces];
    }

    /// <summary>The text of the pieces; characters whose bytes are outside the stream are left as zeros (and skipped).</summary>
    private char[] ReadText()
    {
        var length = Pieces[^1].CpEnd;
        var text = new char[length];
        var ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
        foreach (var piece in Pieces)
        {
            _context.Checkpoint();
            var count = piece.CpEnd - piece.CpStart;
            if (piece.Fc >= Word.Length)
            {
                continue;
            }

            if (piece.Compressed)
            {
                var bytes = Word.AsSpan(piece.Fc, Math.Min(count, Word.Length - piece.Fc));
                ansi.GetChars(bytes, text.AsSpan(piece.CpStart, bytes.Length));
            }
            else
            {
                var available = Math.Min(count, (Word.Length - piece.Fc) / 2);
                for (var i = 0; i < available; i++)
                {
                    text[piece.CpStart + i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(Word.AsSpan(piece.Fc + (2 * i)));
                }
            }
        }

        ReplaceLoneSurrogates(text);
        return text;
    }

    /// <summary>Text with each surrogate that lacks its other half (damage) replaced by a replacement character.</summary>
    public static string WithoutLoneSurrogates(string text)
    {
        var chars = text.ToCharArray();
        ReplaceLoneSurrogates(chars);
        return new string(chars);
    }

    private static void ReplaceLoneSurrogates(Span<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                text[i] = '\uFFFD';
            }
        }
    }

    /// <summary>
    /// The runs of the formatted disk pages a bin table points to ([MS-DOC] 2.8.6 PlcBteChpx, 2.8.7 PlcBtePapx, 2.9.62
    /// ChpxFkp, 2.9.183 PapxFkp), sorted by offset.
    /// </summary>
    private DocFkpRun[] ReadFkps(int index, bool paragraphs)
    {
        var plc = TableData(index);
        var count = (plc.Length - 4) / 8;
        var runs = new List<DocFkpRun>();
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var pn = BinaryPrimitives.ReadUInt32LittleEndian(plc[((4 * (count + 1)) + (4 * i))..]) & 0x3FFFFF;
            var offset = (long)pn * FkpSize;
            if (offset + FkpSize > Word.Length || !seen.Add(pn))
            {
                continue;
            }

            var page = Word.AsSpan((int)offset, FkpSize);
            var crun = page[FkpSize - 1];
            var entrySize = paragraphs ? 13 : 1;
            if (crun == 0 || (4 * (crun + 1)) + (entrySize * crun) > FkpSize - 1)
            {
                continue;
            }

            for (var r = 0; r < crun; r++)
            {
                var start = BinaryPrimitives.ReadUInt32LittleEndian(page[(4 * r)..]);
                var end = BinaryPrimitives.ReadUInt32LittleEndian(page[(4 * (r + 1))..]);
                if (end <= start)
                {
                    continue;
                }

                var wordOffset = page[(4 * (crun + 1)) + (entrySize * r)];
                int propertiesOffset = -1, length = 0;
                if (wordOffset != 0)
                {
                    var at = 2 * wordOffset;
                    if (paragraphs)
                    {
                        // PapxInFkp: cb, then (cb == 0) cb' and 2cb' bytes, else 2cb - 1 bytes.
                        var cb = page[at];
                        var start2 = at + 1;
                        length = 2 * cb - 1;
                        if (cb == 0 && at + 1 < FkpSize)
                        {
                            length = 2 * page[at + 1];
                            start2 = at + 2;
                        }

                        if (length > 0 && start2 + length <= FkpSize - 1)
                        {
                            propertiesOffset = (int)offset + start2;
                        }
                        else
                        {
                            length = 0;
                        }
                    }
                    else
                    {
                        // Chpx: cb, then cb bytes.
                        length = page[at];
                        if (at + 1 + length <= FkpSize - 1)
                        {
                            propertiesOffset = (int)offset + at + 1;
                        }
                        else
                        {
                            length = 0;
                        }
                    }
                }

                runs.Add(new DocFkpRun(start, end, propertiesOffset, length));
            }
        }

        runs.Sort((a, b) => a.FcStart.CompareTo(b.FcStart));
        return [.. runs];
    }
}
