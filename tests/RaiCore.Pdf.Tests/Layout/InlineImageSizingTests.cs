using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Inline images sized relative to their paragraph (#112): resolved at layout, compressible for min-content.</summary>
public class InlineImageSizingTests
{
    private const double Px = 0.75;

    /// <summary>A 400 x 100 point image.</summary>
    private static readonly PdfImage Wide = PdfImage.LoadSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="400pt" height="100pt"><rect width="400" height="100" fill="teal"/></svg>""");

    private static readonly TextStyle Style = new(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);

    private static Paragraph WithImage(InlineImageSizing sizing, double width = 400, double height = 100) =>
        new Paragraph(Style).AddImage(Wide, width, height, sizing: sizing);

    private static InlineImageFragment ImageAt(Paragraph paragraph, double width) =>
        ParagraphLayouter.Layout(paragraph, width).Lines.SelectMany(line => line.Images).Single();

    [Fact]
    public void A_percentage_max_width_resolves_against_the_width_the_paragraph_is_laid_out_at()
    {
        var paragraph = WithImage(new InlineImageSizing(MaxWidth: Length.FromPercent(100)));

        Assert.Equal((200.0, 50.0), (ImageAt(paragraph, 200).Image.Width, ImageAt(paragraph, 200).Image.Height));
        Assert.Equal((400.0, 100.0), (ImageAt(paragraph, 600).Image.Width, ImageAt(paragraph, 600).Image.Height)); // Not enlarged.
    }

    [Fact]
    public void A_percentage_width_keeps_the_aspect_ratio_unless_a_height_is_given()
    {
        Assert.Equal((100.0, 25.0), Size(ImageAt(WithImage(new InlineImageSizing(Width: Length.FromPercent(50))), 200)));
        Assert.Equal((100.0, 70.0), Size(ImageAt(WithImage(new InlineImageSizing(Width: Length.FromPercent(50), Height: 70)), 200)));
        Assert.Equal((110.0, 27.5), Size(ImageAt(WithImage(new InlineImageSizing(Width: new Length(10, 50))), 200))); // calc(50% + 10pt)

        // With both sizes given, a limit squeezes the image instead of scaling it.
        Assert.Equal((200.0, 70.0), Size(ImageAt(WithImage(new InlineImageSizing(Width: Length.FromPoints(300), Height: 70, MaxWidth: Length.FromPercent(100))), 200)));
        Assert.Equal((200.0, 50.0), Size(ImageAt(WithImage(new InlineImageSizing(MaxWidth: Length.FromPercent(100))), 200)));

        static (double, double) Size(InlineImageFragment fragment) => (fragment.Image.Width, fragment.Image.Height);
    }

    [Fact]
    public void The_minimum_wins_over_the_maximum()
    {
        var sizing = new InlineImageSizing(MinWidth: Length.FromPoints(150), MaxWidth: Length.FromPercent(100));

        Assert.Equal(150, ImageAt(WithImage(sizing), 100).Image.Width);
        Assert.Equal(300, ImageAt(WithImage(sizing), 300).Image.Width);
    }

    [Fact]
    public void Relative_images_add_nothing_to_min_content_but_their_size_to_max_content()
    {
        // The size given when building (50 x 12.5, resolved against a guess) does not count: max-content is natural.
        Assert.Equal((0.0, 400.0), ParagraphLayouter.MeasureIntrinsic(WithImage(new InlineImageSizing(MaxWidth: Length.FromPercent(100)), 50, 12.5)));
        Assert.Equal((0.0, 400.0), ParagraphLayouter.MeasureIntrinsic(WithImage(new InlineImageSizing(Width: Length.FromPercent(50)), 50, 12.5)));

        // A fixed minimum stays; a fixed width with only a percentage minimum is not compressible.
        Assert.Equal((150.0, 400.0), ParagraphLayouter.MeasureIntrinsic(WithImage(new InlineImageSizing(MinWidth: Length.FromPoints(150), MaxWidth: Length.FromPercent(100)))));
        Assert.Equal((80.0, 80.0), ParagraphLayouter.MeasureIntrinsic(WithImage(new InlineImageSizing(Width: Length.FromPoints(80), MinWidth: Length.FromPercent(10)), 80, 20)));

        // Without sizing an image is as wide as it is given.
        Assert.Equal((400.0, 400.0), ParagraphLayouter.MeasureIntrinsic(new Paragraph(Style).AddImage(Wide, 400, 100)));
    }

    [Theory]
    [InlineData("max-width: 100%", 1.0, null)]
    [InlineData("width: 50%", 0.5, null)]
    [InlineData("width: 100%; max-width: 120px", null, 90.0)] // A fixed maximum beside a relative width.
    public void An_image_in_a_shrinking_flex_item_follows_the_item(string css, double? fractionOfItem, double? points)
    {
        // Bases of 400px + 300px in a 500px container: both items shrink, and the image must not hold the first open.
        var html = $$"""
            <div style="display: flex; width: 500px">
              <div style="flex: 0 1 400px; padding: 0 14px"><img src="{{Svg}}" style="{{css}}"></div>
              <div style="flex: 0 1 300px">Second</div>
            </div>
            """;
        var (flow, item) = Flow(html);

        var content = flow.Paginate().Placements.First(p => p.Block == item).Width;
        var (drawnWidth, drawnHeight) = DrawnImage(flow);
        var expected = points ?? content * fractionOfItem!.Value;

        Assert.InRange(content, 150, 400 * Px - 1); // The item shrank below its 300 pt basis.
        Assert.Equal(expected, drawnWidth, tolerance: 0.01); // The matrix is written to 5 decimals.
        Assert.Equal(expected / 4, drawnHeight, tolerance: 0.01); // The aspect ratio is kept.
    }

    [Fact]
    public void Images_in_table_cells_follow_the_cell_width()
    {
        var (flow, _) = Flow($"""
            <table style="width: 300px; border-collapse: collapse"><tr>
              <td style="padding: 0; width: 50%"><img src="{Svg}" style="max-width: 100%"></td><td style="padding: 0">x</td>
            </tr></table>
            """);

        Assert.True(DrawnImage(flow).Width <= (300 * Px) - 10, $"The image is {DrawnImage(flow).Width} pt wide in a 225 pt table");
    }

    [Fact]
    public void Images_with_fixed_sizes_are_unchanged()
    {
        var (flow, _) = Flow($"""<div style="display: flex; width: 100px"><div><img src="{Svg}" style="width: 200px"></div></div>""");

        Assert.Equal(200 * Px, DrawnImage(flow).Width, 3); // Overflows, as in browsers.
    }

    /// <summary>A 400 x 100 CSS pixel image.</summary>
    private const string Svg = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='400' height='100'%3E%3Crect width='400' height='100' fill='teal'/%3E%3C/svg%3E";

    private static (FlowDocument Flow, Block FirstItem) Flow(string html)
    {
        var flow = HtmlToPdf.CreateFlowDocument("<style>body { margin: 0 }</style>" + html, new HtmlConverterOptions { Page = new PageSetup(new PdfPageSize(500, 800), Thickness.All(50)) });
        var item = Flatten(flow.Blocks).OfType<FlexBlock>().FirstOrDefault()?.Items[0].Content ?? flow.Blocks[0];
        return (flow, item);
    }

    /// <summary>
    /// The size the (single) image is painted at, from its matrix in the content stream: the SVG is a form XObject of
    /// its natural size (400 x 100 CSS pixels, 300 x 75 points), scaled by the matrix.
    /// </summary>
    private static (double Width, double Height) DrawnImage(FlowDocument flow)
    {
        var doc = new PdfDocument { CompressContent = false };
        flow.RenderTo(doc);
        var match = Regex.Match(Encoding.Latin1.GetString(doc.ToArray()), @"q (?:/\w+ gs )?(-?[\d.]+) 0 0 (-?[\d.]+) -?[\d.]+ -?[\d.]+ cm /\w+ Do Q");
        Assert.True(match.Success, "No image drawn");
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 300, double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 75);
    }

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });
}
