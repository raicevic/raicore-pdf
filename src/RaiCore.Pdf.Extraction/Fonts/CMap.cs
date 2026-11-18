using System.Text;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Extraction.Fonts;

/// <summary>
/// A CMap (Adobe Technical Note #5014, ISO 32000-2 9.7.5 and 9.10.3): how a composite font's string bytes split into
/// codes (codespace ranges), and what those codes map to: CIDs (an encoding CMap) or text (a ToUnicode CMap). Codes
/// are kept with their length in bytes, since <c>&lt;41&gt;</c> and <c>&lt;0041&gt;</c> are different codes.
/// </summary>
internal sealed class CMap
{
    /// <summary>The most mappings (single codes and ranges together) one CMap may hold.</summary>
    internal const int MaxMappings = 200_000;

    /// <summary>
    /// The most operands one section (<c>beginbfchar</c> … <c>endbfchar</c>) may have: 30,000 single codes or 20,000
    /// ranges. The limit is 100 entries, but producers write more.
    /// </summary>
    internal const int MaxSectionOperands = 60_000;

    private readonly List<CodespaceRange> _codespace = [];
    private readonly Dictionary<long, string> _textChars = [];
    private readonly List<TextRange> _textRanges = [];
    private readonly Dictionary<long, int> _cidChars = [];
    private readonly List<CidRange> _cidRanges = [];
    private bool _rangesSorted = true;

    /// <summary>The CMap's own name (<c>/CMapName</c>), if it has one.</summary>
    public string? Name { get; private set; }

    /// <summary>The name of the CMap it builds on (<c>usecmap</c>), if any.</summary>
    public string? UseCMap { get; private set; }

    /// <summary>Whether the CMap is for vertical writing (<c>/WMode 1</c>).</summary>
    public bool IsVertical { get; private set; }

    /// <summary>Whether it has codespace ranges (else codes are split by the caller's default).</summary>
    public bool HasCodespace => _codespace.Count > 0;

    /// <summary>Whether it maps any code to text.</summary>
    public bool HasText => _textChars.Count > 0 || _textRanges.Count > 0;

    private int MappingCount => _textChars.Count + _textRanges.Count + _cidChars.Count + _cidRanges.Count;

    /// <summary>A CMap of fixed-length codes that map to themselves as CIDs (Identity-H and Identity-V: two bytes).</summary>
    public static CMap Identity(int length, bool vertical)
    {
        var cmap = new CMap { Name = vertical ? "Identity-V" : "Identity-H", IsVertical = vertical };
        cmap._codespace.Add(new CodespaceRange(new byte[length], Enumerable.Repeat((byte)0xFF, length).ToArray()));
        cmap._cidRanges.Add(new CidRange(length, 0, length == 1 ? 0xFFu : length == 2 ? 0xFFFFu : uint.MaxValue, 0));
        return cmap;
    }

    /// <summary>
    /// How many bytes the code at the start of <paramref name="bytes"/> has: the length of the codespace range it is
    /// in; when it is in none, the length of a range its first byte fits (as Acrobat does), else
    /// <paramref name="fallback"/>.
    /// </summary>
    public int CodeLength(ReadOnlySpan<byte> bytes, int fallback)
    {
        foreach (var range in _codespace)
        {
            if (range.Matches(bytes))
            {
                return range.Length;
            }
        }

        foreach (var range in _codespace)
        {
            if (bytes.Length >= range.Length && bytes[0] >= range.Low[0] && bytes[0] <= range.High[0])
            {
                return range.Length;
            }
        }

        return Math.Clamp(fallback, 1, Math.Max(1, bytes.Length));
    }

    /// <summary>The text a code maps to, or <c>null</c>. A code of another length with the same value is tried last, for maps written with the wrong code length.</summary>
    public string? Text(uint code, int length)
    {
        if (TextExact(code, length) is { } text)
        {
            return text;
        }

        for (var other = 1; other <= 4; other++)
        {
            if (other != length && (other == 4 || code < 1u << (8 * other)) && TextExact(code, other) is { } loose)
            {
                return loose;
            }
        }

        return null;
    }

    /// <summary>The CID a code maps to, or <c>null</c>.</summary>
    public int? Cid(uint code, int length)
    {
        if (_cidChars.TryGetValue(Key(code, length), out var cid))
        {
            return cid;
        }

        for (var i = _cidRanges.Count - 1; i >= 0; i--)
        {
            var range = _cidRanges[i];
            if (range.Length == length && code >= range.Low && code <= range.High)
            {
                var value = (long)range.Cid + (code - range.Low);
                return value <= int.MaxValue ? (int)value : null;
            }
        }

        return null;
    }

