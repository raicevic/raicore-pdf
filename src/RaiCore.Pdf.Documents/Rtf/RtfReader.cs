using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>What the text and control words of a group are for.</summary>
internal enum RtfDestination
{
    /// <summary>Document text: the body, a header or footer, a note, a table cell, a shape's text.</summary>
    Text,

    /// <summary>Left out, with everything in it (an unknown <c>\*</c> destination, a comment, metadata).</summary>
    Skip,
    FontTable,
    FontEntry,
    ColorTable,
    StyleSheet,
    StyleEntry,
    ListTable,
    List,
    ListLevel,
    LevelText,
    ListOverrideTable,
    ListOverride,
    ListOverrideLevel,
    FieldInstruction,

    /// <summary>The result of a field whose value is computed (<c>PAGE</c>): kept as the fallback, not shown.</summary>
    FieldResult,
    Picture,
    Bookmark,

    /// <summary>A list label as the writer computed it (<c>\listtext</c>, <c>\pntext</c>).</summary>
    ListText,
    Info,
    Title,
    Author,
    Numbering,
    NumberingBefore,
    NumberingAfter,
    ShapePropertyName,

    /// <summary>A shape property's value: text left out, pictures (the shape's picture) kept.</summary>
    ShapePropertyValue,

    /// <summary>A <c>\upr</c> group: its ANSI version is left out, its Unicode version (<c>\ud</c>) read.</summary>
    Unicode,

    /// <summary>A child group of <c>\upr</c> (the ANSI version), unless it is <c>\ud</c>.</summary>
    UnicodeChild,
    DefaultCharacter,
    DefaultParagraph,
}

/// <summary>
/// Reads RTF ([MS-RTF] 1.9.1) into the format-independent <see cref="WordDocument"/>: the font, color and style
/// tables, lists, paragraphs and runs with their formatting, tables (nested ones too), sections with their page setup,
/// headers and footers, fields, pictures, bookmarks and notes. Text is decoded with the code pages of the document and
/// its fonts (<c>\ansicpg</c>, <c>\fcharset</c>, <c>\cpg</c>) and Unicode escapes (<c>\u</c> with <c>\uc</c>).
/// </summary>
/// <remarks>
/// The reader is a loop over the tokens with an explicit stack of groups: no recursion, so the stack depth does not
/// depend on the input. Groups are bounded by <see cref="ResourceLimits.MaxNestingDepth"/>, tokens by
/// <see cref="ResourceLimits.MaxElements"/>, table nesting by <see cref="ResourceLimits.MaxNestingDepth"/>, table cells
/// by <see cref="ResourceLimits.MaxTableCells"/> and pictures by <see cref="ResourceLimits.MaxResourceBytes"/>.
/// </remarks>
internal sealed partial class RtfReader
{
    /// <summary>How many tokens are read between checkpoints.</summary>
    private const int CheckpointInterval = 4096;

    private readonly RtfLexer _lexer;
    private readonly ConversionContext _context;
    private readonly WordDocument _document = new();
    private readonly List<Group> _groups = [];
    private int _depth;
    private long _tokens;

    // Text decoding.
    private readonly List<byte> _bytes = [];
    private Encoding? _bytesEncoding;
    private readonly StringBuilder _run = new();
    private RunFormat? _runFormat;
    private int _skip;

    private RtfReader(byte[] data, ConversionContext context)
    {
        _lexer = new RtfLexer(data, Start(data));
        _context = context;
    }

    /// <summary>The state of a group: its destination, formatting and what it opened (a story, a field).</summary>
    private sealed class Group
    {
        public RtfDestination Destination;
        public RtfCharState Chars;
        public RtfParagraphState Paragraph;
        public int UnicodeSkip = 1;
        public RtfRowDefinition? Row;

        /// <summary>Whether <c>\*</c> was seen: an unknown destination word that follows makes the group skipped.</summary>
        public bool Ignorable;

