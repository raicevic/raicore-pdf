using RaiCore.Pdf.Sheets.Workbooks;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// The formula tokenizer and reference moving the Workbook API uses for insertions and deletions: which tokens are
/// references, and how they move, as Excel moves them.
/// </summary>
public class FormulaReferencesTests
{
    private static ShiftOperation InsertRows(int start, int count, string sheet = "Sheet1") => new(sheet, true, start, count, false);

    private static ShiftOperation DeleteRows(int start, int count, string sheet = "Sheet1") => new(sheet, true, start, count, true);

    private static ShiftOperation InsertColumns(int start, int count, string sheet = "Sheet1") => new(sheet, false, start, count, false);

    private static ShiftOperation DeleteColumns(int start, int count, string sheet = "Sheet1") => new(sheet, false, start, count, true);

    [Theory]
    [InlineData("A1", "A1")]
    [InlineData("SUM(A1:B2)", "A1:B2")]
    [InlineData("$A$1+B$2+$C3", "$A$1|B$2|$C3")]
    [InlineData("SUM(A:C)+SUM(1:3)+SUM($B:$B)+SUM($4:$4)", "A:C|1:3|$B:$B|$4:$4")]
    [InlineData("Sheet1!A1+'My Sheet'!B2:C3", "Sheet1!A1|'My Sheet'!B2:C3")]
    [InlineData("'O''Brien'!A1", "'O''Brien'!A1")]
    [InlineData("SUM(Sheet1:Sheet3!A1)", "Sheet1:Sheet3!A1")]
    [InlineData("LOG10(A1)+ATAN2(B1,C1)", "A1|B1|C1")]
    [InlineData("\"A1\"&B1&\"C1\"\"D1\"", "B1")]
    [InlineData("SUM(Table1[Amount])+Table1[[#Headers],[Item]]", "")]
    [InlineData("[1]Sheet1!A1+B1", "B1")]
    [InlineData("'[Book.xlsx]Sheet 1'!A1", "'[Book.xlsx]Sheet 1'!A1")]
    [InlineData("#REF!+Sheet1!#REF!+A1", "A1")]
    [InlineData("1E+5+A1+1.5", "A1")]
    [InlineData("A1#+@B1:B3", "A1|B1:B3")]
    [InlineData("TRUE+MyName+Sales.2020+A1B2+XFD1048576+XFE1", "XFD1048576")]
    [InlineData("_xlfn.STDEV.S(A1:A9)", "A1:A9")]
    [InlineData("{1,2;3,4}*A1", "A1")]
    [InlineData("Sheet1!MyName+Sheet1!A1", "Sheet1!A1")]
    [InlineData("SUM(A1:LOG10(B1))", "A1|B1")]
    [InlineData("SUM(A1:INDEX(B:B,2))", "A1|B:B")]
    public void Tokenizes_references(string formula, string expected)
    {
        var tokens = FormulaReferences.Tokenize(formula);
        Assert.Equal(formula, string.Concat(tokens.Select(t => t.Text)));
        Assert.Equal(expected, string.Join('|', tokens.Where(t => t.IsReference).Select(t => t.Text)));
    }