    /// <summary>Adds the mappings of <paramref name="parent"/> that this CMap does not have (for <c>usecmap</c>).</summary>
    public void Inherit(CMap parent)
    {
        if (_codespace.Count == 0)
        {
            _codespace.AddRange(parent._codespace);
        }

        foreach (var (key, cid) in parent._cidChars)
        {
            _cidChars.TryAdd(key, cid);
        }

        _cidRanges.InsertRange(0, parent._cidRanges); // Searched last to first: this CMap's own ranges win.
        foreach (var (key, text) in parent._textChars)
        {
            _textChars.TryAdd(key, text);
        }

        _textRanges.InsertRange(0, parent._textRanges);
        _rangesSorted = false;
    }

    /// <summary>Parses a CMap stream's (decoded) data. Malformed parts are skipped.</summary>
    public static CMap Parse(byte[] data, ExtractionDiagnostics diagnostics)
    {
        var cmap = new CMap();
        var lexer = new PdfLexer(data);
        var parser = new PdfParser(lexer);
        var operands = new List<PdfObject>();
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End)
            {
                break;
            }

            if (token.Kind == TokenKind.Keyword && !lexer.IsKeyword(token, "true"u8) && !lexer.IsKeyword(token, "false"u8)
                && !lexer.IsKeyword(token, "null"u8))
            {
                cmap.Apply(lexer, token, operands, diagnostics);
                operands.Clear();
                continue;
            }

            if (token.Kind is TokenKind.Other or TokenKind.ArrayEnd or TokenKind.DictionaryEnd)
            {
                continue; // PostScript procedures ({ }) and stray delimiters.
            }

