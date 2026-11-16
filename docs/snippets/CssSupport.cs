#pragma warning disable CA1849, CA2007
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class CssSupport
{
    public static Task RunAsync()
    {
        var html = """<div style="display: grid; box-shadow: 0 0 4px gray">Grid</div><img src="missing.png">""";

        #region diagnostics
        var issues = new List<HtmlDiagnostic>();
        var pdf = HtmlToPdf.Convert(html, new HtmlConverterOptions { Diagnostics = issues.Add });
        foreach (var issue in issues)
        {
            Console.WriteLine($"{issue.Severity} {issue.Kind}: {issue.Subject} at {issue.ElementPath} ({issue.Count}x): {issue.Message}");
        }
        #endregion

        pdf.Save("diagnostics.pdf");
        return Task.CompletedTask;
    }
}
