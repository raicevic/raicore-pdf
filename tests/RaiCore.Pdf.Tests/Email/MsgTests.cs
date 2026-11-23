using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Email.Msg;
using RaiCore.Pdf.Extraction;

namespace RaiCore.Pdf.Tests.Email;

/// <summary>
/// Outlook messages made by Fixtures/Msg/make-msg.py: read as extract-msg (an independent reader) reads them
/// (expected.json), and converted like the equivalent .eml files written by Python's email package.
/// </summary>
public class MsgTests
{
    public static TheoryData<string> Messages => ["html", "rtf", "text", "embedded"];

    private static JsonElement Expected(string name) =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Msg/expected.json"))).RootElement.GetProperty(name);

    private static string? Optional(JsonElement element, string property) =>
        element.GetProperty(property).ValueKind == JsonValueKind.Null ? null : element.GetProperty(property).GetString();

    private static string Mailbox(string name, string address) => address.Length > 0 ? $"{name} <{address}>" : name;

    [Theory]
    [MemberData(nameof(Messages))]
    public void Messages_read_as_extract_msg_reads_them(string name)
    {
        var expected = Expected(name);

        var message = MsgMessage.Read(Fixtures.Read($"Msg/{name}.msg"), ResourceLimits.Default);

        Assert.Equal(expected.GetProperty("subject").GetString(), message.Subject);
        Assert.Equal(expected.GetProperty("sender").GetString(), Mailbox(message.FromName, message.FromAddress));
        Assert.Equal(Optional(expected, "to"), string.Join("; ", message.Recipients.Where(r => r.Type == 1).Select(r => Mailbox(r.Name, r.Address))));
        Assert.Equal(Optional(expected, "cc") ?? string.Empty, string.Join("; ", message.Recipients.Where(r => r.Type == 2).Select(r => Mailbox(r.Name, r.Address))));
        Assert.Equal(new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc), message.Date);
        Assert.Equal(expected.GetProperty("body").GetString(), (message.TextBody ?? string.Empty).ReplaceLineEndings("\n").Trim());
        if (name != "text")
        {
            // extract-msg makes HTML from a plain text body; the reader leaves that to the converter.
            Assert.Equal(Optional(expected, "html"), message.HtmlBody);
        }

