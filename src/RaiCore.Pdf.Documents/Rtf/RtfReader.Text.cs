using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>Stories (body, headers, footers, notes, shape text), paragraphs, runs and the control words of text.</summary>
internal sealed partial class RtfReader
{
    /// <summary>The <see cref="Group.Data"/> of <c>\header</c> and <c>\footer</c>: for all pages.</summary>
    private static readonly object AllPages = new();

    private readonly List<Story> _stories = [];

    // The sections whose even pages have a header or footer of their own (\headerl, \footerl).
    private readonly HashSet<WordSection> _evenHeader = [];
    private readonly HashSet<WordSection> _evenFooter = [];
    private int _noteNumber;

    /// <summary>Kinds of <see cref="Story"/>.</summary>
    private enum StoryKind
    {
        Body,
        Header,
        Footer,
        Footnote,
        Shape,

        /// <summary>A comment (<c>\annotation</c>), read only for text extraction.</summary>
        Comment,
    }

    /// <summary>
    /// A flow of text with its own blocks: the body, a header or footer, a note, a shape's text. It has the paragraph
    /// being read, the tables open at each nesting level and the fields open in it.
    /// </summary>
    private sealed class Story(StoryKind kind)
    {
        public StoryKind Kind { get; } = kind;

        public List<WordBlock> Blocks { get; } = [];

        /// <summary>The tables being read, by nesting level (index 0 is level 1).</summary>
        public List<TableBuilder> Tables { get; } = [];

        public ParagraphBuilder? Paragraph { get; set; }

        public List<FieldState> Fields { get; } = [];

        /// <summary>The format of an automatic note number (<c>\chftn</c>) waiting for its <c>\footnote</c>, or <c>null</c>.</summary>
        public RunFormat? PendingNoteMark { get; set; }

        /// <summary>Whether a note is an endnote (<c>\ftnalt</c>).</summary>
        public bool Endnote { get; set; }

        /// <summary>The old-style numbering level and number of the paragraph before, or -1.</summary>
        public (int Level, int Number) Numbered { get; set; } = (-1, 0);
    }

    /// <summary>The content of a paragraph being read, and the label its writer computed (<c>\listtext</c>).</summary>
    private sealed class ParagraphBuilder
    {
        public List<WordInline> Inlines { get; } = [];

        public StringBuilder? LabelText { get; set; }

        public RtfCharState LabelChars { get; set; }
    }

    private Story CurrentStory => _stories[^1];

    /// <summary>
    /// Text of the document: a run in the current format (hidden and deleted text left out, unless text extraction
    /// keeps them).
    /// </summary>
    private void BodyText(ReadOnlySpan<char> text)
    {
        var chars = Top.Chars;
        if (LeftOut(chars) || text.IsEmpty)
        {
            return;
        }

        var format = Format(chars);
        if (_run.Length > 0 && !ReferenceEquals(format, _runFormat) && !format.Equals(_runFormat))
        {
            EmitRun();
        }

        _runFormat = format;
        if (FontOf(chars.Font) is { Symbol: true })
        {
            _run.Append(MapSymbols(text.ToString(), chars.Font));
        }
        else
        {
            _run.Append(text);
        }
    }

    /// <summary>Whether text in a format is left out: hidden or deleted text, unless text extraction keeps it.</summary>
    private bool LeftOut(RtfCharState chars) =>
        (chars.Hidden && !_context.Has(ReadExtras.HiddenText)) || (chars.Deleted && !_context.Has(ReadExtras.DeletedText));

    /// <summary>Adds the pending run of text to the paragraph.</summary>
    private void EmitRun()
    {
        FlushBytes();
        if (_run.Length == 0 || _runFormat is null)
        {
            _run.Clear();
            return;
        }

        var run = new TextRun(Clean(_run.ToString()), _runFormat);
        _run.Clear();
        AddInlineCore(run);
    }

    /// <summary>Adds inline content (after the pending text) to the paragraph, inside the open hyperlink if any.</summary>
    private void AddInline(WordInline inline)
    {
        EmitRun();
        AddInlineCore(inline);
    }

    private void AddInlineCore(WordInline inline)
    {
        var story = CurrentStory;
        var paragraph = story.Paragraph ??= new ParagraphBuilder();
        FieldState? link = null;
        for (var i = story.Fields.Count - 1; i >= 0; i--)
        {
            if (story.Fields[i].Action == FieldAction.Link && story.Fields[i].InResult)
            {
                link = story.Fields[i];
                break;
            }
        }

        if (link is null)
        {
            paragraph.Inlines.Add(inline);
            return;
        }

        if (link.Link is null || !ReferenceEquals(link.LinkOwner, paragraph))
        {
            // A link continues in each paragraph its result spans.
            link.Link = new HyperlinkInline { Uri = link.Uri, Anchor = link.Anchor };
            link.LinkOwner = paragraph;
            paragraph.Inlines.Add(link.Link);
        }

        link.Link.Inlines.Add(inline);
    }

