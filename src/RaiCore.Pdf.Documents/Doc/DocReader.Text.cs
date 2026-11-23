using System.Text;
using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// Paragraphs and runs: each paragraph's properties from its mark (style, direct modifiers, list level), its characters
/// in runs of the same properties, special characters (tabs, breaks, pictures, note references, drawing anchors), and
/// fields.
/// </summary>
internal sealed partial class DocReader
{
    /// <summary>The most fields nested in each other that are tracked; deeper ones are read as text.</summary>
    private const int MaxFieldDepth = 32;

    private readonly Dictionary<(int Style, int Run, int Piece), (DocChp Chp, RunFormat Format)> _characters = [];
    private int _pictures;

    /// <summary>A story being read: its blocks, open tables and fields, and the paragraph being built.</summary>
    private sealed class Story(DocStory kind, int start)
    {
        public DocStory Kind { get; } = kind;

        /// <summary>The character position where the story starts (anchors of header drawings count from it).</summary>
        public int Start { get; } = start;

        public bool Note { get; init; }

        public List<WordBlock> Blocks { get; set; } = [];

        public List<TableBuilder> Tables { get; } = [];

        public List<FieldState> Fields { get; } = [];

        /// <summary>Where inlines go: the paragraph's, or a hyperlink's in it.</summary>
        public List<List<WordInline>> Targets { get; } = [];

        public StringBuilder Run { get; } = new();

        public RunFormat? RunFormat { get; set; }
    }

    /// <summary>What a field does with its result.</summary>
    private enum FieldAction
    {
        /// <summary>Its result is shown as it is.</summary>
        Keep,

        /// <summary>Its value is computed by the renderer; the result is the fallback.</summary>
        Compute,

        /// <summary>Its result is a hyperlink.</summary>
        Link,
    }

    /// <summary>A field being read ([MS-DOC] 2.8.25 PlcFld: begin 0x13, separator 0x14, end 0x15).</summary>
    private sealed class FieldState
    {
        public StringBuilder Instruction { get; } = new();

        public bool InResult { get; set; }

        public FieldAction Action { get; set; }

        public FieldKind Kind { get; set; }

        public string? Argument { get; set; }

        public NumberFormat? NumberFormat { get; set; }

        public Uri? Uri { get; set; }

        public string? Anchor { get; set; }

        public RunFormat? ResultFormat { get; set; }

        /// <summary>The field's start in the model, when field codes are kept (<see cref="ReadExtras.FieldCodes"/>).</summary>
        public FieldStart? Marker { get; set; }

        public StringBuilder Result { get; } = new();

        /// <summary>The hyperlink the result goes into, in the current paragraph.</summary>
        public HyperlinkInline? Link { get; set; }
    }

    /// <summary>Reads the paragraphs of a range of a story.</summary>
    private void ReadRange(Story story, int start, int end)
    {
        var text = _file.Text;
        end = Math.Min(end, text.Length);
        var cp = Math.Max(0, start);
        while (cp < end)
        {
            if (++_paragraphs % CheckpointInterval == 0)
            {
                _context.Checkpoint();
            }

            // The paragraph ends at its mark: a paragraph mark, a cell or row mark, or the section mark at the end.
            var mark = cp;
            while (mark < end && text[mark] != '\r' && text[mark] != '\a' && !(text[mark] == '\f' && mark == end - 1))
            {
                mark++;
            }

            var markCp = Math.Min(mark, end - 1);
            var (pap, grpprl) = ParagraphProperties(markCp);
            var markChars = CharacterProperties(markCp, pap.Style);
            var paragraph = new WordParagraph { Format = Format(pap), MarkFormat = markChars.Format };
            CountElement();
            StartParagraph(story, paragraph);
            ReadCharacters(story, cp, mark, pap.Style);
            EndParagraph(story);
            if (pap.ListOverride > 0 && !markChars.Chp.Deleted)
            {
                // A hidden paragraph is counted, but its label is not shown.
                var label = Label(pap, markChars.Chp);
                paragraph.Label = markChars.Chp.Vanish ? null : label;
            }

            Place(story, paragraph, pap, mark < end ? text[mark] : '\r', grpprl);
            cp = mark + 1;
        }
    }

