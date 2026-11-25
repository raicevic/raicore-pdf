using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Text;

/// <summary>
/// Writes the plain text of a <see cref="WordDocument"/> as <see cref="DocumentTextOptions"/> ask: the sections' blocks
/// in order (with their headers and footers), then the notes and the comments. Paragraphs end with the paragraph
/// separator; table rows are their cells' text between cell separators, ended by the row separator, and within a cell
/// (and in nested tables) paragraphs, breaks and tabs become single spaces.
/// </summary>
internal sealed class PlainTextWriter
{
    /// <summary>How many blocks are written between checkpoints.</summary>
    private const int CheckpointInterval = 256;

    private readonly WordDocument _document;
    private readonly DocumentTextOptions _options;
    private readonly ConversionContext _context;
    private readonly StringBuilder _text = new();
    private readonly List<(string Id, int Number)> _footnotes = [];
    private readonly List<(string Id, int Number)> _endnotes = [];
    private readonly List<string> _comments = [];
    private readonly HashSet<string> _commentIds = new(StringComparer.Ordinal);
    private readonly HashSet<List<WordBlock>> _headersWritten = [];

    /// <summary>How many fields' results are being left out (their codes are written instead).</summary>
    private int _skip;

    /// <summary>How deep in table cells the writer is (0 outside tables).</summary>
    private int _cellDepth;

    /// <summary>Where the current cell's text starts.</summary>
    private int _cellStart;

    /// <summary>Whether a space is due before the next text in a cell (between its paragraphs).</summary>
    private bool _pendingSpace;

    /// <summary>
    /// Whether a page or column break was passed in the paragraph: it ends a line if text follows it in the paragraph
    /// (a break at the start or end of a paragraph adds no empty line).
    /// </summary>
    private bool _pendingBreak;

    /// <summary>Whether the paragraph being written has text yet.</summary>
    private bool _paragraphHasText;

    /// <summary>The number and format of the note being written, or <c>null</c> in the body.</summary>
    private (int Number, NumberFormat Format)? _note;

    /// <summary>Whether a comment is being written (references in it are not followed).</summary>
    private bool _inComment;

    private int _blocks;

    public PlainTextWriter(WordDocument document, DocumentTextOptions options, ConversionContext context)
    {
        _document = document;
        _options = options;
        _context = context;
    }

    /// <summary>Writes the document's text.</summary>
    public string Write()
    {
        _context.Checkpoint();
        foreach (var section in _document.Sections)
        {
            if (_options.IncludeHeadersAndFooters)
            {
                WriteHeadersOrFooters(section, section.Headers);
            }

            WriteBlocks(section.Blocks);
            if (_options.IncludeHeadersAndFooters)
            {
                WriteHeadersOrFooters(section, section.Footers);
            }
        }

        if (_skip > 0)
        {
            // A field whose result never ends (damaged, or cut off) ends with the body, on a line of its own.
            _skip = 0;
            EndParagraph();
        }

        // Notes and comments are listed in the order of their references; notes they refer to are not followed.
        foreach (var (id, number) in _footnotes)
        {
            WriteNote(_document.Footnotes, id, number, _document.FootnoteFormat);
        }

        foreach (var (id, number) in _endnotes)
        {
            WriteNote(_document.Endnotes, id, number, _document.EndnoteFormat);
        }

        _inComment = true;
        foreach (var id in _comments)
        {
            WriteStory(_document.Comments[id].Blocks);
        }

        return _text.ToString();
    }

    /// <summary>
    /// The text of blocks of their own (a comment's), with the default options (or those given): paragraphs on lines of
    /// their own, without the last one's end.
    /// </summary>
    public static string TextOf(List<WordBlock> blocks, ConversionContext context, DocumentTextOptions? options = null)
    {
        var document = new WordDocument();
        var section = new WordSection();
        section.Blocks.AddRange(blocks);
        document.Sections.Add(section);
        return new PlainTextWriter(document, options ?? new DocumentTextOptions(), context).Write().TrimEnd('\n');
    }

    /// <summary>A section's own headers or footers that show (first page, other pages, even pages), each once.</summary>
    private void WriteHeadersOrFooters(WordSection section, Dictionary<HeaderFooterKind, List<WordBlock>> parts)
    {
        foreach (var kind in (ReadOnlySpan<HeaderFooterKind>)[HeaderFooterKind.First, HeaderFooterKind.Default, HeaderFooterKind.Even])
        {
            var shown = kind switch
            {
                HeaderFooterKind.First => section.TitlePage,
                HeaderFooterKind.Even => _document.EvenAndOddHeaders,
                _ => true,
            };
            if (shown && parts.TryGetValue(kind, out var blocks) && _headersWritten.Add(blocks))
            {
                WriteStory(blocks);
            }
        }
    }

    private void WriteNote(Dictionary<string, List<WordBlock>> notes, string id, int number, NumberFormat format)
    {
        if (notes.TryGetValue(id, out var blocks))
        {
            _note = (number, format);
            WriteStory(blocks);
            _note = null;
        }
    }

    /// <summary>
    /// Writes a story of its own (a header or footer, a note, a comment): a field left open in the body does not hide
    /// it, and one left open in it ends with it.
    /// </summary>
    private void WriteStory(List<WordBlock> blocks)
    {
        var skip = _skip;
        _skip = 0;
        WriteBlocks(blocks);
        _skip = skip;
    }