    /// <summary>A control word in document text: special characters, breaks, structure, then formatting.</summary>
    private void TextWord(string word, int parameter, bool hasParameter)
    {
        var group = Top;
        if (SpecialCharacter(word) is { } special)
        {
            AppendChar(special);
            return;
        }

        var inResult = group.Destination == RtfDestination.FieldResult;
        switch (word)
        {
            case "par" when !inResult:
                EndParagraph();
                return;
            case "line" when !inResult:
                AddInline(new BreakInline(BreakKind.Line, Format(group.Chars)));
                return;
            case "page" when !inResult:
                AddInline(new BreakInline(BreakKind.Page, Format(group.Chars)));
                return;
            case "column" when !inResult:
                AddInline(new BreakInline(BreakKind.Column, Format(group.Chars)));
                return;
            case "tab" when !inResult:
                if (!LeftOut(group.Chars))
                {
                    AddInline(new TabInline(Format(group.Chars)));
                }

                return;
            case "sect" when !inResult:
                if (CurrentStory.Kind == StoryKind.Body)
                {
                    EndSection();
                }

                return;
            case "cell" when !inResult:
                EndCell(1);
                return;
            case "nestcell" when !inResult:
                EndCell(Math.Max(2, NestingLevel(group.Paragraph)));
                return;
            case "row" when !inResult:
                EndRow(1, group);
                return;
            case "nestrow" when !inResult:
                EndRow(Math.Max(2, NestingLevel(group.Paragraph)), group);
                return;
            case "chftn":
                if (CurrentStory.Kind == StoryKind.Footnote)
                {
                    AddInline(new NoteMark(Format(group.Chars)));
                }
                else
                {
                    EmitRun();
                    CurrentStory.PendingNoteMark = Format(group.Chars);
                }

                return;
            case "ftnalt":
                if (CurrentStory.Kind == StoryKind.Footnote)
                {
                    CurrentStory.Endnote = true;
                }

                return;
            case "chpgn":
                AddInline(new FieldInline(FieldKind.Page, Format(group.Chars), null));
                return;
        }

        if (ApplyCharacter(ref group.Chars, word, parameter, hasParameter) || ApplyParagraph(ref group.Paragraph, word, parameter, hasParameter)
            || RowWord(group, word, parameter, hasParameter) || SectionWord(word, parameter, hasParameter))
        {
            return;
        }

        DocumentWord(word, parameter, hasParameter);
    }

    /// <summary>
    /// Text with surrogates that are not part of a pair (a Unicode escape for half a character, or a pair split by
    /// formatting) replaced by U+FFFD, which text layout and PDF strings need.
    /// </summary>
    internal static string Clean(string text)
    {
        var index = text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF');
        if (index < 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = index; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '\uFFFD';
            }
        }

