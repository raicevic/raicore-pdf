using System.Text;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Extraction.Content;

/// <summary>An operator with its operands, as read from a content stream.</summary>
/// <param name="Operator">The operator, such as <c>Tj</c>; <c>BI</c> for an inline image (see <see cref="Image"/>).</param>
/// <param name="Operands">The operands before it, in order.</param>
/// <param name="Image">The inline image of a <c>BI</c> … <c>ID</c> … <c>EI</c> sequence.</param>
internal readonly record struct ContentOperation(string Operator, IReadOnlyList<PdfObject> Operands, InlineImage? Image = null);

/// <summary>An inline image (ISO 32000-2, 8.9.7): its dictionary as written (abbreviated keys) and its raw data.</summary>
internal sealed record InlineImage(PdfDictionary Dictionary, byte[] Data);

/// <summary>
/// Reads the operators of a content stream (ISO 32000-2, 7.8.2) with the reader's lexer and parser. Like viewers, it
/// never fails on malformed content: stray delimiters are skipped, operands nested too deeply are dropped (with a
/// diagnostic), and an inline image without its <c>EI</c> runs to the end of the data.
/// </summary>
internal sealed class ContentParser
{
    /// <summary>The most operands kept for one operator; real operators take at most a few dozen.</summary>
    internal const int MaxOperands = 1024;

    /// <summary>Operators are at most three characters; longer keywords are garbage and are not turned into strings.</summary>
    private const int MaxOperatorLength = 3;

    private readonly byte[] _data;
    private readonly PdfLexer _lexer;
    private readonly PdfParser _parser;
    private readonly ExtractionDiagnostics _diagnostics;
    private List<PdfObject> _operands = [];

    public ContentParser(byte[] data, ExtractionDiagnostics diagnostics)
    {
        _data = data;
        _lexer = new PdfLexer(data);
        _parser = new PdfParser(_lexer);
        _diagnostics = diagnostics;
    }

    /// <summary>Reads the next operation; <c>false</c> at the end of the data.</summary>
    public bool TryRead(out ContentOperation operation)
    {
        while (true)
        {
            var token = _lexer.Next();
            switch (token.Kind)
            {
                case TokenKind.End:
                    operation = default;
                    return false;
                case TokenKind.Keyword when !IsObjectKeyword(token):
                    var operands = _operands;
                    _operands = [];
                    if (token.Length > MaxOperatorLength)
                    {
                        continue; // Not an operator: drop its operands, as viewers do with unknown operators.
                    }

                    var name = Encoding.Latin1.GetString(_data, token.Start, token.Length);
                    operation = name == "BI" ? new ContentOperation(name, [], ReadInlineImage()) : new ContentOperation(name, operands);
                    return true;
                case TokenKind.Integer or TokenKind.Real or TokenKind.Name or TokenKind.LiteralString or TokenKind.HexString
                    or TokenKind.ArrayStart or TokenKind.DictionaryStart or TokenKind.Keyword:
                    AddOperand(token);
                    break;
                default:
                    break; // A stray delimiter: ']' or '>>' without an opening one, ')', '{', '}'.
            }
        }
    }

    private bool IsObjectKeyword(Token token) =>
        _lexer.IsKeyword(token, "true"u8) || _lexer.IsKeyword(token, "false"u8) || _lexer.IsKeyword(token, "null"u8);

    private void AddOperand(Token token)
    {
        PdfObject? operand;
        try
        {
            operand = _parser.ParseObject(token, 0);
        }
        catch (PdfFormatException ex)
        {
            _diagnostics.Report($"Content stream operand skipped: {ex.Message}");
            return;
        }

        if (operand is null)
        {
            return;
        }

        if (_operands.Count >= MaxOperands)
        {
            _diagnostics.Report($"An operator has more than {MaxOperands} operands; the rest are ignored.");
            return;
        }

        _operands.Add(operand);
    }

