using System.Globalization;
using System.Text;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Workbooks;

/// <summary>One end of a reference: a row and a column from 0 (-1 where it has none, as in <c>$A</c> or <c>$1</c>), each absolute or relative.</summary>
internal readonly record struct ReferencePart(int Row, int Column, bool RowAbsolute, bool ColumnAbsolute)
{
    /// <summary>The part as written: <c>$A$1</c>, <c>B7</c>, <c>$C</c> or <c>$4</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(10);
        if (Column >= 0)
        {
            text.Append(ColumnAbsolute ? "$" : string.Empty).Append(FormulaReferences.ColumnName(Column));
        }

        if (Row >= 0)
        {
            text.Append(RowAbsolute ? "$" : string.Empty).Append((Row + 1).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}

/// <summary>What an area covers.</summary>
internal enum AreaKind
{
    /// <summary>One cell (<c>A1</c>).</summary>
    Cell,

    /// <summary>A rectangle of cells (<c>A1:B5</c>).</summary>
    Range,

    /// <summary>Whole columns (<c>A:C</c>).</summary>
    Columns,

    /// <summary>Whole rows (<c>1:3</c>).</summary>
    Rows,
}

/// <summary>An area a reference names: a cell, a range, whole columns or whole rows.</summary>
internal readonly record struct Area(AreaKind Kind, ReferencePart First, ReferencePart Last)
{
    /// <summary>The topmost row, the leftmost column, the bottom row and the rightmost column (-1 where open).</summary>
    public (int Row1, int Column1, int Row2, int Column2) Bounds =>
        (Math.Min(First.Row, Last.Row), Math.Min(First.Column, Last.Column), Math.Max(First.Row, Last.Row), Math.Max(First.Column, Last.Column));

    /// <summary>The area as written: <c>A1</c>, <c>$A$1:$B$5</c>, <c>A:C</c>, <c>$1:$3</c>.</summary>
    public override string ToString() => Kind == AreaKind.Cell ? First.ToString() : First + ":" + Last;

    /// <summary>Parses a whole text as an area (<c>A1</c>, <c>A1:B5</c>, <c>A:C</c>, <c>1:3</c>), or <c>null</c>.</summary>
    public static Area? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        text = text.Trim();
        return FormulaReferences.TryArea(text, 0, out var area, out var end) && end == text.Length ? area : null;
    }
}

/// <summary>
/// An insertion or deletion of rows or columns in a sheet: <see cref="Count"/> rows (or columns) at <see cref="Start"/>
/// (from 0). Maps positions and spans as Excel does: positions at or after an insertion move down (or right); deleted
/// single positions are gone (<c>#REF!</c>), and spans shrink to what is left of them.
/// </summary>
internal readonly record struct ShiftOperation(string Sheet, bool Rows, int Start, int Count, bool Delete)
{
    /// <summary>The highest row or column index of the axis.</summary>
    public int Max => Rows ? XlsxWorkbook.MaxRows - 1 : XlsxWorkbook.MaxColumns - 1;

    /// <summary>The first index after a deletion.</summary>
    public int End => Start + Count;

    /// <summary>Where a position goes, or <c>null</c> if it is deleted or pushed off the sheet.</summary>
    public int? Index(int index)
    {
        if (index < Start)
        {
            return index;
        }

        if (Delete)
        {
            return index >= End ? index - Count : null;
        }

        var moved = index + Count;
        return moved > Max ? null : moved;
    }

    /// <summary>
    /// Where a span of positions goes (<paramref name="low"/> &lt;= <paramref name="high"/>), or <c>null</c> if all
    /// of it is deleted or pushed off the sheet. An insertion inside a span widens it; a span that reaches the last
    /// row or column keeps ending there.
    /// </summary>
    public (int Low, int High)? Span(int low, int high)
    {
        if (!Delete)
        {
            var newLow = low >= Start ? low + Count : low;
            var newHigh = high >= Start ? high + Count : high;
            return newLow > Max ? null : (newLow, Math.Min(newHigh, Max));
        }

        if (low >= Start && high < End)
        {
            return null;
        }

        return (low < Start ? low : low >= End ? low - Count : Start, high >= End ? high - Count : high < Start ? high : Start - 1);
    }

    /// <summary>
    /// Where a boundary between rows (or columns) goes, as drawing anchors use them: before the row it names, which
    /// moves with that row; one in deleted rows goes to where they were. <c>moved</c> tells whether it was deleted.
    /// </summary>
    public int Boundary(int index, out bool deleted)
    {
        deleted = false;
        if (index < Start)
        {
            return index;
        }

        if (Delete)
        {
            deleted = index < End;
            return index >= End ? index - Count : Start;
        }

        return Math.Min(index + Count, Max);
    }

    /// <summary>Whether the sheet name is the sheet this operation changes (sheet names are compared ignoring case).</summary>
    public bool IsSheet(string? name) => name is not null && string.Equals(name, Sheet, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A token of a formula: text copied as it is, or a reference to cells.</summary>
internal sealed class FormulaToken
{
    public FormulaToken(string text) => Text = text;

    /// <summary>The token's text in the formula.</summary>
    public string Text { get; }

    /// <summary>Whether the token is a reference to cells.</summary>
    public bool IsReference { get; init; }

    /// <summary>The sheet prefix with its <c>!</c> (<c>'My sheet'!</c>, <c>Sheet1:Sheet3!</c>), empty without one.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>The (first) sheet the prefix names, or <c>null</c> without one.</summary>
    public string? Sheet { get; init; }

    /// <summary>The last sheet of a 3-D reference (<c>Sheet1:Sheet3!A1</c>), or <c>null</c>.</summary>
    public string? LastSheet { get; init; }

    /// <summary>The area the reference names.</summary>
    public Area Area { get; init; }
}

/// <summary>
/// Reads the references in formulas as Office Open XML stores them (A1 style, without the leading <c>=</c>), and
/// moves them when rows or columns are inserted or deleted, or when a formula is copied to another cell. A small
/// tokenizer tells references from strings, sheet names, functions, names, numbers and errors; everything that is not
/// a reference is kept exactly as written. Structured references to tables (<c>Table1[Amount]</c>) and references to
/// other workbooks are kept as they are.
/// </summary>
internal static class FormulaReferences
{
    /// <summary>The longest formula text read; Excel's limit is 8,192 characters.</summary>
    public const int MaxLength = 1 << 16;

    private static readonly string[] Errors =
    [
        "#NULL!", "#DIV/0!", "#VALUE!", "#REF!", "#NAME?", "#NUM!", "#N/A", "#GETTING_DATA", "#SPILL!", "#CALC!",
        "#FIELD!", "#BLOCKED!", "#CONNECT!", "#UNKNOWN!", "#BUSY!", "#PYTHON!",
    ];

    /// <summary>The letters of a column from 0 (<c>A</c>, <c>Z</c>, <c>AA</c>).</summary>
    public static string ColumnName(int column)
    {
        Span<char> letters = stackalloc char[4];
        var length = 0;
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
        {
            letters[length++] = (char)('A' + ((c - 1) % 26));
        }

        letters[..length].Reverse();
        return new string(letters[..length]);
    }

    /// <summary>A cell's address (<c>B3</c>) from its row and column from 0.</summary>
    public static string Address(int row, int column) => ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Splits a formula into tokens.</summary>
    public static List<FormulaToken> Tokenize(string formula)
    {
        var tokens = new List<FormulaToken>();
        var raw = new StringBuilder();
        var f = formula;
        var n = f.Length;
        var i = 0;

        void Flush()
        {
            if (raw.Length > 0)
            {
                tokens.Add(new FormulaToken(raw.ToString()));
                raw.Clear();
            }
        }

        void AddReference(int start, int prefixEnd, int end, string? sheet, string? lastSheet, Area area)
        {
            Flush();
            tokens.Add(new FormulaToken(f[start..end])
            {
                IsReference = true,
                Prefix = f[start..prefixEnd],
                Sheet = sheet,
                LastSheet = lastSheet,
                Area = area,
            });
        }

        while (i < n)
        {
            var c = f[i];
            if (c == '"')
            {
                var end = SkipQuoted(f, i, '"');
                raw.Append(f, i, end - i);
                i = end;
            }
            else if (c == '\'')
            {
                // A quoted sheet name ('My sheet'!A1, 'Sheet 1:Sheet 3'!A1, '[1]Sheet1'!A1).
                var end = SkipQuoted(f, i, '\'');
                if (end < n && f[end] == '!' && end - i >= 2 && TryArea(f, end + 1, out var area, out var areaEnd))
                {
                    // A sheet of another workbook ('[1]Sheet1') keeps its brackets, so it names no sheet of this one.
                    var name = f[(i + 1)..(end - 1)].Replace("''", "'", StringComparison.Ordinal);
                    var colon = name.IndexOf(':', StringComparison.Ordinal);
                    AddReference(i, end + 1, areaEnd, colon < 0 ? name : name[..colon], colon < 0 ? null : name[(colon + 1)..], area);
                    i = areaEnd;
                }
                else
                {
                    raw.Append(f, i, end - i);
                    i = end;
                }
            }
            else if (c == '[')
            {
                // Another workbook ([1]Sheet1!A1) or a structured reference without a table name: kept as it is.
                var end = SkipBrackets(f, i);
                var word = end;
                while (word < n && IsWordChar(f[word]))
                {
                    word++;
                }

                if (word < n && f[word] == '!' && TryArea(f, word + 1, out _, out var areaEnd))
                {
                    end = areaEnd;
                }

                raw.Append(f, i, end - i);
                i = end;
            }
            else if (c == '#')
            {
                var length = ErrorLength(f, i);
                raw.Append(f, i, length);
                i += length;
            }
            else if (IsWordChar(c))
            {
                var j = i;
                while (j < n && IsWordChar(f[j]))
                {
                    j++;
                }

                if (j < n && f[j] == '(')
                {
                    // A function (LOG10 is one, not a cell).
                    raw.Append(f, i, j - i);
                    i = j;
                    continue;
                }

                if (j < n && f[j] == '[')
                {
                    // A structured reference (Table1[Amount], Table1[[#Headers],[Amount]]).
                    var end = SkipBrackets(f, j);
                    raw.Append(f, i, end - i);
                    i = end;
                    continue;
                }

                if (j < n && f[j] == '!')
                {
                    if (TryArea(f, j + 1, out var area, out var areaEnd))
                    {
                        AddReference(i, j + 1, areaEnd, f[i..j], null, area);
                        i = areaEnd;
                    }
                    else
                    {
                        raw.Append(f, i, j + 1 - i);
                        i = j + 1;
                    }

                    continue;
                }

                if (j < n && f[j] == ':')
                {
                    // A 3-D reference (Sheet1:Sheet3!A1), unless it is a range (A1:B2, A:C, 1:3).
                    var k = j + 1;
                    while (k < n && IsWordChar(f[k]))
                    {
                        k++;
                    }

                    if (k > j + 1 && k < n && f[k] == '!')
                    {
                        if (TryArea(f, k + 1, out var area, out var areaEnd))
                        {
                            AddReference(i, k + 1, areaEnd, f[i..j], f[(j + 1)..k], area);
                            i = areaEnd;
                        }
                        else
                        {
                            raw.Append(f, i, k + 1 - i);
                            i = k + 1;
                        }

                        continue;
                    }
                }

                if (TryArea(f, i, out var plain, out var plainEnd))
                {
                    AddReference(i, i, plainEnd, null, null, plain);
                    i = plainEnd;
                }
                else
                {
                    raw.Append(f, i, j - i);
                    i = j;
                }
            }
            else
            {
                raw.Append(c);
                i++;
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Moves the references of a formula on a sheet (<paramref name="sheet"/>, for references without a sheet name;
    /// <c>null</c> where they name none, as in charts) for an insertion or deletion. Deleted references become
    /// <c>#REF!</c>. Returns the formula itself when nothing changes.
    /// </summary>
    public static string Shift(string formula, string? sheet, ShiftOperation operation)
    {
        if (formula.Length == 0 || formula.Length > MaxLength)
        {
            return formula;
        }

        var tokens = Tokenize(formula);
        StringBuilder? result = null;
        for (var t = 0; t < tokens.Count; t++)
        {
            var token = tokens[t];
            string? replacement = null;
            if (token.IsReference && RefersTo(token, sheet, operation))
            {
                var moved = Shift(token.Area, operation);
                replacement = moved is not { } area ? token.Prefix + "#REF!" : area == token.Area ? null : token.Prefix + area;
            }

            if (replacement is not null && result is null)
            {
                result = new StringBuilder(formula.Length + 8);
                for (var p = 0; p < t; p++)
                {
                    result.Append(tokens[p].Text);
                }
            }

            result?.Append(replacement ?? token.Text);
        }

        return result?.ToString() ?? formula;
    }

    /// <summary>
    /// The formula of a cell copied <paramref name="rows"/> down and <paramref name="columns"/> across (as the cells of
    /// a shared formula are): its relative references moved, those that leave the sheet <c>#REF!</c>.
    /// </summary>
    public static string Translate(string formula, int rows, int columns)
    {
        if ((rows == 0 && columns == 0) || formula.Length > MaxLength)
        {
            return formula;
        }

        var result = new StringBuilder(formula.Length + 8);
        foreach (var token in Tokenize(formula))
        {
            if (!token.IsReference)
            {
                result.Append(token.Text);
                continue;
            }

            var area = token.Area;
            if (Move(area.First, rows, columns) is { } first && Move(area.Last, rows, columns) is { } last)
            {
                result.Append(token.Prefix).Append(new Area(area.Kind, first, last));
            }
            else
            {
                result.Append(token.Prefix).Append("#REF!");
            }
        }

        return result.ToString();

        static ReferencePart? Move(ReferencePart part, int rows, int columns)
        {
            var row = part.Row < 0 || part.RowAbsolute ? part.Row : part.Row + rows;
            var column = part.Column < 0 || part.ColumnAbsolute ? part.Column : part.Column + columns;
            return (part.Row >= 0 && row is < 0 or >= XlsxWorkbook.MaxRows) || (part.Column >= 0 && column is < 0 or >= XlsxWorkbook.MaxColumns)
                ? null
                : part with { Row = row, Column = column };
        }
    }

    /// <summary>Whether a formula has a reference to a sheet (without a sheet name, on <paramref name="sheet"/>).</summary>
    public static bool RefersToSheet(string formula, string? sheet, string target)
    {
        if (formula.Length > MaxLength)
        {
            return false;
        }

        var operation = new ShiftOperation(target, true, 0, 1, false);
        return Tokenize(formula).Any(t => t.IsReference && RefersTo(t, sheet, operation));
    }

    /// <summary>Whether a reference names the sheet an operation changes (a 3-D reference only when both its ends do).</summary>
    private static bool RefersTo(FormulaToken token, string? sheet, ShiftOperation operation) =>
        token.Sheet is null ? operation.IsSheet(sheet)
        : token.LastSheet is null ? operation.IsSheet(token.Sheet)
        : operation.IsSheet(token.Sheet) && operation.IsSheet(token.LastSheet);

    /// <summary>Where an area on the changed sheet goes, or <c>null</c> if it is deleted.</summary>
    public static Area? Shift(Area area, ShiftOperation operation)
    {
        switch (area.Kind)
        {
            case AreaKind.Cell:
                var index = operation.Rows ? area.First.Row : area.First.Column;
                if (operation.Index(index) is not { } moved)
                {
                    return null;
                }

                var part = operation.Rows ? area.First with { Row = moved } : area.First with { Column = moved };
                return area with { First = part, Last = part };
            case AreaKind.Columns when operation.Rows:
            case AreaKind.Rows when !operation.Rows:
                return area;
            default:
                var a = operation.Rows ? area.First.Row : area.First.Column;
                var b = operation.Rows ? area.Last.Row : area.Last.Column;
                if (operation.Span(Math.Min(a, b), Math.Max(a, b)) is not var (low, high))
                {
                    return null;
                }

                var (newA, newB) = a <= b ? (low, high) : (high, low);
                return operation.Rows
                    ? area with { First = area.First with { Row = newA }, Last = area.Last with { Row = newB } }
                    : area with { First = area.First with { Column = newA }, Last = area.Last with { Column = newB } };
        }
    }

    /// <summary>
    /// Moves a list of areas separated by spaces (a <c>sqref</c>), leaving out deleted ones. Returns <c>null</c> when
    /// all are deleted, the list itself when nothing changes, and areas it cannot read as they are.
    /// </summary>
    public static string? ShiftList(string list, ShiftOperation operation)
    {
        var parts = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(parts.Length);
        var changed = false;
        foreach (var part in parts)
        {
            if (Area.Parse(part) is not { } area)
            {
                result.Add(part);
                continue;
            }

            if (Shift(area, operation) is { } moved)
            {
                var text = moved == area ? part : moved.ToString();
                changed |= !ReferenceEquals(text, part);
                result.Add(text);
            }
            else
            {
                changed = true;
            }
        }

        return result.Count == 0 ? null : changed ? string.Join(' ', result) : list;
    }

    /// <summary>
    /// Reads an area at a position of a text: a cell (<c>$A$1</c>), a range of cells, whole columns or whole rows,
    /// ending where no name could go on. <paramref name="end"/> is the index after it.
    /// </summary>
    internal static bool TryArea(string f, int start, out Area area, out int end)
    {
        area = default;
        end = start;
        if (!TryPart(f, start, out var first, out var firstEnd))
        {
            return false;
        }

        var cell = first.Row >= 0 && first.Column >= 0;
        if (firstEnd < f.Length && f[firstEnd] == ':' && TryPart(f, firstEnd + 1, out var last, out var lastEnd)
            && (last.Row >= 0) == (first.Row >= 0) && (last.Column >= 0) == (first.Column >= 0) && EndsName(f, lastEnd))
        {
            area = new Area(cell ? AreaKind.Range : first.Row >= 0 ? AreaKind.Rows : AreaKind.Columns, first, last);
            end = lastEnd;
            return true;
        }

        // A cell alone, also before a range operator whose other side is not a cell (A1:INDEX(...)).
        area = new Area(AreaKind.Cell, first, first);
        end = firstEnd;
        return cell && EndsName(f, firstEnd);
    }

    /// <summary>Whether a reference may end at a position: no name, function, structured reference or sheet name goes on there.</summary>
    private static bool EndsName(string f, int end) => end >= f.Length || !(IsWordChar(f[end]) || f[end] is '(' or '[' or '!' or '\'' or '"');

    /// <summary>Reads one end of an area: <c>$A$1</c>, <c>A</c> (a column) or <c>1</c> (a row).</summary>
    private static bool TryPart(string f, int start, out ReferencePart part, out int end)
    {
        part = default;
        var i = start;
        var n = f.Length;
        var firstDollar = i < n && f[i] == '$';
        if (firstDollar)
        {
            i++;
        }

        var column = 0;
        var letters = 0;
        while (i < n && char.IsAsciiLetter(f[i]))
        {
            if (++letters > 3)
            {
                end = i;
                return false;
            }

            column = (column * 26) + (char.ToUpperInvariant(f[i]) - 'A' + 1);
            i++;
        }

        var rowDollar = false;
        if (letters > 0 && i < n && f[i] == '$')
        {
            rowDollar = true;
            i++;
        }

        var row = 0;
        var digits = 0;
        while (i < n && char.IsAsciiDigit(f[i]))
        {
            if (++digits > 7)
            {
                end = i;
                return false;
            }

            row = (row * 10) + (f[i] - '0');
            i++;
        }

        end = i;
        if ((letters == 0 && digits == 0) || (rowDollar && digits == 0) || column > XlsxWorkbook.MaxColumns
            || (digits > 0 && (row == 0 || row > XlsxWorkbook.MaxRows)))
        {
            return false;
        }

        part = letters == 0
            ? new ReferencePart(row - 1, -1, firstDollar, false)
            : new ReferencePart(digits == 0 ? -1 : row - 1, column - 1, rowDollar, firstDollar);
        return true;
    }

    /// <summary>The index after a quoted text (a string, or a sheet name), with doubled quotes inside it.</summary>
    private static int SkipQuoted(string f, int start, char quote)
    {
        var i = start + 1;
        while (i < f.Length)
        {
            if (f[i] == quote)
            {
                if (i + 1 < f.Length && f[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return f.Length;
    }

    /// <summary>The index after a bracketed part (nested brackets, <c>'</c> escaping the next character).</summary>
    private static int SkipBrackets(string f, int start)
    {
        var depth = 0;
        for (var i = start; i < f.Length; i++)
        {
            switch (f[i])
            {
                case '\'':
                    i++;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }

                    break;
            }
        }

        return f.Length;
    }

    /// <summary>The length of an error value at a position (<c>#REF!</c>), or 1 for a lone <c>#</c> (the spill operator).</summary>
    private static int ErrorLength(string f, int start)
    {
        foreach (var error in Errors)
        {
            if (string.Compare(f, start, error, 0, error.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return error.Length;
            }
        }

        return 1;
    }

    /// <summary>Whether a character can be part of a name, a reference or a number.</summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '\\' or '?';
}
