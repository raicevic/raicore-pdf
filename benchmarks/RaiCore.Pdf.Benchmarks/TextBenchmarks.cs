using System.IO.Compression;
using System.Security;
using System.Text;
using BenchmarkDotNet.Attributes;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;

namespace RaiCore.Pdf.Benchmarks;

/// <summary>
/// The text of a word-processing document: read directly (<see cref="DocumentText"/>, no layout) against converting it
/// to PDF and extracting the text of the PDF (<see cref="TextExtractor"/>). The documents (44 pages as DOCX, 61 as RTF) are built
/// once in the global setup.
/// </summary>
[MemoryDiagnoser]
[MedianColumn]
[MinColumn]
public class TextBenchmarks
{
    private byte[] _docx = [];
    private byte[] _rtf = [];

    [GlobalSetup]
    public void Setup()
    {
        _docx = Docx(Paragraphs);
        _rtf = Rtf(Paragraphs);
    }

    /// <summary>The paragraphs of the documents: headings every 10 paragraphs, and a 4-column table of 100 rows.</summary>
    private const int Paragraphs = 400;

    /// <summary>The text of the Word document, read directly.</summary>
    [Benchmark(Baseline = true)]
    public string DocxText() => DocumentText.Extract(_docx);

    /// <summary>The text of the Word document, converted to PDF and extracted from the PDF.</summary>
    [Benchmark]
    public string DocxConvertAndExtract() => new TextExtractor(PdfReader.Open(DocxToPdf.ConvertToBytes(_docx))).ExtractText();

    /// <summary>The text of the RTF document, read directly.</summary>
    [Benchmark]
    public string RtfText() => DocumentText.Extract(_rtf);

    /// <summary>The text of the RTF document, converted to PDF and extracted from the PDF.</summary>
    [Benchmark]
    public string RtfConvertAndExtract() => new TextExtractor(PdfReader.Open(RtfToPdf.ConvertToBytes(_rtf))).ExtractText();

    /// <summary>A Word document of <paramref name="paragraphs"/> paragraphs (with headings) and a table.</summary>
    public static byte[] Docx(int paragraphs)
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = new StringBuilder();
        for (var i = 0; i < paragraphs; i++)
        {
            var style = i % 10 == 0 ? "<w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr>" : string.Empty;
            body.Append("<w:p>").Append(style).Append("<w:r><w:t>").Append(SecurityElement.Escape(Documents.Text(i, i % 10 == 0 ? 5 : 80))).Append("</w:t></w:r></w:p>");
        }

        body.Append("<w:tbl><w:tblGrid>").Append(string.Concat(Enumerable.Repeat("<w:gridCol w:w=\"2000\"/>", 4))).Append("</w:tblGrid>");
        for (var row = 0; row < 100; row++)
        {
            body.Append("<w:tr>");
            for (var column = 0; column < 4; column++)
            {
                body.Append("<w:tc><w:p><w:r><w:t>").Append(SecurityElement.Escape(Documents.Text((row * 4) + column, 3))).Append("</w:t></w:r></w:p></w:tc>");
            }

            body.Append("</w:tr>");
        }

        body.Append("</w:tbl>");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", $"<w:document xmlns:w=\"{w}\"><w:body>{body}</w:body></w:document>");
        }

        return buffer.ToArray();
    }

    /// <summary>An RTF document with the same content as <see cref="Docx"/>.</summary>
    public static byte[] Rtf(int paragraphs)
    {
        var rtf = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}");
        for (var i = 0; i < paragraphs; i++)
        {
            rtf.Append(i % 10 == 0 ? @"\pard\fs32\b " : @"\pard\fs22\b0 ").Append(Documents.Text(i, i % 10 == 0 ? 5 : 80)).Append(@"\par").Append('\n');
        }

        for (var row = 0; row < 100; row++)
        {
            rtf.Append(@"\trowd\cellx2000\cellx4000\cellx6000\cellx8000\pard\intbl\fs22\b0 ");
            for (var column = 0; column < 4; column++)
            {
                rtf.Append(Documents.Text((row * 4) + column, 3)).Append(@"\cell ");
            }

            rtf.Append(@"\row").Append('\n');
        }

        return Encoding.ASCII.GetBytes(rtf.Append(@"\pard\par}").ToString());
    }
}