    [Theory]
    [InlineData("A1", "A1")]
    [InlineData("A5", "A7")]
    [InlineData("A4", "A6")]
    [InlineData("A3", "A3")]
    [InlineData("$A$5", "$A$7")]
    [InlineData("SUM(A1:A3)", "SUM(A1:A3)")]
    [InlineData("SUM(A1:A4)", "SUM(A1:A6)")]
    [InlineData("SUM(A4:A9)", "SUM(A6:A11)")]
    [InlineData("SUM(A9:A4)", "SUM(A11:A6)")]
    [InlineData("SUM(B:B)", "SUM(B:B)")]
    [InlineData("SUM(3:4)", "SUM(3:6)")]
    [InlineData("SUM($5:$5)", "SUM($7:$7)")]
    [InlineData("Sheet1!A5+Other!A5+'Sheet1'!A5", "Sheet1!A7+Other!A5+'Sheet1'!A7")]
    [InlineData("SUM(Sheet1:Sheet2!A5)", "SUM(Sheet1:Sheet2!A5)")]
    [InlineData("SUM(Sheet1:Sheet1!A5)", "SUM(Sheet1:Sheet1!A7)")]
    [InlineData("[1]Sheet1!A5", "[1]Sheet1!A5")]
    [InlineData("'[1]Sheet1'!A5+'[Book.xlsx]Sheet1'!A5", "'[1]Sheet1'!A5+'[Book.xlsx]Sheet1'!A5")]
    [InlineData("A5:INDEX(B:B,5)", "A7:INDEX(B:B,5)")]
    [InlineData("\"A5\"&A5", "\"A5\"&A7")]
    [InlineData("sheet1!a5", "sheet1!A7")]
    [InlineData("SUM(A1:A1048576)", "SUM(A1:A1048576)")]
    public void Moves_references_for_inserted_rows(string formula, string expected) =>
        Assert.Equal(expected, FormulaReferences.Shift(formula, "Sheet1", InsertRows(3, 2)));

    [Theory]
    [InlineData("A1", "A1")]
    [InlineData("A3", "#REF!")]
    [InlineData("A4", "#REF!")]
    [InlineData("A5", "A3")]
    [InlineData("Other!A5+Sheet1!A4", "Other!A5+Sheet1!#REF!")]
    [InlineData("SUM(A1:A10)", "SUM(A1:A8)")]
    [InlineData("SUM(A3:A10)", "SUM(A3:A8)")]
    [InlineData("SUM(A1:A3)", "SUM(A1:A2)")]
    [InlineData("SUM(A3:A4)", "SUM(#REF!)")]
    [InlineData("SUM(3:4)+SUM(2:5)", "SUM(#REF!)+SUM(2:3)")]
    [InlineData("SUM(C:C)", "SUM(C:C)")]
    public void Moves_references_for_deleted_rows(string formula, string expected) =>
        Assert.Equal(expected, FormulaReferences.Shift(formula, "Sheet1", DeleteRows(2, 2)));

    [Theory]
    [InlineData("B1+C1+D1", "B1+E1+F1")]
    [InlineData("SUM(B:D)+SUM(1:2)", "SUM(B:F)+SUM(1:2)")]
    [InlineData("$C$1", "$E$1")]
    public void Moves_references_for_inserted_columns(string formula, string expected) =>
        Assert.Equal(expected, FormulaReferences.Shift(formula, "Sheet1", InsertColumns(2, 2)));

    [Theory]
    [InlineData("A1+B1+C1+D1", "A1+#REF!+#REF!+B1")]
    [InlineData("SUM(A1:D1)", "SUM(A1:B1)")]
    [InlineData("SUM(B:C)", "SUM(#REF!)")]
    public void Moves_references_for_deleted_columns(string formula, string expected) =>
        Assert.Equal(expected, FormulaReferences.Shift(formula, "Sheet1", DeleteColumns(1, 2)));

    [Fact]
    public void References_without_a_sheet_name_belong_to_the_formulas_sheet()
    {
        Assert.Equal("A1+A4", FormulaReferences.Shift("A1+A3", "Sheet1", InsertRows(1, 1)));
        Assert.Equal("A1+A3", FormulaReferences.Shift("A1+A3", "Other", InsertRows(1, 1)));
        Assert.Equal("A1+A3", FormulaReferences.Shift("A1+A3", null, InsertRows(1, 1)));
        Assert.Equal("'My Sheet'!A4", FormulaReferences.Shift("'My Sheet'!A3", null, InsertRows(1, 1, "my sheet")));
    }

    [Fact]
    public void Unchanged_formulas_are_returned_as_they_are()
    {
        const string formula = "SUM(a1:a3)";
        Assert.Same(formula, FormulaReferences.Shift(formula, "Sheet1", InsertRows(10, 1)));
    }

