using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Rendering;
using RaiCore.Pdf.Extraction;
using static RaiCore.Pdf.Tests.WordDocuments.Docx;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Text extraction from word-processing documents (<see cref="DocumentText"/>): the fixtures against independent
/// readers (python-docx, striprtf, odfpy, and the POI and Tika expectations for .doc), each option in each format,
/// format detection, and malformed and hostile input.
/// </summary>
public class DocumentTextTests
{
    private const string WordNs = Docx.W;

    private static readonly DocumentTextOptions NoLabels = new() { ListLabels = false };

    // ---- Oracles ----

    /// <summary>
    /// The fixtures whose oracle text is incomplete, so that ours has more words (in order around the oracle's): the
    /// reasons are the oracles'. Every other fixture's words equal the oracle's exactly.
    /// </summary>
    private static readonly Dictionary<string, string> OracleGaps = new(StringComparer.Ordinal)
    {
        ["Documents/fields.docx"] = "python-docx reads no text in w:ins, w:sdt and w:fldSimple",
        ["Documents/Odt/fields.odt"] = "the oracle leaves out page numbers, which we write as saved",
        ["Documents/Rtf/fields.rtf"] = "the oracle drops an object's result and the space after a comment",
        ["Documents/Rtf/unicode.rtf"] = "striprtf reads the ANSI version of \\upr, which hides the Unicode one",
    };