        /// <summary>The destination a <c>\upr</c> group's <c>\ud</c> child reads its text into.</summary>
        public RtfDestination UnicodeDestination;

        /// <summary>Data of the destination (a font, a style, a list level, a picture, a bookmark's name).</summary>
        public object? Data;

        /// <summary>The story the group opened (a header, a note), closed with it.</summary>
        public Story? Story;

        /// <summary>The field the group opened, closed with it.</summary>
        public FieldState? Field;

        public void CopyFrom(Group parent)
        {
            Destination = parent.Destination == RtfDestination.Unicode ? RtfDestination.UnicodeChild : ChildDestination(parent.Destination);
            UnicodeDestination = parent.Destination == RtfDestination.Unicode ? parent.UnicodeDestination : RtfDestination.Skip;
            Chars = parent.Chars;
            Paragraph = parent.Paragraph;
            UnicodeSkip = parent.UnicodeSkip;
            Row = parent.Row;
            Ignorable = false;
            Data = parent.Destination is RtfDestination.List or RtfDestination.ListOverride or RtfDestination.ListLevel or RtfDestination.StyleEntry
                or RtfDestination.Numbering or RtfDestination.ListOverrideLevel
                ? parent.Data
                : null;
            Story = null;
            Field = null;
        }

        /// <summary>The destination of a child group before its own control words: tables' entries are entries.</summary>
        private static RtfDestination ChildDestination(RtfDestination parent) => parent switch
        {
            RtfDestination.FontTable => RtfDestination.FontEntry,
            RtfDestination.StyleSheet => RtfDestination.StyleEntry,
            RtfDestination.FontEntry or RtfDestination.StyleEntry or RtfDestination.ColorTable or RtfDestination.LevelText
                or RtfDestination.Title or RtfDestination.Author or RtfDestination.Bookmark or RtfDestination.ShapePropertyName => RtfDestination.Skip,
            _ => parent,
        };
    }

    /// <summary>Reads an RTF document.</summary>
    /// <exception cref="DocumentFormatException">The data is not RTF (it does not start with <c>{\rtf</c>).</exception>
    public static WordDocument Read(byte[] data, ConversionContext context)
    {
        if (!IsRtf(data))
        {
            throw new DocumentFormatException(data.AsSpan().StartsWith("PK\u0003\u0004"u8)
                ? "The data is a ZIP package (such as a Word document), not an RTF document."
                : "The data is not an RTF document: it does not start with {\\rtf.");
        }

        return new RtfReader(data, context).ReadDocument();
    }

    /// <summary>Whether data starts as RTF does (<c>{\rtf</c>, after optional white space and a byte order mark).</summary>
    internal static bool IsRtf(ReadOnlySpan<byte> data) => data[Start(data)..].StartsWith(@"{\rtf"u8);

