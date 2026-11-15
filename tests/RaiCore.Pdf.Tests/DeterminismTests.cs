using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// The output is a function of the input: the same document gives the same bytes, in this process, on other threads,
/// and (compared by CI across Linux, Windows and macOS) in other processes. Set RAICORE_DETERMINISM_OUTPUT to a folder
/// to have the samples written there.
/// </summary>
public class DeterminismTests
{
    private static readonly DateTimeOffset Moment = new(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(1));

    [Fact]
    public void A_document_gives_the_same_bytes_every_time_and_on_any_thread()
    {
        var first = CoreSample();
        var second = CoreSample();
        byte[]? onThread = null;
        var thread = new Thread(() => onThread = CoreSample()) { CurrentCulture = new CultureInfo("de-DE") }; // Comma decimals.
        thread.Start();
        thread.Join();
        var parallel = Enumerable.Range(0, 4).AsParallel().Select(_ => InCulture("fa-IR", () => CoreSample())).ToList(); // Other digits.

        Assert.Equal(first, second);
        Assert.Equal(first, onThread);
        Assert.All(parallel, bytes => Assert.Equal(first, bytes));
        Write("core.pdf", first);
    }

    [Fact]
    public void Writing_page_by_page_gives_the_same_bytes_every_time()
    {
        var first = StreamedSample();

        Assert.Equal(first, StreamedSample());
        Write("streamed.pdf", first);
    }

    [Fact]
    public async Task Html_conversion_gives_the_same_bytes_every_time()
    {
        var first = await HtmlSample();
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        byte[] second;
        try
        {
            second = await HtmlSample();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }


        Assert.Equal(first, second);
        Assert.Contains("/CreationDate (D:20260304050607+01'00')", Encoding.Latin1.GetString(first), StringComparison.Ordinal);
        Write("html.pdf", first);
    }

    [Fact]
    public void The_clock_dates_the_document_unless_its_dates_are_set()
    {
        static string Saved(Action<PdfDocument> setUp)
        {
            var doc = new PdfDocument { CompressContent = false, IncludeXmpMetadata = true };
            doc.AddPage();
            doc.Attachments.Add(new PdfAttachment("data.txt", "x"u8, "text/plain"));
            setUp(doc);
            return Encoding.Latin1.GetString(doc.ToArray());
        }

        var clocked = Saved(doc => doc.TimeProvider = new FixedClock(Moment));
        var dated = Saved(doc =>
        {
            doc.TimeProvider = new FixedClock(Moment);
            doc.Info.CreationDate = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
        });

        Assert.Contains("/CreationDate (D:20260304050607+01'00')", clocked, StringComparison.Ordinal);
        Assert.Contains("/ModDate (D:20260304050607+01'00')", clocked, StringComparison.Ordinal);
        Assert.Contains("<xmp:CreateDate>2026-03-04T05:06:07+01:00</xmp:CreateDate>", clocked, StringComparison.Ordinal);
        Assert.Contains("/Params <</Size 1/ModDate (D:20260304050607+01'00')", clocked, StringComparison.Ordinal); // The attachment.
        Assert.Contains("/CreationDate (D:20010203040506+00'00')", dated, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", dated, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new PdfDocument().TimeProvider = null!);
    }

    [Fact]
    public void Different_clocks_give_different_bytes()
    {
        Assert.NotEqual(CoreSample(), CoreSample(Moment.AddSeconds(1)));
    }

    /// <summary>A tagged PDF/UA document with embedded fonts, an image, paths, a transparency group, a link, an outline and an attachment.</summary>
    internal static byte[] CoreSample(DateTimeOffset? moment = null)
    {
        var doc = new PdfDocument
        {
            PdfUA = true,
            Language = "da-DK",
            TimeProvider = new FixedClock(moment ?? Moment),
            Info = { Title = "Budgetopfølgning", Author = "RaiCore" },
        };
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var page = doc.AddPage(new PdfPageSize(841.89, 595.28));
        var canvas = page.Canvas;
        var heading = doc.StructureRoot.AddChild(PdfStructureTypes.Heading1);
        canvas.BeginTag(heading).DrawText("Budgetopfølgning æøå − … «» · –", font, 20, 40, 540).EndTag();
        var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        figure.AlternateText = "Et billede og en kurve";
        canvas.BeginTag(figure)
            .DrawImage(PdfImage.Load(Fixtures.Path("Images/rgb.jpg")), 40, 300, 120, 80)
            .SetStrokeColor(new PdfColor(0, 0.4, 0.8)).SetLineWidth(2).SetDash([4, 2])
            .MoveTo(200, 300).CurveTo(260, 420, 320, 240, 380, 360).Stroke()
            .BeginGroup(0.5).SetFillColor(new PdfColor(1, 0.8, 0)).Rectangle(400, 300, 60, 60).Fill().EndGroup()
            .EndTag();
        var link = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph).AddChild(PdfStructureTypes.Link);
        canvas.BeginTag(link).DrawText("raicore", font, 11, 40, 260).EndTag();
        page.AddLink(40, 256, 50, 14, new Uri("https://example.com/"));
        canvas.BeginArtifact(PdfArtifactKind.Pagination).DrawText("Side 1 af 2", font, 9, 760, 20).EndTag();
        var second = doc.AddPage();
        second.Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("Anden side", font, 12, 72, 760).EndTag();
        doc.Outline.Add(new PdfOutlineItem("Start", page, 595));
        doc.Outline.Add(new PdfOutlineItem("Side 2", second, 842));
        doc.Attachments.Add(new PdfAttachment("data.csv", "a;b\n1;2\n"u8, "text/csv"));
        return doc.ToArray();
    }

