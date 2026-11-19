using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Email.Mime;

namespace RaiCore.Pdf.Email;

/// <summary>How an attachment is appended after the message, if it is.</summary>
internal enum AppendKind
{
    /// <summary>Not appended: only listed and embedded.</summary>
    None,

    /// <summary>An image, shown on a page of the message's document.</summary>
    Image,

    /// <summary>A plain text file, shown on a page of the message's document.</summary>
    Text,

    /// <summary>A PDF file, whose pages are imported.</summary>
    Pdf,

    /// <summary>An HTML file, converted and imported.</summary>
    Html,

    /// <summary>An attached message, converted and imported.</summary>
    Message,
}

/// <summary>An attachment of the message: the part, a unique file name and a media type the PDF accepts.</summary>
internal sealed partial class EmailAttachment
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["text/xml"] = ".xml",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["text/csv"] = ".csv",
        ["text/calendar"] = ".ics",
        ["text/vcard"] = ".vcf",
        ["text/x-vcard"] = ".vcf",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/bmp"] = ".bmp",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["image/tiff"] = ".tif",
        ["message/rfc822"] = ".eml",
        ["message/global"] = ".eml",
    };

    private EmailAttachment(MimeEntity part, int index, string fileName, string mediaType)
    {
        Part = part;
        Index = index;
        FileName = fileName;
        MediaType = mediaType;
    }

    /// <summary>The part.</summary>
    public MimeEntity Part { get; }

    /// <summary>The attachment's number, from 1.</summary>
    public int Index { get; }

    /// <summary>The file name, unique among the message's attachments.</summary>
    public string FileName { get; }

    /// <summary>The media type, or <c>application/octet-stream</c> if the part's is not one a PDF can hold.</summary>
    public string MediaType { get; }

    /// <summary>How the attachment is appended (<see cref="AppendKind.None"/> unless attachments are appended and it can be).</summary>
    public AppendKind Append { get; set; }

    /// <summary>The id of the section an appended image or text file starts.</summary>
    public string Anchor => $"raicore-attachment-{Index}";

    /// <summary>The file's content.</summary>
    public byte[] Data => Part.Content;

    /// <summary>Names the attachments of a message: their own names made safe and unique, or names made from their type.</summary>
    public static List<EmailAttachment> For(MessageContent content)
    {
        var attachments = new List<EmailAttachment>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in content.Attachments)
        {
            var index = attachments.Count + 1;
            var type = part.ContentType.MediaType;
            var mediaType = MediaTypeRegex().IsMatch(type) ? type : "application/octet-stream";
            var name = Sanitize(part.FileName);
            if (name.Length == 0 && part.Message is { } message && MimeHeaders.DecodeText(message.Header("Subject") ?? string.Empty) is { Length: > 0 } subject)
            {
                name = Sanitize(subject) + ".eml";
            }

            if (name.Length == 0 || name == ".eml")
            {
                name = $"attachment-{index}{Extensions.GetValueOrDefault(type, string.Empty)}";
            }

            attachments.Add(new(part, index, Unique(name, names, counters), mediaType));
        }

        return attachments;
    }

    /// <summary>The size of the content for people: bytes, KB or MB (1024 based).</summary>
    public string Size()
    {
        var bytes = Data.LongLength;
        return bytes < 1024 ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : bytes < 1024 * 1024 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(bytes / 1024.0):0} KB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");
    }

    /// <summary>
    /// A file name without a path (what follows the last slash or backslash), control characters, surrounding
    /// whitespace and dots, cut to 200 characters; empty if nothing is left.
    /// </summary>
    internal static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var slash = name.LastIndexOfAny(['/', '\\']);
        var text = new StringBuilder();
        foreach (var c in name.AsSpan(slash + 1))
        {
            if (!char.IsControl(c))
            {
                text.Append(c);
            }
        }

        return Cut(text.ToString().Trim().Trim('.').Trim());
    }

    /// <summary>
    /// The file name of the embedded original message: its subject without control characters and with
    /// <c>_</c> for the characters file systems do not allow (<c>/ \ : * ? " &lt; &gt; |</c>), runs of whitespace
    /// as one space, without surrounding whitespace and dots and cut to 200 characters, or <c>message</c> if nothing
    /// is left; then <c>.mht</c> for a web archive and <c>.eml</c> for an email, and <c>(2)</c> and so on if one
    /// of <paramref name="taken"/> has the name.
    /// </summary>
    internal static string OriginalFileName(string subject, bool webArchive, IEnumerable<string> taken)
    {
        var text = new StringBuilder();
        foreach (var c in subject)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                }
            }
            else
            {
                text.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }
        }

        var stem = Cut(text.ToString().Trim().Trim('.').Trim()).TrimEnd(' ', '.');
        var name = (stem.Length > 0 ? stem : "message") + (webArchive ? ".mht" : ".eml");
        return Unique(name, new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase), []);
    }

    /// <summary>The text cut to 200 characters (not between the halves of a surrogate pair).</summary>
    private static string Cut(string text) =>
        text.Length <= 200 ? text : text[..(char.IsHighSurrogate(text[199]) ? 199 : 200)];

    /// <summary>The name, or <c>name (2).ext</c>, <c>name (3).ext</c> and so on if it is taken.</summary>
    private static string Unique(string name, HashSet<string> names, Dictionary<string, int> counters)
    {
        if (names.Add(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        var n = counters.GetValueOrDefault(name, 1); // Where the last search for this name ended: no quadratic searching.
        while (true)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({++n}){extension}");
            if (names.Add(candidate))
            {
                counters[name] = n;
                return candidate;
            }
        }
    }

    // The media types PdfAttachment accepts (as PDF/A-3 validators do).
    [GeneratedRegex("^[a-z0-9_.+-]+/[a-z0-9_.+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaTypeRegex();
}
