using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;

namespace RaiCore.Pdf.Tests.Tagging;

/// <summary>
/// A tagged business report drawn with the core API only, the way a caller with its own layout code uses it: a
/// renderer that places text in rectangles (top-left origin), draws rules, fills, ellipses and curves, and counts its
/// pages in a first pass for "Side n af m". It must be PDF/UA-1, deterministic, and tagged throughout (strict).
/// </summary>
public class TaggedReportTests
{
    /// <summary>
    /// The SHA-256 of the report. A change to the library's output changes it: when the change is intended, check the
    /// report (veraPDF in CI, and by eye) and put the new hash here.
    /// </summary>
    private const string GoldenHash = "852bd9c06ade83d00ff5b0ec6f64d11ba360c0f48197b011461bdd19e63e1012";

    private const int Rows = 30;

    private static readonly PdfFont Regular = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Bold = PdfFont.Load(Fixtures.Path("LiberationSans-Bold.ttf"));

    /// <summary>Danish and typographic characters that must survive text extraction.</summary>
    private const string Explanation =
        "Forklaring: \u00abAfvigelse\u00bb er forbrug \u2212 budget \u00b7 tal i kr. \u2013 \u00e6\u00f8\u00e5 \u00c6\u00d8\u00c5 \u2026";

    [Fact]
    public void The_report_has_the_expected_structure_in_reading_order()
    {
        var root = TaggedPdfInspector.Inspect(Report());

        var body = string.Join(' ', Enumerable.Repeat("TR(TD TD TD TD)", Rows));
        Assert.Equal($"Document(H1 H2 Div(P P) Figure H3 Table(THead(TR(TH TH TH TH)) TBody({body}) TFoot(TR(TD))))", root.ToString());
        Assert.Equal("Budgetopf\u00f8lgning 2026", root.Children[0].Text);
        Assert.Equal(Explanation, root.Descendants().Single(e => e.Type == "TFoot").Text);
        Assert.Equal("Konto 30", root.Descendants().Where(e => e.Type == "TD").ElementAt((Rows - 1) * 4).Text);
    }

    [Fact]
    public void Table_cells_carry_their_attributes_and_the_table_spans_both_pages()
    {
        var root = TaggedPdfInspector.Inspect(Report());
        var table = root.Descendants().Single(e => e.Type == "Table");
        var headers = table.Descendants().Where(e => e.Type == "TH").ToList();
        var explanation = table.Descendants().Single(e => e.Type == "TFoot").Descendants().Single(e => e.Type == "TD");

        Assert.All(headers, th => Assert.Equal("Column", (Attribute(th, "Table", "Scope") as PdfName)?.Value));
        Assert.Equal(4, (Attribute(explanation, "Table", "ColSpan") as PdfInteger)?.Value);
        Assert.Equal([0, 1], table.Descendants().SelectMany(e => e.Content).Select(c => c.Page).Distinct().Order());
        Assert.Null(Attribute(table, "Layout", "BBox")); // On two pages: no box.
    }

    [Fact]
    public void The_figure_has_alternate_text_and_a_bounding_box()
    {
        var figure = TaggedPdfInspector.Inspect(Report()).Children.Single(e => e.Type == "Figure");

        Assert.Equal("Tre smileys og en kurve over forbruget", figure.Entry("Alt"));
        var box = Assert.IsType<PdfArray>(Attribute(figure, "Layout", "BBox")).Items.Select(Number).ToArray();
        Assert.Equal(40 - 1.125, box[0], 3); // The left smiley's edge, and three quarters of its 1.5 pt outline.
        Assert.True(box[2] > 700 && box[3] > box[1] + 60, $"BBox [{string.Join(' ', box)}]");
    }

    [Fact]
    public void Metadata_pages_and_fonts_are_as_a_PdfUA_report_needs()
    {
        var bytes = Report();
        var reader = PdfReader.Open(bytes);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Equal(2, reader.Pages.Count);
        Assert.All(reader.Pages, page => Assert.Equal((841.89, 595.28), (Math.Round(page.Width, 2), Math.Round(page.Height, 2))));
        Assert.Equal("da-DK", PdfText.Decode(Assert.IsType<PdfString>(reader.Catalog["Lang"]).Bytes));
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+LiberationSans\b", text);
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+LiberationSans-Bold\b", text); // A real bold face, subset.
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", text, StringComparison.Ordinal);
        Assert.Contains("/DisplayDocTitle true", text, StringComparison.Ordinal);
        Assert.True(bytes.Length < 80_000, $"{bytes.Length} bytes: the fonts should be subset"); // Each full face is over 300 KB.
    }

