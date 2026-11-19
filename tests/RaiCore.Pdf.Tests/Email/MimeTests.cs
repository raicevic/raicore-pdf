using System.Text;
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Email.Mime;

namespace RaiCore.Pdf.Tests.Email;

/// <summary>The MIME layer: parsing parts, transfer encodings, charsets, encoded words and parameters.</summary>
public class MimeTests
{
    private static MimeEntity Parse(string message, ResourceLimits? limits = null) =>
        MimeParser.Parse(Encoding.Latin1.GetBytes(message.ReplaceLineEndings("\r\n")), limits ?? ResourceLimits.Default);

    private static string Content(MimeEntity part) => Encoding.Latin1.GetString(part.RawContent.Span);

    [Fact]
    public void A_multipart_is_split_into_its_parts_without_the_line_breaks_before_boundaries()
    {
        var message = Parse("""
            Content-Type: multipart/mixed; boundary="b1"
            Subject: test

            preamble
            --b1
            Content-Type: text/plain

            first
            --b1
            Content-Type: text/html

            <p>second</p>

            --b1--
            epilogue
            """);

        Assert.Equal("multipart/mixed", message.ContentType.MediaType);
        Assert.Equal(["first", "<p>second</p>\r\n"], message.Children.Select(Content));
        Assert.Equal(["text/plain", "text/html"], message.Children.Select(c => c.ContentType.MediaType));
        Assert.Equal("test", message.Header("subject"));
    }

    [Fact]
    public void Lf_line_endings_work_like_crlf()
    {
        var message = MimeParser.Parse("Content-Type: multipart/mixed; boundary=b\n\n--b\n\nlf only\n--b--\n"u8.ToArray(), ResourceLimits.Default);

        Assert.Equal("lf only", Content(Assert.Single(message.Children)));
    }

    [Fact]
    public void Nested_multiparts_end_at_their_own_boundaries()
    {
        var message = Parse("""
            Content-Type: multipart/mixed; boundary=outer

            --outer
            Content-Type: multipart/alternative; boundary=inner

            --inner

            plain
            --inner
            Content-Type: text/html

            html
            --inner--
            --outer
            Content-Type: application/pdf

            pdf
            --outer--
            """);

        Assert.Equal(2, message.Children.Count);
        Assert.Equal(["plain", "html"], message.Children[0].Children.Select(Content));
        Assert.Equal("pdf", Content(message.Children[1]));
    }

    [Fact]
    public void A_part_without_its_closing_boundary_ends_at_the_enclosing_one()
    {
        var message = Parse("""
            Content-Type: multipart/mixed; boundary=outer

            --outer
            Content-Type: multipart/alternative; boundary=inner

            --inner

            never closed
            --outer
            Content-Type: text/plain

            after
            """);

        Assert.Equal(2, message.Children.Count);
        Assert.Equal("never closed", Content(Assert.Single(message.Children[0].Children)));
        Assert.Equal("after", Content(message.Children[1]));
    }

    [Theory]
    [InlineData("--b  ", true)]
    [InlineData("--b\t", true)]
    [InlineData("--b--  trailing", true)]
    [InlineData("--b--", true)]
    [InlineData("--bx", false)]
    [InlineData("--b x", false)]
    [InlineData("-b", false)]
    public void Boundary_lines_may_have_trailing_whitespace_but_nothing_else(string line, bool isBoundary)
    {
        var message = Parse($"Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\n\r\none\r\n{line}\r\n\r\ntwo\r\n--b--\r\n");

        Assert.Equal(isBoundary ? "one" : $"one\r\n{line}\r\n\r\ntwo", Content(message.Children[0]));
    }

    [Fact]
    public void Folded_header_fields_are_unfolded_and_mbox_from_lines_skipped()
    {
        var message = Parse("""
            From sender@example.com Thu Nov 19 10:00:00 2026
            Subject: a long
             subject
            	continued
            X-Empty:

            body
            """);

        Assert.Equal("a long subject\tcontinued", message.Header("Subject"));
        Assert.Equal(string.Empty, message.Header("X-Empty"));
        Assert.Null(message.Header("From"));
        Assert.Equal("body", Content(message));
    }

    [Fact]
    public void A_line_that_is_no_header_field_starts_the_content()
    {
        var message = Parse("Subject: x\r\nthis is content\r\nmore\r\n");

        Assert.Equal("this is content\r\nmore\r\n", Content(message));
        Assert.Equal("text/plain", message.ContentType.MediaType);
    }

