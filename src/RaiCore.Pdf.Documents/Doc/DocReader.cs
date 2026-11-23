using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// Reads a Word 97-2003 document ([MS-DOC]) into the format-independent <see cref="WordDocument"/>: the main text in
/// its sections (page setup from each section's SEPX), headers and footers (PlcfHdd), footnotes and endnotes, text
/// boxes, paragraphs and runs with their style sheet and direct formatting, lists, tables (nested ones too), fields,
/// bookmarks, and inline and floating pictures.
/// </summary>
/// <remarks>
/// The text is read as stories of character positions, paragraph by paragraph: each paragraph's properties come from
/// its end mark, each run's from the character run that holds it. Nothing recurses on the input: text boxes and notes
/// are read after the text that refers to them, each once. Paragraphs and inlines are bounded by
/// <see cref="ResourceLimits.MaxElements"/>, table nesting by <see cref="ResourceLimits.MaxNestingDepth"/>, table cells
/// by <see cref="ResourceLimits.MaxTableCells"/> and pictures by <see cref="ResourceLimits.MaxResourceBytes"/>.
/// </remarks>
internal sealed partial class DocReader
{
    /// <summary>How many paragraphs are read between checkpoints.</summary>
    private const int CheckpointInterval = 256;

    /// <summary>The most header and footer stories read (six for each section).</summary>
    private const int MaxSections = 4096;

    private readonly DocFile _file;
    private readonly ConversionContext _context;
    private readonly DocStyles _styles;
    private readonly DocLists _lists;
    private readonly DocDrawings _drawings;
    private readonly WordDocument _document = new();
    private readonly Dictionary<int, int> _footnoteReferences = [];
    private readonly Dictionary<int, int> _endnoteReferences = [];
    private readonly Dictionary<int, List<string>> _bookmarks = [];
    private readonly Dictionary<int, DocShape> _mainShapes;
    private readonly Dictionary<int, DocShape> _headerShapes;
    private readonly Queue<(AnchoredBlocks Target, int Start, int End, bool Header)> _textBoxes = new();
    private readonly HashSet<(int, bool)> _textBoxesRead = [];
    private WordSection? _section;
    private long _elements;
    private int _paragraphs;

    private DocReader(DocFile file, ConversionContext context)
    {
        _file = file;
        _context = context;
        _styles = DocStyles.Read(file);
        _lists = DocLists.Read(file);
        _drawings = DocDrawings.Read(file);
        _mainShapes = _drawings.Anchors(headers: false);
        _headerShapes = _drawings.Anchors(headers: true);
    }

    /// <summary>Reads a Word 97-2003 document.</summary>
    /// <exception cref="DocumentFormatException">The data is not a Word 97-2003 document or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted or in the Word 6/95 format.</exception>
    public static WordDocument Read(byte[] data, ConversionContext context) => new DocReader(DocFile.Open(data, context), context).ReadDocument();

