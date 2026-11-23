using RaiCore.Pdf.Documents.Doc;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>Word 97-2003 property modifiers ([MS-DOC] 2.6): their operand sizes and what they set.</summary>
public class DocPropertiesTests
{
    private static List<(ushort Code, byte[] Operand)> Modifiers(byte[] grpprl)
    {
        var result = new List<(ushort, byte[])>();
        var reader = new SprmReader(grpprl);
        while (reader.Next())
        {
            result.Add((reader.Code, reader.Operand.ToArray()));
        }

        return result;
    }

    [Fact]
    public void Operand_sizes_follow_the_spra_bits()
    {
        byte[] grpprl =
        [
            0x35, 0x08, 0x01, // sprmCFBold: 1 byte (spra 0).
            0x43, 0x4A, 0x18, 0x00, // sprmCHps: 2 bytes (spra 2).
            0x70, 0x68, 0x01, 0x02, 0x03, 0x00, // sprmCCv: 4 bytes (spra 3).
            0x0D, 0xC6, 0x02, 0xAA, 0xBB, // sprmPChgTabsPapx: variable, a size byte first (spra 6).
            0x00, 0xE0, 0x01, 0x02, 0x03, // A 3-byte operand (spra 7).
            0x08, 0xD6, 0x03, 0x00, 0x01, 0x02, // sprmTDefTable: a 2-byte size counting itself less one.
            0x35, 0x08, // Truncated: the list ends.
        ];
        var modifiers = Modifiers(grpprl);
        Assert.Equal([0x0835, 0x4A43, 0x6870, 0xC60D, 0xE000, 0xD608], modifiers.Select(m => m.Code));
        Assert.Equal([1, 2, 4, 2, 3, 2], modifiers.Select(m => m.Operand.Length));
        Assert.Equal([0x01, 0x02], modifiers[5].Operand);

        // sprmPChgTabs with the size 255: computed from its counts (one deletion of 4 bytes, one addition of 3).
        byte[] tabs = [0x15, 0xC6, 0xFF, 0x01, 0x10, 0x00, 0x05, 0x00, 0x01, 0x20, 0x00, 0x01, 0x35, 0x08, 0x00];
        var computed = Modifiers(tabs);
        Assert.Equal(9, computed[0].Operand.Length);
        Assert.Equal((ushort)0x0835, computed[1].Code);
        Assert.Empty(Modifiers([0x15, 0xC6, 0xFF, 0x09]));
        Assert.Empty(Modifiers([0x08, 0xD6, 0xFF]));
        Assert.Empty(Modifiers([0x0D, 0xC6]));
    }

    [Fact]
    public void Toggles_refer_to_the_style()
    {
        var style = DocChp.Default(0);
        style.Bold = true;
        var chp = style;
        DocProperties.ApplyCharacter(ref chp, [0x35, 0x08, 0x81, 0x36, 0x08, 0x81], style);
        Assert.False(chp.Bold); // 0x81: the opposite of the style's.
        Assert.True(chp.Italic);
        DocProperties.ApplyCharacter(ref chp, [0x35, 0x08, 0x80, 0x36, 0x08, 0x00], style);
        Assert.True(chp.Bold); // 0x80: the style's.
        Assert.False(chp.Italic);
        DocProperties.ApplyCharacter(ref chp, [0x37, 0x08, 0x01, 0x53, 0x2A, 0x01, 0x3A, 0x08, 0x01, 0x3B, 0x08, 0x01, 0x3C, 0x08, 0x01, 0x00, 0x08, 0x01, 0x55, 0x08, 0x01], style);
        Assert.True(chp.Strike && chp.DoubleStrike && chp.SmallCaps && chp.Caps && chp.Vanish && chp.Deleted && chp.Special);
    }

