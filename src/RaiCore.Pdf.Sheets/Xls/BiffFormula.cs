using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// Writes the parsed formula of a BIFF8 record (its tokens in reverse Polish order, [MS-XLS] 2.5.198) as the text Excel
/// shows, for <see cref="Xlsx.ConditionalFormula"/> to compute: constants, references to cells and ranges of the sheet,
/// operators, parentheses and the functions <see cref="Xlsx.ConditionalFormula"/> supports. Anything else (other sheets,
/// names, arrays, other functions) gives <c>null</c>.
/// </summary>
internal static class BiffFormula
{
    /// <summary>The deepest the stack of a formula goes.</summary>
    private const int MaxStack = 1024;

    /// <summary>The functions by their index ([MS-XLS] 2.5.198.17 Ftab) and their argument count when fixed (-1 when given).</summary>
    private static readonly Dictionary<int, (string Name, int Arguments)> Functions = new()
    {
        [0] = ("COUNT", -1), [1] = ("IF", -1), [2] = ("ISNA", 1), [3] = ("ISERROR", 1), [4] = ("SUM", -1), [5] = ("AVERAGE", -1),
        [6] = ("MIN", -1), [7] = ("MAX", -1), [8] = ("ROW", -1), [9] = ("COLUMN", -1), [24] = ("ABS", 1), [25] = ("INT", 1),
        [27] = ("ROUND", 2), [32] = ("LEN", 1), [34] = ("TRUE", 0), [35] = ("FALSE", 0), [36] = ("AND", -1), [37] = ("OR", -1),
        [38] = ("NOT", 1), [39] = ("MOD", 2), [82] = ("SEARCH", -1), [112] = ("LOWER", 1), [113] = ("UPPER", 1), [115] = ("LEFT", -1),
        [116] = ("RIGHT", -1), [118] = ("TRIM", 1), [124] = ("FIND", -1), [127] = ("ISTEXT", 1), [128] = ("ISNUMBER", 1),
        [129] = ("ISBLANK", 1), [169] = ("COUNTA", -1), [221] = ("TODAY", 0), [346] = ("COUNTIF", 2), [347] = ("COUNTBLANK", 1),
    };

    private static readonly string[] Operators = ["+", "-", "*", "/", "^", "&", "<", "<=", "=", ">=", ">", "<>"];

