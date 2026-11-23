using RaiCore.Pdf.Html.Diagnostics;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>
/// HTML fragments (see <see cref="HtmlFragment"/>): their blocks go in a document whose pages are made elsewhere, so
/// what only pages have is left out and reported.
/// </summary>
internal sealed partial class PagedMedia
{
    /// <summary>
    /// Leaves out running elements, running strings and page names, and reports them and the <c>@page</c> rules (which
    /// only margin boxes read, and a fragment has none), each kind once at the elements it concerns. Bookmarks and
    /// generated content stay.
    /// </summary>
    public void RestrictToFragment(DiagnosticCollector diagnostics)
    {
        if (_pageRules.Count > 0)
        {
            diagnostics.Report(
                HtmlDiagnosticKind.UnsupportedRule,
                HtmlDiagnosticSeverity.Warning,
                "@page",
                "@page rules (page size, margins, headers and footers) have no effect in an HTML fragment: its pages are those of the document it is placed in.");
        }

        foreach (var element in _running.Keys)
        {
            diagnostics.Report(
                HtmlDiagnosticKind.UnsupportedValue,
                HtmlDiagnosticSeverity.Warning,
                "position: running()",
                "Running elements need page margin boxes, which an HTML fragment has not: the element stays in the flow.",
                element);
        }

        foreach (var element in _stringSets.Keys)
        {
            diagnostics.Report(
                HtmlDiagnosticKind.UnsupportedProperty,
                HtmlDiagnosticSeverity.Warning,
                "string-set",
                "Running strings need page margin boxes, which an HTML fragment has not: string-set is ignored.",
                element);
        }

        foreach (var (element, declaration) in _pageNames)
        {
            if (declaration.Name is not null)
            {
                diagnostics.Report(
                    HtmlDiagnosticKind.UnsupportedProperty,
                    HtmlDiagnosticSeverity.Warning,
                    "page",
                    "Named pages have no effect in an HTML fragment: its pages are those of the document it is placed in.",
                    element);
            }
        }

        _running.Clear();
        _stringSets.Clear();
        _pageNames.Clear();
    }
}
