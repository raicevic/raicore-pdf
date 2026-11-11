using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

public class HyphenatorTests
{
    [Theory]
    [InlineData("en-us", "hyphenation", "hy-phen-ation")]
    [InlineData("en-us", "justification", "jus-ti-fi-ca-tion")]
    [InlineData("en-us", "Typography", "Ty-pog-ra-phy")]
    [InlineData("en-us", "computer", "com-puter")] // "er" is shorter than the 3 letters English keeps after a hyphen.
    [InlineData("en-gb", "hyphenation", "hy-phen-a-tion")]
    [InlineData("de", "Silbentrennung", "Sil-ben-tren-nung")]
    [InlineData("de", "Donaudampfschifffahrt", "Do-nau-dampf-schiff-fahrt")]
    [InlineData("da", "sammens\u00E6tning", "sam-men-s\u00E6t-ning")]
    [InlineData("nl", "woordafbreking", "woord-af-bre-king")]
    [InlineData("fr", "typographie", "ty-po-gra-phie")]
    [InlineData("fr", "l\u2019ambassadeur", "l\u2019am-bas-sa-deur")]
    [InlineData("es", "desarrollo", "de-sa-rro-llo")]
    [InlineData("it", "informazione", "in-for-ma-zio-ne")]
    [InlineData("pt", "informa\u00E7\u00E3o", "in-for-ma-\u00E7\u00E3o")]
    [InlineData("pl", "przedsi\u0119biorstwo", "przed-si\u0119-bior-stwo")]
    [InlineData("nb", "utvikling", "ut-vik-ling")]
    public void Bundled_patterns_hyphenate_like_TeX(string language, string word, string expected) =>
        Assert.Equal(expected, Hyphenated(Hyphenator.ForLanguage(language)!, word));

    [Fact]
    public void Every_bundled_language_loads()
    {
        Assert.All(Hyphenator.BundledLanguages, language =>
        {
            var hyphenator = Hyphenator.ForLanguage(language);
            Assert.NotNull(hyphenator);
            Assert.InRange(hyphenator.MinBefore, 1, 3);
            Assert.InRange(hyphenator.MinAfter, 1, 3);
        });
    }

    [Fact]
    public void Exceptions_win_over_patterns_in_any_case()
    {
        var english = Hyphenator.ForLanguage("en-us")!;

        Assert.Equal("ta-ble", Hyphenated(english, "table"));
        Assert.Equal("Ta-ble", Hyphenated(english, "Table"));
        Assert.Empty(english.Hyphenate("present")); // Listed without hyphens: never hyphenated.
        Assert.Empty(english.Hyphenate("PRESENT"));
    }

    [Fact]
    public void Nynorsk_shares_the_Bokmal_patterns_but_has_its_own_exceptions()
    {
        Assert.Equal("at-ten-de", Hyphenated(Hyphenator.ForLanguage("nb")!, "attende"));
        Assert.Equal("att-en-de", Hyphenated(Hyphenator.ForLanguage("nn")!, "attende"));
        Assert.Equal(Hyphenated(Hyphenator.ForLanguage("nb")!, "utvikling"), Hyphenated(Hyphenator.ForLanguage("nn")!, "utvikling"));
    }

    [Fact]
    public void Minimum_fragment_lengths_drop_points_near_the_ends()
    {
        var english = Hyphenator.ForLanguage("en-us")!;

        Assert.Equal([2, 6], english.Hyphenate("hyphenation", 2, 3));
        Assert.Equal([6], english.Hyphenate("hyphenation", 3, 3));
        Assert.Equal([2], english.Hyphenate("hyphenation", 2, 6));
        Assert.Equal([2, 6], english.Hyphenate("hyphenation", 0, 0)); // At least one character on each side.
        Assert.Empty(english.Hyphenate("hyphenation", 6, 6)); // Shorter than both limits together.
    }

