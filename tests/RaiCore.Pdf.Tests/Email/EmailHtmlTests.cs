using System.Text;
using RaiCore.Pdf.Email;

namespace RaiCore.Pdf.Tests.Email;

/// <summary>Turning bodies into the HTML the converter lays out: plain text, HTML documents, their style sheets.</summary>
public class EmailHtmlTests
{
    private static string Linked(string text)
    {
        var html = new StringBuilder();
        PlainText.AppendLinked(html, text);
        return html.ToString();
    }

    [Theory]
    [InlineData("see https://example.com/a?b=1&c=2.", "see <a href=\"https://example.com/a?b=1&amp;c=2\">https://example.com/a?b=1&amp;c=2</a>.")]
    [InlineData("(http://example.com/x_(y))", "(<a href=\"http://example.com/x_(y)\">http://example.com/x_(y)</a>)")]
    [InlineData("[ftp://example.com/f]", "[<a href=\"ftp://example.com/f\">ftp://example.com/f</a>]")]
    [InlineData("go to www.example.org, now", "go to <a href=\"http://www.example.org\">www.example.org</a>, now")]
    [InlineData("mail mailto:a@example.com!", "mail <a href=\"mailto:a@example.com\">mailto:a@example.com</a>!")]
    [InlineData("<https://example.com/>", "&lt;<a href=\"https://example.com/\">https://example.com/</a>&gt;")]
    [InlineData("HTTPS://EXAMPLE.COM", "<a href=\"HTTPS://EXAMPLE.COM\">HTTPS://EXAMPLE.COM</a>")]
    [InlineData("say \"https://example.com\"", "say &quot;<a href=\"https://example.com\">https://example.com</a>&quot;")]
    [InlineData("awww.example.com and http:// alone", "awww.example.com and http:// alone")]
    [InlineData("a < b & c > \0d", "a &lt; b &amp; c &gt; d")]
    [InlineData("https://example.com/')*;:!?", "<a href=\"https://example.com/\">https://example.com/</a>')*;:!?")]
    public void Plain_text_is_escaped_with_its_urls_as_links(string text, string expected)
    {
        Assert.Equal(expected, Linked(text));
    }