    [Fact]
    public void The_report_is_deterministic_and_matches_its_golden_hash()
    {
        var first = Report();
        var hash = Convert.ToHexStringLower(SHA256.HashData(first));

        Assert.Equal(first, Report());
        Assert.True(hash == GoldenHash, $"The report's bytes changed: its SHA-256 is now {hash}. If that is intended, update GoldenHash.");
    }

    [Fact]
    public void The_report_validates_as_PdfUA_with_veraPDF() => ExternalTools.AssertVeraPdfCompliant(Report(), "ua1");

    [Fact]
    public void The_report_passes_qpdf() => ExternalTools.AssertQpdfClean(Report());

    [Fact]
    public void Extracted_text_round_trips_through_the_ToUnicode_maps()
    {
        var text = ExternalTools.ExtractText(Report()).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("Budgetopf\u00f8lgning 2026", text, StringComparison.Ordinal);
        Assert.Contains(Explanation, text, StringComparison.Ordinal);
        Assert.Contains("Side 1 af 2", text, StringComparison.Ordinal);
        Assert.Contains("Side 2 af 2", text, StringComparison.Ordinal);
    }

    /// <summary>Renders the report twice, as callers without a forward reference do: once to count the pages, once for real.</summary>
    private static byte[] Report()
    {
        var pages = new ReportRenderer(totalPages: null).Render().Pages.Count;
        return new ReportRenderer(pages).Render().ToArray();
    }