    [Fact]
    public void The_highest_vote_decides_and_only_odd_votes_allow_a_hyphen()
    {
        Assert.Equal([3], Hyphenator.FromPatterns(["a1b"]).Hyphenate("xxabxx", 1, 1));
        Assert.Empty(Hyphenator.FromPatterns(["a1b", "2b"]).Hyphenate("xxabxx", 1, 1));
        Assert.Equal([3], Hyphenator.FromPatterns(["a1b", "2b", "xa3b"]).Hyphenate("xxabxx", 1, 1));
        Assert.Empty(Hyphenator.FromPatterns(["a1b", ".xxa2"]).Hyphenate("xxabxx", 1, 1)); // ".": the start of the word.
        Assert.Equal([4], Hyphenator.FromPatterns(["a1b", ".xxa2"]).Hyphenate("yxxabxx", 1, 1));
        Assert.Empty(Hyphenator.FromPatterns(["a1b", "a2bxx."]).Hyphenate("xxabxx", 1, 1)); // ...and its end.
        Assert.Equal([3], Hyphenator.FromPatterns(["a1b", "a2bxx."]).Hyphenate("xxabxxy", 1, 1));
        Assert.Equal([2, 3, 4], Hyphenator.FromPatterns(["1a", "1b", "1x"]).Hyphenate("xxabxx", 2, 2));
    }

    [Fact]
    public void Custom_exceptions_and_patterns_are_case_insensitive()
    {
        var hyphenator = Hyphenator.FromPatterns(["A1B"], ["Hyph-en-ate", "nothing"], minBefore: 1, minAfter: 1);

        Assert.Equal([3], hyphenator.Hyphenate("xxABxx"));
        Assert.Equal([4, 6], hyphenator.Hyphenate("HYPHENATE"));
        Assert.Empty(hyphenator.Hyphenate("nothing"));
    }

    [Fact]
    public void Never_separates_a_combining_mark_from_its_letter()
    {
        var hyphenator = Hyphenator.FromPatterns(["e1", "1c"], minBefore: 1, minAfter: 1);

        Assert.Equal([3], hyphenator.Hyphenate("abecd"));
        Assert.Equal([4], hyphenator.Hyphenate("abe\u0301cd")); // After the mark, never before it.
        Assert.Empty(Hyphenator.FromPatterns(["e1"]).Hyphenate("abe\u0301cd", 1, 1));
    }

    [Theory]
    [InlineData("en-US", "en-us")]
    [InlineData("EN_us", "en-us")]
    [InlineData("en", "en-us")]
    [InlineData("en-Latn-US", "en-us")]
    [InlineData("en-AU", "en-gb")]
    [InlineData("en-GB-oxendict", "en-gb")]
    [InlineData("de-CH", "de")]
    [InlineData("de-1996", "de")]
    [InlineData("no", "nb")]
    [InlineData("nb-NO", "nb")]
    [InlineData("pt-BR", "pt")]
    public void Language_tags_fall_back_to_shorter_ones(string tag, string bundled) =>
        Assert.Same(Hyphenator.ForLanguage(bundled), Hyphenator.ForLanguage(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sv")] // Its patterns are LPPL-only, not bundled.
    [InlineData("zh-Hans")]
    public void Languages_without_patterns_have_no_hyphenator(string? tag) => Assert.Null(Hyphenator.ForLanguage(tag));

    [Fact]
    public void Registered_hyphenators_take_precedence_and_can_be_removed()
    {
        var custom = Hyphenator.FromPatterns(["1b"]);
        try
        {
            Hyphenator.Register("x-test", custom);
            Hyphenator.Register("en-x-custom", custom);

            Assert.Same(custom, Hyphenator.ForLanguage("x-test-region"));
            Assert.Same(custom, Hyphenator.ForLanguage("en-X-Custom"));
            Assert.Same(Hyphenator.ForLanguage("en-us"), Hyphenator.ForLanguage("en-x-other"));
        }
        finally
        {
            Hyphenator.Register("x-test", null);
            Hyphenator.Register("en-x-custom", null);
        }

        Assert.Null(Hyphenator.ForLanguage("x-test"));
    }

    private static string Hyphenated(Hyphenator hyphenator, string word)
    {
        var result = word;
        var points = hyphenator.Hyphenate(word);
        for (var i = points.Count - 1; i >= 0; i--)
        {
            result = result.Insert(points[i], "-");
        }

        return result;
    }
}