    /// <summary>
    /// The text of a formula's tokens; <paramref name="baseRow"/> and <paramref name="baseColumn"/> are the cell relative
    /// references of the <c>N</c> kind (<c>PtgRefN</c>, <c>PtgAreaN</c>) are written from (in conditional formatting:
    /// the top left cell of the rule's first range).
    /// </summary>
    public static string? ToText(ReadOnlySpan<byte> tokens, int baseRow, int baseColumn)
    {
        var stack = new List<string>();
        var position = 0;
        while (position < tokens.Length)
        {
            if (stack.Count > MaxStack || (stack.Count > 0 && stack[^1].Length > Xlsx.ConditionalFormula.MaxLength))
            {
                return null; // Too deep or too long for a formula (which also bounds the work of writing it).
            }

            var ptg = tokens[position++];
            var data = tokens[position..];
            switch (ptg)
            {
                case >= 0x03 and <= 0x0E:
                {
                    if (stack.Count < 2)
                    {
                        return null;
                    }

                    var right = Pop(stack);
                    stack[^1] = stack[^1] + Operators[ptg - 0x03] + right;
                    break;
                }

                case 0x12 or 0x13 or 0x14:
                    if (stack.Count < 1)
                    {
                        return null;
                    }

                    stack[^1] = ptg == 0x14 ? stack[^1] + "%" : (ptg == 0x12 ? "+" : "-") + stack[^1];
                    break;
                case 0x15:
                    if (stack.Count < 1)
                    {
                        return null;
                    }

                    stack[^1] = "(" + stack[^1] + ")";
                    break;
                case 0x16:
                    stack.Add(string.Empty);
                    break;
                case 0x17:
                {
                    // A short Unicode string: its length, its flags (one byte a character or two) and its characters.
                    if (data.Length < 2)
                    {
                        return null;
                    }

                    int count = data[0], size = (data[1] & 0x01) != 0 ? 2 : 1;
                    if (data.Length < 2 + (count * size))
                    {
                        return null;
                    }

                    var text = size == 2 ? Encoding.Unicode.GetString(data.Slice(2, count * 2)) : Encoding.Latin1.GetString(data.Slice(2, count));
                    stack.Add("\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                    position += 2 + (count * size);
                    break;
                }

                case 0x19:
                {
                    // An attribute: a SUM of one value (tAttrSum) counts; spaces, IFs and GOTOs are only for calculation.
                    if (data.Length < 3)
                    {
                        return null;
                    }

                    var kind = data[0];
                    if ((kind & 0x04) != 0)
                    {
                        return null; // CHOOSE.
                    }

                    if ((kind & 0x10) != 0)
                    {
                        if (stack.Count < 1)
                        {
                            return null;
                        }

                        stack[^1] = "SUM(" + stack[^1] + ")";
                    }

                    position += 3;
                    break;
                }

                case 0x1C:
                    if (data.Length < 1)
                    {
                        return null;
                    }

                    stack.Add(XlsValue.Error(data[0]).Text);
                    position += 1;
                    break;
                case 0x1D:
                    if (data.Length < 1)
                    {
                        return null;
                    }

                    stack.Add(data[0] != 0 ? "TRUE" : "FALSE");
                    position += 1;
                    break;
                case 0x1E:
                    if (data.Length < 2)
                    {
                        return null;
                    }

                    stack.Add(BiffReader.U16(data, 0).ToString(CultureInfo.InvariantCulture));
                    position += 2;
                    break;
                case 0x1F:
                {
                    if (data.Length < 8)
                    {
                        return null;
                    }

                    var number = BinaryPrimitives.ReadDoubleLittleEndian(data);
                    if (!double.IsFinite(number))
                    {
                        return null;
                    }

                    stack.Add(number.ToString("R", CultureInfo.InvariantCulture));
                    position += 8;
                    break;
                }

                case >= 0x20 and <= 0x7F:
                {
                    var used = Operand((byte)(0x20 | (ptg & 0x1F)), data, stack, baseRow, baseColumn);
                    if (used < 0)
                    {
                        return null;
                    }

                    position += used;
                    break;
                }

                default:
                    return null;
            }
        }

        return stack.Count == 1 ? stack[0] : null;
    }

    private static string Pop(List<string> stack)
    {
        var top = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return top;
    }

    /// <summary>An operand or a call (of any class: reference, value or array); the bytes it took after its token, or -1 if not supported.</summary>
    private static int Operand(byte ptg, ReadOnlySpan<byte> data, List<string> stack, int baseRow, int baseColumn)
    {
        switch (ptg)
        {
            case 0x21 when data.Length >= 2:
            case 0x22 when data.Length >= 3:
            {
                // A call: PtgFunc has a fixed number of arguments, PtgFuncVar gives it (and marks calls of macros).
                var index = ptg == 0x21 ? BiffReader.U16(data, 0) : BiffReader.U16(data, 1) & 0x7FFF;
                if ((ptg == 0x22 && (BiffReader.U16(data, 1) & 0x8000) != 0) || !Functions.TryGetValue(index, out var function))
                {
                    return -1;
                }

                var count = ptg == 0x22 ? data[0] & 0x7F : function.Arguments;
                if (count < 0 || count > stack.Count)
                {
                    return -1;
                }

                var arguments = stack.GetRange(stack.Count - count, count);
                stack.RemoveRange(stack.Count - count, count);
                stack.Add(function.Name + "(" + string.Join(",", arguments) + ")");
                return ptg == 0x21 ? 2 : 3;
            }

            case 0x24 or 0x2C when data.Length >= 4:
            {
                stack.Add(Cell(BiffReader.U16(data, 0), BiffReader.U16(data, 2), ptg == 0x2C, baseRow, baseColumn));
                return 4;
            }

            case 0x25 or 0x2D when data.Length >= 8:
            {
                var relative = ptg == 0x2D;
                stack.Add(Cell(BiffReader.U16(data, 0), BiffReader.U16(data, 4), relative, baseRow, baseColumn) + ":"
                    + Cell(BiffReader.U16(data, 2), BiffReader.U16(data, 6), relative, baseRow, baseColumn));
                return 8;
            }

            case 0x3A when data.Length >= 6:
                // A 3D reference: conditional formatting cannot refer to other sheets, so it is to the sheet itself.
                stack.Add(Cell(BiffReader.U16(data, 2), BiffReader.U16(data, 4), false, baseRow, baseColumn));
                return 6;
            case 0x3B when data.Length >= 10:
                stack.Add(Cell(BiffReader.U16(data, 2), BiffReader.U16(data, 6), false, baseRow, baseColumn) + ":"
                    + Cell(BiffReader.U16(data, 4), BiffReader.U16(data, 8), false, baseRow, baseColumn));
                return 10;
            case 0x2A when data.Length >= 4:
                stack.Add("#REF!");
                return 4;
            case 0x2B when data.Length >= 8:
                stack.Add("#REF!");
                return 8;
            case 0x29 when data.Length >= 2:
                return 2; // A sub-expression's length: its tokens follow.
            default:
                return -1;
        }
    }

    /// <summary>
    /// A cell's reference: its row and column field (the column's bit 14 and 15 mark a relative column and row). In the
    /// <c>N</c> kind the relative parts are offsets from the base cell (16 bits for the row, 8 for the column).
    /// </summary>
    private static string Cell(int row, int columnField, bool offsets, int baseRow, int baseColumn)
    {
        var relativeColumn = (columnField & 0x4000) != 0;
        var relativeRow = (columnField & 0x8000) != 0;
        var column = columnField & 0xFF;
        if (offsets)
        {
            row = relativeRow ? (baseRow + (short)row) & 0xFFFF : row;
            column = relativeColumn ? (baseColumn + (sbyte)column) & 0xFF : column;
        }

        var letters = string.Empty;
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + ((n - 1) % 26)) + letters;
        }

        return (relativeColumn ? string.Empty : "$") + letters + (relativeRow ? string.Empty : "$") + (row + 1).ToString(CultureInfo.InvariantCulture);
    }
}
