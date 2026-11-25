namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Default styles for HTML elements, following the "Rendering" section of the HTML standard (the defaults browsers use).
/// It is added before the document's own styles, and every selector is wrapped in <c>:where()</c>, which has no
/// specificity: any author rule overrides it, as browsers' own styles are (the HTML standard's <c>a:link</c> would
/// otherwise beat an author's <c>a</c>). The universal rule keeps the custom properties LogicalSides adds from being
/// inherited.
/// </summary>
internal static class UserAgentStyleSheet
{
    public const string Css = """
        :where(*) { --raicore-rtl-margin-left: initial; --raicore-rtl-margin-right: initial; --raicore-rtl-padding-left: initial; --raicore-rtl-padding-right: initial; }
        :where(html) { display: block; font-family: serif; font-size: 16px; line-height: normal; color: black; }
        :where(address, blockquote, center, dialog, div, figure, figcaption, footer, form, header, hr, legend, listing,
        main, p, plaintext, pre, search, xmp, article, aside, h1, h2, h3, h4, h5, h6, hgroup, nav, section,
        dl, dt, dd, ol, ul, menu, body, details, summary, fieldset) { display: block; }
        :where(head, script, style, template, title, meta, link, base, noscript, datalist, param, area, map) { display: none; }
        :where([hidden]) { display: none; }
        :where([dir="ltr"]) { direction: ltr; }
        :where([dir="rtl"]) { direction: rtl; }
        :where([dir], bdi) { unicode-bidi: isolate; }
        :where(bdo, bdo[dir]) { unicode-bidi: bidi-override; }
        :where(li) { display: list-item; }
        :where(table) { display: table; border-collapse: separate; border-spacing: 2px; box-sizing: border-box; }
        :where(caption) { display: table-caption; text-align: center; }
        :where(thead, tbody, tfoot) { display: table-row-group; vertical-align: middle; }
        :where(tr) { display: table-row; vertical-align: inherit; }
        :where(td, th) { display: table-cell; padding: 1px; vertical-align: inherit; }
        :where(th) { font-weight: bold; text-align: center; }

        :where(body) { margin: 8px; }
        :where(p, dl, multicol, pre, listing, xmp, plaintext) { margin-top: 1em; margin-bottom: 1em; }
        :where(blockquote, figure) { margin: 1em 40px; }
        :where(dd) { margin-inline-start: 40px; }
        :where(ol, ul, menu) { margin-top: 1em; margin-bottom: 1em; padding-inline-start: 40px; }
        :where(ol ol, ol ul, ul ol, ul ul, menu ul, menu ol, ul menu, ol menu) { margin-top: 0; margin-bottom: 0; }
        :where(ul, menu) { list-style-type: disc; }
        :where(ol) { list-style-type: decimal; }
        :where(ul ul, ol ul, menu ul) { list-style-type: circle; }
        :where(ul ul ul, ul ol ul, ol ul ul, ol ol ul) { list-style-type: square; }

        :where(h1) { font-size: 2em; margin-top: 0.67em; margin-bottom: 0.67em; }
        :where(h2) { font-size: 1.5em; margin-top: 0.83em; margin-bottom: 0.83em; }
        :where(h3) { font-size: 1.17em; margin-top: 1em; margin-bottom: 1em; }
        :where(h4) { font-size: 1em; margin-top: 1.33em; margin-bottom: 1.33em; }
        :where(h5) { font-size: 0.83em; margin-top: 1.67em; margin-bottom: 1.67em; }
        :where(h6) { font-size: 0.67em; margin-top: 2.33em; margin-bottom: 2.33em; }
        :where(h1, h2, h3, h4, h5, h6) { font-weight: bold; break-after: avoid; }
        :where(h1) { bookmark-level: 1; } :where(h2) { bookmark-level: 2; } :where(h3) { bookmark-level: 3; }
        :where(h4) { bookmark-level: 4; } :where(h5) { bookmark-level: 5; } :where(h6) { bookmark-level: 6; }

        :where(b, strong) { font-weight: bold; }
        :where(i, em, cite, var, dfn, address) { font-style: italic; }
        :where(pre, code, kbd, samp, tt, listing, xmp, plaintext) { font-family: monospace; }
        :where(pre, listing, xmp, plaintext) { white-space: pre; }
        :where(small) { font-size: smaller; }
        :where(big) { font-size: larger; }
        :where(sub, sup) { font-size: smaller; }
        :where(sub) { vertical-align: sub; }
        :where(sup) { vertical-align: super; }
        :where(center) { text-align: -webkit-center; }
        :where(a:link, a:visited, a) { color: #0000EE; }
        :where(mark) { color: black; }
        :where(q)::before { content: open-quote; }
        :where(q)::after { content: close-quote; }
        :where(hr) { border-style: solid; border-width: 1px 0 0 0; border-color: gray; margin: 0.5em 0; }
        """;
}