    private void WriteBlocks(List<WordBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (++_blocks % CheckpointInterval == 0)
            {
                _context.Checkpoint();
            }

            switch (block)
            {
                case WordParagraph paragraph:
                    WriteParagraph(paragraph);
                    break;
                case WordTable table:
                    WriteTable(table);
                    break;
            }
        }
    }

    private void WriteParagraph(WordParagraph paragraph)
    {
        if (_options.ListLabels && paragraph.Label is { } label && label.Text.Length > 0)
        {
            Append(label.Text);
            Append(label.Suffix switch
            {
                LabelSuffix.Tab => InCell ? " " : "\t",
                LabelSuffix.Space => " ",
                _ => string.Empty,
            });
        }

        List<AnchoredBlocks>? anchored = null;
        WriteInlines(paragraph.Inlines, ref anchored);
        EndParagraph();
        if (anchored is not null && _options.IncludeTextBoxes)
        {
            foreach (var blocks in anchored)
            {
                WriteBlocks(blocks.Blocks);
            }
        }
    }

    private void WriteInlines(List<WordInline> inlines, ref List<AnchoredBlocks>? anchored)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    if (Shown(run.Format))
                    {
                        Append(run.Text);
                    }

                    break;
                case TabInline tab:
                    if (Shown(tab.Format))
                    {
                        Append(InCell ? " " : "\t");
                    }

                    break;
                case BreakInline line:
                    if (!Shown(line.Format))
                    {
                        break;
                    }

                    if (line.Kind == BreakKind.Line)
                    {
                        EndLine();
                    }
                    else
                    {
                        _pendingBreak = _paragraphHasText;
                    }

                    break;
                case HyperlinkInline link:
                    WriteInlines(link.Inlines, ref anchored);
                    break;
                case FieldInline field:
                    Append(field.CachedResult ?? string.Empty);
                    break;
                case FieldStart start:
                    if (_options.FieldCodes)
                    {
                        Append("{ " + start.Code.Trim() + " }");
                        _skip++;
                    }

                    break;
                case FieldEnd:
                    if (_skip > 0)
                    {
                        _skip--;
                    }

                    break;
                case NoteReference reference:
                    WriteNoteReference(reference);
                    break;
                case NoteMark:
                    if (_note is { } note)
                    {
                        Append(Numbering.Format(note.Number, note.Format));
                    }

                    break;
                case CommentReference comment:
                    // Comments are in the model only when the options include them (ReadExtras.Comments).
                    if (!_inComment && _skip == 0 && _document.Comments.ContainsKey(comment.Id) && _commentIds.Add(comment.Id))
                    {
                        _comments.Add(comment.Id);
                    }

                    break;
                case AnchoredBlocks blocks:
                    if (_skip == 0)
                    {
                        (anchored ??= []).Add(blocks);
                    }

                    break;
            }
        }
    }

    /// <summary>A note's number (or custom mark) where it is referred to, from the body only; the note is listed at the end.</summary>
    private void WriteNoteReference(NoteReference reference)
    {
        if (!_options.IncludeNotes || _note is not null || _inComment || _skip > 0)
        {
            return;
        }

        var footnote = reference.Kind == NoteKind.Footnote;
        var list = footnote ? _footnotes : _endnotes;
        var number = list.Count + 1;
        list.Add((reference.Id, number));
        Append(reference.CustomMark ?? Numbering.Format(number, footnote ? _document.FootnoteFormat : _document.EndnoteFormat));
    }

    private void WriteTable(WordTable table)
    {
        var nested = InCell;
        foreach (var row in table.Rows)
        {
            for (var i = 0; i < row.Cells.Count; i++)
            {
                if (i > 0)
                {
                    if (nested)
                    {
                        _pendingSpace = true;
                    }
                    else
                    {
                        Append(_options.TableCellSeparator);
                    }
                }

                WriteCell(row.Cells[i], nested);
            }

            if (nested)
            {
                _pendingSpace = true;
            }
            else
            {
                Append(_options.TableRowSeparator);
            }
        }
    }

    private void WriteCell(WordCell cell, bool nested)
    {
        var start = _cellStart;
        _cellDepth++;
        if (!nested)
        {
            _cellStart = _text.Length;
            _pendingSpace = false;
        }

        WriteBlocks(cell.Blocks);
        _cellDepth--;
        _cellStart = start;
        if (!nested)
        {
            _pendingSpace = false;
        }
    }

    private bool InCell => _cellDepth > 0;

    /// <summary>Whether text in a format is written: hidden and deleted text only when the options ask for it.</summary>
    private bool Shown(RunFormat format) =>
        (!format.Hidden || _options.IncludeHiddenText) && (!format.Deleted || _options.IncludeDeletedText);

    /// <summary>Ends a paragraph: the separator, or in a table cell a space before what follows.</summary>
    private void EndParagraph()
    {
        _pendingBreak = false;
        _paragraphHasText = false;
        EndLine();
    }

    /// <summary>Ends a line: the separator, or in a table cell a space before what follows.</summary>
    private void EndLine()
    {
        if (_skip > 0)
        {
            return;
        }

        if (InCell)
        {
            _pendingSpace = true;
        }
        else
        {
            _text.Append(_options.ParagraphSeparator);
        }
    }

    /// <summary>Appends text, unless a field's result is being left out; in a cell, after the space due before it.</summary>
    private void Append(string text)
    {
        if (_skip > 0 || text.Length == 0)
        {
            return;
        }

        if (_pendingBreak)
        {
            _pendingBreak = false;
            EndLine();
        }

        _paragraphHasText = true;
        if (_pendingSpace)
        {
            if (InCell && _text.Length > _cellStart && _text[^1] != ' ')
            {
                _text.Append(' ');
            }

            _pendingSpace = false;
        }

        _text.Append(text);
    }
}