    /// <summary>The fixtures with oracle text: (key under Fixtures, oracle, whether the oracle includes text boxes).</summary>
    public static TheoryData<string> OracleFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var (key, _) in OracleTexts())
        {
            data.Add(key);
        }

        return data;
    }

    private static IEnumerable<(string Key, List<string> Texts)> OracleTexts()
    {
        static List<string> Flatten(JsonElement items)
        {
            var result = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(item.GetString()!);
                }
                else
                {
                    result.AddRange(Flatten(item));
                }
            }

            return result;
        }

        using (var docx = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "expected.json")))))
        {
            foreach (var property in docx.RootElement.EnumerateObject())
            {
                var key = property.Name.StartsWith("../", StringComparison.Ordinal) ? property.Name[3..] : "Documents/" + property.Name;
                yield return (key, Flatten(property.Value));
            }
        }

        using (var rtf = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "Rtf", "expected.json")))))
        {
            foreach (var property in rtf.RootElement.EnumerateObject())
            {
                yield return (property.Name, Flatten(property.Value.GetProperty("text")));
            }
        }

        using (var odt = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "Odt", "expected.json")))))
        {
            foreach (var property in odt.RootElement.EnumerateObject())
            {
                yield return (property.Name, Flatten(property.Value));
            }
        }

        using (var doc = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "Doc", "expected.json")))))
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.GetProperty("error").ValueKind == JsonValueKind.Null)
                {
                    yield return ("Documents/Doc/" + property.Name, property.Value.TryGetProperty("text", out var text) ? Flatten(text) : []);
                }
            }
        }
    }

    /// <summary>Words without punctuation, case folded: what the oracles and we must agree on.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string([.. raw.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
            if (word.Length > 0)
            {
                words.Add(word);
            }
        }

        return words;
    }

    /// <summary>
    /// The body text (list labels left out, as the oracles do) has the oracle's words in order: exactly the oracle's
    /// words, except for the gaps of <see cref="OracleGaps"/> and for .doc, whose expectations (from POI's and Tika's
    /// own tests) are paragraphs that follow each other. Text boxes are compared where the oracle reads them (ODT).
    /// </summary>
    [Theory]
    [MemberData(nameof(OracleFixtures))]
    public void Fixtures_have_the_oracle_text(string key)
    {
        var expected = Words(string.Join(' ', OracleTexts().First(t => t.Key == key).Texts));
        var textBoxes = key.EndsWith("dt", StringComparison.Ordinal) || key.EndsWith(".ott", StringComparison.Ordinal) || key.EndsWith(".doc", StringComparison.Ordinal);
        var actual = Words(DocumentText.Extract(Fixtures.Read(key), new DocumentTextOptions { ListLabels = false, IncludeTextBoxes = textBoxes }));
        if (OracleGaps.ContainsKey(key))
        {
            // The oracle's letters are in ours in order (it may also join words that we keep apart).
            var ours = string.Concat(actual);
            var position = 0;
            foreach (var c in string.Concat(expected))
            {
                position = ours.IndexOf(c, position) + 1;
                Assert.True(position > 0, $"{key}: the oracle's text is not in ours, in order.");
            }

            Assert.True(actual.Count - expected.Count <= 12, $"{key}: {actual.Count - expected.Count} more words than the oracle.");
        }
        else if (key.EndsWith(".doc", StringComparison.Ordinal))
        {
            var position = 0;
            foreach (var word in expected)
            {
                var found = actual.IndexOf(word, position);
                Assert.True(found >= 0, $"{key}: '{word}' is missing after word {position}.");
                position = found + 1;
            }
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// What the oracles find in headers, footers and notes (and POI's .doc list labels) is in the text with those
    /// included, and comments only with comments included.
    /// </summary>
    [Fact]
    public void Headers_footers_notes_and_labels_match_the_oracles()
    {
        var all = new DocumentTextOptions { IncludeHeadersAndFooters = true, IncludeNotes = true };
        static string Normalized(string text) => " " + string.Join(' ', Words(text)) + " ";
        var checkedItems = 0;
        using (var doc = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "Doc", "expected.json")))))
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.GetProperty("error").ValueKind != JsonValueKind.Null)
                {
                    continue;
                }

                var text = DocumentText.Extract(Fixtures.Read(Path.Combine("Documents", "Doc", property.Name)), all);
                var normalized = Normalized(text);
                foreach (var item in property.Value.GetProperty("anywhere").EnumerateArray())
                {
                    // Tika checks some of these as parts of words (an e-mail address, a word of a longer one).
                    var expected = Normalized(item.GetString()!);
                    Assert.True(normalized.Contains(expected, StringComparison.Ordinal) || Words(text).Any(w => w.Contains(expected.Trim(), StringComparison.Ordinal))
                        || (expected.Trim() == "footnote appears here" && normalized.Contains(" footnote appears here1 ", StringComparison.Ordinal)),
                        $"{property.Name}: '{item.GetString()}' is missing.");
                    checkedItems++;
                }

                foreach (var label in property.Value.GetProperty("labels").EnumerateArray())
                {
                    Assert.Contains(Normalized(label.GetString()!), normalized, StringComparison.Ordinal);
                    checkedItems++;
                }

                foreach (var absent in property.Value.GetProperty("absent").EnumerateArray())
                {
                    Assert.DoesNotContain(absent.GetString()!, text, StringComparison.Ordinal);
                }
            }
        }

        using (var rtf = JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Documents", "Rtf", "expected.json")))))
        {
            foreach (var property in rtf.RootElement.EnumerateObject())
            {
                var normalized = Normalized(DocumentText.Extract(Fixtures.Read(property.Name), all));
                foreach (var item in property.Value.GetProperty("anywhere").EnumerateArray())
                {
                    Assert.Contains(Normalized(item.GetString()!), normalized, StringComparison.Ordinal);
                    checkedItems++;
                }
            }
        }

        Assert.True(checkedItems > 50, $"Only {checkedItems} items were checked.");
    }

    [Fact]
    public void Doc_comments_are_included_only_when_asked()
    {
        var data = Fixtures.Read(Path.Combine("Documents", "Doc", "footnote.doc"));
        Assert.Equal("Test text\n", DocumentText.Extract(data));
        Assert.Equal("Test text\nTestComment\n", DocumentText.Extract(data, new DocumentTextOptions { IncludeComments = true }));
        Assert.Equal("Test text1i\n1\tTestFootnote\ni\tTestEndnote\n", DocumentText.Extract(data, new DocumentTextOptions { IncludeNotes = true }));
    }

    [Fact]
    public void Doc_field_codes_replace_their_results()
    {
        var data = Fixtures.Read(Path.Combine("Documents", "Doc", "testWORD_boldHyperlink.doc"));
        var codes = DocumentText.Extract(data, new DocumentTextOptions { FieldCodes = true });
        Assert.Contains("{ HYPERLINK \"http://tika.apache.org/\" }; bold, I say.", codes, StringComparison.Ordinal);
        Assert.DoesNotContain("hyper", codes, StringComparison.Ordinal);
        Assert.Contains("hyper  link; bold", DocumentText.Extract(data), StringComparison.Ordinal);
    }

    [Fact]
    public void Doc_text_boxes_headers_and_lists()
    {
        var data = Fixtures.Read(Path.Combine("Documents", "Doc", "testWORD_various.doc"));
        var text = DocumentText.Extract(data);
        Assert.StartsWith("Footnote appears here\nHere is a text box\n", text, StringComparison.Ordinal);
        Assert.Contains("\u2022\tBullet 1\n", text, StringComparison.Ordinal);
        Assert.Contains("1)\tNumber bullet 1\n", text, StringComparison.Ordinal);
        Assert.Contains("Row 1 Col 1\tRow 1 Col 2\tRow 1 Col 3\nRow 2 Col 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This is the header text.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Here is a text box", DocumentText.Extract(data, new DocumentTextOptions { IncludeTextBoxes = false }), StringComparison.Ordinal);
        var all = DocumentText.Extract(data, new DocumentTextOptions { IncludeHeadersAndFooters = true });
        Assert.StartsWith("This is the header text.\n", all, StringComparison.Ordinal);
        Assert.Contains("This is the footer text.\n", all, StringComparison.Ordinal);
    }

    // ---- DOCX ----

    private static string Extract(byte[] data, DocumentTextOptions? options = null) => DocumentText.Extract(data, options);

    [Fact]
    public void Docx_paragraphs_labels_tabs_and_breaks()
    {
        const string numbering = """
            <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl>
              <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="(%2)"/><w:suff w:val="space"/></w:lvl>
              <w:lvl w:ilvl="2"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="-"/><w:suff w:val="nothing"/></w:lvl></w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
            """;
        static string Item(string text, int level) => P(text, $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"1\"/></w:numPr>");
        var data = Document(
            P("Title") + Item("One", 0) + Item("Sub", 1) + Item("Dash", 2) + Item("Two", 0)
            + "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>before</w:t><w:br w:type=\"page\"/><w:t>after</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>end</w:t><w:br w:type=\"page\"/></w:r></w:p>"
            + "<w:p><w:r><w:br w:type=\"column\"/><w:t>start</w:t></w:r></w:p>"
            + "<w:p><w:r><w:br w:type=\"page\"/><w:t>top</w:t></w:r></w:p>"
            + "<w:p/>",
            numbering: numbering);
        Assert.Equal("Title\n1.\tOne\n(a) Sub\n-Dash\n2.\tTwo\na\tb\nc\nbefore\nafter\nend\nstart\ntop\n\n", Extract(data));
        Assert.Equal("Title\nOne\nSub\nDash\nTwo\na\tb\nc\nbefore\nafter\nend\nstart\ntop\n\n", Extract(data, NoLabels));
        Assert.Equal("Title\r\n1.\tOne\r\n(a) Sub\r\n-Dash\r\n2.\tTwo\r\na\tb\r\nc\r\nbefore\r\nafter\r\nend\r\nstart\r\ntop\r\n\r\n",
            Extract(data, new DocumentTextOptions { ParagraphSeparator = "\r\n" }));
    }

    [Fact]
    public void Docx_tables_write_cells_and_rows_with_their_separators()
    {
        var data = Document(
            P("Before")
            + "<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>"
            + "<w:tr><w:tc><w:p><w:r><w:t>A1</w:t></w:r></w:p><w:p><w:r><w:t>more</w:t><w:tab/><w:t>x</w:t><w:br/><w:t>y</w:t></w:r></w:p></w:tc>"
            + "<w:tc><w:p/></w:tc></w:tr>"
            + "<w:tr><w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc><w:tc>"
            + "<w:tbl><w:tblGrid><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/></w:tblGrid>"
            + "<w:tr><w:tc><w:p><w:r><w:t>n1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>n2</w:t></w:r></w:p></w:tc></w:tr>"
            + "<w:tr><w:tc><w:p><w:r><w:t>n3</w:t></w:r></w:p></w:tc><w:tc><w:p/></w:tc></w:tr></w:tbl>"
            + "<w:p><w:r><w:t>after nested</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"
            + P("After"));
        Assert.Equal("Before\nA1 more x y\t\nB1\tn1 n2 n3 after nested\nAfter\n", Extract(data));
        Assert.Equal("Before\nA1 more x y | \nB1 | n1 n2 n3 after nested\nAfter\n",
            Extract(data, new DocumentTextOptions { TableCellSeparator = " | " }));
        Assert.Equal("Before\nA1 more x y\t;B1\tn1 n2 n3 after nested;After\n",
            Extract(data, new DocumentTextOptions { TableRowSeparator = ";" }));
    }

    [Fact]
    public void Docx_list_labels_in_cells_are_followed_by_a_space()
    {
        const string numbering = """
            <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl></w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
            """;
        var data = Document(
            "<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc>"
            + P("item", "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>") + "</w:tc></w:tr></w:tbl>",
            numbering: numbering);
        Assert.Equal("1. item\n", Extract(data));
    }

    private static byte[] DocxWithParts()
    {
        var header = Part("hdr", P("Default header"));
        var first = Part("hdr", P("First header"));
        var even = Part("hdr", P("Even header"));
        var footer = Part("ftr", P("Page footer"));
        var notes = Part("footnotes",
            "<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>"
            + "<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> The footnote.</w:t></w:r></w:p></w:footnote>");
        var endnotes = Part("endnotes", "<w:endnote w:id=\"2\"><w:p><w:r><w:endnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> The endnote.</w:t></w:r></w:p></w:endnote>");
        var comments = Part("comments", "<w:comment w:id=\"5\" w:author=\"A\"><w:p><w:r><w:t>The comment.</w:t></w:r></w:p></w:comment>"
            + "<w:comment w:id=\"6\"><w:p><w:r><w:t>Unreferenced.</w:t></w:r></w:p></w:comment>");
        var body =
            "<w:p><w:commentRangeStart w:id=\"5\"/><w:r><w:t>Body</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r><w:r><w:t xml:space=\"preserve\"> and</w:t></w:r>"
            + "<w:r><w:endnoteReference w:id=\"2\"/></w:r><w:commentRangeEnd w:id=\"5\"/><w:r><w:commentReference w:id=\"5\"/></w:r></w:p>"
            + "<w:p><w:r><w:t>Box:</w:t></w:r><w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\"><v:textbox><w:txbxContent>" + P("Boxed text") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>"
            + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/><w:headerReference w:type=\"first\" r:id=\"rF\"/><w:headerReference w:type=\"even\" r:id=\"rE\"/>"
            + "<w:footerReference w:type=\"default\" r:id=\"rT\"/><w:titlePg/></w:sectPr>";
        return Document(body,
            parts: [("word/header1.xml", header), ("word/header2.xml", first), ("word/header3.xml", even), ("word/footer1.xml", footer),
                ("word/footnotes.xml", notes), ("word/endnotes.xml", endnotes), ("word/comments.xml", comments)],
            relationships: [("rH", "header", "header1.xml", false), ("rF", "header", "header2.xml", false), ("rE", "header", "header3.xml", false),
                ("rT", "footer", "footer1.xml", false), ("rN", "footnotes", "footnotes.xml", false), ("rEn", "endnotes", "endnotes.xml", false),
                ("rC", "comments", "comments.xml", false)]);
    }

    [Fact]
    public void Docx_headers_footers_notes_comments_and_text_boxes_by_option()
    {
        var data = DocxWithParts();
        Assert.Equal("Body and\nBox:\nBoxed text\n", Extract(data));
        Assert.Equal("Body and\nBox:\n", Extract(data, new DocumentTextOptions { IncludeTextBoxes = false }));
        Assert.Equal("First header\nDefault header\nBody and\nBox:\nBoxed text\nPage footer\n", Extract(data, new DocumentTextOptions { IncludeHeadersAndFooters = true }));
        Assert.Equal("Body1 andi\nBox:\nBoxed text\n1 The footnote.\ni The endnote.\n", Extract(data, new DocumentTextOptions { IncludeNotes = true }));
        Assert.Equal("Body and\nBox:\nBoxed text\nThe comment.\n", Extract(data, new DocumentTextOptions { IncludeComments = true }));
        Assert.Equal("First header\nDefault header\nBody1 andi\nBox:\nPage footer\n1 The footnote.\ni The endnote.\nThe comment.\n",
            Extract(data, new DocumentTextOptions { IncludeHeadersAndFooters = true, IncludeNotes = true, IncludeComments = true, IncludeTextBoxes = false }));
    }

    [Fact]
    public void Docx_even_headers_show_with_different_even_and_odd_pages()
    {
        var text = Extract(Document(
            P("Body") + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/><w:headerReference w:type=\"even\" r:id=\"rE\"/></w:sectPr>",
            settings: "<w:evenAndOddHeaders/>",
            parts: [("word/header1.xml", Part("hdr", P("Odd"))), ("word/header2.xml", Part("hdr", P("Even")))],
            relationships: [("rH", "header", "header1.xml", false), ("rE", "header", "header2.xml", false)]),
            new DocumentTextOptions { IncludeHeadersAndFooters = true });
        Assert.Equal("Odd\nEven\nBody\n", text);
    }

    [Fact]
    public void Docx_headers_shared_by_sections_are_written_once()
    {
        var text = Extract(Document(
            P("One") + "<w:p><w:pPr><w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/></w:sectPr></w:pPr></w:p>"
            + P("Two") + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/></w:sectPr>",
            parts: [("word/header1.xml", Part("hdr", P("Shared")))],
            relationships: [("rH", "header", "header1.xml", false)]),
            new DocumentTextOptions { IncludeHeadersAndFooters = true });
        Assert.Equal("Shared\nOne\n\nTwo\n", text);
    }

    [Fact]
    public void Docx_hidden_and_deleted_text_by_option()
    {
        var data = Document(
            "<w:p><w:r><w:t xml:space=\"preserve\">Shown </w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t xml:space=\"preserve\">hidden </w:t><w:tab/></w:r>"
            + "<w:del w:id=\"1\" w:author=\"A\"><w:r><w:delText xml:space=\"preserve\">deleted </w:delText></w:r></w:del>"
            + "<w:ins w:id=\"2\" w:author=\"A\"><w:r><w:t xml:space=\"preserve\">inserted </w:t></w:r></w:ins>"
            + "<w:moveFrom w:id=\"3\" w:author=\"A\"><w:r><w:delText>moved</w:delText></w:r></w:moveFrom><w:r><w:t>end</w:t></w:r></w:p>");
        Assert.Equal("Shown inserted end\n", Extract(data));
        Assert.Equal("Shown hidden \tinserted end\n", Extract(data, new DocumentTextOptions { IncludeHiddenText = true }));
        Assert.Equal("Shown deleted inserted movedend\n", Extract(data, new DocumentTextOptions { IncludeDeletedText = true }));
        Assert.Equal("Shown hidden \tdeleted inserted movedend\n", Extract(data, new DocumentTextOptions { IncludeHiddenText = true, IncludeDeletedText = true }));
    }

    [Fact]
    public void Docx_field_codes_or_results()
    {
        var data = Document(
            "<w:p><w:r><w:t xml:space=\"preserve\">Page </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE \\* roman </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>iv</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
            + "<w:r><w:t xml:space=\"preserve\"> by </w:t></w:r><w:fldSimple w:instr=\" AUTHOR \"><w:r><w:t>Ann</w:t></w:r></w:fldSimple>"
            + "<w:r><w:t xml:space=\"preserve\">, see </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
            + "<w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \"https://example.com\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:t>the site</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:t>.</w:t></w:r></w:p>"
            // A field without a separator, and a nested one in an instruction (its code is not part of the outer one).
            + "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>XE \"entry\"</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\">IF </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>MERGEFIELD x</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> = 1 \"yes\"</w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>yes</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"
            // A field still in its instruction at the paragraph's end is closed there.
            + "<w:p><w:r><w:t xml:space=\"preserve\">Broken </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>DATE</w:instrText></w:r></w:p>"
            + P("Next"));
        Assert.Equal("Page iv by Ann, see the site.\nyes\nBroken \nNext\n", Extract(data));
        Assert.Equal("Page { PAGE \\* roman } by { AUTHOR }, see { HYPERLINK \"https://example.com\" }.\n{ XE \"entry\" }{ IF  = 1 \"yes\" }\nBroken { DATE }\nNext\n",
            Extract(data, new DocumentTextOptions { FieldCodes = true }));
    }

    [Fact]
    public void Docx_field_codes_hide_results_across_paragraphs()
    {
        var data = Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-3\" </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>Entry one</w:t></w:r></w:p>"
            + P("Entry two") + "<w:p><w:r><w:t>Entry three</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" + P("Body"));
        Assert.Equal("Entry one\nEntry two\nEntry three\nBody\n", Extract(data));
        Assert.Equal("{ TOC \\o \"1-3\" }\nBody\n", Extract(data, new DocumentTextOptions { FieldCodes = true }));
    }

    [Fact]
    public void Docx_hidden_and_deleted_text_stay_out_of_computed_fields()
    {
        var data = Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:rPr><w:vanish/></w:rPr><w:t>9</w:t></w:r><w:del w:id=\"1\" w:author=\"A\"><w:r><w:delText>8</w:delText></w:r></w:del><w:r><w:t>7</w:t></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"
            + "<w:p><w:fldSimple w:instr=\"PAGE\"><w:r><w:rPr><w:vanish/></w:rPr><w:t>6</w:t></w:r><w:r><w:t>5</w:t></w:r></w:fldSimple></w:p>");
        Assert.Equal("7\n5\n", Extract(data, new DocumentTextOptions { IncludeHiddenText = true, IncludeDeletedText = true }));
    }

    [Fact]
    public void Hidden_and_deleted_text_kept_for_extraction_is_not_drawn()
    {
        var data = Document(
            "<w:p><w:r><w:t xml:space=\"preserve\">Shown </w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>Secret</w:t></w:r>"
            + "<w:del w:id=\"1\" w:author=\"A\"><w:r><w:delText>Removed</w:delText></w:r></w:del></w:p>");
        var context = new ConversionContext(ResourceLimits.Default, null, CancellationToken.None) { Extras = ReadExtras.HiddenText | ReadExtras.DeletedText };
        var document = DocxConverter.Read(data, context);
        var pdf = new DocumentRenderer(document, new DocxConverterOptions(), context).Render("test").ToArray();
        var text = AllText(pdf);
        Assert.Contains("Shown", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Removed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_are_not_read()
    {
        var picture = $"<w:drawing><wp:inline><wp:extent cx=\"952500\" cy=\"952500\"/><wp:docPr id=\"1\" name=\"P\"/><a:graphic><a:graphicData uri=\"{Pic}\">"
            + $"<pic:pic><pic:blipFill><a:blip r:embed=\"rImg\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";
        var data = Document($"<w:p><w:r><w:t>Before</w:t></w:r><w:r>{picture}</w:r><w:r><w:t>after</w:t></w:r></w:p>",
            parts: [("word/media/big.png", new byte[200_000])], relationships: [("rImg", "image", "media/big.png", false)]);
        var limits = ResourceLimits.Default with { MaxDecompressedBytes = 100_000 };
        Assert.Throws<ResourceLimitExceededException>(() => DocxToPdf.Convert(data, new DocxConverterOptions { Limits = limits }));
        Assert.Equal("Beforeafter\n", DocumentText.Extract(data, new DocumentTextOptions { Limits = limits }));
    }

    // ---- RTF ----

    [Fact]
    public void Rtf_options()
    {
        var data = Rtf.Document(
            @"{\header \pard Head\par}{\footer \pard Foot\par}"
            + @"\pard Text{\v  hidden}{\deleted  gone}{\field{\*\fldinst PAGE}{\fldrslt 3}}{\chftn{\footnote \pard{\chftn} Note.\par}}"
            + @" more{\*\atnid A}{\*\atnauthor Ann}\chatn{\*\annotation{\*\atnref 1}\pard Remark.\par}.\par"
            + @"\pard{\field{\*\fldinst HYPERLINK ""https://example.com""}{\fldrslt site}}\tab x{\v\tab}y\par"
            + @"\trowd\cellx2000\cellx4000\pard\intbl A\cell B\cell\row\pard After\par");
        Assert.Equal("Text3 more.\nsite\txy\nA\tB\nAfter\n", Extract(data));
        Assert.Equal("Text hidden3 more.\nsite\tx\ty\nA\tB\nAfter\n", Extract(data, new DocumentTextOptions { IncludeHiddenText = true }));
        Assert.Equal("Text gone3 more.\nsite\txy\nA\tB\nAfter\n", Extract(data, new DocumentTextOptions { IncludeDeletedText = true }));
        Assert.Equal("Text{ PAGE } more.\n{ HYPERLINK \"https://example.com\" }\txy\nA\tB\nAfter\n", Extract(data, new DocumentTextOptions { FieldCodes = true }));
        Assert.Equal("Text31 more.\nsite\txy\nA\tB\nAfter\n1 Note.\n", Extract(data, new DocumentTextOptions { IncludeNotes = true }));
        Assert.Equal("Text3 more.\nsite\txy\nA\tB\nAfter\nRemark.\n", Extract(data, new DocumentTextOptions { IncludeComments = true }));
        Assert.Equal("Head\nText3 more.\nsite\txy\nA\tB\nAfter\nFoot\n", Extract(data, new DocumentTextOptions { IncludeHeadersAndFooters = true }));
    }

    // ---- ODT ----

    [Fact]
    public void Odt_options()
    {
        var data = Odt.Document(
            "<text:tracked-changes><text:changed-region text:id=\"ct1\"><text:deletion><office:change-info><dc:creator>A</dc:creator></office:change-info>"
            + "<text:p>gone</text:p><text:p>too</text:p></text:deletion></text:changed-region></text:tracked-changes>"
            + "<text:p>Text<text:span text:style-name=\"H\"> hidden</text:span><text:change text:change-id=\"ct1\"/>"
            + "<text:note text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body><text:p>Note.</text:p></text:note-body></text:note>"
            + " more<office:annotation><dc:creator>Ann</dc:creator><text:p>Remark.</text:p></office:annotation>.<text:hidden-text text:string-value=\" [cond]\" text:is-hidden=\"true\"/></text:p>"
            + "<text:list><text:list-item><text:p>Item</text:p></text:list-item></text:list>"
            + "<table:table><table:table-column table:number-columns-repeated=\"2\"/><table:table-row><table:table-cell><text:p>A</text:p></table:table-cell>"
            + "<table:table-cell><text:p>B</text:p></table:table-cell></table:table-row></table:table>"
            + "<text:p>Frame<draw:frame text:anchor-type=\"paragraph\"><draw:text-box><text:p>Boxed</text:p></draw:text-box></draw:frame></text:p>"
            + "<text:p>Page <text:page-number>4</text:page-number></text:p>",
            automatic: "<style:style style:name=\"H\" style:family=\"text\"><style:text-properties text:display=\"none\"/></style:style>");
        Assert.Equal("Text more.\nItem\nA\tB\nFrame\nBoxed\nPage 4\n", Extract(data));
        Assert.Equal("Text hidden more. [cond]\nItem\nA\tB\nFrame\nBoxed\nPage 4\n", Extract(data, new DocumentTextOptions { IncludeHiddenText = true }));
        Assert.Equal("Textgone too more.\nItem\nA\tB\nFrame\nBoxed\nPage 4\n", Extract(data, new DocumentTextOptions { IncludeDeletedText = true }));
        Assert.Equal("Text1 more.\nItem\nA\tB\nFrame\nBoxed\nPage 4\n1 Note.\n", Extract(data, new DocumentTextOptions { IncludeNotes = true }));
        Assert.Equal("Text more.\nItem\nA\tB\nFrame\nBoxed\nPage 4\nRemark.\n", Extract(data, new DocumentTextOptions { IncludeComments = true }));
        Assert.Equal("Text more.\nItem\nA\tB\nFrame\nPage 4\n", Extract(data, new DocumentTextOptions { IncludeTextBoxes = false, FieldCodes = true }));
    }

    [Fact]
    public void Odt_list_labels_follow_the_list_style()
    {
        var text = Extract(Fixtures.Read(Path.Combine("Documents", "Odt", "lists.odt")));
        Assert.Contains("1.\t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.\t", Extract(Fixtures.Read(Path.Combine("Documents", "Odt", "lists.odt")), NoLabels), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_that_starts_with_an_empty_paragraph_has_no_leading_space()
    {
        var data = Document(
            "<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc><w:p/><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc>"
            + "<w:tc><w:p/><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
        Assert.Equal("A\tB\n", Extract(data));
    }

    [Fact]
    public void Notes_referred_to_in_notes_are_not_followed()
    {
        var notes = Part("footnotes", "<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> See</w:t></w:r>"
            + "<w:r><w:endnoteReference w:id=\"2\"/></w:r></w:p></w:footnote>");
        var endnotes = Part("endnotes", "<w:endnote w:id=\"2\"><w:p><w:r><w:endnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> Inner.</w:t></w:r></w:p></w:endnote>");
        var data = Document("<w:p><w:r><w:t>Body</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>",
            parts: [("word/footnotes.xml", notes), ("word/endnotes.xml", endnotes)],
            relationships: [("rN", "footnotes", "footnotes.xml", false), ("rE", "endnotes", "endnotes.xml", false)]);
        Assert.Equal("Body1\n1 See\n", Extract(data, new DocumentTextOptions { IncludeNotes = true }));
    }

    [Fact]
    public void A_field_left_open_in_the_body_does_not_hide_the_notes()
    {
        var notes = Part("footnotes", "<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> Note.</w:t></w:r></w:p></w:footnote>");
        var data = Document(
            "<w:p><w:r><w:t>Body</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>"
            + "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>TOC</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:t>never ends</w:t></w:r></w:p>",
            parts: [("word/footnotes.xml", notes)], relationships: [("rN", "footnotes", "footnotes.xml", false)]);
        Assert.Equal("Body1\n{ TOC }\n1 Note.\n", Extract(data, new DocumentTextOptions { IncludeNotes = true, FieldCodes = true }));

        // Nor the headers of the sections it goes on into.
        var sections = Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>TOC</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:t>hidden</w:t></w:r></w:p><w:p><w:pPr><w:sectPr/></w:pPr></w:p>"
            + P("also hidden") + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH\"/></w:sectPr>",
            parts: [("word/header1.xml", Part("hdr", P("Second header")))], relationships: [("rH", "header", "header1.xml", false)]);
        Assert.Equal("{ TOC }Second header\n\n", Extract(sections, new DocumentTextOptions { IncludeHeadersAndFooters = true, FieldCodes = true }));
    }

    [Fact]
    public void Rtf_deleted_and_hidden_line_breaks_are_left_out()
    {
        var data = Rtf.Document(@"\pard One{\deleted  gone\line}{\v  secret\line} two\par");
        Assert.Equal("One two\n", Extract(data));
        Assert.Equal("One gone\n secret\n two\n", Extract(data, new DocumentTextOptions { IncludeDeletedText = true, IncludeHiddenText = true }));
    }

    [Fact]
    public void Comments_referred_to_in_comments_are_not_listed()
    {
        var comments = Part("comments", "<w:comment w:id=\"5\"><w:p><w:r><w:t>Outer</w:t></w:r><w:r><w:commentReference w:id=\"6\"/></w:r></w:p></w:comment>"
            + "<w:comment w:id=\"6\"><w:p><w:r><w:t>Inner</w:t></w:r></w:p></w:comment>");
        var data = Document("<w:p><w:r><w:t>Body</w:t></w:r><w:r><w:commentReference w:id=\"5\"/></w:r></w:p>",
            parts: [("word/comments.xml", comments)], relationships: [("rC", "comments", "comments.xml", false)]);
        Assert.Equal("Body\nOuter\n", Extract(data, new DocumentTextOptions { IncludeComments = true }));
        var odt = Odt.Document("<text:p>Body<office:annotation><text:p>Outer<office:annotation><text:p>Inner</text:p></office:annotation></text:p></office:annotation></text:p>");
        Assert.Equal("Body\nOuter\n", Extract(odt, new DocumentTextOptions { IncludeComments = true }));
    }

    [Fact]
    public void Odt_comments_in_notes_are_listed_with_the_notes()
    {
        var data = Odt.Document("<text:p>Text<text:note text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body>"
            + "<text:p>Note<office:annotation><dc:creator>A</dc:creator><text:p>On the note.</text:p></office:annotation>.</text:p></text:note-body></text:note></text:p>");
        Assert.Equal("Text1\n1 Note.\nOn the note.\n", Extract(data, new DocumentTextOptions { IncludeNotes = true, IncludeComments = true }));
        Assert.Equal("Text\n", Extract(data, new DocumentTextOptions { IncludeComments = true }));
    }

    [Fact]
    public void A_word_package_with_a_content_part_is_read_as_a_word_document()
    {
        var data = Docx.Package(
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{P("Word text")}</w:body></w:document>"),
            ("content.xml", "<not-odf/>"));
        Assert.Equal("Word text\n", Extract(data));
    }

    // ---- Formats, overloads and errors ----

    [Theory]
    [InlineData("Documents/minimal.docx", "Hello, minimal document.\n")]
    [InlineData("Documents/strict.docx", "Hello, strict document.\nItalic\n")]
    [InlineData("Documents/Doc/capitalized.doc", "The following word is: capitalized.\n")]
    [InlineData("Documents/Rtf/minimal.rtf", "Hello, minimal RTF document.\n")]
    [InlineData("Documents/Odt/minimal.odt", "Hello, OpenDocument.\n")]
    [InlineData("Documents/Odt/template.ott", "Template text.\n")]
    public void The_format_is_detected_from_the_data(string name, string expected)
    {
        var data = Fixtures.Read(name);
        Assert.Equal(expected, DocumentText.Extract(data));
    }

    [Fact]
    public void Flat_odt_and_rtf_with_a_byte_order_mark_are_detected()
    {
        Assert.StartsWith("Flat", DocumentText.Extract(Fixtures.Read(Path.Combine("Documents", "Odt", "flat.fodt"))), StringComparison.Ordinal);
        Assert.Equal("Hi\n", DocumentText.Extract([0xEF, 0xBB, 0xBF, (byte)'\n', .. "{\\rtf1 Hi\\par}"u8]));
    }

    [Fact]
    public async Task Overloads_give_the_same_text()
    {
        var data = Fixtures.Read(Path.Combine("Documents", "lists.docx"));
        var expected = DocumentText.Extract(data);
        Assert.Equal(expected, DocumentText.Extract(new MemoryStream(data)));
        Assert.Equal(expected, DocumentText.Extract(Fixtures.Path(Path.Combine("Documents", "lists.docx"))));
        Assert.Equal(expected, await DocumentText.ExtractAsync(data, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(expected, await DocumentText.ExtractAsync(new MemoryStream(data), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_and_arguments()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var data = Fixtures.Read(Path.Combine("Documents", "lists.docx"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentText.ExtractAsync(data, cancellationToken: canceled.Token));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract((string)null!));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract(data, new DocumentTextOptions { Limits = null! }));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract(data, new DocumentTextOptions { ParagraphSeparator = null! }));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract(data, new DocumentTextOptions { TableCellSeparator = null! }));
        Assert.Throws<ArgumentNullException>(() => DocumentText.Extract(data, new DocumentTextOptions { TableRowSeparator = null! }));
    }

    [Fact]
    public void Unsupported_and_encrypted_data_throw_the_documented_exceptions()
    {
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract([]));
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract("plain text, not a document"u8.ToArray()));
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract("<html><body>no</body></html>"u8.ToArray()));
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract("PK\u0003\u0004 broken"u8.ToArray()));
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract(Docx.Package(("other.txt", "x"))));
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Documents", "Doc", "clusterfuzz-testcase-minimized-POIHWPFFuzzer-5418937293340672.doc"))));
        Assert.Throws<DocumentFormatException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Tika", "testODTnotaZipFile.odt"))));
        Assert.Throws<NotSupportedException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Poi", "bug53475-password-is-pass.docx"))));
        Assert.Throws<NotSupportedException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Documents", "Doc", "testWORD_protected_passtika.doc"))));
        Assert.Throws<NotSupportedException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Documents", "Doc", "testWORD6.doc"))));
        Assert.Throws<NotSupportedException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Tika", "testODTEncrypted.odt"))));
    }

    [Fact]
    public void Limits_bound_the_reading()
    {
        var limit = Assert.Throws<ResourceLimitExceededException>(() => DocumentText.Extract(Fixtures.Read(Path.Combine("Poi", "deep-table-cell.docx"))));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), limit.LimitName);
        var elements = new DocumentTextOptions { Limits = ResourceLimits.Default with { MaxElements = 40 } };
        foreach (var name in new[] { "Documents/tables.docx", "Documents/Doc/testWORD.doc", "Documents/Rtf/tables.rtf", "Documents/Odt/tables.odt" })
        {
            var exception = Assert.Throws<ResourceLimitExceededException>(() => DocumentText.Extract(Fixtures.Read(name), elements));
            Assert.Equal(nameof(ResourceLimits.MaxElements), exception.LimitName);
        }

        var cells = new DocumentTextOptions { Limits = ResourceLimits.Default with { MaxTableCells = 3 } };
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), Assert.Throws<ResourceLimitExceededException>(() => DocumentText.Extract(Fixtures.Read("Documents/tables.docx"), cells)).LimitName);
        var time = new DocumentTextOptions { Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) } };
        Assert.Throws<ResourceLimitExceededException>(() => DocumentText.Extract(Fixtures.Read("Tika/testRTFHyperlink.rtf"), time));
    }

    /// <summary>
    /// Damaged and cut copies of a fixture of each format give text or throw only the documented exceptions, with every
    /// option on (which reads more of the document).
    /// </summary>
    [Theory]
    [InlineData("Documents/fields.docx")]
    [InlineData("Documents/Doc/testWORD_various.doc")]
    [InlineData("Documents/Doc/footnote.doc")]
    [InlineData("Documents/Rtf/fields.rtf")]
    [InlineData("Documents/Odt/fields.odt")]
    [InlineData("Documents/Odt/flat.fodt")]
    public void Damaged_documents_throw_only_the_documented_exceptions(string name)
    {
        var original = Fixtures.Read(name);
        var options = new DocumentTextOptions
        {
            IncludeHeadersAndFooters = true, IncludeNotes = true, IncludeComments = true, IncludeHiddenText = true, IncludeDeletedText = true, FieldCodes = true,
            Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromSeconds(20) },
        };
        var random = new Random(name.Length * 7919);
        for (var round = 0; round < 60; round++)
        {
            byte[] data;
            if (round % 6 == 0)
            {
                data = original[..random.Next(original.Length)];
            }
            else
            {
                data = (byte[])original.Clone();
                for (var i = 0; i < 1 + (round % 8); i++)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }
            }

            var exception = Record.Exception(() => DocumentText.Extract(data, options));
            Assert.True(exception is null or DocumentFormatException or NotSupportedException or ResourceLimitExceededException, $"{name} round {round}: {exception}");
        }
    }

    [Fact]
    public void Extraction_is_much_faster_than_converting()
    {
        // Not a benchmark (see benchmarks/): a sanity check that no layout happens.
        var data = Fixtures.Read(Path.Combine("Tika", "testRTFHyperlink.rtf"));
        DocumentText.Extract(data);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            DocumentText.Extract(data);
        }

        var extract = clock.Elapsed;
        clock.Restart();
        var text = new TextExtractor(PdfReader.Open(RtfToPdf.ConvertToBytes(data))).ExtractText();
        var convert = clock.Elapsed;
        Assert.NotEmpty(text);
        Assert.True(extract < convert, $"Extracting three times took {extract}, converting once {convert}.");
    }

    [Fact]
    public void Text_has_no_layout_artifacts()
    {
        var text = DocumentText.Extract(Fixtures.Read(Path.Combine("Documents", "tables.docx")));
        Assert.DoesNotContain('\u200B', text);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("Number\tDescription\tAmount\nRow 1\tItem number 1 of the long table\t12.50\n", text, StringComparison.Ordinal);
    }
}
