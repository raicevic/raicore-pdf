using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Tests;

internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static byte[] Read(string name) => File.ReadAllBytes(Path(name));
}

/// <summary>Runs optional command-line validators (qpdf, pdftotext). Tests using them skip when absent; CI installs them.</summary>
internal static class ExternalTools
{
    public static string? Find(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => System.IO.Path.Combine(dir, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
            .FirstOrDefault(File.Exists);

    /// <summary>Writes <paramref name="pdf"/> to a temp file, runs the tool on it and returns (exit code, stdout + stderr).</summary>
    public static (int ExitCode, string Output) RunOnPdf(string tool, byte[] pdf, params string[] argumentsBeforeAndAfter)
    {
        var path = Find(tool);
        Assert.SkipWhen(path is null, $"{tool} is not installed (it runs in CI).");

        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(file, pdf);
            var arguments = argumentsBeforeAndAfter.Select(a => a == "{pdf}" ? file : a).ToArray();
            using var process = Process.Start(new ProcessStartInfo(path!, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Asserts that qpdf finds no structural problems.</summary>
    public static void AssertQpdfClean(byte[] pdf)
    {
        var (exitCode, output) = RunOnPdf("qpdf", pdf, "--check", "{pdf}");
        Assert.True(exitCode == 0, output);
        Assert.Contains("No syntax or stream encoding errors found", output, StringComparison.Ordinal);
    }

    /// <summary>Extracts text with poppler's pdftotext, which relies on the ToUnicode maps.</summary>
    public static string ExtractText(byte[] pdf)
    {
        var (exitCode, output) = RunOnPdf("pdftotext", pdf, "-enc", "UTF-8", "-layout", "{pdf}", "-");
        Assert.True(exitCode == 0, output);
        return Regex.Replace(output, @"[ \t]+", " ");
    }
}