    /// <summary>Reads an inline image after <c>BI</c>: the dictionary up to <c>ID</c>, then the data up to <c>EI</c>.</summary>
    private InlineImage ReadInlineImage()
    {
        var dictionary = new PdfDictionary();
        while (true)
        {
            var token = _lexer.Next();
            if (token.Kind == TokenKind.End)
            {
                _diagnostics.Report("An inline image (BI) has no image data (ID).");
                return new InlineImage(dictionary, []);
            }

            if (_lexer.IsKeyword(token, "ID"u8))
            {
                break;
            }

            if (token.Kind != TokenKind.Name)
            {
                continue;
            }

            var key = PdfName.FromBytes(_lexer.GetName(token));
            var valueToken = _lexer.Next();
            if (_lexer.IsKeyword(valueToken, "ID"u8))
            {
                break;
            }

            try
            {
                if (_parser.ParseObject(valueToken, 0) is { } value and not PdfNull)
                {
                    dictionary[key] = value;
                }
            }
            catch (PdfFormatException ex)
            {
                _diagnostics.Report($"Inline image entry skipped: {ex.Message}");
            }
        }

        // One whitespace byte separates ID from the data.
        var start = _lexer.Position;
        if (start < _data.Length && PdfLexer.IsWhitespace(_data[start]))
        {
            start++;
        }

        var length = ExpectedLength(dictionary);
        var searchFrom = length is { } known && known <= _data.Length - start ? start + (int)known : start;
        var end = FindEndImage(searchFrom);
        if (end < 0)
        {
            _diagnostics.Report("An inline image has no end (EI); its data runs to the end of the content.");
            _lexer.Position = _data.Length;
            return new InlineImage(dictionary, _data[start..]);
        }

        var dataEnd = length is { } exact && exact <= end - start
            ? start + (int)exact
            : end > start && PdfLexer.IsWhitespace(_data[end - 1]) ? end - 1 : end;
        _lexer.Position = end + 2;
        return new InlineImage(dictionary, _data[start..dataEnd]);
    }

    /// <summary>The position of the <c>EI</c> that ends inline image data, searching from <paramref name="from"/>; -1 if none.</summary>
    private int FindEndImage(int from)
    {
        for (var i = from; i + 1 < _data.Length; i++)
        {
            if (_data[i] == 'E' && _data[i + 1] == 'I'
                && (i == from || PdfLexer.IsWhitespace(_data[i - 1]))
                && (i + 2 == _data.Length || PdfLexer.IsWhitespace(_data[i + 2]) || PdfLexer.IsDelimiter(_data[i + 2])))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The length of unfiltered inline image data from its size, bits and color space, or <c>null</c> when it cannot be
    /// known (a filter, or a named color space from the resources).
    /// </summary>
    private static long? ExpectedLength(PdfDictionary dictionary)
    {
        if ((dictionary["F"] ?? dictionary["Filter"]) is not null)
        {
            return null;
        }

        if (Integer(dictionary["W"] ?? dictionary["Width"]) is not { } width
            || Integer(dictionary["H"] ?? dictionary["Height"]) is not { } height
            || width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

        var isMask = (dictionary["IM"] ?? dictionary["ImageMask"]) is PdfBoolean { Value: true };
        var bits = isMask ? 1 : Integer(dictionary["BPC"] ?? dictionary["BitsPerComponent"]) ?? 8;
        int? components = isMask ? 1 : (dictionary["CS"] ?? dictionary["ColorSpace"]) switch
        {
            PdfName { Value: "G" or "DeviceGray" or "CalGray" } => 1,
            PdfName { Value: "RGB" or "DeviceRGB" or "CalRGB" } => 3,
            PdfName { Value: "CMYK" or "DeviceCMYK" } => 4,
            PdfArray { Items.Count: > 0 } array when array.Items[0] is PdfName { Value: "I" or "Indexed" } => 1,
            null => 1,
            _ => null,
        };

        if (components is not { } count || bits is not (1 or 2 or 4 or 8 or 16))
        {
            return null;
        }

        var rowBytes = ((width * count * bits) + 7) / 8;
        return rowBytes > long.MaxValue / height ? null : rowBytes * height;
    }

    private static long? Integer(PdfObject? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real when real.Value == Math.Floor(real.Value) && Math.Abs(real.Value) < 1e15 => (long)real.Value,
        _ => null,
    };
}