    private WordDocument ReadDocument()
    {
        _document.Title = Clean(_file.Title);
        _document.Author = Clean(_file.Author);
        ReadDocumentProperties();
        ReadNoteReferences(2, _footnoteReferences);
        ReadNoteReferences(46, _endnoteReferences);
        ReadBookmarks();
        if (_file.TableData(4).Length > 4)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "Comments (annotations) are left out.");
        }

        ReadSections();
        ReadNotes(3, DocStory.Footnotes, _footnoteReferences, _document.Footnotes, "f");
        ReadNotes(47, DocStory.Endnotes, _endnoteReferences, _document.Endnotes, "e");
        ReadTextBoxes();
        if (_document.Sections.Count == 0)
        {
            _document.Sections.Add(new WordSection());
        }

        return _document;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim('\0', ' ');
        return string.IsNullOrEmpty(trimmed) ? null : DocFile.WithoutLoneSurrogates(trimmed);
    }

    /// <summary>The document properties ([MS-DOC] 2.7 Dop): different headers on facing pages, the default tab stop.</summary>
    private void ReadDocumentProperties()
    {
        var dop = _file.TableData(31);
        if (dop.Length >= 12)
        {
            _document.EvenAndOddHeaders = (dop[0] & 1) != 0;
            var tab = BinaryPrimitives.ReadUInt16LittleEndian(dop[10..]);
            if (tab is >= 36 and <= 14400)
            {
                _document.DefaultTabStop = tab / 20.0;
            }
        }
    }

    /// <summary>A PLC's character positions: <paramref name="count"/> + 1 of them before the data, or empty.</summary>
    private static int[] Positions(ReadOnlySpan<byte> plc, int dataSize, out int count)
    {
        count = plc.Length >= 4 ? (plc.Length - 4) / (4 + dataSize) : 0;
        var positions = new int[count + 1];
        for (var i = 0; i <= count && plc.Length >= 4; i++)
        {
            positions[i] = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * i)..]);
        }

        return count == 0 ? [] : positions;
    }

    /// <summary>Where the notes' reference marks are in the text (PlcffndRef, PlcfendRef): their index by character position.</summary>
    private void ReadNoteReferences(int index, Dictionary<int, int> references)
    {
        var positions = Positions(_file.TableData(index), 2, out var count);
        for (var i = 0; i < count; i++)
        {
            references.TryAdd(positions[i], i);
        }
    }

    /// <summary>The bookmarks' names (SttbfBkmk) at their first character (PlcfBkf).</summary>
    private void ReadBookmarks()
    {
        var names = ReadStrings(_file.TableData(21));
        var positions = Positions(_file.TableData(22), 4, out var count);
        for (var i = 0; i < count && i < names.Count; i++)
        {
            if (names[i].Length == 0)
            {
                continue;
            }

            if (!_bookmarks.TryGetValue(positions[i], out var list))
            {
                list = [];
                _bookmarks[positions[i]] = list;
            }

            list.Add(names[i]);
        }
    }

    /// <summary>The strings of an extended string table ([MS-DOC] 2.2.4 STTB with <c>fExtend</c>).</summary>
    private static List<string> ReadStrings(ReadOnlySpan<byte> sttb)
    {
        var result = new List<string>();
        if (sttb.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(sttb) != 0xFFFF)
        {
            return result;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(sttb[2..]);
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(sttb[4..]);
        var position = 6;
        for (var i = 0; i < count && position + 2 <= sttb.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(sttb[position..]);
            position += 2;
            if (2 * length > sttb.Length - position)
            {
                break;
            }

            result.Add(Encoding.Unicode.GetString(sttb.Slice(position, 2 * length)));
            position += (2 * length) + extra;
        }

        return result;
    }

    /// <summary>
    /// The sections (PlcfSed with each section's SEPX): their page setup, their text from the main story, and their
    /// headers and footers.
    /// </summary>
    private void ReadSections()
    {
        var mainLength = Math.Min(_file.StoryLength(DocStory.Main), _file.Text.Length);
        var plc = _file.TableData(6);
        var positions = Positions(plc, 12, out var count);
        var sections = new List<(int Start, int End, DocSep Sep)>();
        var previous = 0;
        for (var i = 0; i < count && previous < mainLength; i++)
        {
            var end = i == count - 1 ? mainLength : Math.Clamp(positions[i + 1], previous, mainLength);
            if (end <= previous && i < count - 1)
            {
                continue;
            }

            var sed = plc.Slice((4 * (count + 1)) + (12 * i), 12);
            sections.Add((previous, end, Sep(BinaryPrimitives.ReadUInt32LittleEndian(sed[2..]))));
            previous = end;
        }

        if (sections.Count == 0)
        {
            sections.Add((0, mainLength, DocSep.Default));
        }

        var story = new Story(DocStory.Main, 0);
        var headers = Positions(_file.TableData(11), 0, out var headerCount);
        for (var i = 0; i < sections.Count; i++)
        {
            var (start, end, sep) = sections[i];
            _section = Section(sep);
            _document.Sections.Add(_section);
            story.Blocks = _section.Blocks;
            ReadRange(story, start, end);
            CloseTables(story, 0);
            if (i < MaxSections)
            {
                ReadHeaders(_section, headers, headerCount, i);
            }
        }
    }

    /// <summary>A section's properties: the defaults with its SEPX ([MS-DOC] 2.9.263) applied.</summary>
    private DocSep Sep(uint fcSepx)
    {
        var sep = DocSep.Default;
        if (fcSepx != 0xFFFFFFFF && fcSepx <= _file.Word.Length - 2)
        {
            var size = BinaryPrimitives.ReadInt16LittleEndian(_file.Word.AsSpan((int)fcSepx));
            if (size > 0 && size <= _file.Word.Length - (int)fcSepx - 2)
            {
                DocProperties.ApplySection(ref sep, _file.Word.AsSpan((int)fcSepx + 2, size));
            }
        }

        return sep;
    }

    private WordSection Section(DocSep sep)
    {
        static double Points(int twips, double fallback) => twips is > 0 and <= 31680 ? twips / 20.0 : fallback;

        var section = new WordSection
        {
            PageWidth = Points(sep.PageWidth, 612),
            PageHeight = Points(sep.PageHeight, 792),
            MarginTop = Math.Abs(sep.Top) <= 31680 ? Math.Abs(sep.Top) / 20.0 : 72,
            MarginBottom = Math.Abs(sep.Bottom) <= 31680 ? Math.Abs(sep.Bottom) / 20.0 : 72,
            MarginLeft = (Math.Clamp(sep.Left, 0, 31680) + Math.Clamp(sep.Gutter, 0, 31680)) / 20.0,
            MarginRight = Math.Clamp(sep.Right, 0, 31680) / 20.0,
            HeaderDistance = Math.Clamp(sep.HeaderTop, 0, 31680) / 20.0,
            FooterDistance = Math.Clamp(sep.FooterBottom, 0, 31680) / 20.0,
            TitlePage = sep.TitlePage,
            Columns = sep.Columns,
            Start = sep.Break switch
            {
                0 => SectionStart.Continuous,
                3 => SectionStart.EvenPage,
                4 => SectionStart.OddPage,
                _ => SectionStart.NextPage,
            },
            PageNumberStart = sep.RestartPageNumbers ? sep.PageNumberStart : null,
            PageNumberFormat = NumberFormatOf(sep.PageNumberFormat),
        };
        // A page too small for its margins (as some writers save) gets the width or height of A4 or Letter paper.
        if (section.MarginLeft + section.MarginRight > section.PageWidth - 72)
        {
            section.PageWidth = Math.Abs(section.PageHeight - 841.9) < 1 ? 595.3 : 612;
        }

        if (section.MarginTop + section.MarginBottom > section.PageHeight - 72)
        {
            section.PageHeight = Math.Abs(section.PageWidth - 595.3) < 1 ? 841.9 : 792;
        }

        if (section.MarginLeft + section.MarginRight > section.PageWidth - 72)
        {
            section.MarginLeft = section.MarginRight = 72;
        }

        if (section.MarginTop + section.MarginBottom > section.PageHeight - 72)
        {
            section.MarginTop = section.MarginBottom = 72;
        }

        if (section.Columns > 1)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"A section with {section.Columns} text columns is laid out in one column.");
        }

        return section;
    }

    /// <summary>The number format of an <c>nfc</c> (page numbers, list levels).</summary>
    internal static NumberFormat NumberFormatOf(int nfc) => nfc switch
    {
        1 => NumberFormat.UpperRoman,
        2 => NumberFormat.LowerRoman,
        3 => NumberFormat.UpperLetter,
        4 => NumberFormat.LowerLetter,
        5 => NumberFormat.Ordinal,
        22 => NumberFormat.DecimalZero,
        _ => NumberFormat.Decimal,
    };

    /// <summary>
    /// A section's headers and footers (PlcfHdd, [MS-DOC] 2.8.15): after the six note separators, six stories for each
    /// section (even header, odd header, even footer, odd footer, first header, first footer); an empty story is
    /// inherited from the section before.
    /// </summary>
    private void ReadHeaders(WordSection section, int[] positions, int count, int index)
    {
        var start = _file.StoryStart(DocStory.Headers);
        var length = _file.StoryLength(DocStory.Headers);
        for (var k = 0; k < 6; k++)
        {
            var i = 6 + (6 * index) + k;
            if (i >= count)
            {
                return;
            }

            var from = Math.Clamp(positions[i], 0, length);
            var to = Math.Clamp(positions[i + 1], from, length);
            if (to <= from)
            {
                continue;
            }

            var story = new Story(DocStory.Headers, start);
            ReadRange(story, start + from, start + to);
            CloseTables(story, 0);

            // A story ends with an extra paragraph mark of its own.
            if (story.Blocks.Count > 1 && story.Blocks[^1] is WordParagraph { Inlines.Count: 0, Label: null })
            {
                story.Blocks.RemoveAt(story.Blocks.Count - 1);
            }

            var kind = (k % 2 == 0 && k < 4) ? HeaderFooterKind.Even : k >= 4 ? HeaderFooterKind.First : HeaderFooterKind.Default;
            var target = k is 0 or 1 or 4 ? section.Headers : section.Footers;
            target[kind] = story.Blocks;
        }
    }

    /// <summary>The notes (PlcffndTxt, PlcfendTxt): each note's story, by the id of its reference.</summary>
    private void ReadNotes(int index, DocStory kind, Dictionary<int, int> references, Dictionary<string, List<WordBlock>> notes, string prefix)
    {
        if (references.Count == 0)
        {
            return;
        }

        var start = _file.StoryStart(kind);
        var length = _file.StoryLength(kind);
        var plc = _file.TableData(index);
        var count = plc.Length / 4;
        for (var i = 0; i + 1 < count && i < references.Count; i++)
        {
            var from = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * i)..]), 0, length);
            var to = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * (i + 1))..]), from, length);
            var story = new Story(kind, start) { Note = true };
            ReadRange(story, start + from, start + to);
            CloseTables(story, 0);
            notes[prefix + i.ToString(CultureInfo.InvariantCulture)] = story.Blocks;
        }
    }

    /// <summary>The text boxes anchored in the text, each read once (and those they anchor in turn).</summary>
    private void ReadTextBoxes()
    {
        while (_textBoxes.TryDequeue(out var request))
        {
            var story = new Story(request.Header ? DocStory.HeaderTextBoxes : DocStory.TextBoxes, _file.StoryStart(request.Header ? DocStory.HeaderTextBoxes : DocStory.TextBoxes))
            {
                Blocks = request.Target.Blocks,
            };
            ReadRange(story, request.Start, request.End);
            CloseTables(story, 0);
        }
    }

    /// <summary>
    /// The text of a text box (1-based in PlcftxbxTxt or PlcfHdrtxbxTxt), read later into the blocks returned; or
    /// <c>null</c> when the text box is not defined or already shown.
    /// </summary>
    private AnchoredBlocks? TextBox(int number, bool header)
    {
        if (!_textBoxesRead.Add((number, header)))
        {
            return null;
        }

        var kind = header ? DocStory.HeaderTextBoxes : DocStory.TextBoxes;
        var positions = Positions(_file.TableData(header ? 58 : 56), 22, out var count);
        if (number < 1 || number > count)
        {
            return null;
        }

        var start = _file.StoryStart(kind);
        var length = _file.StoryLength(kind);
        var from = Math.Clamp(positions[number - 1], 0, length);
        var to = Math.Clamp(positions[number], from, length);
        if (to <= from)
        {
            return null;
        }

        var target = new AnchoredBlocks();
        _textBoxes.Enqueue((target, start + from, start + to, header));
        return target;
    }

    /// <summary>Counts an element (a paragraph, an inline) against <see cref="ResourceLimits.MaxElements"/>.</summary>
    private void CountElement()
    {
        if (++_elements > _context.Limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _context.Limits.MaxElements,
                $"The document has more paragraphs and runs than ResourceLimits.MaxElements allows ({_context.Limits.MaxElements}).");
        }
    }
}