    [Fact]
    public void Trimming_a_url_takes_linear_time()
    {
        var url = "http://x" + new string(')', 200_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var html = Linked(url);

        Assert.StartsWith("<a href=\"http://x\">http://x</a>)", html, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }

    [Theory]
    [InlineData("one \ntwo\nthree", false, "one two\nthree")]
    [InlineData("one \ntwo", true, "onetwo")]
    [InlineData("> quoted \n> more\n>> deeper\nplain", false, "> quoted more\n>> deeper\nplain")]
    [InlineData("> soft \nnot quoted", false, "> soft \nnot quoted")]
    [InlineData(" >stuffed \nline", false, ">stuffed line")]
    [InlineData("text\n-- \nsignature", false, "text\n-- \nsignature")]
    [InlineData("last soft ", false, "last soft ")]
    [InlineData("", false, "")]
    [InlineData("a\n\nb\n", false, "a\n\nb\n")]
    public void Flowed_text_is_joined_at_its_soft_line_breaks(string text, bool deleteSpace, string expected)
    {
        Assert.Equal(expected, PlainText.Unflow(text, deleteSpace));
    }

    [Fact]
    public void Plain_text_keeps_its_line_breaks_in_a_pre_wrap_element()
    {
        var html = new StringBuilder();
        PlainText.AppendHtml(html, "a\r\nb \r\nc", flowed: true, deleteSpace: false, "cls");

        Assert.Equal("<div class=\"cls\">a\nb c</div>\n", html.ToString());
        Assert.Contains(".raicore-email-text { white-space: pre-wrap;", EmailHtml.StyleSheet, StringComparison.Ordinal);
    }

    [Fact]
    public void An_html_document_is_taken_apart_into_styles_body_and_language()
    {
        var parts = HtmlParts.Split("""
            <!DOCTYPE html><html lang="da" xml:lang="da"><head><title>t</title>
            <!-- <style>p { hidden: 1 }</style> -->
            <style type="text/css"><!-- p { color: red } --></style>
            <link rel="stylesheet" href="a.css"><meta name="x" content="y">
            </head><body bgcolor="#eeeeee" text=red style='margin: 0' lang=en dir="rtl" onload="x()"><p>Hi</p></body></html>
            """);

        Assert.Equal("<style type=\"text/css\"> p { color: red } </style>\n<link rel=\"stylesheet\" href=\"a.css\">\n", parts.Head);
        Assert.Equal("<p>Hi</p>", parts.Body);
        Assert.Equal(" style=\"background-color: #eeeeee; color: red; margin: 0\" lang=\"en\" dir=\"rtl\"", parts.BodyAttributes);
        Assert.Equal("da", parts.Language);
    }

    [Theory]
    [InlineData("<p>fragment</p>", "", "<p>fragment</p>")]
    [InlineData("<html><head><style>a{}</style></head><p>no body</p></html>", "<style>a{}</style>\n", "<p>no body</p>")]
    [InlineData("<body><p>a</p></body><p>after</p></body>", "", "<p>a</p></body><p>after</p>")]
    [InlineData("<BODY class=\"x\">upper</BODY >", "", "upper")]
    [InlineData("<bodyx>not a body</bodyx>", "", "<bodyx>not a body</bodyx>")]
    [InlineData("<style>unclosed<body>x", "<style>unclosed</style>\n", "x")]
    [InlineData("<style>unclosed", "", "<style>unclosed")]
    [InlineData("<body>open <!-- comment", "", "open <!-- comment-->")]
    [InlineData("<!-- <body> --><body>real</body>", "", "real")]
    [InlineData("<!-- never closed <body>x", "", "<!-- never closed <body>x-->")]
    [InlineData("<body title='a>b'>quoted</body>", "", "quoted")]
    [InlineData("<link href=x><body>b", "<link href=x>\n", "b")]
    public void Html_without_the_usual_structure_still_comes_apart(string html, string head, string body)
    {
        var parts = HtmlParts.Split(html);

        Assert.Equal(head, parts.Head);
        Assert.Equal(body, parts.Body);
    }

    [Theory]
    [InlineData("p { a: 1 } @page { size: Letter; margin: 1in } q { b: 2 }", "p { a: 1 }  q { b: 2 }")]
    [InlineData("@page WordSection1 {size:612pt 792pt;} div.WordSection1 {page:WordSection1;}", " div.WordSection1 {page:WordSection1;}")]
    [InlineData("@PAGE :first { @top-center { content: '}' } } x {}", " x {}")]
    [InlineData("@page { /* } */ margin: 0 } y {}", " y {}")]
    [InlineData("@page;z {}", "z {}")]
    [InlineData("@page { never closed", "")]
    [InlineData("@pages {} @page-x {}", "@pages {} @page-x {}")]
    [InlineData("a { content: \"@page {}\" } /* @page {} */", "a { content: \"@page {}\" } /* @page {} */")]
    [InlineData("<!--\np { c: 1 }\n-->", "\np { c: 1 }\n")]
    [InlineData("a { content: 'unclosed", "a { content: 'unclosed")]
    public void Style_sheets_lose_their_page_rules_and_comment_markers(string css, string expected)
    {
        Assert.Equal(expected, HtmlParts.CleanStyleSheet(css));
    }

    [Theory]
    [InlineData("C:\\fakepath\\report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("  ..hidden.  ", "hidden")]
    [InlineData("a\u0000b\u001Fc\u007F.txt", "abc.txt")]
    [InlineData("/", "")]
    [InlineData(null, "")]
    public void File_names_are_made_safe(string? name, string expected)
    {
        Assert.Equal(expected, EmailAttachment.Sanitize(name));
    }

    [Fact]
    public void Long_file_names_are_cut_without_splitting_a_surrogate_pair()
    {
        Assert.Equal(200, EmailAttachment.Sanitize(new string('a', 300)).Length);
        var emoji = new string('a', 199) + "\uD83D\uDE00" + "tail";
        Assert.Equal(new string('a', 199), EmailAttachment.Sanitize(emoji));
    }
}
