namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Default styles for HTML elements, following the "Rendering" section of the HTML standard (the defaults browsers use).
/// It is added before the document's own styles, so any author rule overrides it.
/// </summary>
internal static class UserAgentStyleSheet
{
    public const string Css = """
        html { display: block; font-family: serif; font-size: 16px; line-height: normal; color: black; }
        address, blockquote, center, dialog, div, figure, figcaption, footer, form, header, hr, legend, listing,
        main, p, plaintext, pre, search, xmp, article, aside, h1, h2, h3, h4, h5, h6, hgroup, nav, section,
        dl, dt, dd, ol, ul, menu, body, details, summary, fieldset { display: block; }
        head, script, style, template, title, meta, link, base, noscript, datalist, param, area, map { display: none; }
        [hidden] { display: none; }
        [dir="ltr"] { direction: ltr; }
        [dir="rtl"] { direction: rtl; }
        [dir], bdi { unicode-bidi: isolate; }
        bdo, bdo[dir] { unicode-bidi: bidi-override; }
        li { display: list-item; }
        table { display: table; border-collapse: separate; border-spacing: 2px; box-sizing: border-box; }
        caption { display: table-caption; text-align: center; }
        thead, tbody, tfoot { display: table-row-group; vertical-align: middle; }
        tr { display: table-row; vertical-align: inherit; }
        td, th { display: table-cell; padding: 1px; vertical-align: inherit; }
        th { font-weight: bold; text-align: center; }

        body { margin: 8px; }
        p, dl, multicol, pre, listing, xmp, plaintext { margin-top: 1em; margin-bottom: 1em; }
        blockquote, figure { margin: 1em 40px; }
        dd { margin-left: 40px; }
        ol, ul, menu { margin-top: 1em; margin-bottom: 1em; padding-left: 40px; }
        ol ol, ol ul, ul ol, ul ul, menu ul, menu ol, ul menu, ol menu { margin-top: 0; margin-bottom: 0; }
        ul, menu { list-style-type: disc; }
        ol { list-style-type: decimal; }
        ul ul, ol ul, menu ul { list-style-type: circle; }
        ul ul ul, ul ol ul, ol ul ul, ol ol ul { list-style-type: square; }

        h1 { font-size: 2em; margin-top: 0.67em; margin-bottom: 0.67em; }
        h2 { font-size: 1.5em; margin-top: 0.83em; margin-bottom: 0.83em; }
        h3 { font-size: 1.17em; margin-top: 1em; margin-bottom: 1em; }
        h4 { font-size: 1em; margin-top: 1.33em; margin-bottom: 1.33em; }
        h5 { font-size: 0.83em; margin-top: 1.67em; margin-bottom: 1.67em; }
        h6 { font-size: 0.67em; margin-top: 2.33em; margin-bottom: 2.33em; }
        h1, h2, h3, h4, h5, h6 { font-weight: bold; break-after: avoid; }
        h1 { bookmark-level: 1; } h2 { bookmark-level: 2; } h3 { bookmark-level: 3; }
        h4 { bookmark-level: 4; } h5 { bookmark-level: 5; } h6 { bookmark-level: 6; }

        b, strong { font-weight: bold; }
        i, em, cite, var, dfn, address { font-style: italic; }
        pre, code, kbd, samp, tt, listing, xmp, plaintext { font-family: monospace; }
        pre, listing, xmp, plaintext { white-space: pre; }
        small { font-size: smaller; }
        big { font-size: larger; }
        sub, sup { font-size: smaller; }
        sub { vertical-align: sub; }
        sup { vertical-align: super; }
        center { text-align: center; }
        a:link, a:visited, a { color: #0000EE; }
        mark { color: black; }
        q::before { content: open-quote; }
        q::after { content: close-quote; }
        hr { border-style: solid; border-width: 1px 0 0 0; border-color: gray; margin: 0.5em 0; }
        """;
}
