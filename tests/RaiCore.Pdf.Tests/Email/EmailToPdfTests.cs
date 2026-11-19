using System.Reflection;
using System.Text;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.Email;

/// <summary>
/// Converting messages. The corpus in Fixtures/Email holds synthetic messages in the structures common mail clients
/// write (made by a script; byte-exact, see .gitattributes):
/// <list type="bullet">
/// <item><c>outlook-related.eml</c>: mixed &gt; related &gt; alternative (Windows-1252 quoted-printable), a cid: image, a PDF attachment named by RFC 2047.</item>
/// <item><c>gmail-related.eml</c>: mixed &gt; related &gt; alternative (UTF-8, base64 and quoted-printable), a cid: image, a JPEG attachment.</item>
/// <item><c>apple-mail-inline.eml</c>: alternative &gt; (plain, mixed &gt; (HTML, inline JPEG, HTML)).</item>
/// <item><c>thunderbird-flowed.eml</c>: mixed &gt; (format=flowed 8bit text, a text attachment named by RFC 2231 continuations, an attached message).</item>
/// <item><c>iso-2022-jp.eml</c>: Japanese plain text, encoded-word subject and sender.</item>
/// <item><c>malformed.eml</c>: LF line endings, missing closing boundaries, a part without a blank line after its header, broken encodings, an unknown charset, a raw 8-bit subject.</item>
/// <item><c>chrome-web-archive.mht</c>: a web archive with absolute Content-Locations, a style sheet and an image by relative URL.</item>
/// <item><c>word-web-archive.mht</c>: a web archive with file: locations, Windows-1252.</item>
/// <item><c>outlook-invite.eml</c>: an alternative of plain text, HTML and text/calendar.</item>
/// </list>
/// </summary>
public class EmailToPdfTests
{
    private static readonly DateTimeOffset Moment = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Message(string name) => Fixtures.Read(Path.Combine("Email", name));

    private static EmailConverterOptions Options(bool append = false, Action<HtmlDiagnostic>? diagnostics = null) => new()
    {
        AppendAttachments = append,
        Html = new HtmlConverterOptions { TimeProvider = new DeterminismTests.FixedClock(Moment), Diagnostics = diagnostics },
    };

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static IReadOnlyList<ExtractedImage> Images(byte[] pdf, int page) => new ImageExtractor(PdfReader.Open(pdf)).ExtractPage(page).Images;

