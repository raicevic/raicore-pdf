using System.Security.Cryptography;
using System.Text.Json;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Email.Cfb;
using RaiCore.Pdf.Email.Msg;
using RaiCore.Pdf.Extraction;

namespace RaiCore.Pdf.Tests.Email;

/// <summary>
/// Real-world compound files from Apache POI's test data (Fixtures/Poi, Apache License 2.0): an Outlook message, a
/// Word, an Excel and a PowerPoint file, read as olefile and extract-msg (independent readers) read them.
/// </summary>
public class PoiSampleTests
{
    public static TheoryData<string> Files => [
        "quick.msg", "simple_test_msg.msg", "HTMLBodyBinary_UTF-8.msg", "ASCII_CP1251_LCID1049.msg",
        "documentProperties.doc", "chinese-provinces.xls", "Single_Coloured_Page.ppt"];

    public static TheoryData<string> Messages => ["quick.msg", "simple_test_msg.msg", "HTMLBodyBinary_UTF-8.msg", "ASCII_CP1251_LCID1049.msg"];

    private static JsonElement Expected(string file) =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Poi/expected.json"))).RootElement.GetProperty(file);

    private static Dictionary<string, CfbEntry> Streams(CfbEntry storage, string prefix = "")
    {
        var streams = new Dictionary<string, CfbEntry>(StringComparer.Ordinal);
        foreach (var child in storage.Children)
        {
            if (child.Kind == CfbEntryKind.Stream)
            {
                streams[prefix + child.Name] = child;
            }
            else
            {
                foreach (var (path, entry) in Streams(child, prefix + child.Name + "/"))
                {
                    streams[path] = entry;
                }
            }
        }

        return streams;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_stream_of_the_word_excel_powerpoint_and_outlook_files_reads_as_olefile_reads_it(string file)
    {
        var cfb = CompoundFile.Open(Fixtures.Read("Poi/" + file));

        var streams = Streams(cfb.Root);
        var expected = Expected(file).GetProperty("streams").EnumerateObject().ToList();
        Assert.Equal(expected.Select(s => s.Name).Order(StringComparer.Ordinal), streams.Keys.Order(StringComparer.Ordinal));
        foreach (var stream in expected)
        {
            var data = cfb.Read(streams[stream.Name]);
            Assert.Equal(stream.Value.GetProperty("size").GetInt32(), data.Length);
            Assert.Equal(stream.Value.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(data)));
        }
    }

    [Theory]
    [InlineData("documentProperties.doc")]
    [InlineData("Single_Coloured_Page.ppt")]
    public void Summary_information_reads_as_olefile_reads_it(string file)
    {
        var cfb = CompoundFile.Open(Fixtures.Read("Poi/" + file));

        var summary = PropertySetStream.Summary(cfb.Read(cfb.Root["\u0005SummaryInformation"]!));

        Assert.Equal(Expected(file).GetProperty("title").GetString(), summary[PropertySetStream.Title]);
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Outlook_messages_read_and_convert(string file)
    {
        var expected = Expected(file).GetProperty("message");
        var data = Fixtures.Read("Poi/" + file);

        var message = MsgMessage.Read(data, ResourceLimits.Default);
        var text = new TextExtractor(PdfReader.Open(EmailToPdf.ConvertToBytes(data))).ExtractText();

        // extract-msg decodes the 8-bit strings of the code page 1251 message as Windows-1252; they are Russian.
        var subject = file == "ASCII_CP1251_LCID1049.msg"
            ? "Subject \u0430\u0432\u0442\u043E\u043C\u0430\u0442\u0438\u0447\u0435\u0441\u043A\u0438 Subject"
            : expected.GetProperty("subject").GetString()!.TrimEnd('\0');
        Assert.Equal(subject, message.Subject);
        Assert.Contains(subject, text, StringComparison.Ordinal);
        if (expected.GetProperty("sender").GetString() is { } sender)
        {
            Assert.Contains(message.FromName, sender, StringComparison.Ordinal);
        }

        var body = expected.GetProperty("body").GetString()!.TrimEnd('\0');
        if (body.Length > 0 && file != "ASCII_CP1251_LCID1049.msg")
        {
            Assert.Contains(body.Split('\n')[0], text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_html_body_stored_as_bytes_is_decoded_by_the_message_code_page()
    {
        var message = MsgMessage.Read(Fixtures.Read("Poi/HTMLBodyBinary_UTF-8.msg"), ResourceLimits.Default);

        Assert.Equal("Subject \u00F6\u00E4\u00FC Subject", message.Subject);
        Assert.Contains("\u00F6\u00E4\u00FC", message.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1031, null)] // German: Windows-1252.
    [InlineData(1049, 1251)] // Russian.
    [InlineData(1045, 1250)] // Polish.
    [InlineData(1032, 1253)] // Greek.
    [InlineData(1055, 1254)] // Turkish.
    [InlineData(1037, 1255)] // Hebrew.
    [InlineData(1025, 1256)] // Arabic.
    [InlineData(1062, 1257)] // Latvian.
    [InlineData(1066, 1258)] // Vietnamese.
    [InlineData(1054, 874)] // Thai.
    [InlineData(1041, 932)] // Japanese.
    [InlineData(1042, 949)] // Korean.
    [InlineData(2052, 936)] // Chinese (simplified).
    [InlineData(1028, 950)] // Chinese (Taiwan).
    [InlineData(3076, 950)] // Chinese (Hong Kong).
    public void The_locale_of_a_message_gives_the_code_page_of_its_8_bit_strings(int localeId, int? codePage)
    {
        Assert.Equal(codePage, MsgMessage.AnsiCodePage(localeId));
    }
}
