using System.Text;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Tests.Email;

/// <summary><see cref="EmailConverterOptions.EmbedOriginal"/>: the message itself embedded as the PDF's source file.</summary>
public class EmbedOriginalTests
{
    private static readonly DateTimeOffset Moment = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Message(string name) => Fixtures.Read(Path.Combine("Email", name));

    private static EmailConverterOptions Options(
        bool embedOriginal = true, PdfAConformance conformance = PdfAConformance.None, Action<HtmlDiagnostic>? diagnostics = null) => new()
        {
            EmbedOriginal = embedOriginal,
            Html = new HtmlConverterOptions
            {
                TimeProvider = new DeterminismTests.FixedClock(Moment),
                PdfAConformance = conformance,
                Language = "en",
                Diagnostics = diagnostics,
            },
        };

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    private static PdfAttachment Original(PdfDocument document) =>
        Assert.Single(document.Attachments, a => a.Relationship == PdfAttachmentRelationship.Source);

    /// <summary>The saved file specifications in the catalog's associated files: name, relationship, media type, description and content.</summary>
    private static List<(string Name, string Relationship, string MediaType, string? Description, byte[] Data)> Saved(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        var files = new List<(string, string, string, string?, byte[])>();
        if (reader.Resolve(reader.Catalog["AF"]) is not PdfArray associated)
        {
            return files;
        }

        foreach (var item in associated.Items)
        {
            var spec = (PdfDictionary)reader.Resolve(item)!;
            var file = (PdfStreamObject)reader.Resolve(((PdfDictionary)spec["EF"]!)["F"])!;
            files.Add((
                Text((PdfString)spec["UF"]!),
                ((PdfName)spec["AFRelationship"]!).Value,
                ((PdfName)file.Dictionary["Subtype"]!).Value,
                spec["Desc"] is PdfString description ? Text(description) : null,
                StreamDecoder.Decode(file, reader.Resolve)));
        }

        return files;

        static string Text(PdfString text) => text.Bytes is [0xFE, 0xFF, .. var utf16]
            ? Encoding.BigEndianUnicode.GetString(utf16)
            : Encoding.Latin1.GetString(text.Bytes);
    }

    [Fact]
    public void Off_by_default()
    {
        Assert.False(new EmailConverterOptions().EmbedOriginal);

        var document = EmailToPdf.Convert(Message("gmail-related.eml"));

        Assert.Equal(["sunset.jpg"], document.Attachments.Select(a => a.FileName));
        Assert.DoesNotContain(document.Attachments, a => a.Relationship == PdfAttachmentRelationship.Source);
    }

