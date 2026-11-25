using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>The outer halves of a collapsed table's grid lines (<see cref="TableLayouter.OuterHalves"/>, #152).</summary>
public class CollapsedTableHalvesTests
{
    [Fact]
    public void The_halves_follow_rows_added_after_they_were_computed()
    {
        var table = new TableBlock { BorderCollapse = BorderCollapse.Collapse, OuterLinesInside = true }
            .Add(new TableRow(new TableCell(new SpacerBlock(10)) { Style = new BoxStyle { Border = Borders.All(2, PdfColor.Black) } }));
        Assert.Equal(1, TableLayouter.OuterHalves(table).Bottom.Points, 6);

        table.Add(new TableRow(new TableCell(new SpacerBlock(10)) { Style = new BoxStyle { Border = Borders.All(10, PdfColor.Black) } }));

        var halves = TableLayouter.OuterHalves(table);
        Assert.Equal(5, halves.Bottom.Points, 6);
        Assert.Equal(1, halves.Top.Points, 6);
        Assert.Equal(5, halves.Left.Points, 6); // The widest line on each side.
        Assert.Equal(5, halves.Right.Points, 6);
    }
}
