using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Reading;

/// <summary>
/// Parses PDF objects (ISO 32000-2, section 7.3) from tokens into the object model the writer uses. Parsing is
/// lenient, like viewers are: dictionary entries with a non-name key are skipped, containers end at the end of the
/// data or at a keyword that cannot be inside them (<c>endobj</c>, <c>stream</c>), and unknown keywords are ignored.
/// Nesting deeper than <see cref="MaxDepth"/> is an error, so hostile input cannot exhaust the stack.
/// </summary>
internal sealed class PdfParser(PdfLexer lexer)
{
    /// <summary>The deepest nesting of arrays and dictionaries accepted. Real files rarely nest more than ten levels.</summary>
    public const int MaxDepth = 100;

    public PdfLexer Lexer => lexer;

    /// <summary>Parses the object at the current position, or returns <c>null</c> when there is none (a keyword or the end).</summary>
    public PdfObject? ParseObject() => ParseObject(lexer.Next(), 0);

    /// <summary>Parses an object starting with <paramref name="token"/>; <c>null</c> when the token cannot start one.</summary>
    public PdfObject? ParseObject(Token token, int depth)
    {
        switch (token.Kind)
        {
            case TokenKind.Integer:
                return ParseIntegerOrReference(token);
            case TokenKind.Real:
                var real = lexer.GetReal(token);
                return lexer.IsWellFormedNumber(token)
                    ? PdfReal.FromSource(real, lexer.GetText(token))
                    : new PdfReal(real);
            case TokenKind.Name:
                return PdfName.FromBytes(lexer.GetName(token));
            case TokenKind.LiteralString:
                return new PdfString(lexer.GetLiteralString(token));
            case TokenKind.HexString:
                return new PdfString(lexer.GetHexString(token), hex: true);
            case TokenKind.ArrayStart:
                return ParseArray(depth + 1);
            case TokenKind.DictionaryStart:
                return ParseDictionary(depth + 1);
            case TokenKind.Keyword:
                if (lexer.IsKeyword(token, "true"u8))
                {
                    return PdfBoolean.True;
                }

                if (lexer.IsKeyword(token, "false"u8))
                {
                    return PdfBoolean.False;
                }

                if (lexer.IsKeyword(token, "null"u8))
                {
                    return PdfNull.Instance;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Whether a keyword ends any object being parsed: it starts or ends an indirect object or a stream.</summary>
    public bool IsObjectBoundary(Token token) =>
        token.Kind == TokenKind.Keyword
        && (lexer.IsKeyword(token, "endobj"u8) || lexer.IsKeyword(token, "stream"u8) || lexer.IsKeyword(token, "endstream"u8)
            || lexer.IsKeyword(token, "obj"u8) || lexer.IsKeyword(token, "xref"u8) || lexer.IsKeyword(token, "trailer"u8)
            || lexer.IsKeyword(token, "startxref"u8));

    private PdfObject ParseIntegerOrReference(Token token)
    {
        var value = lexer.GetInteger(token);
        if (value is not { } number)
        {
            return new PdfReal(lexer.GetReal(token)); // Too large for a long: keep it as a real.
        }

        // "12 0 R" is a reference: look ahead two tokens, and step back when it is not one.
        var position = lexer.Position;
        var second = lexer.Next();
        if (second.Kind == TokenKind.Integer && lexer.GetInteger(second) is { } generation)
        {
            var third = lexer.Next();
            if (lexer.IsKeyword(third, "R"u8))
            {
                return number is > 0 and <= int.MaxValue && generation is >= 0 and <= 65535
                    ? new PdfReference((int)number, (int)generation)
                    : PdfNull.Instance; // A reference to an impossible object is a reference to nothing.
            }
        }

        lexer.Position = position;
        return new PdfInteger(number);
    }

    private PdfArray ParseArray(int depth)
    {
        CheckDepth(depth);
        var array = new PdfArray();
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind is TokenKind.ArrayEnd or TokenKind.End)
            {
                break;
            }

            if (token.Kind == TokenKind.DictionaryEnd || IsObjectBoundary(token))
            {
                lexer.Position = token.Start; // The array was not closed; let the enclosing parser see the token.
                break;
            }

            if (ParseObject(token, depth) is { } item)
            {
                array.Items.Add(item);
            }
        }

        return array;
    }

    private PdfDictionary ParseDictionary(int depth)
    {
        CheckDepth(depth);
        var keys = new Dictionary<PdfName, int>();
        var entries = new List<KeyValuePair<PdfName, PdfObject>>();
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind is TokenKind.DictionaryEnd or TokenKind.End)
            {
                break;
            }

            if (token.Kind == TokenKind.ArrayEnd || IsObjectBoundary(token))
            {
                lexer.Position = token.Start;
                break;
            }

            if (token.Kind != TokenKind.Name)
            {
                _ = ParseObject(token, depth); // Not a key: skip it (and anything nested in it).
                continue;
            }

            var key = PdfName.FromBytes(lexer.GetName(token));
            var valueToken = lexer.Next();
            if (valueToken.Kind is TokenKind.DictionaryEnd or TokenKind.End)
            {
                break; // A key without a value.
            }

            if (valueToken.Kind == TokenKind.ArrayEnd || IsObjectBoundary(valueToken))
            {
                lexer.Position = valueToken.Start;
                break;
            }

            var value = ParseObject(valueToken, depth);

            // A null value is the same as no entry (ISO 32000-2, 7.3.7). A repeated key: the last one wins.
            if (value is null || value is PdfNull)
            {
                continue;
            }

            if (keys.TryGetValue(key, out var index))
            {
                entries[index] = new(key, value);
            }
            else
            {
                keys[key] = entries.Count;
                entries.Add(new(key, value));
            }
        }

        var dictionary = new PdfDictionary();
        foreach (var (key, value) in entries)
        {
            dictionary.AddUnchecked(key, value);
        }

        return dictionary;
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new PdfFormatException($"Arrays and dictionaries are nested more than {MaxDepth} levels deep.");
        }
    }
}
