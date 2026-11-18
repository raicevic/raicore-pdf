using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Extraction.Fonts;

internal sealed partial class FontDecoder
{
    /// <summary>The Flags bit of a font descriptor for symbolic fonts (ISO 32000-2, 9.8.2).</summary>
    private const int SymbolicFlag = 1 << 2;

    /// <summary>The most CIDs a font's widths or a CIDToGIDMap may describe one by one.</summary>
    private const int MaxCids = 65536;

    /// <summary>How many bytes of a Type 1 font program are searched for its built-in encoding.</summary>
    private const int MaxType1Header = 256 * 1024;

    /// <summary>
    /// The decoder of a font dictionary. Never throws for malformed fonts: what cannot be read is reported to
    /// <paramref name="diagnostics"/> and guessed (Latin-1 text, Helvetica widths).
    /// </summary>
    public static FontDecoder Load(PdfReader reader, PdfDictionary font, ExtractionDiagnostics diagnostics)
    {
        var subtype = (reader.Resolve(font["Subtype"]) as PdfName)?.Value;
        var baseFont = (reader.Resolve(font["BaseFont"]) as PdfName)?.Value ?? (reader.Resolve(font["Name"]) as PdfName)?.Value ?? "";
        var name = StandardFontNames.WithoutSubsetTag(baseFont);
        var kind = subtype switch
        {
            "Type0" => FontKind.Type0,
            "Type3" => FontKind.Type3,
            "TrueType" => FontKind.TrueType,
            _ => FontKind.Type1, // Type1, MMType1, or missing.
        };

        var decoder = new FontDecoder(name, kind, diagnostics);
        try
        {
            if (kind == FontKind.Type0)
            {
                decoder.LoadComposite(reader, font);
            }
            else
            {
                decoder.LoadSimple(reader, font);
            }

            if (Stream(reader, font["ToUnicode"]) is { } toUnicode && decoder.Decode(reader, toUnicode, "ToUnicode map") is { } data)
            {
                decoder._toUnicode = CMap.Parse(data, diagnostics);
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or FontFormatException or NotSupportedException)
        {
            diagnostics.Report($"Font {DisplayName(name)} could not be read completely: {ex.Message}");
        }

        return decoder;
    }

    /// <summary>The decoder used when a font is missing: WinAnsi text and Helvetica widths.</summary>
    public static FontDecoder Fallback(ExtractionDiagnostics diagnostics)
    {
        var decoder = new FontDecoder("Helvetica", FontKind.Type1, diagnostics);
        decoder.SetSimpleEncoding(SimpleEncodings.WinAnsi, null);
        decoder.SetStandardMetrics(StandardFont.Helvetica);
        return decoder;
    }

    private void LoadSimple(PdfReader reader, PdfDictionary font)
    {
        var descriptor = reader.Resolve(font["FontDescriptor"]) as PdfDictionary;
        var flags = Integer(reader.Resolve(descriptor?["Flags"])) ?? 0;
        var fontFile = descriptor is null ? null : Stream(reader, descriptor["FontFile"]);
        IsEmbedded = fontFile is not null || (descriptor is not null && (descriptor["FontFile2"] ?? descriptor["FontFile3"]) is not null);
        var standard = StandardFontNames.Find(Name);
        if (Kind == FontKind.Type3)
        {
            FontMatrix = Matrix.FromNumbers(Numbers(reader, font["FontMatrix"])) ?? FontMatrix;
        }

        // The base encoding: the one named, else the font's built-in one (read from an embedded Type 1 program), else
        // StandardEncoding for nonsymbolic fonts (ISO 32000-2, 9.6.5). Symbolic fonts without one have no text.
        string?[]? builtIn = null;
        string? baseTable = null;
        PdfArray? differences = null;
        switch (reader.Resolve(font["Encoding"]))
        {
            case PdfName encodingName:
                baseTable = SimpleEncodings.ByName(encodingName.Value);
                if (baseTable is null)
                {
                    _diagnostics.Report($"Font {DisplayName(Name)} has an unknown encoding /{encodingName.Value}.");
                }

                break;
            case PdfDictionary encoding:
                if (reader.Resolve(encoding["BaseEncoding"]) is PdfName baseName)
                {
                    baseTable = SimpleEncodings.ByName(baseName.Value);
                }

                differences = reader.Resolve(encoding["Differences"]) as PdfArray;
                break;
        }

        if (baseTable is null)
        {
            if (Kind == FontKind.Type1 && fontFile is not null)
            {
                builtIn = ReadType1Encoding(reader, fontFile);
            }

            if (builtIn is null && Kind != FontKind.Type3 && (flags & SymbolicFlag) == 0
                && standard is not (StandardFont.Symbol or StandardFont.ZapfDingbats))
            {
                baseTable = SimpleEncodings.Standard;
            }
        }

        SetSimpleEncoding(baseTable, builtIn);
        if (differences is not null)
        {
            ApplyDifferences(reader, differences);
        }

        // Widths: /FirstChar and /Widths, else (the standard fonts may omit them) the standard font's metrics.
        var scale = Kind == FontKind.Type3 ? FontMatrix.A : 0.001;
        if (reader.Resolve(font["Widths"]) is PdfArray widths)
        {
            _simpleWidths = new double[256];
            Array.Fill(_simpleWidths, double.NaN);
            var first = Integer(reader.Resolve(font["FirstChar"])) ?? 0;
            for (var i = 0; i < widths.Items.Count && first + i < 256; i++)
            {
                if (first + i >= 0 && Number(reader.Resolve(widths.Items[i])) is { } width)
                {
                    _simpleWidths[first + i] = width * scale;
                }
            }

            _missingWidth = (Number(reader.Resolve(descriptor?["MissingWidth"])) ?? 0) * scale;
        }
        else if (Kind != FontKind.Type3)
        {
            SetStandardMetrics(standard ?? StandardFont.Helvetica);
            if (standard is null)
            {
                _diagnostics.Report($"Font {DisplayName(Name)} has no widths; Helvetica's are used.");
            }
        }

        if (Kind == FontKind.Type3)
        {
            var box = Numbers(reader, font["FontBBox"]);
            if (box.Count == 4 && box[3] > box[1])
            {
                SetVerticalExtent(box[3] * FontMatrix.D, box[1] * FontMatrix.D);
            }
        }
        else
        {
            SetVerticalExtent(reader, descriptor, standard);
        }
    }

    private void LoadComposite(PdfReader reader, PdfDictionary font)
    {
        var cidFont = reader.Resolve(font["DescendantFonts"]) switch
        {
            PdfArray { Items.Count: > 0 } descendants => reader.Resolve(descendants.Items[0]) as PdfDictionary,
            PdfDictionary direct => direct,
            _ => null,
        };

        _encoding = LoadEncodingCMap(reader, reader.Resolve(font["Encoding"]), 0);
        if (cidFont is null)
        {
            _diagnostics.Report($"Font {DisplayName(Name)} has no descendant CIDFont.");
            return;
        }

        _defaultWidth = (Number(reader.Resolve(cidFont["DW"])) ?? 1000) / 1000;
        ReadCidWidths(reader, reader.Resolve(cidFont["W"]) as PdfArray);
        var dw2 = Numbers(reader, cidFont["DW2"]);
        if (dw2.Count == 2)
        {
            _defaultVertical = (dw2[0] / 1000, dw2[1] / 1000);
        }

        ReadVerticalMetrics(reader, reader.Resolve(cidFont["W2"]) as PdfArray);
        var descriptor = reader.Resolve(cidFont["FontDescriptor"]) as PdfDictionary;
        IsEmbedded = descriptor is not null && (descriptor["FontFile"] ?? descriptor["FontFile2"] ?? descriptor["FontFile3"]) is not null;
        SetVerticalExtent(reader, descriptor, null);

        // Without a ToUnicode map, an embedded TrueType program's cmap still says what most glyphs are.
        if (descriptor is not null && Stream(reader, descriptor["FontFile2"]) is { } program)
        {
            var cidToGid = reader.Resolve(cidFont["CIDToGIDMap"]);
            _loadCidText = () => ReadTrueTypeText(reader, program, cidToGid);
        }
    }

    /// <summary>The encoding CMap of a composite font: a predefined name, or an embedded CMap stream (which may build on another).</summary>
    private CMap LoadEncodingCMap(PdfReader reader, PdfObject? encoding, int depth)
    {
        switch (encoding)
        {
            case PdfName { Value: "Identity-H" }:
                return CMap.Identity(2, false);
            case PdfName { Value: "Identity-V" }:
                return CMap.Identity(2, true);
            case PdfName unicode when unicode.Value.StartsWith("Uni", StringComparison.Ordinal)
                && (unicode.Value.Contains("UCS2", StringComparison.Ordinal) || unicode.Value.Contains("UTF16", StringComparison.Ordinal)):
                // The codes are UTF-16 (UCS-2 has no surrogates); the CIDs are unknown without Adobe's CMap files.
                _unicodeEncoding = true;
                return UnicodeCMap(unicode.Value.EndsWith("-V", StringComparison.Ordinal));
            case PdfName other:
                _diagnostics.Report($"Font {DisplayName(Name)} uses the predefined CMap /{other.Value}, which is not supported; codes are read as two bytes.");
                return CMap.Identity(2, other.Value.EndsWith("-V", StringComparison.Ordinal));
            case PdfStreamObject stream when depth < 4 && Decode(reader, stream, "encoding CMap") is { } data:
                var cmap = CMap.Parse(data, _diagnostics);
                var parent = reader.Resolve(stream.Dictionary["UseCMap"]) ?? (cmap.UseCMap is { } parentName ? new PdfName(parentName) : null);
                if (parent is not null)
                {
                    cmap.Inherit(LoadEncodingCMap(reader, parent, depth + 1));
                }

                if (reader.Resolve(stream.Dictionary["WMode"]) is PdfInteger { Value: 1 } && !cmap.IsVertical)
                {
                    var vertical = CMap.Identity(2, true);
                    vertical.Inherit(cmap);
                    return vertical;
                }

                return cmap;
            default:
                _diagnostics.Report($"Font {DisplayName(Name)} has no usable encoding CMap; Identity-H is assumed.");
                return CMap.Identity(2, false);
        }
    }

    /// <summary>A CMap whose codes are UTF-16: two bytes, or four for a surrogate pair.</summary>
    private static CMap UnicodeCMap(bool vertical)
    {
        var data = """
            /WMode 0 def
            3 begincodespacerange <0000> <D7FF> <D800DC00> <DBFFDFFF> <E000> <FFFF> endcodespacerange
            """u8.ToArray();
        var cmap = CMap.Parse(data, new ExtractionDiagnostics());
        if (!vertical)
        {
            return cmap;
        }

        var verticalMap = CMap.Identity(2, true);
        verticalMap.Inherit(cmap);
        return verticalMap;
    }

    private void SetSimpleEncoding(string? baseTable, string?[]? builtIn)
    {
        _simpleText = new string?[256];
        for (var code = 0; code < 256; code++)
        {
            _simpleText[code] = builtIn is not null ? builtIn[code]
                : baseTable is not null && baseTable[code] != '\0' ? baseTable[code].ToString()
                : null;
        }
    }

    /// <summary>Applies <c>/Differences</c>: <c>[code /name /name … code /name …]</c>.</summary>
    private void ApplyDifferences(PdfReader reader, PdfArray differences)
    {
        long code = -1;
        foreach (var item in differences.Items)
        {
            switch (reader.Resolve(item))
            {
                case PdfInteger number:
                    code = number.Value;
                    break;
                case PdfName glyph when code is >= 0 and < 256:
                    _simpleText![code] = GlyphNames.ToUnicode(glyph.Value);
                    code++;
                    break;
                case PdfName:
                    code++;
                    break;
            }
        }
    }

    private void SetStandardMetrics(StandardFont font)
    {
        _simpleWidths = new double[256];
        Array.Fill(_simpleWidths, double.NaN);
        if (StandardFontMetrics.Get(font) is not { } metrics)
        {
            _missingWidth = 0.6; // Symbol and ZapfDingbats: no open metrics; an average width.
            return;
        }

        // The metrics are indexed by WinAnsi code; find each code's character in that encoding.
        for (var code = 0; code < 256; code++)
        {
            if (_simpleText?[code] is { Length: 1 } text && WinAnsiEncoding.CanEncode(text[0]))
            {
                _simpleWidths[code] = (AdobeCoreWidths.Get(font, text[0]) ?? metrics.Widths[WinAnsiEncoding.Encode(text[0])]) / 1000.0;
            }
        }

        _missingWidth = metrics.Widths[' '] / 1000.0;
        SetVerticalExtent(metrics.Ascent / 1000.0, metrics.Descent / 1000.0);
    }

    private void SetVerticalExtent(PdfReader reader, PdfDictionary? descriptor, StandardFont? standard)
    {
        var ascent = Number(reader.Resolve(descriptor?["Ascent"]));
        var descent = Number(reader.Resolve(descriptor?["Descent"]));
        var box = descriptor is null ? [] : Numbers(reader, descriptor["FontBBox"]);
        if (ascent is > 0 && descent is { } d)
        {
            SetVerticalExtent(ascent.Value / 1000, -Math.Abs(d) / 1000);
        }
        else if (box.Count == 4 && box[3] > box[1])
        {
            SetVerticalExtent(box[3] / 1000, box[1] / 1000);
        }
        else if (standard is { } font && StandardFontMetrics.Get(font) is { } metrics)
        {
            SetVerticalExtent(metrics.Ascent / 1000.0, metrics.Descent / 1000.0);
        }
    }

    /// <summary>Sets the ascent and descent when they are plausible (some fonts have zeros or nonsense).</summary>
    private void SetVerticalExtent(double ascent, double descent)
    {
        if (double.IsFinite(ascent) && double.IsFinite(descent) && ascent is > 0 and <= 4 && descent is <= 0 and >= -4)
        {
            Ascent = ascent;
            Descent = descent;
        }
    }

    /// <summary>Reads <c>/W</c>: <c>[c [w1 w2 …] cfirst clast w …]</c> (ISO 32000-2, 9.7.4.3).</summary>
    private void ReadCidWidths(PdfReader reader, PdfArray? widths)
    {
        if (widths is null)
        {
            return;
        }

        _cidWidths = [];
        _cidWidthRanges = [];
        var items = widths.Items.Select(reader.Resolve).ToList();
        for (var i = 0; i + 1 < items.Count;)
        {
            if (Integer(items[i]) is not { } first || first < 0)
            {
                i++;
                continue;
            }

            if (items[i + 1] is PdfArray list)
            {
                for (var j = 0; j < list.Items.Count && _cidWidths.Count < MaxCids; j++)
                {
                    if (Number(reader.Resolve(list.Items[j])) is { } width && first + j <= int.MaxValue)
                    {
                        _cidWidths[(int)(first + j)] = width / 1000;
                    }
                }

                i += 2;
            }
            else if (i + 2 < items.Count && Integer(items[i + 1]) is { } last && Number(items[i + 2]) is { } width)
            {
                if (last >= first && last <= int.MaxValue && _cidWidthRanges.Count < MaxCids)
                {
                    _cidWidthRanges.Add(((int)first, (int)last, width / 1000));
                }

                i += 3;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>Reads <c>/W2</c>: <c>[c [w1y vx vy …] cfirst clast w1y vx vy …]</c>.</summary>
    private void ReadVerticalMetrics(PdfReader reader, PdfArray? metrics)
    {
        if (metrics is null)
        {
            return;
        }

        _verticalMetrics = [];
        _verticalRanges = [];
        var items = metrics.Items.Select(reader.Resolve).ToList();
        for (var i = 0; i + 1 < items.Count;)
        {
            if (Integer(items[i]) is not { } first || first is < 0 or > int.MaxValue)
            {
                i++;
                continue;
            }

            if (items[i + 1] is PdfArray list)
            {
                var values = list.Items.Select(v => Number(reader.Resolve(v))).ToList();
                for (var j = 0; j + 2 < values.Count && _verticalMetrics.Count < MaxCids; j += 3)
                {
                    if (values[j] is { } advance && values[j + 1] is { } x && values[j + 2] is { } y && first + (j / 3) <= int.MaxValue)
                    {
                        _verticalMetrics[(int)(first + (j / 3))] = new VerticalMetrics(advance / 1000, x / 1000, y / 1000);
                    }
                }

                i += 2;
            }
            else if (i + 4 < items.Count && Integer(items[i + 1]) is { } last && Number(items[i + 2]) is { } advance
                && Number(items[i + 3]) is { } x && Number(items[i + 4]) is { } y)
            {
                if (last >= first && last <= int.MaxValue && _verticalRanges.Count < MaxCids)
                {
                    _verticalRanges.Add(((int)first, (int)last, new VerticalMetrics(advance / 1000, x / 1000, y / 1000)));
                }

                i += 5;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// The text of each CID from an embedded TrueType program: the CIDToGIDMap gives the glyph, and the font's
    /// <c>cmap</c> (read backwards) the character it is for.
    /// </summary>
    private string?[]? ReadTrueTypeText(PdfReader reader, PdfStreamObject program, PdfObject? cidToGid)
    {
        try
        {
            if (Decode(reader, program, "font program") is not { } data)
            {
                return null;
            }

            var font = OpenTypeFont.Load(data);
            var byGlyph = new Dictionary<int, string>();
            foreach (var (codePoint, glyph) in CmapParser.Parse(font, out _).OrderBy(p => IsPrivateUse(p.Key)).ThenBy(p => p.Key))
            {
                // A damaged cmap can map to surrogates or past U+10FFFF, which are no characters.
                if (glyph != 0 && !IsPrivateUse(codePoint) && System.Text.Rune.IsValid(codePoint))
                {
                    byGlyph.TryAdd(glyph, char.ConvertFromUtf32(codePoint));
                }
            }

            if (byGlyph.Count == 0)
            {
                return null;
            }

            var map = cidToGid is PdfStreamObject mapStream ? Decode(reader, mapStream, "CIDToGIDMap") : null;
            var count = map is null ? Math.Min(font.NumGlyphs, MaxCids) : Math.Min(map.Length / 2, MaxCids);
            var text = new string?[count];
            for (var cid = 0; cid < count; cid++)
            {
                var glyph = map is null ? cid : (map[2 * cid] << 8) | map[(2 * cid) + 1];
                text[cid] = byGlyph.GetValueOrDefault(glyph);
            }

            return text;
        }
        catch (Exception ex) when (ex is FontFormatException or PdfFormatException or NotSupportedException)
        {
            _diagnostics.Report($"Font {DisplayName(Name)}: the embedded font program could not be read ({ex.Message}).");
            return null;
        }
    }

    private static bool IsPrivateUse(int codePoint) => codePoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000;

    /// <summary>
    /// The built-in encoding of a Type 1 font program, from its clear-text part: <c>/Encoding StandardEncoding def</c>,
    /// or <c>dup code /name put</c> entries. <c>null</c> when it has none.
    /// </summary>
    private string?[]? ReadType1Encoding(PdfReader reader, PdfStreamObject program)
    {
        if (Decode(reader, program, "font program") is not { } data)
        {
            return null;
        }

        var length = Integer(reader.Resolve(program.Dictionary["Length1"])) is { } length1 && length1 > 0 ? (int)Math.Min(length1, data.Length) : data.Length;
        var start = data.Length >= 6 && data[0] == 0x80 && data[1] == 0x01 ? 6 : 0; // A PFB segment header.
        var lexer = new PdfLexer(data[start..Math.Max(start, Math.Min(length, MaxType1Header))]);
        string?[]? encoding = null;
        Token previous = default, second = default, third = default;
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End || lexer.IsKeyword(token, "eexec"u8))
            {
                break;
            }

            if (encoding is null && previous.Kind == TokenKind.Name && lexer.IsKeyword(token, "StandardEncoding"u8)
                && lexer.GetName(previous).AsSpan().SequenceEqual("Encoding"u8))
            {
                return [.. SimpleEncodings.Standard.Select(c => c == '\0' ? null : c.ToString())];
            }

            // dup <code> /<name> put
            if (lexer.IsKeyword(token, "put"u8) && lexer.IsKeyword(third, "dup"u8) && second.Kind == TokenKind.Integer
                && previous.Kind == TokenKind.Name && lexer.GetInteger(second) is { } code && code is >= 0 and < 256)
            {
                encoding ??= new string?[256];
                encoding[code] = GlyphNames.ToUnicode(System.Text.Encoding.Latin1.GetString(lexer.GetName(previous)));
            }

            third = second;
            second = previous;
            previous = token;
        }

        return encoding;
    }

    private byte[]? Decode(PdfReader reader, PdfStreamObject stream, string what)
    {
        try
        {
            return reader.Store.Decode(stream);
        }
        catch (PdfFormatException ex)
        {
            _diagnostics.Report($"Font {DisplayName(Name)}: its {what} could not be decoded ({ex.Message}).");
            return null;
        }
    }

    private static PdfStreamObject? Stream(PdfReader reader, PdfObject? value) => reader.Resolve(value) as PdfStreamObject;

    private static string DisplayName(string name) => name.Length == 0 ? "(unnamed)" : name;

    internal static double? Number(PdfObject? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null,
    };

    internal static long? Integer(PdfObject? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real when Math.Abs(real.Value) < 1e15 => (long)Math.Round(real.Value),
        _ => null,
    };

    private static List<double> Numbers(PdfReader reader, PdfObject? value) =>
        reader.Resolve(value) is PdfArray array
            ? [.. array.Items.Select(i => Number(reader.Resolve(i))).Where(n => n is not null).Select(n => n!.Value).Where(double.IsFinite)]
            : [];
}