    [Fact]
    public void Character_modifiers_set_their_properties()
    {
        var chp = DocChp.Default(3);
        Assert.Equal(20, chp.HalfPoints);
        Assert.Equal(3, chp.Font);
        Assert.Equal(-1, chp.PictureLocation);
        DocProperties.ApplyCharacter(ref chp,
        [
            0x43, 0x4A, 0x30, 0x00, // 24 points.
            0x3E, 0x2A, 0x03, // Double underline.
            0x42, 0x2A, 0x06, // Red (ico).
            0x0C, 0x2A, 0x07, // Yellow highlight.
            0x48, 0x2A, 0x01, // Superscript.
            0x45, 0x48, 0x06, 0x00, // Raised 3 points.
            0x40, 0x88, 0x28, 0x00, // 2 points of letter spacing.
            0x4F, 0x4A, 0x05, 0x00, // Font 5.
            0x03, 0x6A, 0x10, 0x00, 0x00, 0x00, // A picture at 16.
            0x09, 0x6A, 0x02, 0x00, 0xB7, 0xF0, // Symbol 0xF0B7 in font 2.
            0x5A, 0x08, 0x01, // Right-to-left.
            0x0A, 0x08, 0x01, // An embedded object.
        ], chp);
        Assert.Equal(48, chp.HalfPoints);
        Assert.Equal(3, chp.Underline);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), chp.Color);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), chp.Highlight);
        Assert.Equal(1, chp.Iss);
        Assert.Equal(6, chp.HalfPointPosition);
        Assert.Equal(40, chp.Spacing);
        Assert.Equal(5, chp.Font);
        Assert.Equal(16, chp.PictureLocation);
        Assert.Equal((2, '\uF0B7'), (chp.SymbolFont, chp.SymbolCharacter));
        Assert.True(chp.RightToLeft && chp.Ole2);

        // RGB colors, automatic ones, shading and limits.
        DocProperties.ApplyCharacter(ref chp, [0x70, 0x68, 0x10, 0x20, 0x30, 0x00, 0x43, 0x4A, 0xFF, 0xFF, 0x71, 0xCA, 0x0A, 0, 0, 0, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00], chp);
        Assert.Equal(PdfColor.FromRgb(0x10, 0x20, 0x30), chp.Color);
        Assert.Equal(3276, chp.HalfPoints);
        Assert.Equal(PdfColor.FromRgb(0x00, 0xFF, 0x00), chp.Shading);
        DocProperties.ApplyCharacter(ref chp, [0x70, 0x68, 0x10, 0x20, 0x30, 0xFF, 0x42, 0x2A, 0x11], chp);
        Assert.Null(chp.Color);
    }

    [Fact]
    public void Paragraph_modifiers_set_their_properties()
    {
        var pap = DocPap.Default;
        Assert.True(pap.WidowControl);
        Assert.Equal(0, pap.TableLevel);
        DocProperties.ApplyParagraph(ref pap,
        [
            0x00, 0x46, 0x02, 0x00, // Style 2.
            0x61, 0x24, 0x03, // Justified.
            0x05, 0x24, 0x01, 0x06, 0x24, 0x01, 0x07, 0x24, 0x01, 0x31, 0x24, 0x00, // Keep, keep with next, page break before, no widow control.
            0x0A, 0x26, 0x02, 0x0B, 0x46, 0x03, 0x00, // Level 2 of list override 3.
            0x5E, 0x84, 0xD0, 0x02, 0x5D, 0x84, 0x68, 0x01, 0x60, 0x84, 0x98, 0xFE, // Indents 720, 360, -360.
            0x13, 0xA4, 0xF0, 0x00, 0x14, 0xA4, 0x78, 0x00, // 240 before, 120 after.
            0x12, 0x64, 0xEC, 0xFF, 0x00, 0x00, // Exactly 20 twips (negative: exact).
            0x16, 0x24, 0x01, 0x49, 0x66, 0x02, 0x00, 0x00, 0x00, 0x4B, 0x24, 0x01, 0x4C, 0x24, 0x01, // In a nested table cell.
            0x40, 0x26, 0x01, 0x41, 0x24, 0x01, 0x6D, 0x24, 0x01, // Outline level 1, right-to-left, contextual spacing.
            0x24, 0x64, 0x08, 0x01, 0x06, 0x00, // A red single top border of 1 point (Brc80).
            0x2D, 0x44, 0xE0, 0x00, // Shading: clear with a yellow back.
        ]);
        Assert.Equal(2, pap.Style);
        Assert.Equal(3, pap.Justification);
        Assert.True(pap.Keep && pap.KeepFollow && pap.PageBreakBefore);
        Assert.False(pap.WidowControl);
        Assert.Equal((2, 3), (pap.ListLevel, pap.ListOverride));
        Assert.Equal((720, 360, -360), (pap.Left, pap.Right, pap.FirstLine));
        Assert.Equal((240, 120), (pap.Before, pap.After));
        Assert.Equal((-20, false), (pap.Line, pap.LineMultiple));
        Assert.Equal(2, pap.TableLevel);
        Assert.True(pap.InnerCellEnd && pap.InnerRowEnd && pap.RightToLeft && pap.ContextualSpacing);
        Assert.Equal(1, pap.OutlineLevel);
        Assert.Equal(new BorderLine(1, PdfColor.FromRgb(255, 0, 0)), pap.BorderTop);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), pap.Shading);

        // Tab stops: two added (centered with dots, right), then one deleted.
        DocProperties.ApplyParagraph(ref pap, [0x0D, 0xC6, 0x08, 0x00, 0x02, 0xA0, 0x05, 0xD0, 0x02, 0x09, 0x02]);
        Assert.Equal([(720, (byte)0x02), (1440, (byte)0x09)], pap.Tabs!.Select(t => ((int)t.Position, t.Descriptor)));
        DocProperties.ApplyParagraph(ref pap, [0x0D, 0xC6, 0x04, 0x01, 0xD0, 0x02, 0x00]);
        Assert.Equal([1440], pap.Tabs!.Select(t => (int)t.Position));
        DocProperties.ApplyParagraph(ref pap, [0x4A, 0x66, 0xFF, 0xFF, 0xFF, 0xFF]); // sprmPDtap: one level less.
        Assert.Equal(1, pap.TableLevel);
    }

    [Fact]
    public void Borders_and_shading_decode()
    {
        Assert.Equal(BorderLine.None, DocProperties.Border([0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal(BorderLine.None, DocProperties.Border([0x08, 0x00, 0x01, 0x00]));
        Assert.Equal(new BorderLine(0.75, PdfColor.Black, BorderLineStyle.Double), DocProperties.Border([0x06, 0x03, 0x00, 0x00]));
        Assert.Equal(BorderLineStyle.Dotted, DocProperties.Border([0x04, 0x06, 0x01, 0x00]).Style);
        Assert.Equal(BorderLineStyle.Dashed, DocProperties.Border([0x04, 0x07, 0x01, 0x00]).Style);
        Assert.Equal(0.25, DocProperties.Border([0x10, 0x05, 0x01, 0x00]).Width);
        Assert.Equal(new BorderLine(2, PdfColor.FromRgb(1, 2, 3)), DocProperties.Border([0x01, 0x02, 0x03, 0x00, 0x10, 0x01, 0x00, 0x00]));
        Assert.Equal(BorderLine.None, DocProperties.Border([0x01, 0x02, 0x03, 0x00, 0x10, 0xFF, 0x00, 0x00]));

        // Shd80: fore 1 (black), back 8 (white), patterns clear, solid and 50%.
        Assert.Equal(PdfColor.White, DocProperties.Shading([0x01, 0x01]));
        Assert.Equal(PdfColor.Black, DocProperties.Shading([0x01, 0x05]));
        Assert.Equal(new PdfColor(0.5, 0.5, 0.5), DocProperties.Shading([0x01, 0x21]));
        Assert.Null(DocProperties.Shading([0xFF, 0xFF]));
        Assert.Null(DocProperties.Shading([0x00, 0x00]));
        Assert.Null(DocProperties.Shading([0x01]));

        // Shd: RGB colors.
        Assert.Equal(PdfColor.FromRgb(10, 20, 30), DocProperties.Shading([0, 0, 0, 0xFF, 10, 20, 30, 0, 0, 0]));
        Assert.Equal(PdfColor.FromRgb(1, 2, 3), DocProperties.Shading([1, 2, 3, 0, 10, 20, 30, 0, 1, 0]));
        Assert.Equal(new PdfColor((10 / 255.0 * 0.1) + (1 - 0.1), (20 / 255.0 * 0.1) + (1 - 0.1), (30 / 255.0 * 0.1) + (1 - 0.1)), DocProperties.Shading([10, 20, 30, 0, 0, 0, 0, 0xFF, 3, 0]));
    }

    [Fact]
    public void Sections_and_rows_decode()
    {
        var sep = DocSep.Default;
        DocProperties.ApplySection(ref sep,
        [
            0x09, 0x30, 0x00, 0x0A, 0x30, 0x01, 0x0B, 0x50, 0x01, 0x00, 0x0E, 0x30, 0x02, 0x11, 0x30, 0x01, 0x1C, 0x50, 0x05, 0x00,
            0x1F, 0xB0, 0x82, 0x2E, 0x20, 0xB0, 0xC6, 0x41, 0x21, 0xB0, 0x8A, 0x05, 0x22, 0xB0, 0x37, 0x02,
            0x23, 0x90, 0xA0, 0x05, 0x24, 0x90, 0x60, 0xFA, 0x25, 0xB0, 0x68, 0x01, 0x17, 0xB0, 0xC5, 0x02, 0x18, 0xB0, 0xD0, 0x02,
        ]);
        Assert.Equal((0, true, 2, 2, true, 5), (sep.Break, sep.TitlePage, sep.Columns, sep.PageNumberFormat, sep.RestartPageNumbers, sep.PageNumberStart));
        Assert.Equal((11906, 16838, 1418, 567, 1440, -1440, 360, 709, 720), (sep.PageWidth, sep.PageHeight, sep.Left, sep.Right, sep.Top, sep.Bottom, sep.Gutter, sep.HeaderTop, sep.FooterBottom));

        // Two cells from -108 to 4000 and 8000; the first starts a merge, the second continues a vertical one.
        byte[] defTable =
        [
            0x08, 0xD6, 0x30, 0x00, 0x02, 0x94, 0xFF, 0xA0, 0x0F, 0x40, 0x1F,
            0x81, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x20, 0x01, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x02, 0x96, 0x6C, 0x00, // Gap 108.
            0x8A, 0x54, 0x01, 0x00, // Centered.
            0x04, 0x34, 0x01, 0x03, 0x34, 0x01, 0x07, 0x94, 0x10, 0xFE, // Header row, cannot split, exactly 496 twips.
            0x09, 0xD6, 0x04, 0x00, 0x00, 0xE0, 0x00, // Cell shading (Shd80): none, then clear with a yellow back.
            0x2C, 0xD6, 0x03, 0x01, 0x02, 0x02, // Second cell at the bottom.
            0x34, 0xD6, 0x06, 0x00, 0x02, 0x0F, 0x03, 0x2D, 0x00, // Padding 45 on all sides.
        ];
        var row = DocProperties.Row(defTable)!;
        Assert.Equal(-108, row.Left);
        Assert.Equal([4000, 8000], row.Cells.Select(c => c.Right));
        Assert.Equal((1, 0), (row.Cells[0].HorizontalMerge, row.Cells[0].VerticalMerge));
        Assert.Equal(CellVerticalAlignment.Center, row.Cells[0].VerticalAlignment);
        Assert.Equal(1, row.Cells[1].VerticalMerge);
        Assert.Equal(CellVerticalAlignment.Bottom, row.Cells[1].VerticalAlignment);
        Assert.Equal(new BorderLine(1, PdfColor.Black), row.Cells[0].Top);
        Assert.Null(row.Cells[1].Top);
        Assert.Equal((108, (byte)1, true, true, -496), (row.GapHalf, row.Justification, row.Header, row.CantSplit, row.Height));
        Assert.Null(row.Cells[0].Shading);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), row.Cells[1].Shading);
        Assert.Equal((45, 45, 45, 45), (row.PaddingTop, row.PaddingLeft, row.PaddingBottom, row.PaddingRight));
        Assert.Null(DocProperties.Row([0x16, 0x24, 0x01]));
    }
}