    /// <summary>
    /// A paragraph's properties from its mark's PAPX ([MS-DOC] 2.9.179 PapxInFkp): its style's, with the list level's
    /// and then its own modifiers applied (and those of a fast-saved piece, and a PAPX too large for the page that is
    /// kept in the Data stream). Returns the paragraph's own modifiers too, which hold a table row's definition.
    /// </summary>
    private (DocPap Pap, byte[] Grpprl) ParagraphProperties(int cp)
    {
        var pieceIndex = _file.PieceAt(cp);
        ReadOnlySpan<byte> papx = [];
        ReadOnlySpan<byte> pieceModifiers = [];
        if (pieceIndex >= 0)
        {
            var piece = _file.Pieces[pieceIndex];
            var run = DocFile.RunAt(_file.ParagraphRuns, piece.FcOf(cp));
            if (run >= 0)
            {
                papx = _file.RunBytes(_file.ParagraphRuns[run]);
            }

            pieceModifiers = _file.PieceModifiers(piece.Prm);
        }

        var istd = papx.Length >= 2 ? papx[0] | (papx[1] << 8) : 0;
        var own = new List<byte>(papx.Length > 2 ? papx[2..].ToArray() : []);
        own.AddRange(pieceModifiers);
        AddHugePapx(own);
        var grpprl = own.ToArray();
        var pap = _styles.Paragraph(istd).Pap;
        DocProperties.ApplyParagraph(ref pap, grpprl);
        if (pap.ListOverride > 0 && _lists.LevelOf(pap.ListOverride, pap.ListLevel) is { Papx.Length: > 0 } level)
        {
            var styled = _styles.Paragraph(istd).Pap;
            DocProperties.ApplyParagraph(ref styled, level.Papx);
            DocProperties.ApplyParagraph(ref styled, grpprl);
            pap = styled;
        }

        pap.Style = istd;
        return (pap, grpprl);
    }