            try
            {
                if (parser.ParseObject(token, 0) is { } operand)
                {
                    if (operands.Count >= MaxSectionOperands)
                    {
                        diagnostics.Report($"A CMap section has more than {MaxSectionOperands} operands; the rest are ignored.");
                        continue;
                    }

                    operands.Add(operand);
                }
            }
            catch (PdfFormatException ex)
            {
                diagnostics.Report($"CMap entry skipped: {ex.Message}");
            }
        }

        return cmap;
    }

    private void Apply(PdfLexer lexer, Token keyword, List<PdfObject> operands, ExtractionDiagnostics diagnostics)
    {
        if (lexer.IsKeyword(keyword, "endcodespacerange"u8))
        {
            for (var i = 0; i + 1 < operands.Count; i += 2)
            {
                if (operands[i] is PdfString low && operands[i + 1] is PdfString high && low.Bytes.Length is >= 1 and <= 4
                    && low.Bytes.Length == high.Bytes.Length && _codespace.Count < 64)
                {
                    _codespace.Add(new CodespaceRange(low.Bytes.ToArray(), high.Bytes.ToArray()));
                }
            }
        }
        else if (lexer.IsKeyword(keyword, "endbfchar"u8))
        {
            for (var i = 0; i + 1 < operands.Count; i += 2)
            {
                if (Code(operands[i]) is { } code && Destination(operands[i + 1]) is { } text && HasRoom(diagnostics))
                {
                    _textChars[Key(code.Value, code.Length)] = text;
                }
            }
        }
        else if (lexer.IsKeyword(keyword, "endbfrange"u8))
        {
            for (var i = 0; i + 2 < operands.Count; i += 3)
            {
                if (Code(operands[i]) is not { } low || Code(operands[i + 1]) is not { } high || low.Length != high.Length
                    || high.Value < low.Value || !HasRoom(diagnostics))
                {
                    continue;
                }

                switch (operands[i + 2])
                {
                    case PdfString start when Destination(start) is { Length: > 0 } first:
                        _textRanges.Add(new TextRange(low.Length, low.Value, high.Value, first, null));
                        break;
                    case PdfArray array:
                        var texts = array.Items.Take((int)Math.Min(high.Value - low.Value + 1, 65536)).Select(Destination).ToArray();
                        _textRanges.Add(new TextRange(low.Length, low.Value, low.Value + (uint)texts.Length - 1, null, texts));
                        break;
                }

                _rangesSorted = false;
            }
        }
        else if (lexer.IsKeyword(keyword, "endcidchar"u8))
        {
            for (var i = 0; i + 1 < operands.Count; i += 2)
            {
                if (Code(operands[i]) is { } code && operands[i + 1] is PdfInteger { Value: >= 0 and <= int.MaxValue } cid && HasRoom(diagnostics))
                {
                    _cidChars[Key(code.Value, code.Length)] = (int)cid.Value;
                }
            }
        }
        else if (lexer.IsKeyword(keyword, "endcidrange"u8))
        {
            for (var i = 0; i + 2 < operands.Count; i += 3)
            {
                if (Code(operands[i]) is { } low && Code(operands[i + 1]) is { } high && low.Length == high.Length
                    && high.Value >= low.Value && operands[i + 2] is PdfInteger { Value: >= 0 and <= int.MaxValue } cid
                    && HasRoom(diagnostics))
                {
                    _cidRanges.Add(new CidRange(low.Length, low.Value, high.Value, (int)cid.Value));
                }
            }
        }
        else if (lexer.IsKeyword(keyword, "usecmap"u8))
        {
            if (operands.Count > 0 && operands[^1] is PdfName parent)
            {
                UseCMap = parent.Value;
            }
        }
        else if (lexer.IsKeyword(keyword, "def"u8) && operands.Count >= 2 && operands[^2] is PdfName key)
        {
            switch (key.Value)
            {
                case "WMode" when operands[^1] is PdfInteger mode:
                    IsVertical = mode.Value == 1;
                    break;
                case "CMapName" when operands[^1] is PdfName name:
                    Name = name.Value;
                    break;
            }
        }
    }

    private bool HasRoom(ExtractionDiagnostics diagnostics)
    {
        if (MappingCount < MaxMappings)
        {
            return true;
        }

        diagnostics.Report($"A CMap has more than {MaxMappings} mappings; the rest are ignored.");
        return false;
    }

    private string? TextExact(uint code, int length)
    {
        if (_textChars.TryGetValue(Key(code, length), out var text))
        {
            return text;
        }

        if (_textRanges.Count == 0)
        {
            return null;
        }

        if (!_rangesSorted)
        {
            // Stable, so that among ranges with the same start the later one (which overrides) stays later.
            var sorted = _textRanges.OrderBy(r => r.Low).ToList();
            _textRanges.Clear();
            _textRanges.AddRange(sorted);
            _rangesSorted = true;
        }

        // The last range starting at or before the code; ranges rarely overlap, so look back a little for one that does.
        int lo = 0, hi = _textRanges.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_textRanges[mid].Low <= code)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (var i = found; i >= 0 && i > found - 8; i--)
        {
            var range = _textRanges[i];
            if (range.Length == length && code >= range.Low && code <= range.High)
            {
                return range.TextOf(code);
            }
        }

        return null;
    }

    private static long Key(uint code, int length) => ((long)length << 32) | code;

    private static (uint Value, int Length)? Code(PdfObject operand)
    {
        if (operand is not PdfString { Bytes.Length: >= 1 and <= 4 } text)
        {
            return null;
        }

        var value = 0u;
        foreach (var b in text.Bytes)
        {
            value = (value << 8) | b;
        }

        return (value, text.Bytes.Length);
    }

    /// <summary>The text of a ToUnicode destination: UTF-16BE bytes (one byte is taken as a character), or a glyph name.</summary>
    private static string? Destination(PdfObject operand)
    {
        var text = operand switch
        {
            PdfString { Bytes.Length: 1 } one => ((char)one.Bytes[0]).ToString(),
            PdfString utf16 => Encoding.BigEndianUnicode.GetString(utf16.Bytes[..(utf16.Bytes.Length & ~1)]),
            PdfName name => GlyphNames.ToUnicode(name.Value),
            _ => null,
        };

        // A NUL maps to nothing (some producers write <0000> for glyphs without text).
        text = text?.Replace("\0", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private readonly record struct CodespaceRange(byte[] Low, byte[] High)
    {
        public int Length => Low.Length;

        /// <summary>Whether each byte of the code is within the range's bounds for that byte (5014, section 4.2).</summary>
        public bool Matches(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < Low.Length)
            {
                return false;
            }

            for (var i = 0; i < Low.Length; i++)
            {
                if (bytes[i] < Low[i] || bytes[i] > High[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly record struct CidRange(int Length, uint Low, uint High, int Cid);

    /// <summary>A <c>bfrange</c>: an incrementing start text (its last UTF-16 unit counts up), or one text per code.</summary>
    private readonly record struct TextRange(int Length, uint Low, uint High, string? Start, string?[]? Texts)
    {
        public string? TextOf(uint code)
        {
            var offset = code - Low;
            if (Texts is not null)
            {
                return offset < Texts.Length ? Texts[offset] : null;
            }

            var last = Start![^1] + offset;
            return last > 0xFFFF ? null : string.Concat(Start.AsSpan(0, Start.Length - 1), ((char)last).ToString());
        }
    }
}