        return new string(chars);
    }

    /// <summary>The character of a control word that stands for one (<c>\emdash</c>, <c>\lquote</c>), or <c>null</c>.</summary>
    private static char? SpecialCharacter(string word) => word switch
    {
        "emdash" => '\u2014',
        "endash" => '\u2013',
        "emspace" => '\u2003',
        "enspace" => '\u2002',
        "qmspace" => '\u2005',
        "bullet" => '\u2022',
        "lquote" => '\u2018',
        "rquote" => '\u2019',
        "ldblquote" => '\u201C',
        "rdblquote" => '\u201D',
        "zwj" => '\u200D',
        "zwnj" => '\u200C',
        "zwbo" => '\u200B',
        "ltrmark" => '\u200E',
        "rtlmark" => '\u200F',
        _ => null,
    };

    /// <summary>The table nesting level of a paragraph: 0 outside tables.</summary>
    private int NestingLevel(RtfParagraphState paragraph)
    {
        var level = paragraph.InTable ? Math.Max(1, paragraph.NestingLevel) : 0;
        if (level > _context.Limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _context.Limits.MaxNestingDepth,
                $"The document nests tables deeper than ResourceLimits.MaxNestingDepth allows ({_context.Limits.MaxNestingDepth}).");
        }

        return level;
    }

    /// <summary>Ends the paragraph (<c>\par</c>): its format is the paragraph and character state at its mark.</summary>
    private void EndParagraph()
    {
        EmitRun();
        var story = CurrentStory;
        var paragraph = BuildParagraph(story);
        Place(story, paragraph, NestingLevel(Top.Paragraph));
    }

    /// <summary>Makes the story's current paragraph (empty if none was started) with the current formatting and its label.</summary>
    private WordParagraph BuildParagraph(Story story)
    {
        var group = Top;
        var builder = story.Paragraph;
        story.Paragraph = null;
        var state = group.Paragraph;
        var format = Format(state);
        var paragraph = new WordParagraph { MarkFormat = Format(group.Chars) };
        if (builder is not null)
        {
            paragraph.Inlines.AddRange(builder.Inlines);
        }

        paragraph.Label = Label(story, state, group.Chars, builder, ref format) is { } label ? label with { Text = Clean(label.Text) } : null;
        paragraph.Format = format;
        return paragraph;
    }

    /// <summary>Adds a block to a story at a table nesting level, closing deeper tables and opening missing ones.</summary>
    private void Place(Story story, WordBlock block, int level)
    {
        CloseTables(story, level);
        while (story.Tables.Count < level)
        {
            story.Tables.Add(new TableBuilder());
        }

        (level == 0 ? story.Blocks : story.Tables[level - 1].Cell).Add(block);
    }

    /// <summary>Starts a story (a header, footer, note or shape text) in a group, with formatting reset.</summary>
    private void StartStory(Group group, StoryKind kind)
    {
        EmitRun();
        if (group.Story is not null || group.Field is not null)
        {
            group.Destination = RtfDestination.Skip; // A second story or a story in a field's own group: malformed.
            return;
        }

        var story = new Story(kind);
        _stories.Add(story);
        group.Story = story;
        group.Destination = RtfDestination.Text;
        group.Paragraph = _defaultParagraph;
        group.Chars = _defaultChars;
        group.Row = null;
    }

    /// <summary>Ends a story when its group closes, and puts its blocks where they belong.</summary>
    private void EndStory(Group group)
    {
        var story = group.Story!;
        if (story.Paragraph is not null)
        {
            EndParagraph();
        }

        CloseTables(story, 0);
        _stories.Remove(story);
        switch (story.Kind)
        {
            case StoryKind.Footnote:
                var id = (++_noteNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                (story.Endnote ? _document.Endnotes : _document.Footnotes)[id] = story.Blocks;
                var parent = CurrentStory;
                var mark = parent.PendingNoteMark;
                parent.PendingNoteMark = null;
                AddInline(new NoteReference(story.Endnote ? NoteKind.Endnote : NoteKind.Footnote, id, mark ?? Format(Parent?.Chars ?? _defaultChars))
                {
                    CustomMark = mark is null ? string.Empty : null, // Without \chftn, the mark is the text before the note.
                });
                break;
            case StoryKind.Header:
            case StoryKind.Footer:
                var parts = story.Kind == StoryKind.Header ? _section.Headers : _section.Footers;
                var explicitEven = story.Kind == StoryKind.Header ? _evenHeader : _evenFooter;
                if (group.Data is HeaderFooterKind kind)
                {
                    parts[kind] = story.Blocks;
                    explicitEven.Add(_section);
                }
                else
                {
                    parts[HeaderFooterKind.Default] = story.Blocks;
                    if (!explicitEven.Contains(_section))
                    {
                        parts[HeaderFooterKind.Even] = story.Blocks;
                    }
                }

                break;
            case StoryKind.Comment:
                if (group.Data is string comment && _document.Comments.TryGetValue(comment, out var annotation))
                {
                    annotation.Blocks.AddRange(story.Blocks);
                }

                break;
            case StoryKind.Shape:
                if (story.Blocks.Count > 0)
                {
                    var anchored = new AnchoredBlocks();
                    anchored.Blocks.AddRange(story.Blocks);
                    AddInline(anchored);
                }

                break;
        }
    }

    /// <summary>Starts capturing a list label as the writer computed it (<c>\listtext</c> or <c>\pntext</c>).</summary>
    private void StartListText(Group group)
    {
        SetDestination(RtfDestination.ListText);
        var paragraph = CurrentStory.Paragraph ??= new ParagraphBuilder();
        paragraph.LabelText = new StringBuilder();
        paragraph.LabelChars = group.Chars;
    }

    private void ListText(ReadOnlySpan<char> text)
    {
        if (CurrentStory.Paragraph is not { LabelText: { } label } paragraph)
        {
            return;
        }

        var chars = Top.Chars;
        if (label.Length < 64)
        {
            label.Append(MapSymbols(text.ToString(), chars.Font));
        }

        paragraph.LabelChars = chars;
    }
}
