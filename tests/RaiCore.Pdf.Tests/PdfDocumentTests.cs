using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests;

public partial class PdfDocumentTests
{
    internal static PdfDocument CreateSample(bool compress = true)
    {
        var doc = new PdfDocument { CompressContent = compress };
        doc.Info.Title = "Prøve – sample";
        doc.Info.CreationDate = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(1));

        var page = doc.AddPage(PdfPageSize.A4);
        page.Canvas
            .SetFillColor(PdfColor.FromRgb(220, 30, 30))
            .Rectangle(72, 600, 200, 100)
            .Fill()
            .SetFillColor(PdfColor.Black)
            .DrawText("Hello from RaiCore.Pdf", StandardFont.Helvetica, 24, 72, 760)
            .DrawText("Blåbærgrød koster 25 €", StandardFont.TimesBold, 14, 72, 730);

        doc.AddPage(PdfPageSize.Letter.Landscape).Canvas
            .DrawText("Page 2", StandardFont.Courier, 12, 72, 500);
        return doc;
    }

    [Fact]
    public void Output_has_header_and_trailer()
    {
        var text = Encoding.Latin1.GetString(CreateSample().ToArray());

        Assert.StartsWith("%PDF-1.7\n", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Count 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Xref_offsets_point_at_their_objects()
    {
        var bytes = CreateSample().ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        var startXref = int.Parse(StartXrefRegex().Match(text).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", text[startXref..], StringComparison.Ordinal);

        var entries = XrefEntryRegex().Matches(text[startXref..]);
        Assert.NotEmpty(entries);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i + 1} 0 obj", text[offset..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Output_is_deterministic_when_creation_date_is_set() =>
        Assert.Equal(CreateSample().ToArray(), CreateSample().ToArray());

    [Fact]
    public void Danish_letters_and_euro_are_encoded_as_win_ansi()
    {
        var text = Encoding.Latin1.GetString(CreateSample(compress: false).ToArray());

        // å = 0xE5 (\345), æ = 0xE6 (\346), ø = 0xF8 (\370), € = 0x80 (\200)
        Assert.Contains(@"(Bl\345b\346rgr\370d koster 25 \200)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_without_pages_throws() =>
        Assert.Throws<InvalidOperationException>(() => new PdfDocument().ToArray());

    [Fact]
    public void Passes_qpdf_structural_check()
    {
        var qpdf = FindOnPath("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf is not installed (it runs in CI).");

        var path = Path.Combine(Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.pdf");
        try
        {
            CreateSample().Save(path);
            using var process = Process.Start(new ProcessStartInfo(qpdf!, ["--check", path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("No syntax or stream encoding errors found", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
            .FirstOrDefault(File.Exists);

    [GeneratedRegex(@"startxref\n(\d+)\n%%EOF")]
    private static partial Regex StartXrefRegex();

    [GeneratedRegex(@"(\d{10}) 00000 n\r\n")]
    private static partial Regex XrefEntryRegex();
}
