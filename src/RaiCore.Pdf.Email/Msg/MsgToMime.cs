using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Email.Msg;

/// <summary>
/// Writes an Outlook message as the MIME message (RFC 5322, 2045-2049) a mail client would have sent, so that it is
/// read and converted exactly as an <c>.eml</c> file: header fields as RFC 2047 encoded words, the body as HTML (or
/// plain text) in UTF-8, images the HTML shows by <c>cid:</c> beside it in <c>multipart/related</c>, the other
/// attachments in <c>multipart/mixed</c> (file names as RFC 2231 parameters), and attached messages as
/// <c>message/rfc822</c> parts, written the same way.
/// </summary>
internal static class MsgToMime
{
    /// <summary>The MIME message of an Outlook message.</summary>
    public static byte[] Write(MsgMessage message)
    {
        var output = new StringBuilder();
        Write(message, output, 0);
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private static void Write(MsgMessage message, StringBuilder output, int level)
    {
        Header(output, "From", Mailbox(message.FromName, message.FromAddress));
        foreach (var (type, field) in new[] { (1, "To"), (2, "Cc"), (3, "Bcc") })
        {
            var recipients = message.Recipients.Where(r => r.Type == type).Select(r => Mailbox(r.Name, r.Address)).Where(m => m.Length > 0).ToList();
            if (recipients.Count > 0)
            {
                Header(output, field, string.Join(", ", recipients));
            }
        }

        if (message.Date is { } date)
        {
            Header(output, "Date", date.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture));
        }

        Header(output, "Subject", Encoded(message.Subject));
        if (message.MessageId is { Length: > 0 } id && id.All(c => c is > ' ' and < '\u007F'))
        {
            Header(output, "Message-ID", id);
        }

        Header(output, "MIME-Version", "1.0");

        var html = message.HtmlBody;
        var inline = html is null ? [] : message.Attachments.Where(a => a.Message is null && a.ContentId is { Length: > 0 } cid
            && html.Contains("cid:" + cid, StringComparison.OrdinalIgnoreCase)).ToList();
        var attached = message.Attachments.Where(a => !inline.Contains(a) && !(a.IsHidden && a.ContentId is not null && html is not null)).ToList();

        var mixed = Boundary("mixed", level);
        Header(output, "Content-Type", $"multipart/mixed; boundary=\"{mixed}\"");
        output.Append("\r\n");

        output.Append("--").Append(mixed).Append("\r\n");
        if (inline.Count > 0)
        {
            var related = Boundary("related", level);
            Header(output, "Content-Type", $"multipart/related; boundary=\"{related}\"");
            output.Append("\r\n");
            output.Append("--").Append(related).Append("\r\n");
            Text(output, "text/html", html!);
            foreach (var image in inline)
            {
                output.Append("--").Append(related).Append("\r\n");
                Part(output, image, inline: true);
            }

            output.Append("--").Append(related).Append("--\r\n");
        }
        else if (html is not null)
        {
            Text(output, "text/html", html);
        }
        else
        {
            Text(output, "text/plain", message.TextBody ?? string.Empty);
        }

        foreach (var attachment in attached)
        {
            output.Append("--").Append(mixed).Append("\r\n");
            if (attachment.Message is { } embedded)
            {
                Header(output, "Content-Type", "message/rfc822" + Parameter("name", FileName(attachment, embedded)));
                Header(output, "Content-Disposition", "attachment" + Parameter("filename", FileName(attachment, embedded)));
                output.Append("\r\n");
                Write(embedded, output, level + 1);
                output.Append("\r\n");
            }
            else
            {
                Part(output, attachment, inline: false);
            }
        }

        output.Append("--").Append(mixed).Append("--\r\n");
    }

    /// <summary>An attached message's file name: its own, else its subject; with <c>.eml</c> when it has no extension (it is written as one).</summary>
    private static string FileName(MsgAttachment attachment, MsgMessage embedded)
    {
        var name = attachment.FileName.Length > 0 ? attachment.FileName : embedded.Subject.Length > 0 ? embedded.Subject : "message";
        return Path.HasExtension(name) && !name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) ? name : Path.ChangeExtension(name, ".eml");
    }

    /// <summary>A text part in UTF-8, base64 encoded (so line lengths and 8-bit text never matter).</summary>
    private static void Text(StringBuilder output, string mediaType, string text)
    {
        Header(output, "Content-Type", mediaType + "; charset=utf-8");
        Header(output, "Content-Transfer-Encoding", "base64");
        output.Append("\r\n");
        Base64(output, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>An attached file, or an image shown inline by its content id.</summary>
    private static void Part(StringBuilder output, MsgAttachment attachment, bool inline)
    {
        var mediaType = attachment.MimeType is { } type && type.Contains('/', StringComparison.Ordinal) && type.All(c => c is > ' ' and < '\u007F' and not '"' and not ';')
            ? type.Trim().ToLowerInvariant()
            : "application/octet-stream";
        var name = attachment.FileName.Length > 0 ? Parameter("name", attachment.FileName) : string.Empty;
        Header(output, "Content-Type", mediaType + name);
        Header(output, "Content-Transfer-Encoding", "base64");
        Header(output, "Content-Disposition", (inline ? "inline" : "attachment") + (attachment.FileName.Length > 0 ? Parameter("filename", attachment.FileName) : string.Empty));
        if (attachment.ContentId is { Length: > 0 } cid && cid.All(c => c is > ' ' and < '\u007F' and not '<' and not '>'))
        {
            Header(output, "Content-ID", $"<{cid}>");
        }

        output.Append("\r\n");
        Base64(output, attachment.Data);
    }

    private static void Header(StringBuilder output, string name, string value)
    {
        if (value.Length > 0)
        {
            output.Append(name).Append(": ").Append(value).Append("\r\n");
        }
    }

    /// <summary>A mailbox: <c>"Name" &lt;address&gt;</c>, the name as an encoded word when it is not plain ASCII.</summary>
    private static string Mailbox(string name, string address)
    {
        address = new string(address.Where(c => c is > ' ' and < '\u007F' and not '<' and not '>' and not ',').ToArray());
        name = name.Trim();
        if (name.Length == 0)
        {
            return address;
        }

        var display = Encoded(name);
        return address.Length > 0 ? $"{display} <{address}>" : display;
    }

    /// <summary>Text as RFC 2047 encoded words in UTF-8 (base64), split so no word is over 75 characters.</summary>
    private static string Encoded(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var words = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            // About 45 bytes of UTF-8 per word, never splitting a surrogate pair.
            var length = Math.Min(15, text.Length - start);
            if (length < text.Length - start && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            words.Add("=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text.Substring(start, length))) + "?=");
            start += length;
        }

        return string.Join("\r\n ", words);
    }

    /// <summary>A parameter as RFC 2231 extended UTF-8 (<c>; name*=utf-8''...</c>).</summary>
    private static string Parameter(string name, string value)
    {
        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' || b is (byte)'.' or (byte)'-' or (byte)'_')
            {
                encoded.Append((char)b);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return $"; {name}*=utf-8''{encoded}";
    }

    private static void Base64(StringBuilder output, byte[] data)
    {
        var text = Convert.ToBase64String(data);
        for (var i = 0; i < text.Length; i += 76)
        {
            output.Append(text, i, Math.Min(76, text.Length - i)).Append("\r\n");
        }
    }

    private static string Boundary(string kind, int level) => $"=_raicore_{kind}_{level.ToString(CultureInfo.InvariantCulture)}";
}