    [Fact]
    public void The_message_is_embedded_unchanged_as_the_source()
    {
        var message = Message("gmail-related.eml");

        var document = EmailToPdf.Convert(message, Options());
        var original = Original(document);

        Assert.Equal("Offsite photos \u2014 terrace.eml", original.FileName);
        Assert.Equal("message/rfc822", original.MediaType);
        Assert.Equal("Original message", original.Description);
        Assert.Equal(message, original.Data.ToArray());
        Assert.Equal(["Offsite photos \u2014 terrace.eml", "sunset.jpg"], document.Attachments.Select(a => a.FileName).Order(StringComparer.Ordinal));

        var pdf = document.ToArray();
        var saved = Assert.Single(Saved(pdf), f => f.Relationship == "Source");
        Assert.Equal(("Offsite photos \u2014 terrace.eml", "message/rfc822", "Original message"), (saved.Name, saved.MediaType, saved.Description));
        Assert.Equal(message, saved.Data);
        Assert.Equal("Unspecified", Assert.Single(Saved(pdf), f => f.Name == "sunset.jpg").Relationship);
        AssertValid(pdf);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA3B)]
    [InlineData(PdfAConformance.PdfA3A)]
    public void Pdf_a_3_embeds_it_with_the_source_relationship(PdfAConformance conformance)
    {
        var message = Message("outlook-related.eml");
        var diagnostics = new List<HtmlDiagnostic>();

        var pdf = EmailToPdf.ConvertToBytes(message, Options(conformance: conformance, diagnostics: diagnostics.Add));

        var saved = Assert.Single(Saved(pdf), f => f.Relationship == "Source");
        Assert.Equal("Quarterly figures \u2013 caf\u00E9 report.eml", saved.Name);
        Assert.Equal(message, saved.Data);
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("PDF/A-2", StringComparison.Ordinal));
        AssertValid(pdf);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B)]
    [InlineData(PdfAConformance.PdfA2A)]
    public void Pdf_a_2_leaves_it_out_and_says_so_as_for_attachments(PdfAConformance conformance)
    {
        var diagnostics = new List<HtmlDiagnostic>();

        var document = EmailToPdf.Convert(Message("gmail-related.eml"), Options(conformance: conformance, diagnostics: diagnostics.Add));
        var pdf = document.ToArray();

        Assert.Empty(document.Attachments);
        Assert.Empty(Saved(pdf));
        var warning = Assert.Single(diagnostics, d => d.Message.Contains("original message", StringComparison.Ordinal));
        Assert.Equal(HtmlDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Offsite photos \u2014 terrace.eml", warning.Subject);
        Assert.Contains("PDF/A-2", warning.Message, StringComparison.Ordinal);
        Assert.Single(diagnostics, d => d.Message.StartsWith("The attachments are not embedded", StringComparison.Ordinal));
        AssertValid(pdf);
    }

    [Fact]
    public void A_web_archive_is_embedded_as_mhtml()
    {
        var message = Message("chrome-web-archive.mht");

        var original = Original(EmailToPdf.Convert(message, Options()));

        Assert.Equal("Example page.mht", original.FileName);
        Assert.Equal("multipart/related", original.MediaType);
        Assert.Equal("Original web archive", original.Description);
        Assert.Equal(message, original.Data.ToArray());
    }

    [Theory]
    [InlineData("Re: Q3/Q4 report?", false, "Re_ Q3_Q4 report_.eml")]
    [InlineData("a\\b*c\"d<e>f|g", false, "a_b_c_d_e_f_g.eml")]
    [InlineData("  ..Hidden.. ", false, "Hidden.eml")]
    [InlineData("one\t\r\n  two\u0001three", false, "one two three.eml")]
    [InlineData("Caf\u00E9 \u65E5\u672C", false, "Caf\u00E9 \u65E5\u672C.eml")]
    [InlineData("", false, "message.eml")]
    [InlineData(" ... ", false, "message.eml")]
    [InlineData("", true, "message.mht")]
    [InlineData("Page", true, "Page.mht")]
    public void The_file_name_comes_from_the_subject(string subject, bool webArchive, string expected) =>
        Assert.Equal(expected, EmailAttachment.OriginalFileName(subject, webArchive, []));

    [Fact]
    public void Long_subjects_are_cut_to_200_characters()
    {
        Assert.Equal(new string('x', 200) + ".eml", EmailAttachment.OriginalFileName(new string('x', 300), false, []));
        // Not between the halves of a surrogate pair, and without the space or dot the cut may leave at the end.
        Assert.Equal(new string('x', 199) + ".eml", EmailAttachment.OriginalFileName(new string('x', 199) + "\U0001F600 tail", false, []));
        Assert.Equal(new string('x', 198) + ".eml", EmailAttachment.OriginalFileName(new string('x', 198) + ". tail", false, []));
    }

    [Fact]
    public void Messages_without_a_subject_are_named_message_eml()
    {
        var message = "From: a@example.com\r\nTo: b@example.org\r\nContent-Type: text/plain\r\n\r\nNo subject here.\r\n"u8.ToArray();

        var original = Original(EmailToPdf.Convert(message, Options()));

        Assert.Equal("message.eml", original.FileName);
        Assert.Equal(message, original.Data.ToArray());
    }

    [Fact]
    public async Task Every_entry_point_embeds_the_bytes_it_was_given()
    {
        var message = Message("thunderbird-flowed.eml");
        var options = Options();
        var file = Path.Combine(Path.GetTempPath(), $"raicore-email-{Guid.NewGuid():N}.eml");
        await File.WriteAllBytesAsync(file, message, TestContext.Current.CancellationToken);
        try
        {
            using var stream = new MemoryStream(message);
            var documents = new[]
            {
                EmailToPdf.Convert(message, options),
                EmailToPdf.Convert(new MemoryStream(message), options),
                EmailToPdf.Convert(file, options),
                await EmailToPdf.ConvertAsync(message, options, TestContext.Current.CancellationToken),
                await EmailToPdf.ConvertAsync(new AsyncReadStream(message), options, TestContext.Current.CancellationToken),
            };
            foreach (var document in documents)
            {
                var original = Original(document);
                Assert.Equal("Re_ Minutes and agenda.eml", original.FileName);
                Assert.Equal(message, original.Data.ToArray());
            }

            foreach (var pdf in new[] { EmailToPdf.ConvertToBytes(message, options), EmailToPdf.ConvertToBytes(stream, options) })
            {
                Assert.Equal(message, Assert.Single(Saved(pdf), f => f.Relationship == "Source").Data);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void It_is_embedded_once_when_attachments_are_appended()
    {
        // The attached message is converted and appended, but only the outer message is the original.
        var message = Message("thunderbird-flowed.eml");
        var options = new EmailConverterOptions { EmbedOriginal = true, AppendAttachments = true };

        var document = EmailToPdf.Convert(message, options);

        Assert.Equal(message, Original(document).Data.ToArray());
        Assert.Equal(3, document.Attachments.Count); // The original, the text file and the attached message.
    }

    private static byte[] WithAttachmentNamed(string fileName) => Encoding.ASCII.GetBytes(
        $"From: a@example.com\r\nTo: b@example.org\r\nSubject: Attached\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n"
        + $"--b\r\nContent-Type: text/plain\r\n\r\nbody\r\n--b\r\nContent-Type: message/rfc822\r\nContent-Disposition: attachment; filename=\"{fileName}\"\r\n\r\nSubject: inner\r\n\r\ninner body\r\n--b--\r\n");

    [Theory]
    [InlineData("Attached.eml", "Attached (2).eml")]
    [InlineData("ATTACHED.EML", "Attached (2).eml")]
    [InlineData("Attached (2).eml", "Attached.eml")]
    public void It_does_not_take_the_name_of_an_embedded_attachment(string attachment, string expected)
    {
        var message = WithAttachmentNamed(attachment);

        var document = EmailToPdf.Convert(message, Options());

        Assert.Equal(expected, Original(document).FileName);
        Assert.Contains(document.Attachments, a => a.FileName == attachment && a.Relationship == PdfAttachmentRelationship.Unspecified);
        Assert.Equal(2, Saved(document.ToArray()).Count); // Unique names, so the document saves.
    }

    [Fact]
    public void Attachments_that_are_not_embedded_leave_their_names_free()
    {
        var options = new EmailConverterOptions { EmbedOriginal = true, EmbedAttachments = false };

        var document = EmailToPdf.Convert(WithAttachmentNamed("Attached.eml"), options);

        Assert.Equal("Attached.eml", Assert.Single(document.Attachments).FileName);
        Assert.Equal(PdfAttachmentRelationship.Source, document.Attachments[0].Relationship);
    }
}