    private static PdfObject? Attribute(InspectedElement element, string owner, string key)
    {
        var attributes = element.Dictionary["A"] switch
        {
            PdfDictionary single => [single],
            PdfArray several => several.Items.OfType<PdfDictionary>().ToList(),
            _ => [],
        };
        return attributes.FirstOrDefault(a => a["O"] is PdfName name && name.Value == owner)?[key];
    }

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => double.NaN,
    };

    /// <summary>
    /// The renderer a report generator would write on top of the core API: top-left coordinates, text placed in
    /// rectangles, and every painted thing either tagged or an artifact.
    /// </summary>
    private sealed class ReportRenderer(int? totalPages)
    {
        private const double Width = 841.89;
        private const double Height = 595.28;
        private const double Margin = 40;
        private const double Bottom = Height - 50;
        private const double RowHeight = 18;
        private static readonly double[] Columns = [Margin, 260, 420, 580, Width - Margin];
        private static readonly PdfColor Black = new(0, 0, 0);
        private static readonly PdfColor Grey = new(0.45, 0.45, 0.45);
        private static readonly PdfColor White = new(1, 1, 1);
        private static readonly PdfColor Stripe = new(0.94, 0.95, 0.97);
        private static readonly PdfColor Accent = new(0.0, 0.36, 0.62);

        private readonly PdfDocument _doc = new()
        {
            PdfUA = true,
            StrictTagging = true,
            Language = "da-DK",
            TimeProvider = new DeterminismTests.FixedClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(1))),
            Info = { Title = "Budgetopf\u00f8lgning 2026", Author = "\u00d8konomiafdelingen" },
        };

        private PdfCanvas _canvas = null!;
        private double _y;

        public PdfDocument Render()
        {
            NewPage();
            var root = _doc.StructureRoot;
            Text(root.AddChild(PdfStructureTypes.Heading1), "Budgetopf\u00f8lgning 2026", Bold, 20, Margin, _y, 500, 28);
            _y += 36;
            Text(root.AddChild(PdfStructureTypes.Heading2), "Overblik", Bold, 14, Margin, _y, 500, 20);
            _y += 24;

            // A summary box: a Div with two paragraphs on a filled, outlined background.
            var summary = root.AddChild(PdfStructureTypes.Division);
            Artifact(() => _canvas.SetFillColor(Stripe).SetStrokeColor(Accent).SetLineWidth(0.75).Rectangle(Margin, Y(_y + 44), 500, 44).FillAndStroke());
            Text(summary.AddChild(PdfStructureTypes.Paragraph), "Forbruget ligger 3,2 % under budgettet \u2013 prim\u00e6rt p\u00e5 l\u00f8n.", Regular, 10, Margin + 8, _y + 4, 480, 18);
            Text(summary.AddChild(PdfStructureTypes.Paragraph), "N\u00e6ste opf\u00f8lgning: 1. juni \u00b7 ansvarlig: \u00d8konomi", Regular, 10, Margin + 8, _y + 22, 480, 18);
            _y += 56;

            DrawFigure(root.AddChild(PdfStructureTypes.Figure));
            _y += 100;
            Text(root.AddChild(PdfStructureTypes.Heading3), "Detaljer pr. konto", Bold, 12, Margin, _y, 500, 18);
            _y += 22;
            DrawTable(root.AddChild(PdfStructureTypes.Table));
            Footer();
            return _doc;
        }

        /// <summary>Three smileys (ellipses, round-capped mouths) and a trend curve, with alternate text.</summary>
        private void DrawFigure(PdfStructureElement figure)
        {
            figure.AlternateText = "Tre smileys og en kurve over forbruget";
            _canvas.BeginTag(figure).SaveState();
            PdfColor[] faces = [new(0.2, 0.7, 0.3), new(1, 0.8, 0), new(0.85, 0.2, 0.2)];
            for (var i = 0; i < 3; i++)
            {
                var (cx, cy) = (Margin + 30 + (i * 80), Y(_y + 40));
                _canvas.SetFillColor(faces[i]).SetStrokeColor(Black).SetLineWidth(1.5);
                Ellipse(cx, cy, 30, 30);
                _canvas.FillAndStroke().SetFillColor(Black);
                Ellipse(cx - 10, cy + 8, 3, 3);
                _canvas.Fill();
                Ellipse(cx + 10, cy + 8, 3, 3);
                _canvas.Fill();
                var smile = 8 - (i * 8); // Happy, neutral, sad.
                _canvas.SetLineCap(LineCap.Round).SetLineWidth(2.5).MoveTo(cx - 12, cy - 10).CurveTo(cx - 6, cy - 10 - smile, cx + 6, cy - 10 - smile, cx + 12, cy - 10).Stroke();
            }

            _canvas.SetLineCap(LineCap.Butt).SetStrokeColor(Accent).SetLineWidth(2).SetDash([6, 3])
                .MoveTo(320, Y(_y + 80)).CurveTo(420, Y(_y - 10), 560, Y(_y + 90), 760, Y(_y + 10)).Stroke();
            _canvas.RestoreState().EndTag();
        }

        /// <summary>A table with a header row (repeated as an artifact on later pages), striped rows and an explanation footer.</summary>
        private void DrawTable(PdfStructureElement table)
        {
            string[] heads = ["Konto", "Budget", "Forbrug", "Afvigelse"];
            var head = table.AddChild(PdfStructureTypes.TableHead).AddChild(PdfStructureTypes.TableRow);
            var cells = heads.Select(_ =>
            {
                var th = head.AddChild(PdfStructureTypes.TableHeaderCell);
                th.Scope = PdfTableScope.Column;
                return th;
            }).ToArray();
            HeaderRow(heads, cells);

            var body = table.AddChild(PdfStructureTypes.TableBody);
            for (var row = 1; row <= Rows; row++)
            {
                if (_y + RowHeight > Bottom)
                {
                    Footer();
                    NewPage();
                    HeaderRow(heads, null); // Repeated header: an artifact.
                }

                if (row % 2 == 0)
                {
                    Artifact(() => _canvas.SetFillColor(Stripe).Rectangle(Margin, Y(_y + RowHeight), Width - (2 * Margin), RowHeight).Fill());
                }

                var tr = body.AddChild(PdfStructureTypes.TableRow);
                var budget = row * 12_500;
                var spent = budget - (row * 731);
                string[] values = [$"Konto {row}", Amount(budget), Amount(spent), Amount(spent - budget)];
                for (var c = 0; c < 4; c++)
                {
                    Text(tr.AddChild(PdfStructureTypes.TableDataCell), values[c], Regular, 9, Columns[c] + 4, _y, Columns[c + 1] - Columns[c] - 8, RowHeight, right: c > 0);
                }

                _y += RowHeight;
            }

            Artifact(() => _canvas.SetStrokeColor(Grey).SetLineWidth(0.5).MoveTo(Margin, Y(_y)).LineTo(Width - Margin, Y(_y)).Stroke());
            var explanation = table.AddChild(PdfStructureTypes.TableFoot).AddChild(PdfStructureTypes.TableRow).AddChild(PdfStructureTypes.TableDataCell);
            explanation.ColumnSpan = 4;
            Text(explanation, Explanation, Regular, 9, Margin + 4, _y + 2, Width - (2 * Margin) - 8, RowHeight);
            _y += RowHeight;
        }

        private void HeaderRow(string[] heads, PdfStructureElement[]? cells)
        {
            Artifact(() => _canvas.SetFillColor(Accent).Rectangle(Margin, Y(_y + RowHeight), Width - (2 * Margin), RowHeight).Fill());
            for (var c = 0; c < heads.Length; c++)
            {
                var (x, width) = (Columns[c] + 4, Columns[c + 1] - Columns[c] - 8);
                if (cells is null)
                {
                    Artifact(() => DrawString(heads[c], Bold, 9, x, _y, width, RowHeight, right: c > 0, White));
                }
                else
                {
                    Text(cells[c], heads[c], Bold, 9, x, _y, width, RowHeight, right: c > 0, White);
                }
            }

            _y += RowHeight;
        }

        private void NewPage()
        {
            _canvas = _doc.AddPage(new PdfPageSize(Width, Height)).Canvas;
            _y = Margin + 24;
            Artifact(() =>
            {
                DrawString("Budgetopf\u00f8lgning \u00b7 Fortroligt", Regular, 8, Margin, Margin - 16, 400, 12, color: Grey);
                _canvas.SetStrokeColor(Accent).SetLineWidth(1).MoveTo(Margin, Y(Margin)).LineTo(Width - Margin, Y(Margin)).Stroke();
            });
        }

        private void Footer()
        {
            var total = totalPages?.ToString(CultureInfo.InvariantCulture) ?? "?";
            Artifact(() => DrawString($"Side {_doc.Pages.Count} af {total}", Regular, 8, Width - Margin - 100, Height - 30, 100, 12, right: true, Grey));
        }

        /// <summary>Draws text as the content of <paramref name="element"/>.</summary>
        private void Text(PdfStructureElement element, string text, PdfFont font, double size, double x, double top, double width, double height, bool right = false, PdfColor? color = null)
        {
            _canvas.BeginTag(element);
            DrawString(text, font, size, x, top, width, height, right, color);
            _canvas.EndTag();
        }

        /// <summary>
        /// Text in a rectangle given by its top-left corner, left- or right-aligned and centred vertically between the
        /// font's ascent and descent, using the same measurement as the drawing.
        /// </summary>
        private void DrawString(string text, PdfFont font, double size, double x, double top, double width, double height, bool right = false, PdfColor? color = null)
        {
            _canvas.SetFillColor(color ?? Black);
            var left = right ? x + width - font.MeasureText(text, size) : x;
            var baseline = top + (height / 2) + ((font.Ascent + font.Descent) / 2 * size);
            _canvas.DrawText(text, font, size, left, Y(baseline));
        }

        /// <summary>Draws decoration as an artifact, keeping its colors and line styles to itself.</summary>
        private void Artifact(Action draw)
        {
            _canvas.BeginArtifact().SaveState();
            draw();
            _canvas.RestoreState().EndTag();
        }

        /// <summary>An ellipse as four Bezier curves.</summary>
        private void Ellipse(double cx, double cy, double rx, double ry)
        {
            const double K = 0.5522847498;
            _canvas.MoveTo(cx + rx, cy)
                .CurveTo(cx + rx, cy + (K * ry), cx + (K * rx), cy + ry, cx, cy + ry)
                .CurveTo(cx - (K * rx), cy + ry, cx - rx, cy + (K * ry), cx - rx, cy)
                .CurveTo(cx - rx, cy - (K * ry), cx - (K * rx), cy - ry, cx, cy - ry)
                .CurveTo(cx + (K * rx), cy - ry, cx + rx, cy - (K * ry), cx + rx, cy)
                .ClosePath();
        }

        /// <summary>A distance from the top of the page as a PDF y coordinate.</summary>
        private static double Y(double fromTop) => Height - fromTop;

        /// <summary>An amount the Danish way (12.500, \u2212731), without depending on the operating system's culture data.</summary>
        private static string Amount(int value) =>
            (value < 0 ? "\u2212" : string.Empty) + Math.Abs(value).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
    }
}
