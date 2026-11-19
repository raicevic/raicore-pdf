using System.Diagnostics;
using RaiCore.Pdf.Email.Mime;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Email;

/// <summary>
/// Converts a parsed message: builds its HTML, converts that, then appends the attachments that are converted
/// separately (PDF files, HTML files, attached messages) and embeds the attachments.
/// </summary>
internal sealed class EmailConverter
{
    /// <summary>The creator the PDFs name unless the options name one.</summary>
    public const string Creator = "RaiCore.Pdf.Email";

    private readonly EmailConverterOptions _options;
    private readonly ResourceLimits _limits;
    private readonly Stopwatch _clock;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[]? _original;

    private EmailConverter(EmailConverterOptions options, Stopwatch clock, CancellationToken cancellationToken, byte[]? original = null)
    {
        _options = options;
        _original = original;
        _limits = options.Html.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        _clock = clock;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Parses and converts a message.</summary>
    public static Task<PdfDocument> ConvertAsync(byte[] data, EmailConverterOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options.Html, nameof(options));
        var limits = options.Html.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        MimeEntity root;
        using (limits.Enforce(cancellationToken))
        {
            root = MimeParser.Parse(data, limits);
        }

        return new EmailConverter(options, clock, cancellationToken, data).ConvertAsync(root);
    }

    /// <summary>The options of a conversion, from the user's HTML options: the message's loader, styles, base and metadata.</summary>
    internal static HtmlConverterOptions DeriveHtmlOptions(
        HtmlConverterOptions html, ResourceLoader loader, Uri? baseUri, string? styleSheet, string? title, string? author, ResourceLimits limits) => new()
        {
            Fonts = html.Fonts,
            FallbackFontFamilies = html.FallbackFontFamilies,
            Page = html.Page,
            BaseUri = baseUri,
            ResourceLoader = loader,
            Hyphenators = html.Hyphenators,
            UserStyleSheet = styleSheet,
            CreateBookmarks = html.CreateBookmarks,
            Title = html.Title ?? title,
            Author = html.Author ?? author,
            Subject = html.Subject,
            Keywords = html.Keywords,
            Creator = html.Creator ?? Creator,
            Language = html.Language,
            Encryption = html.Encryption,
            Tagged = html.Tagged,
            PdfUA = html.PdfUA,
            PdfAConformance = html.PdfAConformance,
            Limits = limits,
            TimeProvider = html.TimeProvider,
            Diagnostics = html.Diagnostics,
        };

    private async Task<PdfDocument> ConvertAsync(MimeEntity root)
    {
        var content = MessageReader.Read(root);
        var attachments = EmailAttachment.For(content);
        var html = _options.Html;
        var resources = new MessageResources(content, html.ResourceLoader ?? ResourceLoaders.Default, html.BaseUri);
        if (_options.AppendAttachments)
        {
            foreach (var attachment in attachments)
            {
                attachment.Append = AppendKindOf(attachment);
            }
        }

        var markup = EmailHtml.Build(content, attachments, _options, resources);
        var styleSheet = EmailHtml.StyleSheet + (html.UserStyleSheet is { } user ? "\n" + user : string.Empty);
        var subject = MimeHeaders.DecodeText(root.Header("Subject") ?? string.Empty);
        var author = MimeHeaders.FirstName(root.Header("From") ?? string.Empty);
        var options = DeriveHtmlOptions(html, resources.Load, resources.BaseUri, styleSheet, subject.Length > 0 ? subject : null, author.Length > 0 ? author : null, RemainingLimits());
        var document = await HtmlToPdf.ConvertAsync(markup, options, _cancellationToken).ConfigureAwait(false);

        foreach (var attachment in attachments)
        {
            if (attachment.Append is AppendKind.Pdf or AppendKind.Html or AppendKind.Message)
            {
                await AppendAsync(document, attachment, resources).ConfigureAwait(false);
            }
        }

        if (_options.EmbedOriginal && _original is not null)
        {
            EmbedOriginal(document, content.IsWebArchive, subject, _options.EmbedAttachments ? attachments.Select(a => a.FileName) : []);
        }

        if (_options.EmbedAttachments && attachments.Count > 0)
        {
            if (document.PdfAConformance is PdfAConformance.PdfA2B or PdfAConformance.PdfA2A)
            {
                Report(HtmlDiagnosticKind.ResourceNotLoaded, HtmlDiagnosticSeverity.Warning, attachments[0].FileName,
                    "The attachments are not embedded: PDF/A-2 does not allow attached files (PDF/A-3 does).");
            }
            else
            {
                foreach (var attachment in attachments)
                {
                    document.Attachments.Add(new PdfAttachment(attachment.FileName, attachment.Data, attachment.MediaType));
                }
            }
        }

        return document;
    }