    /// <summary>Where text is on a page, from the top (PDF y grows upwards: a larger value is higher up).</summary>
    private static double Top(byte[] pdf, int page, string text) =>
        new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text).First().Bounds.Top;

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    public static TheoryData<string, bool> Corpus()
    {
        var data = new TheoryData<string, bool>();
        foreach (var file in Directory.GetFiles(Fixtures.Path("Email")).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file), false);
            data.Add(Path.GetFileName(file), true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_message_of_the_corpus_converts_with_all_its_attachments_accounted_for(string name, bool append)
    {
        var expected = new Dictionary<string, string[]>
        {
            ["outlook-related.eml"] = ["Kvartalsrapport \u00E6\u00F8\u00E5.pdf"],
            ["gmail-related.eml"] = ["sunset.jpg"],
            ["apple-mail-inline.eml"] = ["floorplan.jpg"],
            ["thunderbird-flowed.eml"] = ["Minutes m\u00F8de 2026-11-18.txt", "Original agenda.eml"],
            ["iso-2022-jp.eml"] = [],
            ["malformed.eml"] = ["notes(1).bin", "unterminated.txt"],
            ["chrome-web-archive.mht"] = [],
            ["word-web-archive.mht"] = [],
            ["outlook-invite.eml"] = ["attachment-1.ics"],
        };
        var diagnostics = new List<HtmlDiagnostic>();

        var document = EmailToPdf.Convert(Message(name), Options(append, diagnostics.Add));
        var pdf = document.ToArray();

        Assert.Equal(expected[name], document.Attachments.Select(a => a.FileName));
        Assert.All(document.Attachments, a => Assert.False(a.Data.IsEmpty));
        Assert.DoesNotContain(diagnostics, d => d.Kind is HtmlDiagnosticKind.ResourceNotLoaded or HtmlDiagnosticKind.ResourceInvalid);
        Assert.NotEmpty(Pages(pdf)[0]);
        AssertValid(pdf);
    }

    [Fact]
    public void An_outlook_message_has_its_header_block_body_and_inline_image_in_place()
    {
        var document = EmailToPdf.Convert(Message("outlook-related.eml"), Options());
        var pdf = document.ToArray();
        var text = Pages(pdf);

        Assert.Single(text);
        var page = text[0].ReplaceLineEndings("\n");
        Assert.StartsWith("Quarterly figures \u2013 caf\u00E9 report\n", page, StringComparison.Ordinal);
        Assert.Contains("From: Example, Alice <alice@example.com>", page, StringComparison.Ordinal);
        Assert.Contains("To: Bob Example <bob@example.org>, J\u00F8rgen S\u00F8rensen <jorgen@example.org>", page, StringComparison.Ordinal);
        Assert.Contains("Cc: carol@example.org", page, StringComparison.Ordinal);
        Assert.Contains("Date: Tue, 17 Nov 2026 08:14:00 +0000", page, StringComparison.Ordinal);
        Assert.Contains("Attachments: Kvartalsrapport \u00E6\u00F8\u00E5.pdf (2 KB)", page, StringComparison.Ordinal);
        Assert.Contains("Na\u00EFve caf\u00E9 prices rose 5 \u20AC \u2013 see the chart:", page, StringComparison.Ordinal);

        // The cid: image is between the lines around it, at the size the HTML gives it (160 x 60 px).
        var image = Assert.Single(Images(pdf, 0));
        Assert.True(image.Bounds.Top < Top(pdf, 0, "see the chart") && image.Bounds.Y > Top(pdf, 0, "Kind regards"), $"{image.Bounds}");
        Assert.Equal(120, image.Width, 0.5);
        Assert.Equal(45, image.Height, 0.5);

        // Word's @page rule (Letter, 1-inch margins) and named page do not change the page: A4, one page.
        Assert.Equal(PdfPageSize.A4.Width, document.Pages[0].Width, 0.01);

        var attachment = Assert.Single(document.Attachments);
        Assert.Equal("application/pdf", attachment.MediaType);
        Assert.True(attachment.Data.Span.StartsWith("%PDF-"u8));
        Assert.Equal("Quarterly figures \u2013 caf\u00E9 report", document.Info.Title);
        Assert.Equal("Example, Alice", document.Info.Author);
        Assert.Equal("RaiCore.Pdf.Email", document.Info.Creator);
    }

    [Fact]
    public void Appended_pdf_attachments_follow_the_message_with_a_bookmark()
    {
        var document = EmailToPdf.Convert(Message("outlook-related.eml"), Options(append: true));
        var pdf = document.ToArray();
        var text = Pages(pdf);

        Assert.Equal(3, text.Length);
        Assert.Contains("Attached PDF, page one", text[1], StringComparison.Ordinal);
        Assert.Contains("Attached PDF, page two", text[2], StringComparison.Ordinal);
        Assert.Equal(420, document.Pages[1].Width, 0.01); // Its own page size.
        Assert.Contains(document.Outline, item => item.Title == "Kvartalsrapport \u00E6\u00F8\u00E5.pdf");
        Assert.Single(document.Attachments); // Still embedded too.
        AssertValid(pdf);
    }

    [Fact]
    public void Appended_images_get_a_page_each_with_their_name_linked_from_the_list()
    {
        var document = EmailToPdf.Convert(Message("gmail-related.eml"), Options(append: true));
        var pdf = document.ToArray();
        var text = Pages(pdf);

        Assert.Equal(2, text.Length);
        Assert.Contains("Offsite photos \u2014 terrace", text[0], StringComparison.Ordinal);
        Assert.Contains("Attachments: sunset.jpg (3 KB)", text[0], StringComparison.Ordinal);
        Assert.Single(Images(pdf, 0)); // The cid: image of the body (not the attached JPEG).
        Assert.Equal("sunset.jpg", text[1].Trim());
        Assert.Equal(200 * 0.75, Assert.Single(Images(pdf, 1)).Width, 0.5);
        var link = document.Pages[0].Links.Single(l => l.Uri is null);
        Assert.NotNull(link);
        Assert.Contains(Bookmarks(document.Outline), item => item.Title == "sunset.jpg"); // Under the subject's.
    }

    private static IEnumerable<PdfOutlineItem> Bookmarks(IEnumerable<PdfOutlineItem> items) =>
        items.SelectMany(item => Bookmarks(item.Children).Prepend(item));

    [Fact]
    public void An_inline_image_between_html_parts_is_shown_where_it_is()
    {
        var pdf = EmailToPdf.ConvertToBytes(Message("apple-mail-inline.eml"), Options());

        var image = Assert.Single(Images(pdf, 0));
        Assert.True(image.Bounds.Top < Top(pdf, 0, "Here is the floor plan") && image.Bounds.Y > Top(pdf, 0, "Let me know"), $"{image.Bounds}");
        Assert.Contains("Attachments: floorplan.jpg (2 KB)", Pages(pdf)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_flows_and_attached_messages_and_text_files_are_appended()
    {
        var document = EmailToPdf.Convert(Message("thunderbird-flowed.eml"), Options(append: true));
        var pdf = document.ToArray();
        var text = Pages(pdf);

        Assert.Equal(3, text.Length);
        var body = text[0].ReplaceLineEndings(" ");
        Assert.Contains("Hi Gita, this paragraph is written as format=flowed", body, StringComparison.Ordinal);
        Assert.Contains("> On 17/11/2026 Gita wrote: Can you send the minutes and the agenda?", body, StringComparison.Ordinal);
        Assert.Contains("Attachments: Minutes m\u00F8de 2026-11-18.txt (70 bytes), Original agenda.eml (259 bytes)", body, StringComparison.Ordinal);
        Assert.Contains(document.Pages[0].Links, l => l.Uri?.ToString() == "https://example.org/minutes/2026-11-18");
        Assert.Contains(document.Pages[0].Links, l => l.Uri?.ToString() == "http://www.example.org/");

        Assert.Contains("Minutes of 18 November 2026", text[1], StringComparison.Ordinal);
        Assert.Contains("Original agenda", text[2], StringComparison.Ordinal);
        Assert.Contains("From: Ivan Example <ivan@example.net>", text[2], StringComparison.Ordinal);
        Assert.Contains("Agenda: 1. Budget 2. Offsite", text[2], StringComparison.Ordinal);

        var agenda = document.Attachments[1];
        Assert.Equal("message/rfc822", agenda.MediaType);
        Assert.StartsWith("From: Ivan Example", Encoding.ASCII.GetString(agenda.Data.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void Japanese_text_in_iso_2022_jp_is_decoded()
    {
        var document = EmailToPdf.Convert(Message("iso-2022-jp.eml"), Options());

        Assert.Equal("\u4F1A\u8B70\u306E\u304A\u77E5\u3089\u305B", document.Info.Title);
        Assert.Equal("\u5C71\u7530", document.Info.Author);
    }

    [Fact]
    public void A_malformed_message_shows_what_can_be_read()
    {
        var pdf = EmailToPdf.ConvertToBytes(Message("malformed.eml"), Options());
        var text = Pages(pdf)[0];

        Assert.Contains("Broken \u201Cboundaries\u201D test", text, StringComparison.Ordinal);
        Assert.Contains("The HTML version \u2014 with a broken =ZZ escape and a soft line break.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain fallback", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chrome-web-archive.mht", "Archived page", "Example page")]
    [InlineData("word-web-archive.mht", "Memo with an embedded image \u2013 r\u00E9sum\u00E9:", null)]
    public void Web_archives_have_no_header_block_and_their_resources_by_location(string name, string text, string? title)
    {
        var document = EmailToPdf.Convert(Message(name), Options());
        var pdf = document.ToArray();
        var page = Pages(pdf)[0];

        Assert.StartsWith(text, page, StringComparison.Ordinal);
        Assert.DoesNotContain("From:", page, StringComparison.Ordinal);
        Assert.Equal(100 * 0.75, Assert.Single(Images(pdf, 0)).Width, 0.5);
        Assert.Equal(title, document.Info.Title);
    }

    [Fact]
    public void A_web_archive_style_sheet_is_loaded_by_location()
    {
        var flow = EmailToPdf.Convert(Message("chrome-web-archive.mht"), Options()).ToArray();

        // h1.title { color: rgb(0, 102, 51) } from the archived style sheet: the heading is green.
        var content = Encoding.Latin1.GetString(PdfReader.Open(flow).Pages[0].GetContent());
        Assert.Contains("0 0.4 0.2 rg", content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_calendar_invitation_becomes_an_attachment()
    {
        var document = EmailToPdf.Convert(Message("outlook-invite.eml"), Options());

        var invite = Assert.Single(document.Attachments);
        Assert.Equal("text/calendar", invite.MediaType);
        Assert.StartsWith("BEGIN:VCALENDAR", Encoding.UTF8.GetString(invite.Data.Span), StringComparison.Ordinal);
        Assert.Contains("When: Tuesday, 24 November 2026 09:00\u201310:00 UTC.", Pages(document.ToArray())[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_fields_and_labels_can_be_chosen()
    {
        var options = new EmailConverterOptions
        {
            HeaderFields = ["From", "Message-ID", "X-Missing", " ", "Subject"],
            HeaderLabels = new Dictionary<string, string> { ["from"] = "Fra", ["Attachments"] = "Bilag" },
        };

        var text = Pages(EmailToPdf.ConvertToBytes(Message("outlook-related.eml"), options))[0];

        Assert.Contains("Fra: Example, Alice <alice@example.com>", text, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <AM0PR01MB1234567890ABCDEF@AM0PR01MB1234.example.com>", text, StringComparison.Ordinal);
        Assert.Contains("Bilag: Kvartalsrapport", text, StringComparison.Ordinal);
        Assert.Contains("Quarterly figures", text, StringComparison.Ordinal);
        Assert.DoesNotContain("To:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Missing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_fields_and_list_there_is_no_header_block_and_without_embedding_no_attachments()
    {
        var options = new EmailConverterOptions { HeaderFields = [], ListAttachments = false, EmbedAttachments = false };

        var document = EmailToPdf.Convert(Message("outlook-related.eml"), options);
        var text = Pages(document.ToArray())[0];

        Assert.StartsWith("Hi Bob,", text, StringComparison.Ordinal);
        Assert.Empty(document.Attachments);
    }

    [Fact]
    public void A_web_archive_shows_header_fields_when_they_are_asked_for()
    {
        var text = Pages(EmailToPdf.ConvertToBytes(Message("chrome-web-archive.mht"), new EmailConverterOptions { HeaderFields = ["Subject", "Date"] }))[0];

        Assert.StartsWith("Example page", text, StringComparison.Ordinal);
        Assert.Contains("Date: Thu, 19 Nov 2026 11:30:00 +0100", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_options_override_the_metadata_and_style_the_header_block()
    {
        var options = new EmailConverterOptions
        {
            Html = new HtmlConverterOptions
            {
                Title = "My title",
                Author = "My author",
                Creator = "My app",
                UserStyleSheet = ".raicore-email-subject { color: #ff0000 }",
            },
        };

        var document = EmailToPdf.Convert(Message("outlook-related.eml"), options);
        var content = Encoding.Latin1.GetString(PdfReader.Open(document.ToArray()).Pages[0].GetContent());

        Assert.Equal("My title", document.Info.Title);
        Assert.Equal("My author", document.Info.Author);
        Assert.Equal("My app", document.Info.Creator);
        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_language_comes_from_the_html_or_the_content_language()
    {
        Assert.Equal("en-GB", EmailToPdf.Convert(Message("outlook-related.eml"), Options()).Language); // Content-Language (the body's lang is on <body>).
        Assert.Equal("en", EmailToPdf.Convert(Message("chrome-web-archive.mht"), Options()).Language); // <html lang>.
        Assert.Null(EmailToPdf.Convert(Message("gmail-related.eml"), Options()).Language);
    }

    [Fact]
    public void Pdf_ua_and_tagged_output_work_for_messages()
    {
        var options = new EmailConverterOptions { Html = new HtmlConverterOptions { Tagged = true, PdfUA = true, Language = "en" } };

        var pdf = EmailToPdf.ConvertToBytes(Message("gmail-related.eml"), options);

        Assert.Contains("/StructTreeRoot", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        AssertValid(pdf);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B, 0)]
    [InlineData(PdfAConformance.PdfA3B, 1)]
    public void Pdf_a_2_gets_no_attached_files_and_says_so(PdfAConformance conformance, int attachments)
    {
        var diagnostics = new List<HtmlDiagnostic>();
        var options = new EmailConverterOptions { Html = new HtmlConverterOptions { PdfAConformance = conformance, Diagnostics = diagnostics.Add } };

        var document = EmailToPdf.Convert(Message("gmail-related.eml"), options);
        var pdf = document.ToArray();

        Assert.Equal(attachments, document.Attachments.Count);
        Assert.Equal(attachments == 0, diagnostics.Exists(d => d.Message.Contains("PDF/A-2", StringComparison.Ordinal)));
        Assert.Contains("Attachments: sunset.jpg", Pages(pdf)[0], StringComparison.Ordinal); // Still listed.
    }

    private static byte[] WithAttachment(string fileName, string mediaType, byte[] data, string body = "<p>See attached.</p>") =>
        Encoding.Latin1.GetBytes($"""
            From: a@example.com
            To: b@example.org
            Subject: Attached
            Content-Type: multipart/mixed; boundary=b

            --b
            Content-Type: text/html; charset=utf-8

            {body}
            --b
            Content-Type: {mediaType}; name="{fileName}"
            Content-Disposition: attachment; filename="{fileName}"
            Content-Transfer-Encoding: base64

            {Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks)}
            --b--
            """.ReplaceLineEndings("\r\n"));

    [Fact]
    public void A_damaged_or_encrypted_pdf_attachment_is_embedded_but_not_appended()
    {
        var encrypted = HtmlToPdf.Convert("<p>secret</p>");
        encrypted.Encryption = new PdfEncryption { UserPassword = "user", OwnerPassword = "owner" };
        var messages = new[]
        {
            WithAttachment("broken.pdf", "application/pdf", "%PDF-1.7\n1 0 obj << /Type /Catalog >> garbage"u8.ToArray()),
            WithAttachment("encrypted.pdf", "application/pdf", encrypted.ToArray()),
        };

        foreach (var message in messages)
        {
            var diagnostics = new List<HtmlDiagnostic>();
            var document = EmailToPdf.Convert(message, Options(append: true, diagnostics.Add));
            var pdf = document.ToArray();

            Assert.Single(Pages(pdf));
            Assert.Single(document.Attachments);
            var diagnostic = Assert.Single(diagnostics, d => d.Kind == HtmlDiagnosticKind.ResourceInvalid);
            Assert.Equal(document.Attachments[0].FileName, diagnostic.Subject);
            Assert.StartsWith("The attachment is not appended: ", diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Attachments_are_appended_up_to_the_page_limit()
    {
        var longPdf = HtmlToPdf.Convert(string.Concat(Enumerable.Range(1, 5).Select(i => $"<p style='break-before: page'>page {i}</p>"))).ToArray();
        var diagnostics = new List<HtmlDiagnostic>();
        var options = new EmailConverterOptions
        {
            AppendAttachments = true,
            Html = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxPages = 4 }, Diagnostics = diagnostics.Add },
        };

        var document = EmailToPdf.Convert(WithAttachment("long.pdf", "application/octet-stream", longPdf), options);

        Assert.Single(document.Pages);
        Assert.Contains(diagnostics, d => d.Subject == "long.pdf" && d.Message.Contains("MaxPages", StringComparison.Ordinal));
        Assert.Equal(6, EmailToPdf.Convert(WithAttachment("long.pdf", "application/octet-stream", longPdf), Options(append: true)).Pages.Count);
    }

    [Fact]
    public void Html_attachments_are_converted_and_unsupported_images_are_only_embedded()
    {
        var diagnostics = new List<HtmlDiagnostic>();
        var html = WithAttachment("page.htm", "application/octet-stream", "<h1>Attached page</h1>"u8.ToArray());
        var tiffLike = WithAttachment("scan.xyz", "image/x-unknown", [0x00, 0x01, 0x02, 0x03]);

        var withHtml = EmailToPdf.Convert(html, Options(append: true, diagnostics.Add));
        var withImage = EmailToPdf.Convert(tiffLike, Options(append: true, diagnostics.Add));

        Assert.Contains("Attached page", Pages(withHtml.ToArray())[1], StringComparison.Ordinal);
        Assert.Single(withImage.Pages);
        Assert.Single(withImage.Attachments);
        Assert.Contains(diagnostics, d => d.Subject == "scan.xyz" && d.Message.StartsWith("The attached image is not appended", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pdf_is_recognized_by_its_name_or_content()
    {
        var pdf = HtmlToPdf.Convert("<p>inside</p>").ToArray();

        foreach (var (name, type) in new[] { ("a.pdf", "application/octet-stream"), ("noextension", "application/octet-stream"), ("x.bin", "application/pdf") })
        {
            var document = EmailToPdf.Convert(WithAttachment(name, type, pdf), Options(append: true));
            Assert.Equal(2, document.Pages.Count);
        }

        Assert.Single(EmailToPdf.Convert(WithAttachment("data.bin", "application/octet-stream", [1, 2, 3]), Options(append: true)).Pages);
    }

    [Fact]
    public void Same_named_attachments_get_unique_names()
    {
        var part = "--b\r\nContent-Disposition: attachment; filename=\"a.txt\"\r\n\r\nx\r\n";
        var message = Encoding.ASCII.GetBytes($"Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n\r\nbody\r\n{part}{part}{part}--b\r\nContent-Disposition: attachment; filename=\"a (2).txt\"\r\n\r\ny\r\n--b\r\nContent-Type: application/zip\r\n\r\nz\r\n--b--\r\n");

        var document = EmailToPdf.Convert(message, Options());

        Assert.Equal(["a.txt", "a (2).txt", "a (3).txt", "a (2) (2).txt", "attachment-5.zip"], document.Attachments.Select(a => a.FileName));
    }

    [Fact]
    public void Images_come_from_the_message_and_never_from_local_files()
    {
        var file = Path.Combine(Path.GetTempPath(), $"raicore-email-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Fixtures.Read("PngSuite/basn2c08.png"));
        try
        {
            var requests = new List<Uri>();
            var options = new EmailConverterOptions
            {
                Html = new HtmlConverterOptions
                {
                    ResourceLoader = request =>
                    {
                        requests.Add(request.Uri);
                        return null;
                    },
                },
            };
            var body = $"<img src=\"{new Uri(file).AbsoluteUri}\"><img src=\"https://example.com/remote.png\"><img src=\"cid:missing@example.com\"><img src=\"/rooted.png\">";
            var message = Encoding.UTF8.GetBytes($"Content-Type: text/html\r\n\r\n{body}");

            var pdf = EmailToPdf.ConvertToBytes(message, options);

            Assert.Empty(Images(pdf, 0));
            Assert.Equal([new Uri("https://example.com/remote.png")], requests);

            // The default loader (data: and file: URIs) reaches nothing local either.
            Assert.Empty(Images(EmailToPdf.ConvertToBytes(message), 0));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_loader_that_fetches_remote_images_is_used_for_them()
    {
        var png = Fixtures.Read("PngSuite/basn2c08.png");
        var options = new EmailConverterOptions { Html = new HtmlConverterOptions { ResourceLoader = r => r.Uri.Host == "example.com" ? png : null } };
        var message = "Content-Type: text/html\r\n\r\n<img src=\"https://example.com/logo.png\"><img src=\"data:image/png;base64,"u8.ToArray()
            .Concat(Encoding.ASCII.GetBytes(Convert.ToBase64String(png) + "\">")).ToArray();

        Assert.Equal(2, Images(EmailToPdf.ConvertToBytes(message, options), 0).Count);
    }

    [Fact]
    public void Content_ids_match_case_insensitively_and_escaped_and_relative_locations_resolve()
    {
        var png = Convert.ToBase64String(Fixtures.Read("PngSuite/basn2c08.png"));
        var message = Encoding.ASCII.GetBytes($"""
            Content-Type: multipart/related; boundary=r

            --r
            Content-Type: text/html

            <img src="cid:Image%201@Example.com"><img src="CID:image2@example.com"><img src="pic.png"><img src="/abs/pic2.png">
            --r
            Content-Type: image/png
            Content-ID: <image 1@example.com>
            Content-Transfer-Encoding: base64

            {png}
            --r
            Content-Type: image/png
            Content-ID: <image2@example.com>
            Content-Transfer-Encoding: base64

            {png}
            --r
            Content-Type: image/png
            Content-Location: pic.png
            Content-Transfer-Encoding: base64

            {png}
            --r
            Content-Type: image/png
            Content-Location: /abs/pic2.png
            Content-Transfer-Encoding: base64

            {png}
            --r--
            """.ReplaceLineEndings("\r\n"));

        Assert.Equal(4, Images(EmailToPdf.ConvertToBytes(message), 0).Count);
    }

    [Fact]
    public void Related_parts_are_attachments_when_marked_so_or_named_and_not_referenced()
    {
        var message = Encoding.ASCII.GetBytes("""
            To: x@example.org
            Content-Type: multipart/related; boundary=r; start="<root@x>"

            --r
            Content-Type: image/png
            Content-ID: <used@x>
            Content-Disposition: inline; filename="used.png"

            used
            --r
            Content-Type: text/html
            Content-ID: <root@x>

            <img src="cid:used@x"><p>root</p>
            --r
            Content-Type: application/pdf
            Content-Disposition: attachment; filename="marked.pdf"

            pdf
            --r
            Content-Type: image/png; name="unreferenced.png"

            png
            --r
            Content-Type: text/css
            Content-Location: style.css

            p {}
            --r
            Content-Type: message/rfc822

            Subject: inner

            hi
            --r--
            """.ReplaceLineEndings("\r\n"));

        var document = EmailToPdf.Convert(message, Options());

        Assert.Equal(["marked.pdf", "unreferenced.png", "inner.eml"], document.Attachments.Select(a => a.FileName));
        Assert.Contains("root", Pages(document.ToArray())[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_is_used_when_there_is_no_html_and_unknown_alternatives_are_dropped()
    {
        var message = Encoding.ASCII.GetBytes("""
            Content-Type: multipart/alternative; boundary=a

            --a
            Content-Type: text/plain

            the plain version
            --a
            Content-Type: text/x-amp-html

            <amp>
            --a
            Content-Type: multipart/related; boundary=r

            --r
            Content-Type: image/png

            x
            --r--
            --a--
            """.ReplaceLineEndings("\r\n"));

        var document = EmailToPdf.Convert(message, Options());

        Assert.Contains("the plain version", Pages(document.ToArray())[0], StringComparison.Ordinal);
        Assert.Empty(document.Attachments);
    }

    [Fact]
    public void The_last_html_alternative_wins_also_inside_a_multipart()
    {
        var message = Encoding.ASCII.GetBytes("""
            Content-Type: multipart/alternative; boundary=a

            --a
            Content-Type: text/html

            <p>first html</p>
            --a
            Content-Type: multipart/mixed; boundary=m

            --m
            Content-Type: text/html

            <p>second html</p>
            --m
            Content-Type: application/pdf
            Content-Disposition: attachment; filename=x.pdf

            pdf
            --m--
            --a
            Content-Type: text/plain

            last but plain
            --a--
            """.ReplaceLineEndings("\r\n"));

        var document = EmailToPdf.Convert(message, Options());
        var text = Pages(document.ToArray())[0];

        Assert.Contains("second html", text, StringComparison.Ordinal);
        Assert.DoesNotContain("first html", text, StringComparison.Ordinal);
        Assert.DoesNotContain("last but plain", text, StringComparison.Ordinal);
        Assert.Equal("x.pdf", Assert.Single(document.Attachments).FileName);
    }

    [Fact]
    public void A_message_that_is_only_an_attachment_converts_with_its_header()
    {
        var message = WithAttachment("only.pdf", "application/pdf", HtmlToPdf.Convert("<p>the file</p>").ToArray(), body: string.Empty);
        var single = Encoding.ASCII.GetBytes("Subject: just a file\r\nContent-Type: application/pdf\r\nContent-Disposition: attachment; filename=f.pdf\r\n\r\n%PDF-");

        Assert.Contains("Attachments: only.pdf", Pages(EmailToPdf.ConvertToBytes(message))[0], StringComparison.Ordinal);
        var document = EmailToPdf.Convert(single, Options());
        Assert.Equal("f.pdf", Assert.Single(document.Attachments).FileName);
        Assert.Contains("just a file", Pages(document.ToArray())[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_message_converts_to_an_empty_page()
    {
        var document = EmailToPdf.Convert([], Options());

        Assert.Single(document.Pages);
        Assert.Empty(document.Attachments);
    }

    [Fact]
    public void Nesting_beyond_the_limit_fails_with_the_limit()
    {
        var message = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 10).Select(i => $"Content-Type: multipart/mixed; boundary=b{i}\r\n\r\n--b{i}\r\n")) + "\r\nleaf");
        var options = new EmailConverterOptions { Html = new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 5 } } };

        var error = Assert.Throws<ResourceLimitExceededException>(() => EmailToPdf.Convert(message, options));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), error.LimitName);
    }

    [Fact]
    public void Running_out_of_time_fails_with_the_timeout()
    {
        var options = new EmailConverterOptions
        {
            AppendAttachments = true,
            Html = new HtmlConverterOptions { Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) } },
        };

        var error = Assert.Throws<ResourceLimitExceededException>(() => EmailToPdf.Convert(Message("thunderbird-flowed.eml"), options));
        Assert.Equal(nameof(ResourceLimits.Timeout), error.LimitName);
    }

    [Fact]
    public async Task A_canceled_conversion_throws()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EmailToPdf.ConvertAsync(Message("gmail-related.eml"), null, source.Token));
    }

    [Fact]
    public async Task Every_overload_gives_the_same_pdf()
    {
        var message = Message("gmail-related.eml");
        var expected = EmailToPdf.ConvertToBytes(message, Options());
        var path = Path.Combine(Path.GetTempPath(), $"raicore-email-{Guid.NewGuid():N}.eml");
        File.WriteAllBytes(path, message);
        try
        {
            Assert.Equal(expected, EmailToPdf.Convert(path, Options()).ToArray());
            Assert.Equal(expected, EmailToPdf.Convert(new MemoryStream(message), Options()).ToArray());
            Assert.Equal(expected, EmailToPdf.ConvertToBytes(new MemoryStream(message), Options()));
            Assert.Equal(expected, (await EmailToPdf.ConvertAsync(message, Options(), TestContext.Current.CancellationToken)).ToArray());
            await using var stream = new AsyncReadStream(message);
            Assert.Equal(expected, (await EmailToPdf.ConvertAsync(stream, Options(), TestContext.Current.CancellationToken)).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => EmailToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => EmailToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => EmailToPdf.Convert((string)null!));
        Assert.Throws<ArgumentNullException>(() => EmailToPdf.Convert([], new EmailConverterOptions { Html = null! }));
        Assert.Throws<ArgumentException>(() => EmailToPdf.Convert([], new EmailConverterOptions { Html = new HtmlConverterOptions { Limits = null! } }));
    }

    [Fact]
    public void The_conversion_options_take_every_html_option()
    {
        // Each public property of HtmlConverterOptions set to a value other than its default must reach the options the
        // message is converted with, so that an option added later is not forgotten here.
        var values = new Dictionary<string, object?>
        {
            [nameof(HtmlConverterOptions.Fonts)] = new RaiCore.Pdf.Fonts.FontCollection(),
            [nameof(HtmlConverterOptions.FallbackFontFamilies)] = new[] { "X" },
            [nameof(HtmlConverterOptions.Page)] = new RaiCore.Pdf.Layout.PageSetup(PdfPageSize.A5, RaiCore.Pdf.Layout.Thickness.All(1)),
            [nameof(HtmlConverterOptions.BaseUri)] = new Uri("https://example.com/"),
            [nameof(HtmlConverterOptions.ResourceLoader)] = new ResourceLoader(_ => null),
            [nameof(HtmlConverterOptions.Hyphenators)] = new Dictionary<string, RaiCore.Pdf.Layout.Text.Hyphenator>(),
            [nameof(HtmlConverterOptions.UserStyleSheet)] = "p {}",
            [nameof(HtmlConverterOptions.CreateBookmarks)] = false,
            [nameof(HtmlConverterOptions.Title)] = "t",
            [nameof(HtmlConverterOptions.Author)] = "a",
            [nameof(HtmlConverterOptions.Subject)] = "s",
            [nameof(HtmlConverterOptions.Keywords)] = "k",
            [nameof(HtmlConverterOptions.Creator)] = "c",
            [nameof(HtmlConverterOptions.Language)] = "da",
            [nameof(HtmlConverterOptions.Encryption)] = new PdfEncryption { UserPassword = "u", OwnerPassword = "o" },
            [nameof(HtmlConverterOptions.Tagged)] = true,
            [nameof(HtmlConverterOptions.PdfUA)] = true,
            [nameof(HtmlConverterOptions.PdfAConformance)] = PdfAConformance.PdfA3B,
            [nameof(HtmlConverterOptions.Limits)] = ResourceLimits.Default with { MaxPages = 3 },
            [nameof(HtmlConverterOptions.TimeProvider)] = new DeterminismTests.FixedClock(Moment),
            [nameof(HtmlConverterOptions.Diagnostics)] = new Action<HtmlDiagnostic>(_ => { }),
        };
        var properties = typeof(HtmlConverterOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(values.Keys.Order(StringComparer.Ordinal), properties.Select(p => p.Name).Order(StringComparer.Ordinal));
        var source = new HtmlConverterOptions();
        foreach (var property in properties)
        {
            property.SetValue(source, values[property.Name]);
        }

        // Those the conversion sets itself: the loader (the message's own), the base, the style sheet (built-in styles
        // first) and the limits (the time left); the metadata only when the options have none.
        var loader = new ResourceLoader(_ => null);
        var limits = ResourceLimits.Default with { MaxPages = 7 };
        var derived = EmailConverter.DeriveHtmlOptions(source, loader, new Uri("https://example.org/"), "css", "title", "author", limits);
        foreach (var property in properties)
        {
            var expected = property.Name switch
            {
                nameof(HtmlConverterOptions.ResourceLoader) => loader,
                nameof(HtmlConverterOptions.BaseUri) => new Uri("https://example.org/"),
                nameof(HtmlConverterOptions.UserStyleSheet) => "css",
                nameof(HtmlConverterOptions.Limits) => limits,
                _ => values[property.Name],
            };
            Assert.Equal(expected, property.GetValue(derived));
        }

        var defaults = EmailConverter.DeriveHtmlOptions(new HtmlConverterOptions(), loader, null, null, "title", "author", limits);
        Assert.Equal(("title", "author", "RaiCore.Pdf.Email"), (defaults.Title, defaults.Author, defaults.Creator));
    }
}

/// <summary>A stream that can only be read asynchronously.</summary>
internal sealed class AsyncReadStream(byte[] data) : Stream
{
    private readonly MemoryStream _content = new(data);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _content.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _content.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
        }

        base.Dispose(disposing);
    }
}
