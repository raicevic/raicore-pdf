using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Email.Mime;

/// <summary>
/// Decoding header values: raw 8-bit bytes (UTF-8 as RFC 6532 allows, else Windows-1252), encoded words (RFC 2047,
/// <c>=?charset?B|Q?...?=</c>) and address lists (RFC 5322) for display.
/// </summary>
internal static partial class MimeHeaders
{
    /// <summary>
    /// Decodes a header value held as bytes in a Latin-1 string: 8-bit bytes as UTF-8 (or Windows-1252 if they are not
    /// UTF-8), then encoded words. Adjacent encoded words are joined (the whitespace between them is dropped), and their
    /// bytes are decoded together, so a character split across two of them comes out whole.
    /// </summary>
    public static string Decode(string raw)
    {
        var text = DecodeRaw(raw);
        if (!text.Contains("=?", StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var pendingBytes = new List<byte>();
        string? pendingCharset = null;
        var position = 0;
        foreach (Match match in EncodedWord().Matches(text))
        {
            var between = text[position..match.Index];
            if (pendingBytes.Count > 0 && !string.IsNullOrWhiteSpace(between))
            {
                Flush();
            }

            if (pendingBytes.Count == 0)
            {
                result.Append(between);
            }

            var charset = match.Groups[1].Value;
            var star = charset.IndexOf('*', StringComparison.Ordinal); // RFC 2231 language: =?utf-8*en?Q?...?=
            if (star >= 0)
            {
                charset = charset[..star];
            }

            if (pendingBytes.Count > 0 && !string.Equals(charset, pendingCharset, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
            }

            pendingCharset = charset;
            var payload = match.Groups[3].Value;
            if (match.Groups[2].Value is "B" or "b")
            {
                pendingBytes.AddRange(TransferDecoding.DecodeBase64(Encoding.Latin1.GetBytes(payload)));
            }
            else
            {
                DecodeQ(payload, pendingBytes);
            }

            position = match.Index + match.Length;
        }

        Flush();
        result.Append(text, position, text.Length - position);
        return result.ToString();

        void Flush()
        {
            if (pendingBytes.Count > 0)
            {
                result.Append(Charsets.Decode(pendingBytes.ToArray(), pendingCharset, html: false));
                pendingBytes.Clear();
            }
        }
    }

    /// <summary>Decodes a header value for display: encoded words, and runs of whitespace (from folding) made one space.</summary>
    public static string DecodeText(string raw) => CollapseWhitespace(Decode(raw));

    /// <summary>
    /// Formats an address list for display: <c>Name &lt;address&gt;</c> or the bare address for each, separated by
    /// commas, with display names unquoted and decoded. Groups (<c>team: a@example.com;</c>) show their members.
    /// </summary>
    public static string FormatAddresses(string raw)
    {
        var formatted = new List<string>();
        foreach (var piece in SplitAddresses(raw))
        {
            var address = FormatAddress(piece);
            if (address.Length > 0)
            {
                formatted.Add(address);
            }
        }

        return string.Join(", ", formatted);
    }

    /// <summary>The display name of the first address in a list, or the address if it has none; empty if there is none.</summary>
    public static string FirstName(string raw)
    {
        foreach (var piece in SplitAddresses(raw))
        {
            var (name, address) = ParseAddress(piece);
            if (name.Length > 0 || address.Length > 0)
            {
                return name.Length > 0 ? name : address;
            }
        }

        return string.Empty;
    }

    /// <summary>A <c>Content-ID</c> or <c>cid:</c> reference without its angle brackets and surrounding whitespace.</summary>
    public static string Unbracket(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('<'))
        {
            var end = text.IndexOf('>', StringComparison.Ordinal);
            text = end > 0 ? text[1..end] : text[1..];
        }

        return text.Trim();
    }

    /// <summary>Makes every run of whitespace one space, and trims.</summary>
    public static string CollapseWhitespace(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>The bytes of a Latin-1 string decoded as UTF-8 if they are UTF-8, else as Windows-1252.</summary>
    private static string DecodeRaw(string raw)
    {
        foreach (var c in raw)
        {
            if (c > 0x7F)
            {
                return Charsets.Decode(Encoding.Latin1.GetBytes(raw), null, html: false);
            }
        }

        return raw;
    }

    /// <summary>Decodes the Q encoding of an encoded word: <c>_</c> is a space, <c>=XX</c> a byte.</summary>
    private static void DecodeQ(string payload, List<byte> bytes)
    {
        Span<byte> buffer = stackalloc byte[4];
        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            if (c == '_')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '=' && i + 2 < payload.Length && Uri.IsHexDigit(payload[i + 1]) && Uri.IsHexDigit(payload[i + 2]))
            {
                bytes.Add((byte)((Uri.FromHex(payload[i + 1]) << 4) | Uri.FromHex(payload[i + 2])));
                i += 2;
            }
            else
            {
                // A decoded character (8-bit input was decoded before); encode it back as UTF-8.
                var rune = char.IsSurrogate(c) ? new Rune('\uFFFD') : new Rune(c);
                bytes.AddRange(buffer[..rune.EncodeToUtf8(buffer)]);
            }
        }
    }

    /// <summary>Splits an address list at the commas outside quotes, angle brackets and comments; group syntax is dropped.</summary>
    private static List<string> SplitAddresses(string raw)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var angle = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quoted)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < raw.Length)
                {
                    current.Append(raw[++i]);
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    current.Append(c);
                    break;
                case '(':
                    var start = i;
                    ContentType.SkipComment(raw, ref i);
                    current.Append(raw, start, Math.Min(i, raw.Length) - start);
                    i--;
                    break;
                case '<':
                    angle = true;
                    current.Append(c);
                    break;
                case '>':
                    angle = false;
                    current.Append(c);
                    break;
                case ',' or ';' when !angle:
                    pieces.Add(current.ToString());
                    current.Clear();
                    break;
                case ':' when !angle && IsGroupName(current):
                    current.Clear(); // "team: a@example.com, b@example.com;" lists its members.
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        pieces.Add(current.ToString());
        return pieces;
    }

    /// <summary>Whether text before a colon is a group's display name (it has no <c>@</c>, so it is no address).</summary>
    private static bool IsGroupName(StringBuilder text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '@')
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatAddress(string piece)
    {
        var (name, address) = ParseAddress(piece);
        return name.Length == 0 ? address
            : address.Length == 0 || string.Equals(name, address, StringComparison.OrdinalIgnoreCase) ? name
            : $"{name} <{address}>";
    }

    /// <summary>A display name and address: <c>Name &lt;address&gt;</c>, or <c>address (Name)</c>, or a bare address.</summary>
    private static (string Name, string Address) ParseAddress(string piece)
    {
        var open = IndexOutsideQuotes(piece, '<');
        if (open >= 0)
        {
            var close = piece.IndexOf('>', open + 1);
            var address = (close < 0 ? piece[(open + 1)..] : piece[(open + 1)..close]).Trim();
            return (DisplayName(piece[..open]), Decode(address));
        }

        // address (Name): the comment is the name.
        var comment = IndexOutsideQuotes(piece, '(');
        if (comment >= 0)
        {
            var close = piece.LastIndexOf(')');
            var name = close > comment ? piece[(comment + 1)..close] : piece[(comment + 1)..];
            return (DisplayName(name), CollapseWhitespace(Decode(piece[..comment])));
        }

        return (string.Empty, DisplayName(piece));
    }

    /// <summary>A display name unquoted (the quotes removed, <c>\x</c> unescaped) and decoded.</summary>
    private static string DisplayName(string raw)
    {
        var text = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"')
            {
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                c = raw[++i];
            }

            text.Append(c);
        }

        return DecodeText(text.ToString());
    }

    private static int IndexOutsideQuotes(string text, char target)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && quoted)
            {
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == target && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    // =?charset?B|Q?payload?=: the payload has no '?' (Q encodes it as =3F), so matching is linear.
    [GeneratedRegex(@"=\?([^?\s]{1,75})\?([BbQq])\?([^?]*)\?=", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