    /// <summary>
    /// Embeds the message's own bytes as the document's source (<see cref="PdfAttachmentRelationship.Source"/>), named
    /// after the subject and unique among the embedded attachments; under PDF/A-2 it is left out and reported, as the
    /// attachments are.
    /// </summary>
    private void EmbedOriginal(PdfDocument document, bool webArchive, string subject, IEnumerable<string> taken)
    {
        var fileName = EmailAttachment.OriginalFileName(subject, webArchive, taken);
        if (document.PdfAConformance is PdfAConformance.PdfA2B or PdfAConformance.PdfA2A)
        {
            Report(HtmlDiagnosticKind.ResourceNotLoaded, HtmlDiagnosticSeverity.Warning, fileName,
                "The original message is not embedded: PDF/A-2 does not allow attached files (PDF/A-3 does).");
            return;
        }

        document.Attachments.Add(new PdfAttachment(fileName, _original, webArchive ? "multipart/related" : "message/rfc822")
        {
            Description = webArchive ? "Original web archive" : "Original message",
            Relationship = PdfAttachmentRelationship.Source,
        });
    }

    /// <summary>How an attachment can be appended: by its media type, file name extension or content.</summary>
    private AppendKind AppendKindOf(EmailAttachment attachment)
    {
        var type = attachment.Part.ContentType.MediaType;
        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (type is "message/rfc822" or "message/global")
        {
            return attachment.Part.Message is null ? AppendKind.None : AppendKind.Message;
        }

        if (type == "application/pdf" || extension == ".pdf" || attachment.Data.AsSpan(0, Math.Min(attachment.Data.Length, 1024)).IndexOf("%PDF-"u8) >= 0)
        {
            return AppendKind.Pdf;
        }

        if (type == "text/html" || extension is ".html" or ".htm")
        {
            return AppendKind.Html;
        }

        if (type == "text/plain" || extension == ".txt")
        {
            return AppendKind.Text;
        }

        if (type.StartsWith("image/", StringComparison.Ordinal)
            || extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".tif" or ".tiff")
        {
            // Shown only if it can be: the format is supported and the image within the limits.
            try
            {
                using (_limits.Enforce(_cancellationToken))
                {
                    PdfImage.Load(attachment.Data);
                }

                return AppendKind.Image;
            }
            catch (Exception ex) when (IsAttachmentError(ex))
            {
                Report(HtmlDiagnosticKind.ResourceInvalid, HtmlDiagnosticSeverity.Warning, attachment.FileName, $"The attached image is not appended: {ex.Message}");
            }
        }

        return AppendKind.None;
    }

