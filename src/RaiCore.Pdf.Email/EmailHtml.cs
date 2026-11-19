using System.Text;
using RaiCore.Pdf.Email.Mime;

namespace RaiCore.Pdf.Email;

/// <summary>
/// The HTML document a message is converted from: the header block, then the body parts in order (HTML bodies with
/// their style sheets, plain text, inline images), then the appended images and text files.
/// </summary>
internal static class EmailHtml
{
    /// <summary>The header fields shown when the options name none.</summary>
    public static readonly IReadOnlyList<string> DefaultFields = ["From", "To", "Cc", "Date", "Subject"];

    /// <summary>The styles of the header block and the converter's other elements, applied before the user's style sheet.</summary>
    public const string StyleSheet = """
        .raicore-email-header { display: block; margin: 0 0 12pt 0; padding: 0 0 6pt 0; border: none; border-bottom: 0.75pt solid #808080; background: none; color: #000000; font-family: sans-serif; font-size: 10pt; font-weight: normal; font-style: normal; line-height: 1.3; text-align: left; }
        .raicore-email-subject { display: block; margin: 0 0 6pt 0; padding: 0; border: none; color: #000000; font-family: sans-serif; font-size: 14pt; font-weight: bold; font-style: normal; line-height: 1.25; }
        .raicore-email-header table { width: auto; margin: 0; border: none; border-collapse: collapse; background: none; }
        .raicore-email-header th { padding: 1pt 8pt 1pt 0; border: none; background: none; color: #000000; font-family: sans-serif; font-size: 10pt; font-weight: bold; text-align: left; vertical-align: top; white-space: nowrap; }
        .raicore-email-header td { padding: 1pt 0; border: none; background: none; color: #000000; font-family: sans-serif; font-size: 10pt; font-weight: normal; text-align: left; vertical-align: top; }
        .raicore-email-text { white-space: pre-wrap; font-family: monospace; font-size: 10pt; line-height: 1.3; margin: 0 0 10pt 0; }
        #raicore-email-body, #raicore-email-body * { page: auto; }
        .raicore-email-attachment { break-before: page; }
        .raicore-email-attachment h2 { margin: 0 0 10pt 0; padding: 0 0 4pt 0; border-bottom: 0.75pt solid #808080; color: #000000; font-family: sans-serif; font-size: 12pt; font-weight: bold; }
        .raicore-email-attachment img, .raicore-email-image img { max-width: 100%; }
        """;