    /// <summary>Where the RTF starts: after a byte order mark and up to 64 bytes of white space.</summary>
    private static int Start(ReadOnlySpan<byte> data)
    {
        var i = data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var end = Math.Min(data.Length, i + 64);
        while (i < end && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i;
    }

    private Group Top => _groups[_depth - 1];

    private Group? Parent => _depth >= 2 ? _groups[_depth - 2] : null;

    private WordDocument ReadDocument()
    {
        BeginDocument();
        while (true)
        {
            var token = _lexer.Next();
            if (token.Kind == RtfTokenKind.End)
            {
                break;
            }

            if (++_tokens > _context.Limits.MaxElements)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _context.Limits.MaxElements,
                    $"The document has more tokens than ResourceLimits.MaxElements allows ({_context.Limits.MaxElements}).");
            }

            if (_tokens % CheckpointInterval == 0)
            {
                _context.Checkpoint();
            }

            if (token.Kind == RtfTokenKind.GroupStart)
            {
                PushGroup();
                continue;
            }

            if (_depth == 0)
            {
                if (token.Kind == RtfTokenKind.GroupEnd)
                {
                    continue;
                }

                // Content after the document's last brace (or before its first) is read as part of it.
                PushGroup();
            }

            if (token.Kind == RtfTokenKind.GroupEnd)
            {
                PopGroup();
                if (_depth == 0)
                {
                    break; // The end of the document: what follows its last brace is not RTF.
                }

                continue;
            }

            if (Top.Destination == RtfDestination.Skip)
            {
                continue;
            }

            switch (token.Kind)
            {
                case RtfTokenKind.Text:
                    var span = _lexer.Data.AsSpan(token.Start, token.Length);
                    if (_skip > 0)
                    {
                        var skipped = Math.Min(_skip, span.Length);
                        _skip -= skipped;
                        span = span[skipped..];
                    }

                    Text(span);
                    break;
                case RtfTokenKind.HexByte:
                    if (_skip > 0)
                    {
                        _skip--;
                    }
                    else
                    {
                        HexByte((byte)token.Value);
                    }

                    break;
                case RtfTokenKind.Binary:
                    if (_skip > 0)
                    {
                        _skip--;
                    }
                    else if (Top.Destination == RtfDestination.Picture)
                    {
                        PictureBytes(_lexer.Data.AsSpan(token.Start, token.Length));
                    }

                    break;
                case RtfTokenKind.ControlSymbol:
                    if (_skip > 0)
                    {
                        _skip--;
                    }
                    else
                    {
                        Symbol((char)token.Value);
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    if (_skip > 0 && token.Word != "par")
                    {
                        _skip--;
                        break;
                    }

                    Word(token.Word, token.Parameter, token.HasParameter);
                    break;
            }
        }

        while (_depth > 0)
        {
            PopGroup(); // A document whose groups are not all closed.
        }

        return EndDocument();
    }

    private void PushGroup()
    {
        if (_depth > 0)
        {
            FlushBytes();
        }

        if (_depth >= _context.Limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _context.Limits.MaxNestingDepth,
                $"The document nests groups deeper than ResourceLimits.MaxNestingDepth allows ({_context.Limits.MaxNestingDepth}).");
        }

        if (_groups.Count <= _depth)
        {
            _groups.Add(new Group());
        }

        var group = _groups[_depth];
        if (_depth == 0)
        {
            group.Destination = RtfDestination.Text;
            group.Chars = _defaultChars;
            group.Paragraph = _defaultParagraph;
            group.UnicodeSkip = 1;
            group.Row = null;
            group.Ignorable = false;
            group.Data = null;
            group.Story = null;
            group.Field = null;
        }
        else
        {
            group.CopyFrom(_groups[_depth - 1]);
        }