    [Fact]
    public void Insertions_that_push_references_off_the_sheet_make_them_errors()
    {
        Assert.Equal("#REF!", FormulaReferences.Shift("A1048576", "Sheet1", InsertRows(0, 1)));
        Assert.Equal("SUM(A2:A1048576)", FormulaReferences.Shift("SUM(A1:A1048576)", "Sheet1", InsertRows(0, 1)));
        Assert.Equal("#REF!", FormulaReferences.Shift("XFD1", "Sheet1", InsertColumns(0, 1)));
    }

    [Theory]
    [InlineData("A1+$A$1+A$1+$A1", 2, 1, "B3+$A$1+B$1+$A3")]
    [InlineData("SUM(A1:B2)", 1, 0, "SUM(A2:B3)")]
    [InlineData("SUM(A:A)+SUM(1:1)", 1, 1, "SUM(B:B)+SUM(2:2)")]
    [InlineData("Other!A1+\"A1\"", 1, 0, "Other!A2+\"A1\"")]
    [InlineData("A1", -1, 0, "#REF!")]
    [InlineData("Sheet2!A1", 0, -1, "Sheet2!#REF!")]
    public void Translates_formulas_copied_to_other_cells(string formula, int rows, int columns, string expected) =>
        Assert.Equal(expected, FormulaReferences.Translate(formula, rows, columns));

    [Fact]
    public void Lists_of_areas_drop_deleted_ones()
    {
        Assert.Equal("A1:A2 C3", FormulaReferences.ShiftList("A1:A4 B3 C5", DeleteRows(2, 2)));
        Assert.Null(FormulaReferences.ShiftList("A3 B4", DeleteRows(2, 3)));
        const string unchanged = "A1 B2";
        Assert.Same(unchanged, FormulaReferences.ShiftList(unchanged, DeleteRows(5, 1)));
    }

    [Fact]
    public void Spans_and_positions_move_as_Excel_moves_them()
    {
        var insert = InsertRows(4, 3);
        Assert.Equal(3, insert.Index(3));
        Assert.Equal(7, insert.Index(4));
        Assert.Equal((1, 2), insert.Span(1, 2));
        Assert.Equal((1, 8), insert.Span(1, 5));
        Assert.Equal((7, 8), insert.Span(4, 5));
        var delete = DeleteRows(4, 3);
        Assert.Null(delete.Index(4));
        Assert.Null(delete.Index(6));
        Assert.Equal(4, delete.Index(7));
        Assert.Null(delete.Span(4, 6));
        Assert.Equal((2, 3), delete.Span(2, 5));
        Assert.Equal((4, 5), delete.Span(5, 8));
        Assert.Equal((2, 5), delete.Span(2, 8));
        Assert.Equal(4, delete.Boundary(5, out var deleted));
        Assert.True(deleted);
        Assert.Equal(4, delete.Boundary(7, out deleted));
        Assert.False(deleted);
    }

    [Theory]
    [InlineData("A1", 0, 0)]
    [InlineData("$B$3", 2, 1)]
    [InlineData("xfd1048576", 1048575, 16383)]
    public void Parses_cell_areas(string text, int row, int column)
    {
        var area = Area.Parse(text);
        Assert.NotNull(area);
        Assert.Equal(AreaKind.Cell, area.Value.Kind);
        Assert.Equal((row, column), (area.Value.First.Row, area.Value.First.Column));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A0")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    [InlineData("A1:")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("A:1")]
    [InlineData("A1B")]
    [InlineData("ABCD1")]
    public void Rejects_what_is_not_an_area(string text) => Assert.Null(Area.Parse(text));

    [Fact]
    public void Column_names_round_trip()
    {
        foreach (var column in new[] { 0, 25, 26, 51, 701, 702, 16383 })
        {
            var name = FormulaReferences.ColumnName(column);
            Assert.Equal(column, Area.Parse(name + "1")!.Value.First.Column);
        }

        Assert.Equal("XFD", FormulaReferences.ColumnName(16383));
        Assert.Equal("AA", FormulaReferences.ColumnName(26));
    }
}