    /// <summary>The modifiers of a <c>sprmPHugePapx</c> ([MS-DOC] 2.9.135 PrcData in the Data stream), added to the list.</summary>
    private void AddHugePapx(List<byte> grpprl)
    {
        var reader = new SprmReader(grpprl.ToArray());
        while (reader.Next())
        {
            if (reader.Code == Sprms.PHugePapx && reader.Operand.Length >= 4)
            {
                var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reader.Operand);
                var data = _file.Data;
                if (offset >= 0 && offset <= data.Length - 2)
                {
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                    grpprl.AddRange(data.AsSpan(offset + 2, Math.Min(size, data.Length - offset - 2)));
                }

                return;
            }
        }
    }

    /// <summary>
    /// The character properties at a position ([MS-DOC] 2.4.6.3): the paragraph style's, then the character style
    /// (<c>sprmCIstd</c>), then the CHPX's modifiers and the piece's; and the run format they make. Cached by style, run
    /// and piece.
    /// </summary>
    private (DocChp Chp, RunFormat Format) CharacterProperties(int cp, int paragraphStyle)
    {
        var pieceIndex = _file.PieceAt(cp);
        var run = -1;
        if (pieceIndex >= 0)
        {
            run = DocFile.RunAt(_file.CharacterRuns, _file.Pieces[pieceIndex].FcOf(cp));
        }

        var key = (paragraphStyle, run, pieceIndex < 0 ? -1 : _file.Pieces[pieceIndex].Prm);
        if (_characters.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var basis = _styles.Paragraph(paragraphStyle).Chp;
        ReadOnlySpan<byte> chpx = run >= 0 ? _file.RunBytes(_file.CharacterRuns[run]) : [];
        var reader = new SprmReader(chpx);
        while (reader.Next())
        {
            if (reader.Code == Sprms.CIstd && reader.Operand.Length >= 2)
            {
                _styles.ApplyCharacterStyle(ref basis, reader.Operand[0] | (reader.Operand[1] << 8));
            }
        }

        var chp = basis;
        DocProperties.ApplyCharacter(ref chp, chpx, basis);
        if (pieceIndex >= 0)
        {
            DocProperties.ApplyCharacter(ref chp, _file.PieceModifiers(_file.Pieces[pieceIndex].Prm), basis);
        }

        var result = (chp, Format(chp));
        if (_characters.Count < 65536)
        {
            _characters[key] = result;
        }

        return result;
    }

    /// <summary>The run format of character properties.</summary>
    private RunFormat Format(DocChp chp) => new()
    {
        FontFamily = _styles.FontName(chp.Font),
        FontSize = chp.HalfPoints / 2.0,
        Bold = chp.Bold,
        Italic = chp.Italic,
        Underline = chp.Underline switch
        {
            0 => UnderlineStyle.None,
            3 => UnderlineStyle.Double,
            4 or 20 => UnderlineStyle.Dotted,
            7 or 9 or 10 or 23 or 25 or 26 or 39 or 55 => UnderlineStyle.Dashed,
            11 or 27 or 43 => UnderlineStyle.Wavy,
            _ => UnderlineStyle.Single,
        },
        Strike = chp.Strike || chp.DoubleStrike,
        Color = chp.Color,
        Background = chp.Highlight ?? chp.Shading,
        Position = chp.Iss switch
        {
            1 => VerticalPosition.Superscript,
            2 => VerticalPosition.Subscript,
            _ => VerticalPosition.Baseline,
        },
        BaselineShift = chp.HalfPointPosition / 2.0,
        Caps = chp.Caps,
        SmallCaps = chp.SmallCaps,
        Hidden = chp.Vanish,
        Deleted = chp.Deleted,
        LetterSpacing = chp.Spacing / 20.0,
        RightToLeft = chp.RightToLeft,
    };

    /// <summary>The paragraph format of paragraph properties.</summary>
    private ParagraphFormat Format(DocPap pap)
    {
        var tabs = new List<TabStop>();
        foreach (var (position, descriptor) in pap.Tabs ?? [])
        {
            var alignment = (descriptor & 7) switch
            {
                0 => TabAlignment.Left,
                1 => TabAlignment.Center,
                2 => TabAlignment.Right,
                3 => TabAlignment.Decimal,
                _ => (TabAlignment?)null,
            };
            if (alignment is { } a && position >= 0)
            {
                char? leader = ((descriptor >> 3) & 7) switch
                {
                    1 => '.',
                    2 => '-',
                    3 or 4 => '_',
                    5 => '\u00B7',
                    _ => null,
                };
                tabs.Add(new TabStop(position / 20.0, a, leader));
            }
        }

        var sti = _styles.Sti(pap.Style);
        int? outline = pap.OutlineLevel < 9 ? pap.OutlineLevel : sti is >= 1 and <= 9 ? sti - 1 : null;
        double spacing;
        LineSpacingRule rule;
        if (pap.LineMultiple)
        {
            spacing = pap.Line > 0 ? Math.Clamp(pap.Line / 240.0, 0.25, 10) : 1;
            rule = LineSpacingRule.Multiple;
        }
        else
        {
            spacing = Math.Clamp(Math.Abs(pap.Line) / 20.0, 1, 1584);
            rule = pap.Line < 0 ? LineSpacingRule.Exact : LineSpacingRule.AtLeast;
        }

        return new ParagraphFormat
        {
            Alignment = pap.Justification switch
            {
                1 => WordAlignment.Center,
                2 => WordAlignment.End,
                3 or 4 or 5 or 7 or 8 or 9 => WordAlignment.Justify,
                _ => WordAlignment.Start,
            },
            RightToLeft = pap.RightToLeft,
            IndentStart = Math.Clamp(pap.Left, -31680, 31680) / 20.0,
            IndentEnd = Math.Clamp(pap.Right, -31680, 31680) / 20.0,
            FirstLineIndent = Math.Clamp(pap.FirstLine, -31680, 31680) / 20.0,
            SpaceBefore = Math.Min(pap.Before, 31680) / 20.0,
            SpaceAfter = Math.Min(pap.After, 31680) / 20.0,
            LineSpacing = spacing,
            LineRule = rule,
            ContextualSpacing = pap.ContextualSpacing,
            KeepWithNext = pap.KeepFollow,
            KeepLines = pap.Keep,
            PageBreakBefore = pap.PageBreakBefore,
            WidowControl = pap.WidowControl,
            OutlineLevel = outline,
            StyleId = pap.Style.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Shading = pap.Shading,
            Borders = new BoxSides(Visible(pap.BorderTop), Visible(pap.BorderRight), Visible(pap.BorderBottom), Visible(pap.BorderLeft)),
            BetweenBorder = Visible(pap.BorderBetween),
            TabStops = tabs,
        };
    }

    private static BorderLine? Visible(BorderLine? line) => line is { IsVisible: true } ? line : null;

    /// <summary>The label of a list paragraph: the level's text counted, in the mark's format with the level's modifiers.</summary>
    private ListLabel? Label(DocPap pap, DocChp mark)
    {
        if (_lists.Count(pap.ListOverride, pap.ListLevel) is not { Length: > 0 } text || _lists.LevelOf(pap.ListOverride, pap.ListLevel) is not { } level)
        {
            return null;
        }

        var chp = mark;
        DocProperties.ApplyCharacter(ref chp, level.Chpx, mark);
        chp.Underline = 0;
        chp.Highlight = null;
        chp.Shading = null;
        chp.Vanish = false;
        var format = Format(chp);
        if (_styles.IsSymbolFont(chp.Font) || text.Any(c => c is >= '\uF020' and <= '\uF0FF'))
        {
            text = Symbols(text, _styles.FontName(chp.Font));
            format = format with { FontFamily = _styles.FontName(mark.Font) };
        }

        return new ListLabel(text, format, level.Suffix);
    }

    /// <summary>Text of a symbol font as the Unicode characters its glyphs show.</summary>
    private static string Symbols(string text, string? font)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c < 0x20 ? c.ToString() : Ooxml.SymbolCharacter(c, font));
        }

        return builder.ToString();
    }

    private static void StartParagraph(Story story, WordParagraph paragraph)
    {
        story.Targets.Clear();
        story.Targets.Add(paragraph.Inlines);

        // A hyperlink whose result goes on in this paragraph.
        foreach (var field in story.Fields)
        {
            if (field is { InResult: true, Action: FieldAction.Link })
            {
                var link = new HyperlinkInline { Uri = field.Uri, Anchor = field.Anchor };
                story.Targets[^1].Add(link);
                story.Targets.Add(link.Inlines);
                field.Link = link;
            }
        }
    }

    private void EndParagraph(Story story)
    {
        Flush(story);
        story.Targets.Clear();
    }

    /// <summary>Adds the pending text as a run.</summary>
    private void Flush(Story story)
    {
        if (story.Run.Length > 0 && story.RunFormat is { } format && story.Targets.Count > 0)
        {
            CountElement();
            story.Targets[^1].Add(new TextRun(story.Run.ToString(), format));
        }

        story.Run.Clear();
    }

    private void AddText(Story story, string text, RunFormat format)
    {
        if (!ReferenceEquals(format, story.RunFormat) && story.Run.Length > 0)
        {
            Flush(story);
        }

        story.RunFormat = format;
        story.Run.Append(text);
    }

    private void AddText(Story story, char c, RunFormat format)
    {
        if (!ReferenceEquals(format, story.RunFormat) && story.Run.Length > 0)
        {
            Flush(story);
        }

        story.RunFormat = format;
        story.Run.Append(c);
    }

    private void AddInline(Story story, WordInline inline)
    {
        Flush(story);
        if (story.Targets.Count > 0)
        {
            CountElement();
            story.Targets[^1].Add(inline);
        }
    }

    /// <summary>Reads the characters of a paragraph (before its mark) in runs of the same properties.</summary>
    private void ReadCharacters(Story story, int start, int end, int paragraphStyle)
    {
        var text = _file.Text;
        var cp = start;
        while (cp < end)
        {
            var (chp, format) = CharacterProperties(cp, paragraphStyle);
            var runEnd = RunEnd(cp, end);
            for (; cp < runEnd; cp++)
            {
                if (_bookmarks.TryGetValue(cp, out var names) && !InInstruction(story))
                {
                    foreach (var name in names)
                    {
                        AddInline(story, new BookmarkInline(name));
                    }
                }

                if (!chp.Deleted || _context.Has(ReadExtras.DeletedText))
                {
                    Character(story, cp, text[cp], chp, format);
                }
            }
        }
    }

    /// <summary>Where the run of characters with the properties at a position ends: at its piece's end or its CHPX run's.</summary>
    private int RunEnd(int cp, int end)
    {
        var pieceIndex = _file.PieceAt(cp);
        if (pieceIndex < 0)
        {
            return cp + 1;
        }

        var piece = _file.Pieces[pieceIndex];
        var fc = piece.FcOf(cp);
        var run = DocFile.RunAt(_file.CharacterRuns, fc);
        var limit = Math.Min(piece.CpEnd, end);
        if (run < 0)
        {
            return cp + 1;
        }

        var size = piece.Compressed ? 1 : 2;
        var count = (_file.CharacterRuns[run].FcEnd - fc + size - 1) / size;
        return (int)Math.Clamp(cp + count, cp + 1, limit);
    }

    private static bool InInstruction(Story story)
    {
        foreach (var field in story.Fields)
        {
            if (!field.InResult)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Handles one character: field characters, special characters, and text.</summary>
    private void Character(Story story, int cp, char c, DocChp chp, RunFormat format)
    {
        switch (c)
        {
            case '\u0013':
                BeginField(story);
                return;
            case '\u0014':
                SeparateField(story, format);
                return;
            case '\u0015':
                EndField(story);
                return;
        }

        // In an instruction: the text is the instruction (of the outermost field still in its instruction).
        foreach (var field in story.Fields)
        {
            if (!field.InResult)
            {
                if (c >= ' ' && field.Instruction.Length < 4096)
                {
                    field.Instruction.Append(c);
                }

                return;
            }
        }

        if (story.Fields.Count > 0 && story.Fields[^1].Action == FieldAction.Compute)
        {
            var field = story.Fields[^1];
            field.ResultFormat ??= format;
            if (c >= ' ' && field.Result.Length < 4096)
            {
                field.Result.Append(c);
            }

            return;
        }

        switch (c)
        {
            case '\t':
                AddInline(story, new TabInline(format));
                return;
            case '\v':
                AddInline(story, new BreakInline(BreakKind.Line, format));
                return;
            case '\f':
                AddInline(story, new BreakInline(BreakKind.Page, format));
                return;
            case '\u000E':
                AddInline(story, new BreakInline(BreakKind.Column, format));
                return;
            case '\u0001' when chp.Special || chp.PictureLocation >= 0:
                Picture(story, chp);
                return;
            case '\u0002' when chp.Special:
                NoteReference(story, cp, format);
                return;
            case '\u0008' when chp.Special:
                Drawing(story, cp);
                return;
            case '\u0005' when chp.Special:
                if (story.Kind == DocStory.Main && _commentReferences.TryGetValue(cp, out var comment))
                {
                    AddInline(story, new CommentReference("c" + comment.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }

                return;
            case '\u001E':
                AddText(story, "\u2011", format);
                return;
            case '(' when chp.Special && chp.SymbolFont >= 0:
                AddText(story, Ooxml.SymbolCharacter(chp.SymbolCharacter, _styles.FontName(chp.SymbolFont)), format with { FontFamily = null });
                return;
        }

        if (c < ' ')
        {
            return; // Optional hyphens, separators, comment references, missing text.
        }

        if ((c is >= '\uF020' and <= '\uF0FF') || (_styles.IsSymbolFont(chp.Font) && c < '\u0100'))
        {
            AddText(story, Ooxml.SymbolCharacter(c, _styles.FontName(chp.Font)), format with { FontFamily = null });
            return;
        }

        AddText(story, c, format);
    }

    /// <summary>A note reference in the text (its number), or in a note's own text (the note's number).</summary>
    private void NoteReference(Story story, int cp, RunFormat format)
    {
        if (story.Note)
        {
            AddInline(story, new NoteMark(format));
        }
        else if (story.Kind == DocStory.Main && _footnoteReferences.TryGetValue(cp, out var footnote))
        {
            AddInline(story, new NoteReference(NoteKind.Footnote, "f" + footnote.ToString(System.Globalization.CultureInfo.InvariantCulture), format));
        }
        else if (story.Kind == DocStory.Main && _endnoteReferences.TryGetValue(cp, out var endnote))
        {
            AddInline(story, new NoteReference(NoteKind.Endnote, "e" + endnote.ToString(System.Globalization.CultureInfo.InvariantCulture), format));
        }
    }

    private void BeginField(Story story)
    {
        Flush(story);
        if (story.Fields.Count < MaxFieldDepth)
        {
            var field = new FieldState();
            if (_context.Has(ReadExtras.FieldCodes) && story.Targets.Count > 0 && !InInstruction(story))
            {
                field.Marker = new FieldStart();
                AddInline(story, field.Marker);
            }

            story.Fields.Add(field);
        }
    }

    /// <summary>The separator: the instruction is complete, and decides what the result is.</summary>
    private void SeparateField(Story story, RunFormat format)
    {
        if (story.Fields.Count == 0 || story.Fields[^1].InResult)
        {
            return;
        }

        Flush(story);
        var field = story.Fields[^1];
        field.InResult = true;
        Classify(field);
        if (field.Action == FieldAction.Link && story.Targets.Count > 0 && !InInstruction(story))
        {
            var link = new HyperlinkInline { Uri = field.Uri, Anchor = field.Anchor };
            AddInline(story, link);
            story.Targets.Add(link.Inlines);
            field.Link = link;
        }
        else if (field.Action == FieldAction.Compute)
        {
            field.ResultFormat = format;
        }
    }

    private void EndField(Story story)
    {
        if (story.Fields.Count == 0)
        {
            return;
        }

        Flush(story);
        var field = story.Fields[^1];
        story.Fields.RemoveAt(story.Fields.Count - 1);
        if (!field.InResult)
        {
            Classify(field);
        }

        if (field.Link is { } link)
        {
            var index = story.Targets.FindLastIndex(t => ReferenceEquals(t, link.Inlines));
            if (index > 0)
            {
                story.Targets.RemoveRange(index, story.Targets.Count - index);
            }
        }
        else if (field.Action == FieldAction.Compute && !InInstruction(story))
        {
            var result = field.Result.ToString().Trim();
            AddInline(story, new FieldInline(field.Kind, field.ResultFormat ?? RunFormat.Default, result.Length > 0 ? result : null)
            {
                Argument = field.Argument,
                NumberFormat = field.NumberFormat,
            });
        }

        if (field.Marker is not null && story.Targets.Count > 0)
        {
            AddInline(story, new FieldEnd());
        }
    }

    /// <summary>Decides what a field does from its instruction (<c>PAGE</c>, <c>HYPERLINK "..."</c>).</summary>
    private static void Classify(FieldState field)
    {
        if (field.Marker is { } marker)
        {
            marker.Code = field.Instruction.ToString();
        }

        var tokens = DocxReader.Tokenize(field.Instruction.ToString());
        if (tokens.Count == 0)
        {
            return;
        }

        switch (tokens[0].ToUpperInvariant())
        {
            case "PAGE":
                (field.Action, field.Kind, field.NumberFormat) = (FieldAction.Compute, FieldKind.Page, DocxReader.FormatSwitch(tokens));
                break;
            case "NUMPAGES":
                (field.Action, field.Kind, field.NumberFormat) = (FieldAction.Compute, FieldKind.NumPages, DocxReader.FormatSwitch(tokens) ?? NumberFormat.Decimal);
                break;
            case "SECTIONPAGES":
                (field.Action, field.Kind, field.NumberFormat) = (FieldAction.Compute, FieldKind.SectionPages, DocxReader.FormatSwitch(tokens) ?? NumberFormat.Decimal);
                break;
            case "PAGEREF":
                if (tokens.Count > 1 && !tokens[1].StartsWith('\\'))
                {
                    (field.Action, field.Kind, field.Argument, field.NumberFormat) = (FieldAction.Compute, FieldKind.PageRef, tokens[1], DocxReader.FormatSwitch(tokens));
                }

                break;
            case "HYPERLINK":
                string? address = null, anchor = null;
                for (var i = 1; i < tokens.Count; i++)
                {
                    if (tokens[i] == "\\l" && i + 1 < tokens.Count)
                    {
                        anchor = tokens[++i];
                    }
                    else if (tokens[i].StartsWith('\\'))
                    {
                        i += tokens[i] is "\\o" or "\\t" ? 1 : 0;
                    }
                    else
                    {
                        address ??= tokens[i];
                    }
                }

                var uri = address is null ? null : DocxReader.LinkUri(address, anchor);
                if (uri is not null || anchor is not null)
                {
                    (field.Action, field.Uri, field.Anchor) = (FieldAction.Link, uri, uri is null ? anchor : null);
                }

                break;
        }
    }

    /// <summary>An inline picture ([MS-DOC] 2.9.192 PICFAndOfficeArtData at <c>sprmCPicLocation</c> in the Data stream).</summary>
    private void Picture(Story story, DocChp chp)
    {
        var name = $"Picture {++_pictures}";
        if (chp.Ole2)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An embedded object is shown as its preview picture.");
        }

        if (_drawings.Inline(chp.PictureLocation) is not { } picture)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"{name} is damaged or missing from the document; it is left out.");
            return;
        }

        AddImage(story, picture.Blip, name, picture.Width, picture.Height, ImagePlacement.Inline, null);
    }

    /// <summary>A floating drawing anchored here: a picture, or a text box whose text follows its paragraph.</summary>
    private void Drawing(Story story, int cp)
    {
        var header = story.Kind is DocStory.Headers or DocStory.HeaderTextBoxes;
        var shapes = header ? _headerShapes : _mainShapes;
        if (story.Kind is not (DocStory.Main or DocStory.Headers) || !shapes.TryGetValue(header ? cp - story.Start : cp, out var shape))
        {
            return;
        }

        if (shape.Blip > 0)
        {
            var name = $"Picture {++_pictures}";
            if (_drawings.StoredBlip(shape.Blip) is not { } blip)
            {
                _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"{name} is damaged or missing from the document; it is left out.");
                return;
            }

            var offset = (shape.Left / 20.0) - (shape.PageRelative && _section is { } section ? section.MarginLeft : 0);
            AddImage(story, blip, name, shape.Width / 20.0, shape.Height / 20.0, ImagePlacement.Float, Math.Max(0, offset));
        }
        else if (shape.TextBox > 0)
        {
            if (TextBox(shape.TextBox, header) is { } blocks)
            {
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "The text of a text box is laid out after its paragraph.");
                AddInline(story, blocks);
            }
        }
        else
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, "A drawing shape is left out.");
        }
    }

    private void AddImage(Story story, DocBlip blip, string name, double width, double height, ImagePlacement placement, double? offset)
    {
        if (blip.Data is null)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded,
                $"{name} is in {(blip.Kind.StartsWith("an ", StringComparison.Ordinal) ? blip.Kind : blip.Kind + " format")}, which cannot be drawn (PICT pictures are not supported); it is left out.");
            return;
        }

        if (blip.Data.Length > _context.Limits.MaxResourceBytes)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"{name} is larger than ResourceLimits.MaxResourceBytes allows ({_context.Limits.MaxResourceBytes} bytes); it is left out.");
            return;
        }

        if (blip.Data.Length == 0)
        {
            return;
        }

        if (width <= 0 || height <= 0)
        {
            (width, height) = (72, 72);
        }

        AddInline(story, new ImageInline(blip.Data, Math.Min(width, 1584), Math.Min(height, 1584)) { Name = name, Placement = placement, Offset = offset });
    }
}
