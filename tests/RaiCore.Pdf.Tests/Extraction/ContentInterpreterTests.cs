using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Tests.Reading;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

public class ContentInterpreterTests
{
    // The test pages are 600 x 800 points, so display y = 800 - user y.
    private static (double X, double Y) Origin(TextGlyph glyph) => (Math.Round(glyph.Origin.X, 3), Math.Round(glyph.Origin.Y, 3));

    [Fact]
    public void Glyph_origins_follow_widths_in_display_space()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td (AB) Tj ET", Font(Helvetica)));

        Assert.Equal("AB", sink.Text);
        Assert.Equal((100, 100), Origin(sink.Glyphs[0]));
        Assert.Equal((106, 100), Origin(sink.Glyphs[1]));
        Assert.Equal(111, sink.Glyphs[1].End.X, 3);
        Assert.All(sink.Glyphs, g => Assert.Equal(10, g.FontSize, 3));
        Assert.Equal(["BT", "glyph A", "glyph B", "ET"], sink.Events);
    }

    [Fact]
    public void Glyph_bounds_span_the_advance_and_the_font_ascent_and_descent()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td (A) Tj ET", Font(Helvetica)));

        var glyph = sink.Glyphs[0];
        var font = glyph.Font;
        Assert.Equal(100, glyph.Bounds.Left, 3);
        Assert.Equal(106, glyph.Bounds.Right, 3);
        Assert.Equal(100 - (font.Ascent * 10), glyph.Bounds.Top, 3);
        Assert.Equal(100 - (font.Descent * 10), glyph.Bounds.Bottom, 3);
        Assert.InRange(font.Ascent, 0.6, 1);
        Assert.InRange(font.Descent, -0.4, -0.1);
    }

    [Fact]
    public void TJ_numbers_move_the_pen_back_by_thousandths_of_the_font_size()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td [(A) -1000 (B) 300 (A)] TJ ET", Font(Helvetica)));

        Assert.Equal([100, 116, 118], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
    }

    [Fact]
    public void Character_and_word_spacing_apply_after_each_glyph_and_space()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 2 Tc 5 Tw 100 700 Td (A BA) Tj ET", Font(Helvetica)));

        // A: 6 + 2; space: 2.5 + 2 + 5; B: 5 + 2.
        Assert.Equal([100, 108, 117.5, 124.5], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal([false, true, false, false], sink.Glyphs.Select(g => g.IsWordSpace));
    }

    [Fact]
    public void Horizontal_scaling_narrows_advances_but_not_the_font_size()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 50 Tz 2 Tc 100 700 Td (AB) Tj ET", Font(Helvetica)));

        Assert.Equal([100, 104], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal(10, sink.Glyphs[0].FontSize, 3);
        Assert.Equal(3, sink.Glyphs[0].Bounds.Width, 3);
    }

    [Fact]
    public void TJ_adjustments_are_scaled_horizontally_too()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 50 Tz 100 700 Td [(A) -1000 (B)] TJ ET", Font(Helvetica)));

        Assert.Equal([100, 108], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
    }

    [Fact]
    public void Matrices_multiply_as_in_PDF()
    {
        var product = new Matrix(1, 2, 3, 4, 5, 6).Then(new Matrix(7, 8, 9, 10, 11, 12));

        Assert.Equal(new Matrix(25, 28, 57, 64, 100, 112), product);
    }

    [Fact]
    public void Text_rise_raises_the_baseline()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td (A) Tj 5 Ts (B) Tj -2 Ts (A) Tj ET", Font(Helvetica)));

        Assert.Equal([100, 95, 102], sink.Glyphs.Select(g => Math.Round(g.Origin.Y, 3)));
    }

    [Fact]
    public void Line_operators_move_to_the_next_line_by_the_leading()
    {
        var content = "BT /F1 10 Tf 12 TL 100 700 Td (A) Tj T* (B) Tj (A) ' 3 1 (B) \" 20 -30 TD (A) Tj T* (B) Tj ET";
        var sink = Page(OnePage(content, Font(Helvetica)));

        Assert.Equal(
            [(100, 100), (100, 112), (100, 124), (100, 136), (120, 166), (120, 196)],
            sink.Glyphs.Select(Origin));
    }

    [Fact]
    public void The_double_quote_operator_sets_word_and_character_spacing()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td 4 1 (A B) \" ET", Font(Helvetica)));

        Assert.Equal([100, 107, 114.5], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
    }

    [Fact]
    public void The_text_matrix_and_the_CTM_scale_and_turn_glyphs()
    {
        var content = "q 2 0 0 2 0 0 cm BT /F1 10 Tf 1 0 0 1 50 300 Tm (A) Tj ET Q "
            + "BT /F1 10 Tf 0 1 -1 0 300 300 Tm (AB) Tj ET "
            + "BT /F1 10 Tf (A) Tj ET";
        var sink = Page(OnePage(content, Font(Helvetica)));

        Assert.Equal((100, 200), Origin(sink.Glyphs[0]));
        Assert.Equal(20, sink.Glyphs[0].FontSize, 3);

        // Turned 90 degrees counterclockwise: B is 6 points above A (display y decreases).
        Assert.Equal((300, 500), Origin(sink.Glyphs[1]));
        Assert.Equal((300, 494), Origin(sink.Glyphs[2]));
        Assert.Equal(10, sink.Glyphs[1].FontSize, 3);

        // Q restored the CTM, and BT reset the text matrix.
        Assert.Equal((0, 800), Origin(sink.Glyphs[3]));
    }

    [Fact]
    public void Q_restores_the_text_state_saved_by_q()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 3 Tc q /F1 20 Tf 0 Tc Q 100 700 Td (AB) Tj ET", Font(Helvetica)));

        Assert.Equal([100, 109], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal(10, sink.Glyphs[0].FontSize, 3);
    }

    [Theory]
    [InlineData(0, "", 100, 100)]
    [InlineData(90, "", 700, 100)]
    [InlineData(180, "", 500, 700)]
    [InlineData(270, "", 100, 500)]
    [InlineData(0, "/CropBox [50 50 550 750]", 50, 50)]
    [InlineData(90, "/CropBox [50 30 550 750]", 670, 50)]
    [InlineData(180, "/CropBox [50 30 550 750]", 450, 670)]
    [InlineData(270, "/CropBox [50 30 550 750]", 50, 450)]
    public void Rotation_and_the_crop_box_place_the_display_origin_at_the_top_left(int rotate, string cropBox, double x, double y)
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td (A) Tj ET", Font(Helvetica), $"/MediaBox [0 0 600 800] /Rotate {rotate} {cropBox}"));

        Assert.Equal((x, y), Origin(sink.Glyphs[0]));
    }

    [Fact]
    public void Glyphs_on_a_turned_page_read_from_left_to_right_on_screen()
    {
        // On a page displayed turned by 90 degrees, text drawn upwards in user space reads left to right.
        var sink = Page(OnePage("BT /F1 10 Tf 0 1 -1 0 300 100 Tm (AB) Tj ET", Font(Helvetica), "/MediaBox [0 0 600 800] /Rotate 90"));

        Assert.Equal((100, 300), Origin(sink.Glyphs[0]));
        Assert.Equal((106, 300), Origin(sink.Glyphs[1]));
        Assert.True(sink.Glyphs[0].Bounds.Top < 300 && sink.Glyphs[0].Bounds.Bottom > 300 - 1);
    }

    [Fact]
    public void Form_XObjects_draw_with_their_matrix_and_resources()
    {
        var form = TestPdf.Stream("BT /F2 10 Tf 10 10 Td (A) Tj ET", $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [2 0 0 2 0 0] /Resources << /Font << /F2 {Helvetica} >> >>");
        var inheriting = TestPdf.Stream("BT /F1 10 Tf 0 0 Td (B) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 100 100]");
        var sink = Page(OnePage(
            "q 1 0 0 1 100 100 cm /Fm1 Do Q /Fm2 Do",
            $"<< /Font << /F1 {Helvetica} >> /XObject << /Fm1 5 0 R /Fm2 6 0 R >> >>",
            extra: [form, inheriting]));

        Assert.Equal("AB", sink.Text);
        Assert.Equal((120, 680), Origin(sink.Glyphs[0]));
        Assert.Equal(20, sink.Glyphs[0].FontSize, 3);
        Assert.Equal((0, 800), Origin(sink.Glyphs[1]));
        Assert.Empty(sink.Diagnostics.Messages);
    }

    [Fact]
    public void A_form_that_draws_itself_is_skipped_with_a_diagnostic()
    {
        var form = TestPdf.Stream($"BT /F1 10 Tf (A) Tj ET /Fm1 Do", $"/Subtype /Form /BBox [0 0 1 1] /Resources << /Font << /F1 {Helvetica} >> /XObject << /Fm1 5 0 R >> >>");
        var sink = Page(OnePage("/Fm1 Do", "<< /XObject << /Fm1 5 0 R >> >>", extra: [form]));

        Assert.Equal("A", sink.Text);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("draws itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Forms_nested_too_deeply_are_skipped()
    {
        // Form n draws form n+1: a chain longer than the nesting limit.
        var count = ContentInterpreter.MaxFormDepth + 4;
        var forms = Enumerable.Range(0, count).Select(i => TestPdf.Stream(
            $"BT /F1 10 Tf (A) Tj ET /Next Do",
            $"/Subtype /Form /BBox [0 0 1 1] /Resources << /Font << /F1 {Helvetica} >> /XObject << /Next {6 + i} 0 R >> >>")).ToArray();
        var sink = Page(OnePage("/Fm1 Do", "<< /XObject << /Fm1 5 0 R >> >>", extra: forms));

        Assert.Equal(new string('A', ContentInterpreter.MaxFormDepth), sink.Text);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("nested more than", StringComparison.Ordinal));
    }

    [Fact]
    public void Forms_that_multiply_the_work_stop_at_the_operation_budget()
    {
        // Each form draws the next one ten times: 10^12 glyphs without the budget.
        var forms = Enumerable.Range(0, 12).Select(i => TestPdf.Stream(
            i == 11 ? "BT /F1 10 Tf (A) Tj ET" : string.Concat(Enumerable.Repeat("/Next Do ", 10)),
            $"/Subtype /Form /BBox [0 0 1 1] /Resources << /Font << /F1 {Helvetica} >> /XObject << /Next {6 + i} 0 R >> >>")).ToArray();
        var sink = Page(OnePage("/Fm1 Do", "<< /XObject << /Fm1 5 0 R >> >>", extra: forms), maxOperations: 50_000);

        Assert.InRange(sink.Glyphs.Count, 1, 50_000);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("more than 50000 operators", StringComparison.Ordinal));
    }

    [Fact]
    public void Images_are_reported_with_their_placement()
    {
        var image = TestPdf.Stream("\u00FF\u0000", "/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8");
        var sink = Page(OnePage("q 50 0 0 80 100 200 cm /Im1 Do Q", "<< /XObject << /Im1 5 0 R >> >>", extra: [image]));

        var placement = Assert.Single(sink.Images);
        Assert.Equal(new Box(100, 520, 150, 600), Round(placement.Bounds));
        Assert.Equal("Im1", placement.ResourceName);
        Assert.Equal(5, placement.Reference!.ObjectNumber);
        Assert.NotNull(placement.XObject);
        Assert.Null(placement.Inline);

        // The unit square's top-left corner (0, 1) is the image's first pixel.
        Assert.Equal(new PdfPoint(100, 520), placement.Transform.Transform(0, 1));
    }

    [Fact]
    public void Inline_images_are_read_to_their_end_even_when_the_data_contains_EI()
    {
        // Four bytes of gray pixels, the last three a space and "EI"; then text that must still be read.
        var content = "q 100 0 0 50 10 20 cm BI /W 4 /H 1 /BPC 8 /CS /G ID \u0001 EI EI Q BT /F1 10 Tf 1 2 Td (A) Tj ET";
        var sink = Page(OnePage(content, Font(Helvetica)));

        var placement = Assert.Single(sink.Images);
        Assert.Equal(new Box(10, 730, 110, 780), Round(placement.Bounds));
        Assert.Equal([1, (byte)' ', (byte)'E', (byte)'I'], placement.Inline!.Data);
        Assert.Equal("A", sink.Text);
    }

    [Fact]
    public void Inline_images_with_a_filter_end_at_the_first_EI_between_whitespace()
    {
        var content = "BI /W 3 /H 1 /CS /RGB /BPC 8 /F /AHx ID 41EI 4243> EI BT /F1 10 Tf (B) Tj ET";
        var sink = Page(OnePage(content, Font(Helvetica)));

        Assert.Equal("41EI 4243>"u8.ToArray(), Assert.Single(sink.Images).Inline!.Data);
        Assert.Equal("B", sink.Text);
    }

    [Fact]
    public void An_inline_image_without_EI_runs_to_the_end()
    {
        var sink = Page(OnePage("BI /W 1 /H 1 /F /AHx ID 00", Font(Helvetica)));

        Assert.Single(sink.Images);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("no end (EI)", StringComparison.Ordinal));
    }

    [Fact]
    public void Marked_content_is_reported_with_its_properties()
    {
        var content = "/Span <</ActualText (fi)>> BDC BT /F1 10 Tf (A) Tj ET EMC "
            + "/P <</MCID 3>> BDC /Artifact BMC (B) Tj EMC EMC EMC "
            + "/OC /oc1 BDC (A) Tj";
        var resources = $"<< /Font << /F1 {Helvetica} >> /Properties << /oc1 << /Type /OCG /Name (Layer) >> >> >>";
        var sink = Page(OnePage(content, resources));

        Assert.Equal(
            ["begin Span ActualText=fi", "BT", "glyph A", "ET", "end", "begin P MCID=3", "begin Artifact", "glyph B", "end", "end",
             "begin OC Type=/OCG Name=Layer", "glyph A", "end"],
            sink.Events);
    }

    [Fact]
    public void Invisible_text_is_reported_with_its_render_mode()
    {
        var sink = Page(OnePage("BT /F1 10 Tf 3 Tr (A) Tj 0 Tr (B) Tj ET", Font(Helvetica)));

        Assert.Equal([3, 0], sink.Glyphs.Select(g => g.RenderMode));
    }

    [Fact]
    public void Missing_fonts_and_XObjects_are_reported_and_text_is_still_read()
    {
        var sink = Page(OnePage("BT (A) Tj /F9 12 Tf (B) Tj ET /Im9 Do", "<< >>"));

        Assert.Equal("AB", sink.Text);
        Assert.Equal(12, sink.Glyphs[1].FontSize, 3);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("without a font", StringComparison.Ordinal));
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("/F9 is missing", StringComparison.Ordinal));
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("/Im9 is drawn", StringComparison.Ordinal));
    }

    [Fact]
    public void A_content_stream_that_cannot_be_decoded_is_skipped_and_the_others_are_read()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources {Font(Helvetica)} /Contents [4 0 R 5 0 R 6 0 R] >>",
            TestPdf.Stream("BT /F1 10 Tf (A) Tj ET"),
            TestPdf.Stream("garbage", "/Filter /JBIG2Decode"),
            TestPdf.Stream("BT /F1 10 Tf (B) Tj ET"),
        ]);
        var sink = Page(pdf);

        Assert.Equal("AB", sink.Text);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("could not be decoded", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_content_is_skipped()
    {
        var nested = new string('[', 150) + new string(']', 150);
        var content = $") ] >> {{ }} 1 2 3 Tj {nested} Tj BT /F1 10 Tf 5 Tz Tm (A) Tj Q Q Q EMC ET ET (B) Tj cm 1 0 0 0 0 0 Td";
        var sink = Page(OnePage(content, Font(Helvetica)));

        Assert.Equal("AB", sink.Text);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("operand skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Graphics_states_saved_too_deeply_keep_save_and_restore_balanced()
    {
        // Each level moves one point to the right; the saves past the limit are ignored, and so are their restores.
        var depth = ContentInterpreter.MaxStateDepth + 10;
        var text = "BT /F1 10 Tf 100 700 Td (A) Tj ET ";
        var content = string.Concat(Enumerable.Repeat("q 1 0 0 1 1 0 cm ", depth)) + "Q Q Q Q Q Q Q Q Q Q " + text
            + string.Concat(Enumerable.Repeat("Q ", ContentInterpreter.MaxStateDepth)) + text;
        var sink = Page(OnePage(content, Font(Helvetica)));

        Assert.Equal([(100 + depth, 100), (100, 100)], sink.Glyphs.Select(Origin));
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("levels deep", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_of_an_encrypted_file_is_read()
    {
        var pdf = Fixtures.Read("Reader/Encrypted/aes-256-objstm.pdf");

        Assert.Equal("One", Page(pdf, 0, "user").Text);
        Assert.Equal("Two", Page(pdf, 1, "user").Text);
    }

    private static Box Round(Box box) => new(Math.Round(box.Left, 3), Math.Round(box.Top, 3), Math.Round(box.Right, 3), Math.Round(box.Bottom, 3));
}