        _depth++;
        _skip = 0;
    }

    private void PopGroup()
    {
        FlushBytes();
        var group = Top;
        var parent = Parent;
        if (group.Story is not null || group.Field is not null || (parent is not null && group.Destination != parent.Destination))
        {
            EmitRun();
        }

        EndDestination(group, parent);
        group.Data = null;
        group.Story = null;
        group.Field = null;
        _depth--;
        _skip = 0;
    }

    /// <summary>Plain text: bytes in the current code page.</summary>
    private void Text(ReadOnlySpan<byte> text)
    {
        if (Top.Destination == RtfDestination.Picture)
        {
            PictureHex(text);
            return;
        }

        foreach (var b in text)
        {
            AppendByte(b);
        }
    }

    private void HexByte(byte value)
    {
        if (Top.Destination == RtfDestination.Picture)
        {
            return; // \' in picture data is not data.
        }

        AppendByte(value);
    }

    /// <summary>A control symbol: an escaped character, a special space or hyphen, or <c>\*</c>.</summary>
    private void Symbol(char symbol)
    {
        switch (symbol)
        {
            case '*':
                Top.Ignorable = true;
                return;
            case '\\' or '{' or '}':
                AppendChar(symbol);
                break;
            case '~':
                AppendChar('\u00A0');
                break;
            case '_':
                AppendChar('\u2011');
                break;
        }

        Top.Ignorable = false;
    }

    /// <summary>Appends a byte of text, decoded with the bytes before it in the same code page.</summary>
    private void AppendByte(byte b)
    {
        var encoding = CurrentEncoding();
        if (_bytes.Count > 0 && !ReferenceEquals(encoding, _bytesEncoding))
        {
            FlushBytes();
        }

        _bytesEncoding = encoding;
        _bytes.Add(b);
        if (_bytes.Count >= 4096 && !IsLeadByte(encoding, b))
        {
            FlushBytes();
        }
    }

    /// <summary>Appends a character (a Unicode escape or a special character).</summary>
    private void AppendChar(char c)
    {
        FlushBytes();
        Span<char> one = [c];
        Sink(one);
    }

    /// <summary>Decodes the pending bytes into the destination.</summary>
    private void FlushBytes()
    {
        if (_bytes.Count == 0)
        {
            return;
        }

        var encoding = _bytesEncoding ?? Latin1;
        var bytes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes);
        var chars = encoding.GetString(bytes);
        _bytes.Clear();
        Sink(chars);
    }

    private static bool IsLeadByte(Encoding encoding, byte b) => encoding.IsSingleByte ? false : b >= 0x81;

    /// <summary>Text for the current destination.</summary>
    private void Sink(ReadOnlySpan<char> text)
    {
        var group = Top;
        switch (group.Destination)
        {
            case RtfDestination.Text:
                BodyText(text);
                break;
            case RtfDestination.FieldInstruction:
                if (CurrentField() is { } field && field.Instruction.Length < 4096)
                {
                    field.Instruction.Append(text);
                }

                break;
            case RtfDestination.FieldResult:
                if (CurrentField() is { } result && !group.Chars.Hidden && !group.Chars.Deleted && result.Result.Length < 4096)
                {
                    result.ResultFormat ??= Format(group.Chars);
                    result.Result.Append(text);
                }

                break;
            case RtfDestination.FontTable:
            case RtfDestination.FontEntry:
                FontText(text);
                break;
            case RtfDestination.ColorTable:
                foreach (var c in text)
                {
                    if (c == ';')
                    {
                        EndColor();
                    }
                }

                break;
            case RtfDestination.StyleEntry:
                if (group.Data is RtfStyle style && style.Name.Length < 256)
                {
                    style.Name += text.ToString();
                }

                break;
            case RtfDestination.LevelText:
                if (group.Data is RtfListLevel level && level.Text.Length < 256)
                {
                    level.Text += text.ToString();
                }

                break;
            case RtfDestination.Title:
            case RtfDestination.Author:
                var metadata = group.Destination == RtfDestination.Title ? _title : _author;
                if (metadata.Length < 1024)
                {
                    metadata.Append(text);
                }

                break;
            case RtfDestination.Bookmark:
                if (group.Data is StringBuilder name && name.Length < 256)
                {
                    name.Append(text);
                }

                break;
            case RtfDestination.ListText:
                ListText(text);
                break;
            case RtfDestination.NumberingBefore:
            case RtfDestination.NumberingAfter:
                if (group.Data is RtfParagraphNumbering numbering)
                {
                    var value = MapSymbols(text.ToString(), group.Chars.Font >= 0 ? group.Chars.Font : numbering.Font);
                    if (group.Destination == RtfDestination.NumberingBefore)
                    {
                        numbering.Before = (numbering.Before + value)[..Math.Min(64, numbering.Before.Length + value.Length)];
                    }
                    else
                    {
                        numbering.After = (numbering.After + value)[..Math.Min(64, numbering.After.Length + value.Length)];
                    }
                }

                break;
            case RtfDestination.ShapePropertyName:
                if (_shapeProperty.Length < 64)
                {
                    _shapeProperty.Append(text);
                }

                break;
        }
    }

    /// <summary>A control word.</summary>
    private void Word(string word, int parameter, bool hasParameter)
    {
        FlushBytes();
        var group = Top;
        var ignorable = group.Ignorable;
        group.Ignorable = false;

        if (word == "u")
        {
            var code = parameter < 0 ? parameter + 65536 : parameter; // Negative for code points above 32767.
            AppendChar(code is >= 0 and <= 0xFFFF ? (char)code : '\uFFFD');
            _skip = Top.UnicodeSkip;
            return;
        }

        if (word == "uc")
        {
            group.UnicodeSkip = Math.Clamp(parameter, 0, 16);
            return;
        }

        if (Destination(word, parameter, hasParameter, ignorable))
        {
            return;
        }

        if (ignorable)
        {
            group.Destination = RtfDestination.Skip; // An unknown destination that readers may skip.
            return;
        }

        switch (group.Destination)
        {
            case RtfDestination.Text:
            case RtfDestination.FieldResult:
                TextWord(word, parameter, hasParameter);
                break;
            case RtfDestination.FieldInstruction:
            case RtfDestination.ListText:
            case RtfDestination.NumberingBefore:
            case RtfDestination.NumberingAfter:
            case RtfDestination.Title:
            case RtfDestination.Author:
            case RtfDestination.Bookmark:
                if (SpecialCharacter(word) is { } special)
                {
                    AppendChar(special);
                }
                else
                {
                    ApplyCharacter(ref group.Chars, word, parameter, hasParameter);
                }

                break;
            case RtfDestination.FontTable:
            case RtfDestination.FontEntry:
                FontWord(word, parameter, hasParameter);
                break;
            case RtfDestination.ColorTable:
                ColorWord(word, parameter);
                break;
            case RtfDestination.StyleEntry:
                StyleWord(word, parameter, hasParameter);
                break;
            case RtfDestination.List:
            case RtfDestination.ListLevel:
            case RtfDestination.ListOverride:
            case RtfDestination.ListOverrideLevel:
                ListWord(word, parameter, hasParameter);
                break;
            case RtfDestination.Picture:
                PictureWord(word, parameter, hasParameter);
                break;
            case RtfDestination.Numbering:
                NumberingWord(word, parameter, hasParameter);
                break;
            case RtfDestination.DefaultCharacter:
                ApplyCharacter(ref _defaultChars, word, parameter, hasParameter);
                break;
            case RtfDestination.DefaultParagraph:
                ApplyParagraph(ref _defaultParagraph, word, parameter, hasParameter);
                break;
            case RtfDestination.UnicodeChild:
                break;
        }
    }

    /// <summary>
    /// Handles a control word that starts a destination (in the current group), returning whether it did. Some only
    /// count in their context (<c>\cs</c> in the style sheet, <c>\listlevel</c> in a list).
    /// </summary>
    private bool Destination(string word, int parameter, bool hasParameter, bool ignorable)
    {
        var group = Top;
        var current = group.Destination;
        switch (word)
        {
            case "fonttbl":
                SetDestination(RtfDestination.FontTable);
                return true;
            case "colortbl":
                SetDestination(RtfDestination.ColorTable);
                _colors.Clear();
                _color = default;
                return true;
            case "stylesheet":
                SetDestination(RtfDestination.StyleSheet);
                return true;
            case "listtable":
                SetDestination(RtfDestination.ListTable);
                return true;
            case "listoverridetable":
                SetDestination(RtfDestination.ListOverrideTable);
                return true;
            case "list" when current == RtfDestination.ListTable:
                var list = new RtfList();
                _lists.Add(list);
                group.Data = list;
                SetDestination(RtfDestination.List);
                return true;
            case "listlevel" when current is RtfDestination.List or RtfDestination.ListOverrideLevel:
                StartListLevel(group);
                return true;
            case "leveltext" when current == RtfDestination.ListLevel:
                SetDestination(RtfDestination.LevelText);
                return true;
            case "levelnumbers" when current == RtfDestination.ListLevel:
            case "listname" when current == RtfDestination.List:
                SetDestination(RtfDestination.Skip);
                return true;
            case "listoverride" when current == RtfDestination.ListOverrideTable:
                var listOverride = new RtfListOverride();
                group.Data = listOverride;
                SetDestination(RtfDestination.ListOverride);
                return true;
            case "lfolevel" when current == RtfDestination.ListOverride:
                if (group.Data is RtfListOverride owner)
                {
                    group.Data = new OverrideLevel(owner, Math.Min(owner.LevelCount++, 8));
                }

                SetDestination(RtfDestination.ListOverrideLevel);
                return true;
            case "s" or "cs" or "ts" or "ds" when current == RtfDestination.StyleEntry:
                StyleWord(word, parameter, hasParameter);
                return true;
            case "info":
                SetDestination(RtfDestination.Info);
                return true;
            case "title" when current == RtfDestination.Info:
                SetDestination(RtfDestination.Title);
                return true;
            case "author" when current == RtfDestination.Info:
                SetDestination(RtfDestination.Author);
                return true;
            case "defchp":
                SetDestination(RtfDestination.DefaultCharacter);
                return true;
            case "defpap":
                SetDestination(RtfDestination.DefaultParagraph);
                return true;
        }

        if (current is not (RtfDestination.Text or RtfDestination.FieldResult or RtfDestination.FieldInstruction or RtfDestination.ShapePropertyValue
            or RtfDestination.Unicode or RtfDestination.UnicodeChild or RtfDestination.ListText))
        {
            return false;
        }

        if (current == RtfDestination.UnicodeChild)
        {
            if (word == "ud")
            {
                group.Destination = group.UnicodeDestination;
                return true;
            }

            return ignorable;
        }

        if (current == RtfDestination.ListText && word is not ("pict" or "field"))
        {
            return false;
        }

        if (current == RtfDestination.FieldInstruction)
        {
            if (word is "field" or "fldrslt" or "pict" or "footnote" or "object" or "shp")
            {
                SetDestination(RtfDestination.Skip); // Nested fields in an instruction are left out.
                return true;
            }

            return false;
        }

        switch (word)
        {
            case "field":
                StartField(group);
                return true;
            case "fldinst":
                SetDestination(RtfDestination.FieldInstruction);
                return true;
            case "fldrslt":
                StartFieldResult(group);
                return true;
            case "pict":
                StartPicture(group);
                return true;
            case "shppict":
                return true;
            case "nonshppict":
            case "nonesttables":
            case "shprslt":
            case "xe":
            case "tc":
            case "tcn":
            case "txe":
            case "rxe":
            case "bkmkend":
            case "atnid":
            case "atnauthor":
            case "atndate":
            case "atnref":
            case "atnparent":
            case "atrfstart":
            case "atrfend":
            case "objdata":
            case "objclass":
            case "objname":
                SetDestination(RtfDestination.Skip);
                return true;
            case "annotation":
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "Comments (annotations) are left out.");
                SetDestination(RtfDestination.Skip);
                return true;
            case "do":
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "Drawing objects (\\do) are left out.");
                SetDestination(RtfDestination.Skip);
                return true;
            case "object":
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "An embedded object is shown as its result (its last picture or text).");
                return true;
            case "result":
                return true;
            case "shp":
                _context.Report(DocumentDiagnosticKind.UnsupportedContent, "Shapes are not drawn; their pictures and text are laid out in the text.");
                return true;
            case "shpinst":
                return true;
            case "sp":
                return true;
            case "sn":
                _shapeProperty.Clear();
                SetDestination(RtfDestination.ShapePropertyName);
                return true;
            case "sv":
                SetDestination(_shapeProperty.ToString().Trim() == "pib" ? RtfDestination.ShapePropertyValue : RtfDestination.Skip);
                return true;
            case "shptxt":
                StartStory(group, StoryKind.Shape);
                return true;
            case "bkmkstart":
                group.Data = new StringBuilder();
                SetDestination(RtfDestination.Bookmark);
                return true;
            case "listtext":
            case "pntext":
                StartListText(group);
                return true;
            case "pn":
                group.Data = new RtfParagraphNumbering();
                SetDestination(RtfDestination.Numbering);
                return true;
            case "pnseclvl":
                group.Data = new RtfParagraphNumbering { Level = Math.Clamp(parameter, 1, 9) };
                _sectionLevels[Math.Clamp(parameter, 1, 9) - 1] = (RtfParagraphNumbering)group.Data;
                SetDestination(RtfDestination.Numbering);
                return true;
            case "upr":
                group.UnicodeDestination = current;
                SetDestination(RtfDestination.Unicode);
                return true;
            case "ud":
                return true;
            case "footnote":
                StartStory(group, StoryKind.Footnote);
                return true;
            case "header":
            case "headerr":
            case "headerl":
            case "headerf":
            case "footer":
            case "footerr":
            case "footerl":
            case "footerf":
                if (CurrentStory.Kind != StoryKind.Body || current != RtfDestination.Text)
                {
                    SetDestination(RtfDestination.Skip);
                    return true;
                }

                StartStory(group, word.StartsWith("header", StringComparison.Ordinal) ? StoryKind.Header : StoryKind.Footer);
                group.Data = word switch
                {
                    "header" or "footer" => AllPages, // Odd and even pages, unless \headerl gives even pages their own.
                    "headerf" or "footerf" => HeaderFooterKind.First,
                    "headerl" or "footerl" => HeaderFooterKind.Even,
                    _ => HeaderFooterKind.Default,
                };
                return true;
            case "nesttableprops":
                return true;
        }

        return false;
    }

    private void SetDestination(RtfDestination destination)
    {
        EmitRun();
        Top.Destination = destination;
    }

    /// <summary>Ends what a group opened when it closes: a font, a style, a list, a picture, a story, a field.</summary>
    private void EndDestination(Group group, Group? parent)
    {
        switch (group.Destination)
        {
            case RtfDestination.FontEntry:
            case RtfDestination.FontTable:
                EndFont();
                break;
            case RtfDestination.StyleEntry when parent?.Destination == RtfDestination.StyleSheet:
                EndStyle(group);
                break;
            case RtfDestination.LevelText:
                break;
            case RtfDestination.Picture:
                EndPicture(group);
                break;
            case RtfDestination.Bookmark:
                if (group.Data is StringBuilder name && name.ToString().Trim() is { Length: > 0 } bookmark && !bookmark.StartsWith("_GoBack", StringComparison.Ordinal)
                    && parent is not null && parent.Destination == RtfDestination.Text)
                {
                    AddInline(new BookmarkInline(Clean(bookmark)));
                }

                break;
            case RtfDestination.Numbering:
                if (group.Data is RtfParagraphNumbering numbering && parent is not null && Array.IndexOf(_sectionLevels, numbering) < 0)
                {
                    parent.Paragraph.Numbering = numbering;
                }

                break;
            case RtfDestination.ListOverride:
                if (group.Data is RtfListOverride listOverride && parent?.Destination == RtfDestination.ListOverrideTable)
                {
                    _overrides.TryAdd(listOverride.Number, listOverride);
                }

                break;
        }

        // A story opened in a field's group (or a field in a story's) closes first: it was opened inside.
        if (group.Story is not null)
        {
            EndStory(group);
        }

        if (group.Field is not null)
        {
            EndField(group.Field);
        }

        if (parent is null)
        {
            EndLastSection();
        }
    }
}