    private static readonly HashSet<string> AddressFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "To", "Cc", "Bcc", "Reply-To", "Sender", "Resent-From", "Resent-To", "Resent-Cc", "Resent-Bcc", "Resent-Sender",
        "Disposition-Notification-To", "Return-Receipt-To",
    };

    /// <summary>Builds the document.</summary>
    /// <param name="content">The message.</param>
    /// <param name="attachments">Its attachments.</param>
    /// <param name="options">The options.</param>
    /// <param name="resources">Registers the images the document shows itself.</param>
    public static string Build(MessageContent content, IReadOnlyList<EmailAttachment> attachments, EmailConverterOptions options, MessageResources resources)
    {
        var parts = content.Body.Select(b => b.Kind == BodyKind.Html ? HtmlParts.Split(b.Part.Text) : null).ToList();
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html");
        if ((parts.FirstOrDefault(p => p?.Language is not null)?.Language ?? ContentLanguage(content.Root)) is { } language)
        {
            html.Append(" lang=\"").Append(PlainText.Escape(language)).Append('"');
        }

        html.Append(">\n<head>\n<meta charset=\"utf-8\">\n");
        foreach (var part in parts)
        {
            html.Append(part?.Head);
        }

        html.Append("</head>\n<body>\n");
        AppendHeader(html, content, attachments, options);
        html.Append("<div class=\"raicore-email-body\" id=\"raicore-email-body\">\n");
        var imageIndex = 0;
        for (var i = 0; i < content.Body.Count; i++)
        {
            var (kind, part) = content.Body[i];
            switch (kind)
            {
                case BodyKind.Html:
                    html.Append("<div class=\"raicore-email-part\"").Append(parts[i]!.BodyAttributes).Append(">\n")
                        .Append(parts[i]!.Body).Append("\n</div>\n");
                    break;
                case BodyKind.Text:
                    AppendText(html, part);
                    break;
                default:
                    var name = EmailAttachment.Sanitize(part.FileName);
                    html.Append("<div class=\"raicore-email-image\"><img src=\"").Append(resources.Reference(part, ++imageIndex))
                        .Append("\" alt=\"").Append(PlainText.Escape(name.Length > 0 ? name : "Image")).Append("\"></div>\n");
                    break;
            }
        }

        html.Append("</div>\n");
        foreach (var attachment in attachments)
        {
            if (attachment.Append is AppendKind.Image or AppendKind.Text)
            {
                html.Append("<section class=\"raicore-email-attachment\" id=\"").Append(attachment.Anchor).Append("\">\n<h2>")
                    .Append(PlainText.Escape(attachment.FileName)).Append("</h2>\n");
                if (attachment.Append == AppendKind.Image)
                {
                    html.Append("<img src=\"").Append(resources.Reference(attachment.Part, ++imageIndex)).Append("\" alt=\"")
                        .Append(PlainText.Escape(attachment.FileName)).Append("\">\n");
                }
                else
                {
                    AppendText(html, attachment.Part);
                }

                html.Append("</section>\n");
            }
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>The header fields shown for a message: the options' list, or the default one (none for a web archive).</summary>
    public static IReadOnlyList<string> FieldsFor(MessageContent content, EmailConverterOptions options) =>
        options.HeaderFields ?? (content.IsWebArchive ? [] : DefaultFields);

    /// <summary>A header field of the message decoded for display: address lists formatted, other text with whitespace collapsed.</summary>
    public static string FieldValue(MimeEntity message, string name) =>
        message.Header(name) is not { } raw ? string.Empty
        : AddressFields.Contains(name) ? MimeHeaders.FormatAddresses(raw)
        : MimeHeaders.DecodeText(raw);

    /// <summary>The first language of the message's <c>Content-Language</c> (RFC 3282), if it is a language tag.</summary>
    private static string? ContentLanguage(MimeEntity message) =>
        message.Header("Content-Language")?.Split(',')[0].Trim() is { Length: > 0 and <= 35 } tag
            && tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? tag
            : null;

    private static void AppendText(StringBuilder html, MimeEntity part)
    {
        var type = part.ContentType;
        var flowed = string.Equals(type.Parameter("format"), "flowed", StringComparison.OrdinalIgnoreCase);
        var deleteSpace = string.Equals(type.Parameter("delsp"), "yes", StringComparison.OrdinalIgnoreCase);
        PlainText.AppendHtml(html, part.Text, flowed, deleteSpace, "raicore-email-text");
    }

    private static void AppendHeader(StringBuilder html, MessageContent content, IReadOnlyList<EmailAttachment> attachments, EmailConverterOptions options)
    {
        var fields = FieldsFor(content, options);
        var subject = fields.Any(f => string.Equals(f, "Subject", StringComparison.OrdinalIgnoreCase)) ? FieldValue(content.Root, "Subject") : string.Empty;
        var rows = new StringBuilder();
        foreach (var field in fields)
        {
            if (string.Equals(field, "Subject", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            var value = FieldValue(content.Root, field.Trim());
            if (value.Length > 0)
            {
                AppendRow(rows, Label(options, field.Trim()));
                PlainText.AppendEscaped(rows, value);
                rows.Append("</td></tr>\n");
            }
        }

        if (options.ListAttachments && attachments.Count > 0)
        {
            AppendRow(rows, Label(options, "Attachments"));
            for (var i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                if (i > 0)
                {
                    rows.Append(", ");
                }

                // Appended images and text files are in this document: their names link to them.
                var linked = attachment.Append is AppendKind.Image or AppendKind.Text;
                if (linked)
                {
                    rows.Append("<a href=\"#").Append(attachment.Anchor).Append("\">");
                }

                PlainText.AppendEscaped(rows, attachment.FileName);
                if (linked)
                {
                    rows.Append("</a>");
                }

                rows.Append("\u00A0(").Append(attachment.Size()).Append(')');
            }

            rows.Append("</td></tr>\n");
        }

        if (subject.Length == 0 && rows.Length == 0)
        {
            return;
        }

        html.Append("<div class=\"raicore-email-header\">\n");
        if (subject.Length > 0)
        {
            html.Append("<h1 class=\"raicore-email-subject\">");
            PlainText.AppendEscaped(html, subject);
            html.Append("</h1>\n");
        }

        if (rows.Length > 0)
        {
            html.Append("<table>\n").Append(rows).Append("</table>\n");
        }

        html.Append("</div>\n");
    }

    private static void AppendRow(StringBuilder rows, string label)
    {
        rows.Append("<tr><th scope=\"row\">");
        PlainText.AppendEscaped(rows, label);
        rows.Append(":</th><td>");
    }

    private static string Label(EmailConverterOptions options, string field)
    {
        if (options.HeaderLabels is { } labels)
        {
            if (labels.TryGetValue(field, out var label))
            {
                return label;
            }

            foreach (var (key, value) in labels)
            {
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return field;
    }
}