    /// <summary>Appends the pages of a PDF file, or of an HTML file or attached message converted on their own.</summary>
    private async Task AppendAsync(PdfDocument document, EmailAttachment attachment, MessageResources resources)
    {
        try
        {
            var pages = _limits.MaxPages - document.Pages.Count;
            if (pages <= 0)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), _limits.MaxPages, "The document has as many pages as ResourceLimits.MaxPages allows.");
            }

            byte[] pdf;
            if (attachment.Append == AppendKind.Pdf)
            {
                pdf = attachment.Data;
            }
            else
            {
                // Converted like the message, but without what an imported page leaves behind anyway (encryption,
                // structure, embedded files, metadata), and with at most the pages that are left.
                var nested = Intermediate(_limits with { MaxPages = pages });
                var converted = attachment.Append == AppendKind.Message
                    ? await new EmailConverter(
                        new EmailConverterOptions
                        {
                            Html = nested,
                            HeaderFields = _options.HeaderFields,
                            HeaderLabels = _options.HeaderLabels,
                            EmbedAttachments = false,
                            ListAttachments = _options.ListAttachments,
                            AppendAttachments = _options.AppendAttachments,
                        },
                        _clock,
                        _cancellationToken).ConvertAsync(attachment.Part.Message!).ConfigureAwait(false)
                    : await HtmlToPdf.ConvertAsync(
                        attachment.Part.Text,
                        DeriveHtmlOptions(nested, resources.Load, null, nested.UserStyleSheet, null, null, RemainingLimits() with { MaxPages = pages }),
                        _cancellationToken).ConfigureAwait(false);
                pdf = converted.ToArray();
            }

            using (RemainingLimits().Enforce(_cancellationToken))
            {
                var reader = PdfReader.Open(pdf);
                if (reader.Pages.Count > pages)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), _limits.MaxPages,
                        $"Its {reader.Pages.Count} pages would make the document longer than ResourceLimits.MaxPages ({_limits.MaxPages}).");
                }

                if (attachment.Append == AppendKind.Pdf)
                {
                    // Read in full once, so that a damaged file fails here and not when the document is saved.
                    var copy = new PdfDocument();
                    copy.ImportPages(reader, new PdfImportOptions { KeepOutline = false });
                    reader = PdfReader.Open(copy.ToArray());
                }

                var imported = document.ImportPages(reader, new PdfImportOptions { KeepOutline = false });
                if (_options.Html.CreateBookmarks && imported.Count > 0)
                {
                    document.Outline.Add(new PdfOutlineItem(attachment.FileName, imported[0]));
                }
            }
        }
        catch (Exception ex) when (IsAttachmentError(ex))
        {
            Report(HtmlDiagnosticKind.ResourceInvalid, HtmlDiagnosticSeverity.Warning, attachment.FileName, $"The attachment is not appended: {ex.Message}");
        }
    }

    /// <summary>
    /// The HTML options for converting an attachment on its own, to import its pages: the user's page, fonts, styles
    /// and loader, without encryption, tagging, bookmarks or metadata.
    /// </summary>
    private HtmlConverterOptions Intermediate(ResourceLimits limits) => new()
    {
        Fonts = _options.Html.Fonts,
        FallbackFontFamilies = _options.Html.FallbackFontFamilies,
        Page = _options.Html.Page,
        ResourceLoader = _options.Html.ResourceLoader,
        Hyphenators = _options.Html.Hyphenators,
        UserStyleSheet = _options.Html.UserStyleSheet,
        CreateBookmarks = false,
        PdfAConformance = _options.Html.PdfAConformance,
        Limits = limits,
        TimeProvider = _options.Html.TimeProvider,
        Diagnostics = _options.Html.Diagnostics,
    };

    /// <summary>The limits with the time that is left of the conversion's <see cref="ResourceLimits.Timeout"/>.</summary>
    private ResourceLimits RemainingLimits()
    {
        if (_limits.Timeout == Timeout.InfiniteTimeSpan)
        {
            return _limits;
        }

        var remaining = _limits.Timeout - _clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.Timeout), (long)_limits.Timeout.TotalMilliseconds,
                $"The conversion took longer than ResourceLimits.Timeout ({_limits.Timeout}).");
        }

        return _limits with { Timeout = remaining };
    }

    /// <summary>
    /// Whether an exception is about one attachment, which is then left out (but still embedded): a damaged or encrypted
    /// file, an unsupported format, a document breaking a rule of PDF/A, or a limit on its size. Running out of time
    /// and cancellation end the conversion.
    /// </summary>
    private static bool IsAttachmentError(Exception exception) => exception switch
    {
        ResourceLimitExceededException limit => limit.LimitName != nameof(ResourceLimits.Timeout),
        PdfException or NotSupportedException or InvalidOperationException => true,
        _ => false,
    };

    private void Report(HtmlDiagnosticKind kind, HtmlDiagnosticSeverity severity, string subject, string message) =>
        _options.Html.Diagnostics?.Invoke(new HtmlDiagnostic(kind, severity, subject, message, null, 1));
}