    [Fact]
    public void Parts_in_a_digest_are_messages_by_default()
    {
        var message = Parse("""
            Content-Type: multipart/digest; boundary=d

            --d

            Subject: inner

            hello
            --d--
            """);

        var part = Assert.Single(message.Children);
        Assert.Equal("message/rfc822", part.ContentType.MediaType);
        Assert.Equal("inner", part.Message!.Header("Subject"));
    }

    [Fact]
    public void Attached_messages_are_parsed_from_their_decoded_content()
    {
        var inner = Convert.ToBase64String("Subject: inner\r\nContent-Type: text/html\r\n\r\n<b>x</b>"u8);
        var message = Parse($"""
            Content-Type: multipart/mixed; boundary=b

            --b
            Content-Type: message/rfc822
            Content-Transfer-Encoding: base64

            {inner}
            --b--
            """);

        var nested = message.Children[0].Message!;
        Assert.Equal("inner", nested.Header("Subject"));
        Assert.Equal("<b>x</b>", nested.Text);
    }

    [Fact]
    public void Attached_messages_deeper_than_the_message_depth_are_kept_as_they_are()
    {
        var message = "Subject: innermost\r\n\r\ntext";
        for (var i = 0; i < MimeParser.MaxMessageDepth + 1; i++)
        {
            message = $"Content-Type: message/rfc822\r\n\r\n{message}";
        }

        var part = Parse(message);
        var depth = 0;
        while (part.Message is { } nested)
        {
            part = nested;
            depth++;
        }

        Assert.Equal(MimeParser.MaxMessageDepth, depth);
        Assert.Equal("message/rfc822", part.ContentType.MediaType);
    }

