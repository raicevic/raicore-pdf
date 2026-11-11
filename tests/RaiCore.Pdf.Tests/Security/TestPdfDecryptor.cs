using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Tests.Security;

internal enum TokenKind
{
    Other,
    String,
    Stream,
}

/// <summary>A lexical token of an indirect object: a string or stream (with its bytes) or any other token (as text).</summary>
internal sealed record PdfToken(TokenKind Kind, byte[] Bytes, string Text)
{
    public override string ToString() => Kind == TokenKind.Other ? Text : $"{Kind}[{Convert.ToHexString(Bytes)}]";
}

/// <summary>
/// Test-side reading and decryption, written independently of the library's security handler: a minimal lexer for the
/// files RaiCore.Pdf writes (classic xref table, no object streams) and its own AES-256 (revision 6) key derivation.
/// </summary>
internal static partial class TestPdfDecryptor
{
    /// <summary>Every indirect object of the file, by object number, as tokens.</summary>
    public static SortedDictionary<int, List<PdfToken>> ReadObjects(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var startXref = int.Parse(StartXrefRegex().Match(text).Groups[1].Value, CultureInfo.InvariantCulture);
        var objects = new SortedDictionary<int, List<PdfToken>>();
        var entries = XrefEntryRegex().Matches(text[startXref..]);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i].Groups[1].Value, CultureInfo.InvariantCulture);
            var header = $"{i + 1} 0 obj";
            Assert.Equal(header, text.Substring(offset, header.Length));
            objects[i + 1] = Tokenize(pdf, offset + header.Length, stopAt: "endobj");
        }

        return objects;
    }

    /// <summary>The trailer dictionary's tokens.</summary>
    public static List<PdfToken> ReadTrailer(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        return Tokenize(pdf, text.LastIndexOf("trailer", StringComparison.Ordinal) + "trailer".Length, stopAt: "startxref");
    }

    /// <summary>The object that follows <paramref name="key"/> in a flat token list (the first match).</summary>
    public static PdfToken ValueOf(List<PdfToken> tokens, string key) =>
        tokens[tokens.FindIndex(t => t.Kind == TokenKind.Other && t.Text == key) + 1];

    /// <summary>The object number of the trailer's <c>/Encrypt</c> reference.</summary>
    public static int EncryptObjectNumber(byte[] pdf) =>
        int.Parse(ValueOf(ReadTrailer(pdf), "/Encrypt").Text, CultureInfo.InvariantCulture);

    /// <summary>
    /// Derives the AES-256 file key from a password (ISO 32000-2, Algorithm 2.A, and 2.B for the hash), trying the
    /// owner password first. Returns <c>null</c> if the password matches neither.
    /// </summary>
    public static byte[]? DeriveR6Key(byte[] pdf, string password)
    {
        var encrypt = ReadObjects(pdf)[EncryptObjectNumber(pdf)];
        Assert.Equal("6", ValueOf(encrypt, "/R").Text);
        var o = ValueOf(encrypt, "/O").Bytes;
        var u = ValueOf(encrypt, "/U").Bytes;
        var pw = Encoding.UTF8.GetBytes(password);

        if (Hash(pw, o[32..40], u).SequenceEqual(o[..32]))
        {
            return UnwrapKey(Hash(pw, o[40..48], u), ValueOf(encrypt, "/OE").Bytes);
        }

        if (Hash(pw, u[32..40], []).SequenceEqual(u[..32]))
        {
            return UnwrapKey(Hash(pw, u[40..48], []), ValueOf(encrypt, "/UE").Bytes);
        }

        return null;
    }

    /// <summary>The <c>/Perms</c> entry decrypted with the file key (AES-256-ECB).</summary>
    public static byte[] DecryptPerms(byte[] pdf, byte[] fileKey)
    {
        var encrypt = ReadObjects(pdf)[EncryptObjectNumber(pdf)];
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = fileKey;
        var perms = ValueOf(encrypt, "/Perms").Bytes;
        return aes.CreateDecryptor().TransformFinalBlock(perms, 0, perms.Length);
    }

    /// <summary>AES-CBC decryption with the IV in the first 16 bytes and PKCS#7 padding.</summary>
    public static byte[] AesDecrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = data[..16];
        return aes.CreateDecryptor().TransformFinalBlock(data, 16, data.Length - 16);
    }

    /// <summary>
    /// The objects of <paramref name="encrypted"/> with every string and stream decrypted by <paramref name="decrypt"/>
    /// (data, object number), leaving out the <c>/Encrypt</c> dictionary.
    /// </summary>
    public static SortedDictionary<int, List<PdfToken>> DecryptObjects(byte[] encrypted, Func<byte[], int, byte[]> decrypt)
    {
        var encryptNumber = EncryptObjectNumber(encrypted);
        var result = new SortedDictionary<int, List<PdfToken>>();
        foreach (var (number, tokens) in ReadObjects(encrypted))
        {
            if (number != encryptNumber)
            {
                result[number] = [.. tokens.Select(t => t.Kind == TokenKind.Other ? t : t with { Bytes = decrypt(t.Bytes, number) })];
            }
        }

        return result;
    }

    /// <summary>
    /// Asserts that decrypting <paramref name="encrypted"/> gives back exactly the objects of <paramref name="plain"/>
    /// (the same document saved without encryption): same tokens, strings and stream data. <c>/Length</c> values and the
    /// catalog's <c>/Extensions</c> entry, which encryption adds, are ignored.
    /// </summary>
    public static void AssertDecryptsTo(byte[] plain, byte[] encrypted, Func<byte[], int, byte[]> decrypt)
    {
        var expected = ReadObjects(plain);
        var actual = DecryptObjects(encrypted, decrypt);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (number, tokens) in expected)
        {
            var actualTokens = WithoutEntry(actual[number], "/Extensions");
            Assert.Equal(Normalize(tokens), Normalize(actualTokens));
        }
    }

    private static List<string> Normalize(List<PdfToken> tokens) =>
        [.. tokens.Select((t, i) => i > 0 && tokens[i - 1].Text == "/Length" ? "<length>" : t.ToString())];

    private static List<PdfToken> WithoutEntry(List<PdfToken> tokens, string key)
    {
        var index = tokens.FindIndex(t => t.Kind == TokenKind.Other && t.Text == key);
        if (index < 0)
        {
            return tokens;
        }

        var end = index + 1;
        var depth = 0;
        do
        {
            depth += tokens[end].Text switch { "<<" => 1, ">>" => -1, _ => 0 };
            end++;
        }
        while (depth > 0);

        return [.. tokens.Take(index), .. tokens.Skip(end)];
    }

    /// <summary>Algorithm 2.B, written from the standard independently of the library.</summary>
    private static byte[] Hash(byte[] password, byte[] salt, byte[] userKey)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        byte[] e;
        var round = 0;
        do
        {
            byte[] block = [.. password, .. k, .. userKey];
            var k1 = Enumerable.Repeat(block, 64).SelectMany(b => b).ToArray();
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = k[..16];
            aes.IV = k[16..32];
            e = aes.CreateEncryptor().TransformFinalBlock(k1, 0, k1.Length);
            var selector = (int)(new BigInteger(e.AsSpan(0, 16), isUnsigned: true, isBigEndian: true) % 3);
            k = selector switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            round++;
        }
        while (round < 64 || e[^1] > round - 32);

        return k[..32];
    }

    private static byte[] UnwrapKey(byte[] key, byte[] wrapped)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = new byte[16];
        return aes.CreateDecryptor().TransformFinalBlock(wrapped, 0, wrapped.Length);
    }

    private static List<PdfToken> Tokenize(byte[] pdf, int position, string stopAt)
    {
        var tokens = new List<PdfToken>();
        while (true)
        {
            while (IsWhitespace(pdf[position]))
            {
                position++;
            }

            var b = pdf[position];
            if (b == '(')
            {
                tokens.Add(new PdfToken(TokenKind.String, ReadLiteral(pdf, ref position), string.Empty));
            }
            else if (b == '<' && pdf[position + 1] == '<')
            {
                tokens.Add(Other("<<"));
                position += 2;
            }
            else if (b == '>' && pdf[position + 1] == '>')
            {
                tokens.Add(Other(">>"));
                position += 2;
            }
            else if (b == '<')
            {
                var end = Array.IndexOf(pdf, (byte)'>', position);
                var hex = Encoding.ASCII.GetString(pdf, position + 1, end - position - 1);
                tokens.Add(new PdfToken(TokenKind.String, Convert.FromHexString(hex.PadRight(hex.Length + (hex.Length % 2), '0')), string.Empty));
                position = end + 1;
            }
            else if (b is (byte)'[' or (byte)']')
            {
                tokens.Add(Other(((char)b).ToString()));
                position++;
            }
            else
            {
                var start = position;
                position++;
                while (!IsWhitespace(pdf[position]) && !IsDelimiter(pdf[position]))
                {
                    position++;
                }

                var word = Encoding.Latin1.GetString(pdf, start, position - start);
                if (word == stopAt)
                {
                    return tokens;
                }

                if (word == "stream")
                {
                    var length = int.Parse(tokens[tokens.FindLastIndex(t => t.Text == "/Length") + 1].Text, CultureInfo.InvariantCulture);
                    position += pdf[position] == '\r' ? 2 : 1;
                    tokens.Add(new PdfToken(TokenKind.Stream, pdf[position..(position + length)], string.Empty));
                    position += length;
                }
                else if (word != "endstream")
                {
                    tokens.Add(Other(word));
                }
            }
        }
    }

    private static byte[] ReadLiteral(byte[] pdf, ref int position)
    {
        var bytes = new List<byte>();
        var depth = 1;
        position++;
        while (true)
        {
            var b = pdf[position++];
            switch (b)
            {
                case (byte)'\\':
                    var next = pdf[position++];
                    if (next is >= (byte)'0' and <= (byte)'7')
                    {
                        var value = next - '0';
                        for (var i = 0; i < 2 && pdf[position] is >= (byte)'0' and <= (byte)'7'; i++)
                        {
                            value = (value * 8) + (pdf[position++] - '0');
                        }

                        bytes.Add((byte)value);
                    }
                    else
                    {
                        bytes.Add(next switch
                        {
                            (byte)'n' => (byte)'\n',
                            (byte)'r' => (byte)'\r',
                            (byte)'t' => (byte)'\t',
                            (byte)'b' => (byte)'\b',
                            (byte)'f' => (byte)'\f',
                            _ => next,
                        });
                    }

                    break;
                case (byte)'(':
                    depth++;
                    bytes.Add(b);
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return [.. bytes];
                    }

                    bytes.Add(b);
                    break;
                default:
                    bytes.Add(b);
                    break;
            }
        }
    }

    private static PdfToken Other(string text) => new(TokenKind.Other, [], text);

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/' or (byte)'%';

    [GeneratedRegex(@"startxref\n(\d+)")]
    private static partial Regex StartXrefRegex();

    [GeneratedRegex(@"(\d{10}) 00000 n")]
    private static partial Regex XrefEntryRegex();
}