    /// <summary>A flow document with a table and repeated headers, written page by page.</summary>
    private static byte[] StreamedSample()
    {
        var style = new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);
        var flow = new FlowDocument(PageSetup.A4);
        var table = new TableBlock().Add(new TableRow(new TableCell(new Paragraph(style).Add("Konto")), new TableCell(new Paragraph(style).Add("Beløb"))) { IsHeader = true });
        for (var i = 1; i <= 120; i++)
        {
            table.Add(new TableRow(new TableCell(new Paragraph(style).Add($"Konto {i}")), new TableCell(new Paragraph(style).Add((i * 1234).ToString("N0", CultureInfo.InvariantCulture)))));
        }

        flow.Add(new ParagraphBlock(new Paragraph(style).Add("Oversigt")) { Tag = StructureTag.Heading(1) });
        flow.Add(table);
        flow.PageDecorator = (canvas, info) => canvas.DrawText($"Side {info.Number} af {info.Count}", style.Fonts[0], 8, 500, 20);
        var doc = new PdfDocument { Tagged = true, Language = "da", TimeProvider = new FixedClock(Moment), Info = { Title = "Streamed" } };
        using var output = new MemoryStream();
        doc.BeginSave(output);
        flow.RenderTo(doc);
        doc.EndSave();
        return output.ToArray();
    }

    /// <summary>An HTML document with a web of features (gradients, rounded borders, an SVG image, a table), converted page by page.</summary>
    private static async Task<byte[]> HtmlSample()
    {
        const string Svg = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='120' height='60'%3E%3Crect width='120' height='60' rx='8' fill='teal'/%3E%3C/svg%3E";
        var rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>Konto {i}</td><td>{i * 37}</td></tr>"));
        var html = $$"""
            <html lang="da"><head><title>Rapport</title><style>
            body { font-family: sans-serif }
            .card { background: linear-gradient(90deg, #fde, #def); border: 2px solid #888; border-radius: 8px; padding: 8px; opacity: .9 }
            td, th { border: 1px solid #aaa; padding: 2px 6px }
            </style></head><body>
            <h1>Rapport</h1><div class="card"><p>Indledning med <b>fed</b> og <i>kursiv</i> tekst.</p><img alt="Figur" src="{{Svg}}"></div>
            <table><thead><tr><th>Konto</th><th>Bel&oslash;b</th></tr></thead><tbody>{{rows}}</tbody></table>
            </body></html>
            """;
        using var output = new MemoryStream();
        var options = new HtmlConverterOptions { PdfUA = true, TimeProvider = new FixedClock(Moment) };
        await HtmlToPdf.ConvertToStreamAsync(html, output, options, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static T InCulture<T>(string name, Func<T> action)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
        try
        {
            return action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui);
        }
    }

    /// <summary>Writes a sample to the folder in RAICORE_DETERMINISM_OUTPUT, if set, with its SHA-256 beside it.</summary>
    private static void Write(string name, byte[] bytes)
    {
        if (Environment.GetEnvironmentVariable("RAICORE_DETERMINISM_OUTPUT") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
        File.WriteAllText(Path.Combine(folder, name + ".sha256"), Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\n");
    }

    /// <summary>A clock stopped at one moment, in a time zone with that moment's offset.</summary>
    internal sealed class FixedClock(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("Fixed", moment.Offset, "Fixed", "Fixed");
    }
}