    [Fact]
    public void Nesting_deeper_than_the_limit_throws()
    {
        string Nested(int depth)
        {
            var text = new StringBuilder();
            for (var i = 0; i < depth; i++)
            {
                text.Append($"Content-Type: multipart/mixed; boundary=b{i}\r\n\r\n--b{i}\r\n");
            }

            return text.Append("\r\nleaf").ToString();
        }

        var limits = ResourceLimits.Default with { MaxNestingDepth = 5 };
        Assert.NotNull(Parse(Nested(5), limits));
        var error = Assert.Throws<ResourceLimitExceededException>(() => Parse(Nested(6), limits));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), error.LimitName);

        // However large the limit, nesting stops at MaxDepth: parsing recurses on the caller's stack.
        var deep = Assert.Throws<ResourceLimitExceededException>(() => Parse(Nested(MimeParser.MaxDepth + 1), ResourceLimits.Default with { MaxNestingDepth = 100_000 }));
        Assert.Equal(MimeParser.MaxDepth, deep.Limit);
    }

    [Fact]
    public void More_parts_than_the_limit_throw()
    {
        var parts = string.Concat(Enumerable.Repeat("--b\r\n\r\nx\r\n", 9));
        var message = $"Content-Type: multipart/mixed; boundary=b\r\n\r\n{parts}--b--\r\n";

        Assert.Equal(9, Parse(message, ResourceLimits.Default with { MaxElements = 10 }).Children.Count);
        var error = Assert.Throws<ResourceLimitExceededException>(() => Parse(message, ResourceLimits.Default with { MaxElements = 9 }));
        Assert.Equal(nameof(ResourceLimits.MaxElements), error.LimitName);
    }

    [Fact]
    public void Header_fields_after_the_first_thousand_are_dropped()
    {
        var headers = string.Concat(Enumerable.Range(0, 1005).Select(i => $"X-{i}: {i}\r\n"));
        var message = Parse(headers + "Subject: late\r\n\r\nbody");

        Assert.Equal(1000, message.Headers.Count);
        Assert.Null(message.Header("Subject"));
        Assert.Equal("body", Content(message));
    }

    [Theory]
    [InlineData("SGVsbG8sIHdvcmxkIQ==", "Hello, world!")]
    [InlineData("SGVs\r\nbG8s IHdv*cmxk\r\nIQ", "Hello, world!")]
    [InlineData("SGk=SGk=", "HiHi")]
    [InlineData("-_8", "\u00FB\u00FF")]
    [InlineData("", "")]
    [InlineData("Q", "")]
    public void Base64_is_decoded_leniently(string encoded, string expected)
    {
        Assert.Equal(expected, Encoding.Latin1.GetString(TransferDecoding.Decode(Encoding.ASCII.GetBytes(encoded), "base64")));
    }

    [Theory]
    [InlineData("caf=C3=A9", "caf\u00C3\u00A9")]
    [InlineData("caf=c3=a9", "caf\u00C3\u00A9")]
    [InlineData("soft=\r\nbreak", "softbreak")]
    [InlineData("soft=\nbreak", "softbreak")]
    [InlineData("soft=  \r\nbreak", "softbreak")]
    [InlineData("soft=\t\r\nbreak", "softbreak")]
    [InlineData("end=", "end")]
    [InlineData("broken =ZZ and =4", "broken =ZZ and =4")]
    [InlineData("a= b", "a= b")]
    public void Quoted_printable_is_decoded_leniently(string encoded, string expected)
    {
        Assert.Equal(expected, Encoding.Latin1.GetString(TransferDecoding.Decode(Encoding.ASCII.GetBytes(encoded), "quoted-printable")));
    }

    [Theory]
    [InlineData("7bit")]
    [InlineData("8bit")]
    [InlineData("binary")]
    [InlineData("x-uuencode")]
    [InlineData("")]
    public void Other_transfer_encodings_pass_through(string encoding)
    {
        Assert.Equal("=41 a+b", Encoding.Latin1.GetString(TransferDecoding.Decode("=41 a+b"u8, encoding)));
    }

    [Fact]
    public void The_transfer_encoding_header_is_case_insensitive()
    {
        var part = Parse("Content-Transfer-Encoding:  Base64 \r\n\r\naGk=");

        Assert.Equal("hi", part.Text);
    }

    [Theory]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x80 }, "iso-8859-1", "caf\u00E9 \u20AC")] // Windows-1252, as browsers read ISO-8859-1.
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, "windows-1252", "caf\u00E9")]
    [InlineData(new byte[] { 0xA4 }, "iso-8859-15", "\u20AC")]
    [InlineData(new byte[] { 0xC3, 0xA9 }, "UTF-8", "\u00E9")]
    [InlineData(new byte[] { 0xC3, 0xA9 }, "\"utf8\"", "\u00E9")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, "windows-1252", "a")] // A byte order mark wins.
    [InlineData(new byte[] { 0xC3, 0xA9 }, null, "\u00E9")] // No charset: UTF-8 if valid...
    [InlineData(new byte[] { 0xE9 }, null, "\u00E9")] // ... else Windows-1252.
    [InlineData(new byte[] { 0xC3, 0xA9 }, "us-ascii", "\u00E9")] // Mislabeled ASCII.
    [InlineData(new byte[] { 0xE9 }, "x-bogus", "\u00E9")]
    [InlineData(new byte[] { 0x82, 0xA0 }, "Shift_JIS", "\u3042")]
    [InlineData(new byte[] { 0x82, 0xA0 }, "x-sjis", "\u3042")]
    [InlineData(new byte[] { 0xA4, 0xA2 }, "euc-jp", "\u3042")]
    [InlineData(new byte[] { 0x1B, 0x24, 0x42, 0x24, 0x22, 0x1B, 0x28, 0x42 }, "ISO-2022-JP", "\u3042")]
    [InlineData(new byte[] { 0xC4, 0xE3 }, "gb2312", "\u4F60")]
    [InlineData(new byte[] { 0x81, 0x30, 0x81, 0x30 }, "gbk", "\u0080")] // GB18030 four-byte sequences, for mislabeled text.
    [InlineData(new byte[] { 0xA7, 0x41 }, "big5", "\u4F60")]
    [InlineData(new byte[] { 0xB0, 0xA1 }, "euc-kr", "\uAC00")]
    [InlineData(new byte[] { 0x81, 0x41 }, "euc-kr", "\uAC02")] // Windows-949 (Unified Hangul Code), which mailers label EUC-KR.
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "utf-16", "a")] // The byte order mark of UTF-16 goes too.
    [InlineData(new byte[] { 0xC1 }, "koi8-r", "\u0430")]
    [InlineData(new byte[] { 0x2B, 0x41, 0x4F, 0x6B, 0x2D }, "utf-7", "\u00E9")]
    public void Text_is_decoded_with_its_charset(byte[] data, string? charset, string expected)
    {
        Assert.Equal(expected, Charsets.Decode(data, charset, html: false));
    }

    [Theory]
    [InlineData("<html><head><meta charset=\"windows-1252\"></head><body>\u00E9</body></html>", "<html><head><meta charset=\"windows-1252\"></head><body>\u00E9</body></html>")]
    [InlineData("<meta http-equiv=Content-Type content=\"text/html; charset=iso-8859-1\">\u00E9", "<meta http-equiv=Content-Type content=\"text/html; charset=iso-8859-1\">\u00E9")]
    public void Html_without_a_charset_uses_its_meta_charset(string latin1, string expected)
    {
        // Valid UTF-8 that the document says is Windows-1252: the meta charset wins over guessing UTF-8.
        Assert.Equal("<meta charset=windows-1252>\u00C3\u00A9", Charsets.Decode("<meta charset=windows-1252>\u00E9"u8, null, html: true));

        Assert.Equal(expected, Charsets.Decode(Encoding.Latin1.GetBytes(latin1), null, html: true));
        Assert.NotEqual(expected, Charsets.Decode(Encoding.Latin1.GetBytes(latin1), "utf-8", html: true)); // A MIME charset wins.
    }

    [Theory]
    [InlineData("=?UTF-8?B?Y2Fmw6k=?=", "caf\u00E9")]
    [InlineData("=?utf-8?q?caf=C3=A9_au_lait?=", "caf\u00E9 au lait")]
    [InlineData("=?ISO-8859-1?Q?J=F8rgen?= <j@example.org>", "J\u00F8rgen <j@example.org>")]
    [InlineData("=?utf-8?Q?a?= =?utf-8?Q?b?=", "ab")] // Whitespace between encoded words goes.
    [InlineData("=?utf-8?Q?a?=\r\n =?utf-8?Q?b?=", "ab")]
    [InlineData("=?utf-8?Q?a?= x =?utf-8?Q?b?=", "a x b")]
    [InlineData("=?utf-8?B?w6k=?= =?iso-8859-1?Q?=E9?=", "\u00E9\u00E9")]
    [InlineData("=?utf-8?B?4oKs?=", "\u20AC")]
    [InlineData("=?utf-8?B?4oI=?= =?utf-8?B?rA==?=", "\u20AC")] // A character split across two words.
    [InlineData("=?utf-8*en?Q?hi?=", "hi")] // RFC 2231 language.
    [InlineData("=?x-bogus?Q?caf=E9?=", "caf\u00E9")]
    [InlineData("=?utf-8?X?abc?=", "=?utf-8?X?abc?=")]
    [InlineData("=?utf-8?Q?unterminated", "=?utf-8?Q?unterminated")]
    [InlineData("plain text", "plain text")]
    [InlineData("pre=?utf-8?Q?x?=post", "prexpost")]
    public void Encoded_words_are_decoded(string raw, string expected)
    {
        Assert.Equal(expected, MimeHeaders.Decode(raw));
    }

    [Fact]
    public void Raw_8_bit_header_values_are_utf8_or_windows_1252()
    {
        Assert.Equal("caf\u00E9", MimeHeaders.Decode(Encoding.Latin1.GetString("caf\u00E9"u8)));
        Assert.Equal("\u201Cq\u201D", MimeHeaders.Decode("\u0093q\u0094"));
        Assert.Equal("\u00E9 \u00E9", MimeHeaders.Decode(Encoding.Latin1.GetString("\u00E9 =?utf-8?Q?=C3=A9?="u8)));
    }

    [Theory]
    [InlineData("\"Example, Alice\" <alice@example.com>, bob@example.org", "Example, Alice <alice@example.com>, bob@example.org")]
    [InlineData("=?utf-8?Q?Example=2C_Alice?= <alice@example.com>", "Example, Alice <alice@example.com>")]
    [InlineData("alice@example.com (Alice Example)", "Alice Example <alice@example.com>")]
    [InlineData("\"Quote \\\" inside\" <q@example.com>", "Quote \" inside <q@example.com>")]
    [InlineData("<only@example.com>", "only@example.com")]
    [InlineData("\"same@example.com\" <same@example.com>", "same@example.com")]
    [InlineData("team: a@example.com, b@example.com;", "a@example.com, b@example.com")]
    [InlineData("undisclosed-recipients:;", "")]
    [InlineData("a@example.com,, ,b@example.com", "a@example.com, b@example.com")]
    [InlineData("Broken <unterminated@example.com", "Broken <unterminated@example.com>")]
    [InlineData("Name (comment (nested)) <n@example.com>", "Name (comment (nested)) <n@example.com>")]
    [InlineData("odd@example.com:port", "odd@example.com:port")] // A colon after an address is no group.
    public void Address_lists_are_formatted_for_display(string raw, string expected)
    {
        Assert.Equal(expected, MimeHeaders.FormatAddresses(raw));
    }

    [Theory]
    [InlineData("\"Example, Alice\" <alice@example.com>", "Example, Alice")]
    [InlineData("alice@example.com", "alice@example.com")]
    [InlineData(", second@example.com", "second@example.com")]
    [InlineData("", "")]
    public void The_first_name_of_an_address_list_is_its_display_name_or_address(string raw, string expected)
    {
        Assert.Equal(expected, MimeHeaders.FirstName(raw));
    }

    [Theory]
    [InlineData("text/HTML; charset=\"UTF-8\"", "text/html", "charset", "UTF-8")]
    [InlineData("text/plain (a comment); format=flowed", "text/plain", "format", "flowed")]
    [InlineData("multipart/mixed;\tboundary=\"----=_Part_1\"", "multipart/mixed", "boundary", "----=_Part_1")]
    [InlineData("application/pdf; name=\"=?utf-8?B?w6YucGRm?=\"", "application/pdf", "name", "\u00E6.pdf")]
    [InlineData("application/pdf; name*=utf-8''%C3%A6.pdf", "application/pdf", "name", "\u00E6.pdf")]
    [InlineData("application/pdf; name*=iso-8859-1'da'%E6.pdf", "application/pdf", "name", "\u00E6.pdf")]
    [InlineData("application/pdf; name*1=b.pdf; name*0*=utf-8''%C3%A6", "application/pdf", "name", "\u00E6b.pdf")]
    [InlineData("application/pdf; name=plain.pdf; name*=utf-8''better.pdf", "application/pdf", "name", "better.pdf")]
    [InlineData("application/pdf; name*0=\"a \"; name*0=\"x\"; name*1=\"b\"", "application/pdf", "name", "a b")]
    [InlineData("application/octet-stream; name=report(1).pdf", "application/octet-stream", "name", "report(1).pdf")]
    [InlineData("text/plain; name=\"a\\\"b\"", "text/plain", "name", "a\"b")]
    [InlineData("text/plain; name=\"unterminated", "text/plain", "name", "unterminated")]
    [InlineData("text/plain; NAME = spaced ", "text/plain", "name", "spaced")]
    [InlineData("text/plain; x*=%ZZ", "text/plain", "x", "%ZZ")]
    [InlineData("text; charset=utf-8", "text/plain", "charset", "utf-8")]
    [InlineData("garbage", "text/plain", "charset", null)]
    [InlineData("/html", "text/plain", "charset", null)]
    [InlineData("a/b/c", "text/plain", "charset", null)]
    [InlineData("text/plain; ; =x; charset", "text/plain", "charset", null)]
    [InlineData("text/plain; x*a=1", "text/plain", "x", null)]
    [InlineData("text/plain; *=1", "text/plain", "x", null)]
    [InlineData("text/plain; x*99999=1", "text/plain", "x", null)]
    public void Content_types_are_parsed_with_their_parameters(string value, string mediaType, string parameter, string? expected)
    {
        var type = ContentType.Parse(value);

        Assert.Equal(mediaType, type.MediaType);
        Assert.Equal(expected, type.Parameter(parameter));
    }

    [Fact]
    public void Dispositions_and_file_names_are_read_from_their_headers()
    {
        var part = Parse("""
            Content-Type: application/pdf; name="type-name.pdf"
            Content-Disposition: ATTACHMENT; filename*0*=utf-8''%C3%A6; filename*1="rest.pdf"
            Content-ID: < id@example.com >
            Content-Location: https://example.com/a
             /b.pdf

            data
            """);

        Assert.True(part.IsAttachment);
        Assert.False(part.IsInline);
        Assert.Equal("\u00E6rest.pdf", part.FileName);
        Assert.Equal("id@example.com", part.ContentId);
        Assert.Equal("https://example.com/a/b.pdf", part.ContentLocation);

        var noDisposition = Parse("Content-Type: image/png; name=\"type-name.png\"\r\n\r\nx");
        Assert.Equal("type-name.png", noDisposition.FileName);
        Assert.Null(noDisposition.Disposition);
        Assert.Null(Parse("Content-Type: image/png\r\n\r\nx").FileName);
    }
}
