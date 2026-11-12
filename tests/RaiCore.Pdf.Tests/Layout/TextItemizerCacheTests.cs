using System.Globalization;
using HarfBuzzSharp;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>The itemizer's own grapheme clusters and remembered scripts give what .NET and HarfBuzz give.</summary>
public class TextItemizerCacheTests
{
    [Fact]
    public void Remembered_scripts_are_HarfBuzz_scripts()
    {
        // Twice: the first asks HarfBuzz and remembers, the second reads what was remembered.
        int[] extra = [0x1F600, 0x1F468, 0x10900, 0x20000, 0xE0001, 0x10FFFF];
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var codePoint in Enumerable.Range(0, 0x3400).Where(c => c is < 0xD800 or > 0xDFFF).Concat(extra))
            {
                Assert.Equal((uint)UnicodeFunctions.Default.GetScript(codePoint), (uint)TextItemizer.ScriptOf(codePoint));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("e\u0301x\u0308\u0323")] // Combining marks.
    [InlineData("\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67 \uD83C\uDDE9\uD83C\uDDF0")] // ZWJ sequence, flag.
    [InlineData("\u0915\u094D\u0937\u093F \uAC01\r\n")] // Devanagari conjunct, Hangul, CR LF.
    public void Clusters_are_the_text_elements_of_the_string(string text)
    {
        var expected = new List<(int, int)>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            expected.Add((elements.ElementIndex, ((string)elements.Current).Length));
        }

        Assert.Equal(expected, TextItemizer.GetClusters(text));
    }
}
