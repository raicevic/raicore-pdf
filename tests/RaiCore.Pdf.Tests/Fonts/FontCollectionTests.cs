using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Fonts;

public class FontCollectionTests
{
    private static readonly FontCollection Bundled = BundledFonts.CreateCollection();

    [Theory]
    [InlineData("Arial", 400, false, "LiberationSans")]
    [InlineData("Helvetica", 700, false, "LiberationSans-Bold")]
    [InlineData("Times New Roman", 700, true, "LiberationSerif-BoldItalic")]
    [InlineData("courier new", 400, true, "LiberationMono-Italic")]
    [InlineData("sans-serif", 400, false, "LiberationSans")]
    [InlineData("serif", 400, false, "LiberationSerif")]
    [InlineData("monospace", 700, false, "LiberationMono-Bold")]
    [InlineData("Liberation Sans", 900, false, "LiberationSans-Bold")]
    [InlineData("Liberation Sans", 100, false, "LiberationSans")]
    public void Finds_bundled_fonts_and_substitutes(string family, int weight, bool italic, string expected)
    {
        var font = Bundled.Find(family, weight, italic);

        Assert.NotNull(font);
        Assert.Equal(expected, font.PostScriptName);
    }

    [Fact]
    public void Uses_the_first_available_family_in_a_fallback_list() =>
        Assert.Equal("LiberationSerif", Bundled.Find(["Missing Font", "'Another Missing'", "serif"])?.PostScriptName);

    [Fact]
    public void Returns_null_for_unknown_families() => Assert.Null(Bundled.Find("No Such Font"));

    [Fact]
    public void Lists_families() =>
        Assert.Equal(["Liberation Mono", "Liberation Sans", "Liberation Serif"], Bundled.Families);

    [Fact]
    public void Loads_each_face_once_even_when_used_concurrently()
    {
        var collection = BundledFonts.CreateCollection();
        var fonts = Enumerable.Range(0, 32).AsParallel().Select(_ => collection.Find("Liberation Sans")).ToList();

        Assert.All(fonts, f => Assert.Same(fonts[0], f));
    }

    [Fact]
    public void Indexes_collection_files()
    {
        var collection = new FontCollection().AddFile(Fixtures.Path("RaiCoreTestTT.ttc"));

        Assert.Equal(2, collection.Count);
        Assert.Equal("RaiCoreTestTT-Bold", collection.Find("RaiCore Test TT", 700)?.PostScriptName);
        Assert.Equal("RaiCoreTestTT-Regular", collection.Find("RaiCore Test TT", 400, italic: true)?.PostScriptName);
    }

    [Fact]
    public void Custom_substitutes_take_precedence()
    {
        var collection = BundledFonts.CreateCollection().SetSubstitutes("Calibri", "Liberation Serif");

        Assert.Equal("LiberationSerif", collection.Find("Calibri")?.PostScriptName);
    }

    [Fact]
    public void Scanning_system_fonts_skips_unreadable_files()
    {
        var collection = new FontCollection().AddSystemFonts();

        Assert.True(collection.Count >= 0);
        if (collection.Find("sans-serif") is { } font)
        {
            Assert.True(font.MeasureText("Hello", 12) > 0);
        }
    }

    [Theory]
    [InlineData(400, new[] { 300, 500, 700 }, 500)] // 400 tries 500 first.
    [InlineData(500, new[] { 300, 400, 700 }, 400)] // Then lighter...
    [InlineData(450, new[] { 300, 700 }, 300)] // ...and only then heavier than 500.
    [InlineData(600, new[] { 300, 400, 700, 900 }, 700)] // Heavier than 500 searches upward first.
    [InlineData(600, new[] { 300, 400 }, 400)]
    [InlineData(300, new[] { 200, 400 }, 200)] // Lighter than 400 searches downward first.
    [InlineData(300, new[] { 400, 700 }, 400)]
    public void Matches_weights_like_css(int requested, int[] available, int expected) =>
        Assert.Equal(expected, FontMatcher.Match(available, w => w, _ => false, requested, italic: false));

    [Fact]
    public void Prefers_the_requested_style_over_weight()
    {
        (int Weight, bool Italic)[] faces = [(400, false), (700, true)];

        Assert.Equal((700, true), FontMatcher.Match(faces, f => f.Weight, f => f.Italic, 400, italic: true));
        Assert.Equal((400, false), FontMatcher.Match(faces, f => f.Weight, f => f.Italic, 700, italic: false));
    }
}