        var attachments = message.Attachments.Select(a => a.Message is { } inner ? inner.Subject + ".msg" : a.FileName).Order(StringComparer.Ordinal);
        Assert.Equal(expected.GetProperty("attachments").EnumerateArray().Select(a => a.GetString()!), attachments);
    }

    [Fact]
    public void Attachments_keep_their_data_type_content_id_and_attached_messages()
    {
        var html = MsgMessage.Read(Fixtures.Read("Msg/html.msg"), ResourceLimits.Default);
        var chart = html.Attachments.Single(a => a.FileName == "chart.png");
        Assert.Equal(("image/png", "chart@example", true), (chart.MimeType, chart.ContentId, chart.IsHidden));
        Assert.True(chart.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]));
        Assert.Equal("Draft notes.\r\n"u8.ToArray(), html.Attachments.Single(a => a.FileName.StartsWith("notes", StringComparison.Ordinal)).Data);

        var embedded = MsgMessage.Read(Fixtures.Read("Msg/embedded.msg"), ResourceLimits.Default);
        var inner = Assert.Single(embedded.Attachments).Message!;
        Assert.Equal(("Inner message", "Emmy Noether", "emmy@example.de"), (inner.Subject, inner.FromName, inner.FromAddress));
        Assert.Equal("The inner message body.", inner.TextBody);
        Assert.Equal("Inside the inner message.\r\n"u8.ToArray(), Assert.Single(inner.Attachments).Data);
    }

    private static string PdfText(byte[] message, EmailConverterOptions? options = null) =>
        string.Join("\f", new TextExtractor(PdfReader.Open(EmailToPdf.ConvertToBytes(message, options ?? new EmailConverterOptions { AppendAttachments = true })))
            .ExtractPages().Select(p => string.Join(' ', p.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));

    [Theory]
    [MemberData(nameof(Messages))]
    public void Messages_convert_like_their_eml_equivalents(string name)
    {
        var fromMsg = PdfText(Fixtures.Read($"Msg/{name}.msg"));
        var fromEml = PdfText(Fixtures.Read($"Msg/{name}.eml"));

        // An attached message is written anew from the .msg, so its size differs from Python's; the rest is the same.
        static string WithoutSizes(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\(\d[\d.,]* (bytes|KB|MB)\)", "(size)");
        Assert.Equal(WithoutSizes(fromEml), WithoutSizes(fromMsg));
        Assert.Contains(Expected(name).GetProperty("subject").GetString()!, fromMsg, StringComparison.Ordinal);
    }

    [Fact]
    public void An_html_message_shows_its_inline_image_and_lists_its_other_attachment()
    {
        var document = EmailToPdf.Convert(Fixtures.Read("Msg/html.msg"));

        var reader = PdfReader.Open(document.ToArray());
        var text = new TextExtractor(reader).ExtractText();
        Assert.Contains("Quarterly report \u2013 Q3 \u20AC", text, StringComparison.Ordinal);
        Assert.Contains("Charles Babbage", text, StringComparison.Ordinal);
        Assert.Contains("Mary Somerville", text, StringComparison.Ordinal);
        Assert.Contains("notes \u2013 draft.txt", text, StringComparison.Ordinal);
        Assert.Single(new ImageExtractor(reader).ExtractPage(0).Images); // The chart, in place.
        var attached = document.Attachments.Select(a => a.FileName).ToList();
        Assert.Contains("notes \u2013 draft.txt", attached);
        Assert.DoesNotContain("chart.png", attached); // Shown in the body, hidden in Outlook's list.
    }

    [Fact]
    public void The_original_msg_is_embedded_as_an_outlook_message()
    {
        var data = Fixtures.Read("Msg/text.msg");

        var attachment = Assert.Single(EmailToPdf.Convert(data, new EmailConverterOptions { EmbedOriginal = true }).Attachments);

        Assert.Equal(("Caf\u00E9 menu.msg", "application/vnd.ms-outlook"), (attachment.FileName, attachment.MediaType));
        Assert.Equal(data, attachment.Data.ToArray());
    }

    // --- RTF ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compressed_rtf_decompresses_and_its_html_is_taken_out()
    {
        var message = MsgMessage.Read(Fixtures.Read("Msg/rtf.msg"), ResourceLimits.Default);

        Assert.Equal("<html><body><p>Hello from <b>RTF</b> &amp; HTML.</p><p>Second paragraph.</p></body></html>", message.HtmlBody);
    }

    [Fact]
    public void Rtf_that_is_not_encapsulated_html_gives_its_text()
    {
        const string rtf = "{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0\\green0\\blue0;}{\\*\\generator Writer;}"
            + "\\pard Caf\\'e9 \\b bold\\b0\\par Second\\tab line \\u8364?{\\*\\ignored hidden} end.\\par}";

        Assert.Null(RtfBody.Html(rtf));
        Assert.Equal("Caf\u00E9 bold\nSecond\tline \u20AC end.\n", RtfBody.Text(rtf));
    }

    [Fact]
    public void Uncompressed_rtf_is_read_as_it_is()
    {
        var rtf = "{\\rtf1 plain}"u8.ToArray();
        var data = new byte[16 + rtf.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0), (uint)(rtf.Length + 12));
        BitConverter.TryWriteBytes(data.AsSpan(4), (uint)rtf.Length);
        BitConverter.TryWriteBytes(data.AsSpan(8), 0x414C454Du); // MELA
        rtf.CopyTo(data, 16);

        Assert.Equal(rtf, CompressedRtf.Decompress(data, 1000));
        BitConverter.TryWriteBytes(data.AsSpan(8), 0x12345678u);
        Assert.Throws<PdfFormatException>(() => CompressedRtf.Decompress(data, 1000));
        Assert.Throws<PdfFormatException>(() => CompressedRtf.Decompress(data.AsSpan(0, 10), 1000));

        // Found by fuzzing: a size that ends within the header.
        BitConverter.TryWriteBytes(data.AsSpan(0), 3u);
        BitConverter.TryWriteBytes(data.AsSpan(8), 0x414C454Du);
        Assert.Empty(CompressedRtf.Decompress(data, 1000));
    }

    [Fact]
    public void Compressed_rtf_that_would_grow_past_the_limit_throws()
    {
        var file = RaiCore.Pdf.Cfb.CompoundFile.Open(Fixtures.Read("Msg/rtf.msg"));
        var compressed = file.Read(file.Root["__substg1.0_10090102"]!);
        var rawSize = (int)BitConverter.ToUInt32(compressed, 4);
        Assert.Equal(rawSize, CompressedRtf.Decompress(compressed, rawSize).Length);

        // The header says it is larger than the limit; or it says it is small, but the data goes on.
        Assert.Throws<ResourceLimitExceededException>(() => CompressedRtf.Decompress(compressed, rawSize - 1));
        BitConverter.TryWriteBytes(compressed.AsSpan(4), 10u);
        Assert.Throws<ResourceLimitExceededException>(() => CompressedRtf.Decompress(compressed, 50));
    }

    // --- Damage ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_compound_file_that_is_not_a_message_throws()
    {
        Assert.Throws<PdfFormatException>(() => MsgMessage.Read(Fixtures.Read("Cfb/v3-basic.cfb"), ResourceLimits.Default));
        Assert.Throws<PdfFormatException>(() => EmailToPdf.Convert(Fixtures.Read("Cfb/v3-basic.cfb")));
    }

    [Fact]
    public void A_message_sent_on_behalf_of_someone_is_from_them()
    {
        var message = MsgMessage.Read(Fixtures.Read("Msg/delegate.msg"), ResourceLimits.Default);

        Assert.Equal(("Ada Lovelace", "ada@example.com"), (message.FromName, message.FromAddress));
    }

    [Fact]
    public void Attached_messages_nest_only_as_deep_as_the_limit_allows()
    {
        Assert.Throws<ResourceLimitExceededException>(() =>
            MsgMessage.Read(Fixtures.Read("Msg/embedded.msg"), ResourceLimits.Default with { MaxNestingDepth = 0 }));
        Assert.Throws<ResourceLimitExceededException>(() =>
            MsgMessage.Read(Fixtures.Read("Msg/html.msg"), ResourceLimits.Default with { MaxElements = 3 }));
    }
}
