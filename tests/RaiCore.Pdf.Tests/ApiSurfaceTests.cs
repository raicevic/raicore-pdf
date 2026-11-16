using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// What the public API promises (docs/api-review.md, section 5): which APIs are experimental, and which types users
/// cannot create or see.
/// </summary>
public class ApiSurfaceTests
{
    public static TheoryData<string, string> ExperimentalApis => new()
    {
        { "T:" + typeof(PdfObject).FullName, "RCPDF001" },
        { "T:" + typeof(PdfDictionary).FullName, "RCPDF001" },
        { "T:" + typeof(PdfStreamObject).FullName, "RCPDF001" },
        { "M:" + typeof(PdfReader).FullName + ".Catalog", "RCPDF001" },
        { "M:" + typeof(PdfReader).FullName + ".Trailer", "RCPDF001" },
        { "M:" + typeof(PdfReader).FullName + ".Resolve", "RCPDF001" },
        { "M:" + typeof(PdfReaderPage).FullName + ".Dictionary", "RCPDF001" },
        { "M:" + typeof(PdfReaderPage).FullName + ".Resources", "RCPDF001" },
        { "M:" + typeof(PdfReaderPage).FullName + ".GetContent", "RCPDF001" },
        { "T:" + typeof(TextShaper).FullName, "RCPDF002" },
        { "T:" + typeof(ShapedText).FullName, "RCPDF002" },
        { "T:" + typeof(ShapedRun).FullName, "RCPDF002" },
        { "T:" + typeof(ShapedTextCanvasExtensions).FullName, "RCPDF002" },
        { "T:" + typeof(LineBreaker).FullName, "RCPDF002" },
        { "T:" + typeof(LineBreakOpportunity).FullName, "RCPDF002" },
        { "T:" + typeof(PositionedGlyph).FullName, "RCPDF002" },
        { "M:" + typeof(PdfCanvas).FullName + ".DrawGlyphs", "RCPDF002" },
        { "M:" + typeof(ShapingOptions).FullName + ".Features", "RCPDF002" },
    };

    [Theory]
    [MemberData(nameof(ExperimentalApis))]
    public void Experimental_apis_carry_their_diagnostic_id(string api, string id)
    {
        var attribute = Attribute(api);

        Assert.NotNull(attribute);
        Assert.Equal(id, attribute.DiagnosticId);
        Assert.Equal($"https://github.com/raicevic/raicore-pdf/blob/main/docs/experimental.md#{id}", string.Format(System.Globalization.CultureInfo.InvariantCulture, attribute.UrlFormat!, id));
    }

    [Fact]
    public void The_rest_of_the_reading_and_drawing_api_is_not_experimental()
    {
        Assert.Null(Attribute("T:" + typeof(PdfReader).FullName));
        Assert.Null(Attribute("M:" + typeof(PdfReader).FullName + ".Pages"));
        Assert.Null(Attribute("T:" + typeof(ShapingOptions).FullName));
        Assert.Null(Attribute("M:" + typeof(PdfCanvas).FullName + ".DrawText"));
    }

    [Theory]
    [InlineData(typeof(ParagraphSpan))]
    [InlineData(typeof(InlineDecoration))]
    [InlineData(typeof(InlineLink))]
    [InlineData(typeof(InlineAnchor))]
    [InlineData(typeof(InlineTag))]
    public void A_paragraphs_content_records_are_internal(Type type) => Assert.False(type.IsPublic);

    [Fact]
    public void A_paragraph_exposes_no_content_collections()
    {
        var names = typeof(Paragraph).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

        Assert.DoesNotContain("Spans", names);
        Assert.DoesNotContain("Decorations", names);
        Assert.DoesNotContain("Links", names);
        Assert.DoesNotContain("Anchors", names);
        Assert.DoesNotContain("Tags", names);
    }

    [Theory]
    [InlineData(typeof(InlineLinkFragment))]
    [InlineData(typeof(InlineDecorationFragment))]
    [InlineData(typeof(InlineImageFragment))]
    [InlineData(typeof(InlineBoxFragment))]
    [InlineData(typeof(LineFragment))]
    public void Layout_results_cannot_be_created_by_users(Type type) =>
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

    /// <summary>The <see cref="ExperimentalAttribute"/> of a type (<c>T:</c>) or member (<c>M:</c>, any overload).</summary>
    private static ExperimentalAttribute? Attribute(string api)
    {
        var name = api[2..];
        if (api.StartsWith("T:", StringComparison.Ordinal))
        {
            return Type(name).GetCustomAttribute<ExperimentalAttribute>();
        }

        var dot = name.LastIndexOf('.');
        var members = Type(name[..dot]).GetMember(name[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        Assert.NotEmpty(members);
        return members.Select(m => m.GetCustomAttribute<ExperimentalAttribute>()).FirstOrDefault(a => a is not null);
    }

    private static Type Type(string fullName) =>
        new[] { typeof(PdfDocument).Assembly, typeof(Paragraph).Assembly }.Select(a => a.GetType(fullName)).First(t => t is not null)!;
}
