using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>CSS Nesting (#149): nested rules are flattened before the style sheet is parsed.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class CssNestingTests
{
    [Theory]
    [InlineData("a { color: red; b { color: blue } }", "a { color: red;}\na b { color: blue;}\n")]
    [InlineData("a { &:hover { x: 1 } &.on { x: 2 } & + b { x: 3 } }", "a:hover { x: 1;}\na.on { x: 2;}\na + b { x: 3;}\n")]
    [InlineData("a { > b { x: 1 } }", "a > b { x: 1;}\n")]
    [InlineData("a, b { c, d { x: 1 } }", "a c, b c, a d, b d { x: 1;}\n")]
    [InlineData("a { .p & { x: 1 } }", ".p a { x: 1;}\n")]
    [InlineData("a { b { c { x: 1 } } }", "a b c { x: 1;}\n")]
    [InlineData("a { x: 1; b { y: 2 } z: 3 }", "a { x: 1; z: 3;}\na b { y: 2;}\n")]
    [InlineData("a { @media print { x: 1; b { y: 2 } } }", "@media print {\na { x: 1;}\na b { y: 2;}\n}\n")]
    [InlineData("@media print { a { b { x: 1 } } }", "@media print {\na b { x: 1;}\n}\n")]
    [InlineData("a { :is(b, c) & { x: 1 } }", ":is(b, c) a { x: 1;}\n")]
    [InlineData("a { b[title=\"{ ; }\"] { x: 1 } }", "a b[title=\"{ ; }\"] { x: 1;}\n")]
    [InlineData("a { /* { */ b { x: 1 } }", "a b { x: 1;}\n")]
    public void Nested_rules_become_flat_rules(string css, string flat) =>
        Assert.Equal(flat, CssNesting.Flatten(css));

    [Theory]
    [InlineData("a { color: red } b { color: blue }")]
    [InlineData("color: red; margin: 0")]
    [InlineData("@page { @top-left { content: 'x' } } a { b: c }")]
    [InlineData("@media print { a { x: 1 } }")]
    [InlineData("a { content: '{ b { } }' }")]
    [InlineData("")]
    public void Sheets_without_nested_rules_are_unchanged(string css) =>
        Assert.Same(css, CssNesting.Flatten(css));

    [Fact]
    public void Other_at_rules_keep_their_blocks()
    {
        var flat = CssNesting.Flatten("@page { margin: 1cm; @top-left { content: 'x' } } @font-face { font-family: F } a { b { x: 1 } }");

        Assert.Equal("@page { margin: 1cm; @top-left { content: 'x' } }\n@font-face { font-family: F }\na b { x: 1;}\n", flat);
    }

    [Theory]
    [InlineData("a { b { ")]
    [InlineData("a { b { x: 1")]
    [InlineData("a { & { } } }}} {{{")]
    [InlineData("a { --x: { y: 1 }; b { z: 2 } }")]
    [InlineData("a { @unknown { x: 1 } b { y: 2 } }")]
    public void Broken_or_odd_sheets_do_not_throw(string css) =>
        Assert.NotNull(CssNesting.Flatten(css));

    [Fact]
    public void Deep_nesting_is_cut_off()
    {
        var css = string.Concat(Enumerable.Repeat("a { ", 5000)) + "x: 1" + new string('}', 5000);

        Assert.DoesNotContain("x: 1", CssNesting.Flatten(css), StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_rules_style_the_document()
    {
        var pdf = HtmlToPdf.ConvertToBytes("""
            <style>
              @page { size: 200pt 100pt; margin: 0 } body { margin: 0 }
              section { div { height: 20pt; background: #ff0000; &.on { background: #00ff00 } } }
            </style>
            <section><div></div><div class="on"></div></section>
            """);
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 10));
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(100, 30));
    }

    [Fact]
    public void Nested_rules_are_not_reported_as_unsupported()
    {
        var diagnostics = new List<HtmlDiagnostic>();
        HtmlToPdf.ConvertToBytes(
            "<style>aside { address { color: red } &#from { color: blue } }</style><aside><address>x</address></aside>",
            new HtmlConverterOptions { Diagnostics = diagnostics.Add });

        Assert.Empty(diagnostics);
    }
}
