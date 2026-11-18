using RaiCore.Pdf.Extraction.Fonts;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Extraction.Content;

/// <summary>
/// Runs the content of pages (ISO 32000-2, sections 8 and 9) and reports the glyphs and images they draw to a
/// <see cref="ContentSink"/>, in display space (see <see cref="TextGlyph"/>). It tracks the graphics state, the text
/// state and form XObjects; paths, colors, shadings and clipping are not interpreted. Malformed content never throws:
/// what cannot be read is skipped and reported in <see cref="Diagnostics"/>; only exceeded resource limits
/// (<see cref="ResourceLimitExceededException"/>) and cancellation end a run.
/// </summary>
/// <remarks>Not thread-safe, like the <see cref="PdfReader"/> it reads. Fonts are decoded once per interpreter.</remarks>
internal sealed class ContentInterpreter
{
    /// <summary>The deepest <c>q</c> nesting followed; deeper saves are ignored with their restores.</summary>
    internal const int MaxStateDepth = 256;

    /// <summary>How deeply form XObjects may draw other forms.</summary>
    internal const int MaxFormDepth = 16;

    /// <summary>The most operators run for one page, forms included, so that forms drawing forms cannot multiply the work without bound.</summary>
    internal const int MaxOperationsPerPage = 10_000_000;

    private readonly PdfReader _reader;
    private ContentSink _sink;
    private readonly Dictionary<PdfDictionary, FontDecoder> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfStreamObject, byte[]?> _formContent = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfStreamObject> _activeForms = new(ReferenceEqualityComparer.Instance);
    private FontDecoder? _fallbackFont;
    private int _operations;

    public ContentInterpreter(PdfReader reader, ContentSink sink, ExtractionDiagnostics? diagnostics = null)
    {
        _reader = reader;
        _sink = sink;
        Diagnostics = diagnostics ?? new ExtractionDiagnostics();
    }

    public ExtractionDiagnostics Diagnostics { get; }

    /// <summary>The most operators run for one page (<see cref="MaxOperationsPerPage"/>; lower in tests).</summary>
    internal int MaxOperations { get; init; } = MaxOperationsPerPage;

    /// <summary>
    /// The matrix from a page's user space to its display space: the crop box's top-left corner (after rotation) is the
    /// origin, x grows to the right and y downwards.
    /// </summary>
    public static Matrix DisplayMatrix(PdfReaderPage page)
    {
        var box = page.CropBox;
        var (left, bottom, right, top) = (box.X, box.Y, box.Right, box.Top); // Normalized by the reader.
        return page.Rotate switch
        {
            90 => new Matrix(0, 1, 1, 0, -bottom, -left),
            180 => new Matrix(-1, 0, 0, 1, right, -bottom),
            270 => new Matrix(0, -1, -1, 0, top, right),
            _ => new Matrix(1, 0, 0, -1, -left, top),
        };
    }

    /// <summary>Runs a page's content, reporting to <paramref name="sink"/> (from now on) instead of the sink given at construction.</summary>
    public void Run(PdfReaderPage page, ContentSink sink)
    {
        _sink = sink;
        Run(page);
    }

    /// <summary>Runs a page's content.</summary>
    public void Run(PdfReaderPage page)
    {
        _operations = 0;
        using var content = new MemoryStream();
        foreach (var stream in page.ContentStreams)
        {
            if (Decode(stream, "A content stream") is not { } data)
            {
                continue;
            }

            ResourceLimits.CheckDecompressedBytes(content.Length + (long)data.Length);
            content.Write(data);
            content.WriteByte((byte)'\n'); // Content may be split between streams anywhere between tokens.
        }

        Run(content.ToArray(), page.Resources, GraphicsState.Initial(DisplayMatrix(page)), 0);
    }

    private void Run(byte[] content, PdfDictionary resources, GraphicsState state, int formDepth)
    {
        var parser = new ContentParser(content, Diagnostics);
        var saved = new Stack<GraphicsState>();
        var ignoredSaves = 0;
        var openMarkedContent = 0;
        var inText = false;
        var text = new TextPosition();
        Box? path = null;
        var clipPending = false;
        while (parser.TryRead(out var operation))
        {
            if (++_operations > MaxOperations)
            {
                Diagnostics.Report($"The page runs more than {MaxOperations} operators; the rest of it is skipped.");
                break;
            }

            if ((_operations & 0x3FF) == 0)
            {
                ResourceLimits.Checkpoint();
            }

            var operands = operation.Operands;
            switch (operation.Operator)
            {
                case "q":
                    if (saved.Count < MaxStateDepth)
                    {
                        saved.Push(state);
                    }
                    else
                    {
                        ignoredSaves++;
                        Diagnostics.Report($"Graphics states are saved (q) more than {MaxStateDepth} levels deep; deeper saves are ignored.");
                    }

                    break;
                case "Q":
                    if (ignoredSaves > 0)
                    {
                        ignoredSaves--;
                    }
                    else if (saved.Count > 0)
                    {
                        state = saved.Pop();
                    }

                    break;
                case "m" or "l":
                    path = Extend(path, state.Ctm, operands, 2);
                    break;
                case "c":
                    path = Extend(path, state.Ctm, operands, 6);
                    break;
                case "v" or "y":
                    path = Extend(path, state.Ctm, operands, 4);
                    break;
                case "re":
                    if (operands.Count >= 4 && Number(operands[^4]) is { } rx && Number(operands[^3]) is { } ry
                        && Number(operands[^2]) is { } rw && Number(operands[^1]) is { } rh)
                    {
                        path = Extend(path, state.Ctm, rx, ry);
                        path = Extend(path, state.Ctm, rx + rw, ry + rh);
                        path = Extend(path, state.Ctm, rx + rw, ry);
                        path = Extend(path, state.Ctm, rx, ry + rh);
                    }

                    break;
                case "W" or "W*":
                    clipPending = true;
                    break;
                case "n" or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    // The clipping path takes effect when the path is ended (ISO 32000-2, 8.5.4).
                    if (clipPending && path is { } clipBox)
                    {
                        state = state.ClippedTo(clipBox);
                    }

                    path = null;
                    clipPending = false;
                    break;
                case "cm":
                    if (MatrixOf(operands) is { } cm && cm.Then(state.Ctm) is { IsFinite: true } ctm)
                    {
                        state = state with { Ctm = ctm };
                    }

                    break;
                case "BT":
                    if (inText)
                    {
                        _sink.EndText(); // A missing ET.
                    }

                    text = new TextPosition();
                    inText = true;
                    _sink.BeginText();
                    break;
                case "ET":
                    if (inText)
                    {
                        inText = false;
                        _sink.EndText();
                    }

                    break;
                case "Tf":
                    if (operands.Count >= 2 && operands[^2] is PdfName fontName && Number(operands[^1]) is { } size)
                    {
                        state = state with { Font = FontOf(resources, fontName), FontSize = size };
                    }

                    break;
                case "Tc":
                    state = Number(Last(operands)) is { } charSpacing ? state with { CharSpacing = charSpacing } : state;
                    break;
                case "Tw":
                    state = Number(Last(operands)) is { } wordSpacing ? state with { WordSpacing = wordSpacing } : state;
                    break;
                case "Tz":
                    state = Number(Last(operands)) is { } scaling ? state with { HorizontalScaling = scaling / 100 } : state;
                    break;
                case "TL":
                    state = Number(Last(operands)) is { } leading ? state with { Leading = leading } : state;
                    break;
                case "Ts":
                    state = Number(Last(operands)) is { } rise ? state with { Rise = rise } : state;
                    break;
                case "Tr":
                    state = Number(Last(operands)) is { } mode and >= 0 and <= 7 ? state with { RenderMode = (int)mode } : state;
                    break;
                case "Td" or "TD":
                    if (operands.Count >= 2 && Number(operands[^2]) is { } tx && Number(operands[^1]) is { } ty)
                    {
                        if (operation.Operator == "TD")
                        {
                            state = state with { Leading = -ty };
                        }

                        text.MoveLine(tx, ty);
                    }

                    break;
                case "Tm":
                    if (MatrixOf(operands) is { } tm)
                    {
                        text = new TextPosition { Matrix = tm, LineMatrix = tm };
                    }

                    break;
                case "T*":
                    text.MoveLine(0, -state.Leading);
                    break;
                case "Tj":
                    Show(Last(operands) as PdfString, state, text);
                    break;
                case "'":
                    text.MoveLine(0, -state.Leading);
                    Show(Last(operands) as PdfString, state, text);
                    break;
                case "\"":
                    if (operands.Count >= 3 && Number(operands[^3]) is { } aw && Number(operands[^2]) is { } ac)
                    {
                        state = state with { WordSpacing = aw, CharSpacing = ac };
                        text.MoveLine(0, -state.Leading);
                        Show(operands[^1] as PdfString, state, text);
                    }

                    break;
                case "TJ":
                    if (Last(operands) is PdfArray array)
                    {
                        foreach (var item in array.Items)
                        {
                            if (item is PdfString part)
                            {
                                Show(part, state, text);
                            }
                            else if (Number(item) is { } adjustment)
                            {
                                Adjust(adjustment, state, text);
                            }
                        }
                    }

                    break;
                case "Do":
                    if (Last(operands) is PdfName xObjectName)
                    {
                        DrawXObject(xObjectName, resources, state, formDepth);
                    }

                    break;
                case "BI":
                    if (operation.Image is { } inline)
                    {
                        ReportImage(state, null, null, null, inline);
                    }

                    break;
                case "BMC":
                    if (Last(operands) is PdfName tag)
                    {
                        openMarkedContent++;
                        _sink.BeginMarkedContent(tag.Value, null);
                    }

                    break;
                case "BDC":
                    if (operands.Count >= 2 && operands[^2] is PdfName taggedName)
                    {
                        var properties = operands[^1] switch
                        {
                            PdfDictionary inlineProperties => inlineProperties,
                            PdfName named => _reader.Resolve((_reader.Resolve(resources["Properties"]) as PdfDictionary)?[named]) as PdfDictionary,
                            _ => null,
                        };

                        openMarkedContent++;
                        _sink.BeginMarkedContent(taggedName.Value, properties);
                    }

                    break;
                case "EMC":
                    if (openMarkedContent > 0)
                    {
                        openMarkedContent--;
                        _sink.EndMarkedContent();
                    }

                    break;
            }
        }

        if (inText)
        {
            _sink.EndText();
        }

        // Marked content cannot span content streams of different objects (ISO 32000-2, 14.6).
        for (; openMarkedContent > 0; openMarkedContent--)
        {
            _sink.EndMarkedContent();
        }
    }

    /// <summary>Shows a string (<c>Tj</c>): reports each glyph and moves the text position past it.</summary>
    private void Show(PdfString? text, GraphicsState state, TextPosition position)
    {
        if (text is null)
        {
            return;
        }

        var font = state.Font ?? Fallback("Text is shown without a font (Tf); Helvetica is assumed.");
        var bytes = text.Bytes;
        var scaling = state.HorizontalScaling;
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = font.NextCode(bytes[offset..], out var code);
            offset += length;

            // The text rendering matrix: [Tfs·Th 0 0 Tfs 0 Trise] × Tm × CTM (ISO 32000-2, 9.4.4).
            var trm = new Matrix(state.FontSize * scaling, 0, 0, state.FontSize, 0, state.Rise).Then(position.Matrix).Then(state.Ctm);
            var width = font.Width(code, length);
            var isWordSpace = FontDecoder.IsWordSpace(code, length);
            var spacing = state.CharSpacing + (isWordSpace ? state.WordSpacing : 0);
            PdfPoint origin, end;
            Box bounds;
            if (font.IsVertical)
            {
                var vertical = font.Vertical(code, length);
                var (left, top) = (-vertical.OriginX, -vertical.OriginY);
                origin = trm.Transform(left, top);
                end = trm.Transform(0, vertical.Advance);
                bounds = Box.Around(
                    trm.Transform(left, top + font.Descent), trm.Transform(left + width, top + font.Descent),
                    trm.Transform(left + width, top + font.Ascent), trm.Transform(left, top + font.Ascent));
                position.Advance(0, (vertical.Advance * state.FontSize) + spacing);
            }
            else
            {
                origin = trm.Transform(0, 0);
                end = trm.Transform(width, 0);
                bounds = Box.Around(
                    trm.Transform(0, font.Descent), trm.Transform(width, font.Descent),
                    trm.Transform(width, font.Ascent), trm.Transform(0, font.Ascent));
                position.Advance(((width * state.FontSize) + spacing) * scaling, 0);
            }

            if (!trm.IsFinite || !double.IsFinite(bounds.Left + bounds.Top + bounds.Right + bounds.Bottom))
            {
                continue;
            }

            var mapped = font.Text(code, length);
            var em = trm.TransformVector(0, 1);
            var flow = font.IsVertical ? trm.TransformVector(0, -1) : trm.TransformVector(1, 0);
            var flowLength = Math.Sqrt((flow.X * flow.X) + (flow.Y * flow.Y));
            var direction = flowLength > 0 ? new PdfPoint(flow.X / flowLength, flow.Y / flowLength) : new PdfPoint(1, 0);
            _sink.Glyph(new TextGlyph(
                mapped?.Text ?? string.Empty, mapped?.IsGuess ?? false, code, length, origin, end, bounds,
                Math.Sqrt((em.X * em.X) + (em.Y * em.Y)), font, state.RenderMode, isWordSpace, direction,
                state.Clip is { } clip && !Contains(clip, (bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2)));
        }
    }

    /// <summary>A number in a <c>TJ</c> array: moves the text position back by thousandths of the font size.</summary>
    private static void Adjust(double adjustment, GraphicsState state, TextPosition position)
    {
        var distance = -adjustment / 1000 * state.FontSize;
        if (state.Font?.IsVertical == true)
        {
            position.Advance(0, distance);
        }
        else
        {
            position.Advance(distance * state.HorizontalScaling, 0);
        }
    }

    private void DrawXObject(PdfName name, PdfDictionary resources, GraphicsState state, int formDepth)
    {
        var entry = (_reader.Resolve(resources["XObject"]) as PdfDictionary)?[name];
        if (_reader.Resolve(entry) is not PdfStreamObject xObject)
        {
            Diagnostics.Report($"The XObject /{name.Value} is drawn (Do) but missing from the resources.");
            return;
        }

        switch ((_reader.Resolve(xObject.Dictionary["Subtype"]) as PdfName)?.Value)
        {
            case "Image":
                ReportImage(state, xObject, entry as PdfReference, name.Value, null);
                break;
            case "Form":
                DrawForm(name, xObject, resources, state, formDepth);
                break;
        }
    }

    /// <summary>Runs a form XObject's content with its matrix and resources (ISO 32000-2, 8.10).</summary>
    private void DrawForm(PdfName name, PdfStreamObject form, PdfDictionary resources, GraphicsState state, int formDepth)
    {
        if (formDepth >= MaxFormDepth)
        {
            Diagnostics.Report($"Form XObjects are nested more than {MaxFormDepth} levels deep; deeper ones are skipped.");
            return;
        }

        if (!_activeForms.Add(form))
        {
            Diagnostics.Report($"The form XObject /{name.Value} draws itself; the loop is skipped.");
            return;
        }

        try
        {
            if (!_formContent.TryGetValue(form, out var content))
            {
                content = Decode(form, $"The form XObject /{name.Value}");
                _formContent[form] = content;
            }

            if (content is null)
            {
                return;
            }

            var matrix = MatrixOf(_reader.Resolve(form.Dictionary["Matrix"]) is PdfArray m ? m.Items : []) ?? Matrix.Identity;
            var formResources = _reader.Resolve(form.Dictionary["Resources"]) as PdfDictionary ?? resources;
            using (ResourceLimits.EnterNesting())
            {
                var formState = state with { Ctm = matrix.Then(state.Ctm) };
                var box = _reader.Resolve(form.Dictionary["BBox"]) is PdfArray bbox ? [.. bbox.Items.Select(i => Number(_reader.Resolve(i)))] : new List<double?>();
                if (box.Count == 4 && box.All(n => n is not null))
                {
                    var corners = Extend(Extend(null, formState.Ctm, box[0]!.Value, box[1]!.Value), formState.Ctm, box[2]!.Value, box[3]!.Value);
                    corners = Extend(Extend(corners, formState.Ctm, box[0]!.Value, box[3]!.Value), formState.Ctm, box[2]!.Value, box[1]!.Value);
                    formState = formState.ClippedTo(corners!.Value);
                }

                Run(content, formResources, formState, formDepth + 1);
            }
        }
        finally
        {
            _activeForms.Remove(form);
        }
    }

    private void ReportImage(GraphicsState state, PdfStreamObject? xObject, PdfReference? reference, string? name, InlineImage? inline)
    {
        var ctm = state.Ctm;
        var bounds = Box.Around(ctm.Transform(0, 0), ctm.Transform(1, 0), ctm.Transform(1, 1), ctm.Transform(0, 1));
        if (ctm.IsFinite)
        {
            var clipped = state.Clip is { } clip
                && (clip.Right <= bounds.Left || clip.Left >= bounds.Right || clip.Bottom <= bounds.Top || clip.Top >= bounds.Bottom);
            _sink.Image(new ImagePlacement(ctm, bounds, xObject, reference, name, inline, clipped));
        }
    }

    private FontDecoder FontOf(PdfDictionary resources, PdfName name)
    {
        var fonts = _reader.Resolve(resources["Font"]) as PdfDictionary;
        if (_reader.Resolve(fonts?[name]) is not PdfDictionary font)
        {
            return Fallback($"The font /{name.Value} is missing from the resources; Helvetica is assumed.");
        }

        if (!_fonts.TryGetValue(font, out var decoder))
        {
            decoder = FontDecoder.Load(_reader, font, Diagnostics);
            _fonts[font] = decoder;
        }

        return decoder;
    }

    private FontDecoder Fallback(string message)
    {
        Diagnostics.Report(message);
        return _fallbackFont ??= FontDecoder.Fallback(Diagnostics);
    }

    private byte[]? Decode(PdfStreamObject stream, string what)
    {
        try
        {
            return _reader.Store.Decode(stream);
        }
        catch (PdfFormatException ex)
        {
            Diagnostics.Report($"{what} could not be decoded and is skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>The path's bounding box (in display space) grown by the points among the last <paramref name="count"/> operands.</summary>
    private static Box? Extend(Box? path, Matrix ctm, IReadOnlyList<PdfObject> operands, int count)
    {
        if (operands.Count < count)
        {
            return path;
        }

        for (var i = operands.Count - count; i + 1 < operands.Count; i += 2)
        {
            if (Number(operands[i]) is { } x && Number(operands[i + 1]) is { } y)
            {
                path = Extend(path, ctm, x, y);
            }
        }

        return path;
    }

    private static Box? Extend(Box? path, Matrix ctm, double x, double y)
    {
        var p = ctm.Transform(x, y);
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
        {
            return path;
        }

        return path is not { } box
            ? new Box(p.X, p.Y, p.X, p.Y)
            : new Box(Math.Min(box.Left, p.X), Math.Min(box.Top, p.Y), Math.Max(box.Right, p.X), Math.Max(box.Bottom, p.Y));
    }

    private static bool Contains(Box box, double x, double y) => x >= box.Left && x <= box.Right && y >= box.Top && y <= box.Bottom;

    private static PdfObject? Last(IReadOnlyList<PdfObject> operands) => operands.Count > 0 ? operands[^1] : null;

    private static double? Number(PdfObject? value) => FontDecoder.Number(value) is { } number && double.IsFinite(number) ? number : null;

    /// <summary>A matrix from the last six operands, or <c>null</c>.</summary>
    private static Matrix? MatrixOf(IList<PdfObject> operands) => MatrixOf((IReadOnlyList<PdfObject>)[.. operands]);

    private static Matrix? MatrixOf(IReadOnlyList<PdfObject> operands)
    {
        if (operands.Count < 6)
        {
            return null;
        }

        var numbers = new double[6];
        for (var i = 0; i < 6; i++)
        {
            if (Number(operands[operands.Count - 6 + i]) is not { } number)
            {
                return null;
            }

            numbers[i] = number;
        }

        return Matrix.FromNumbers(numbers);
    }

    /// <summary>The parts of the graphics state the interpreter follows (ISO 32000-2, 8.4 and 9.3).</summary>
    private readonly record struct GraphicsState(
        Matrix Ctm,
        FontDecoder? Font,
        double FontSize,
        double CharSpacing,
        double WordSpacing,
        double HorizontalScaling,
        double Leading,
        double Rise,
        int RenderMode,
        Box? Clip)
    {
        public static GraphicsState Initial(Matrix ctm) => new(ctm, null, 0, 0, 0, 1, 0, 0, 0, null);

        /// <summary>The state with its clip narrowed to <paramref name="box"/> (the bounding box of a clipping path).</summary>
        public GraphicsState ClippedTo(Box box) => this with
        {
            Clip = Clip is not { } clip ? box : new Box(
                Math.Max(clip.Left, box.Left), Math.Max(clip.Top, box.Top),
                Math.Max(Math.Max(clip.Left, box.Left), Math.Min(clip.Right, box.Right)),
                Math.Max(Math.Max(clip.Top, box.Top), Math.Min(clip.Bottom, box.Bottom))),
        };
    }

    /// <summary>The text matrix and text line matrix of a text object (ISO 32000-2, 9.4.2).</summary>
    private sealed class TextPosition
    {
        public Matrix Matrix { get; set; } = Matrix.Identity;

        public Matrix LineMatrix { get; set; } = Matrix.Identity;

        /// <summary>Starts a new line offset from the start of the current one (<c>Td</c>).</summary>
        public void MoveLine(double tx, double ty)
        {
            LineMatrix = Matrix.Translation(tx, ty).Then(LineMatrix);
            Matrix = LineMatrix;
        }

        /// <summary>Moves the text position within the line (after a glyph or a <c>TJ</c> adjustment).</summary>
        public void Advance(double tx, double ty) => Matrix = Matrix.Translation(tx, ty).Then(Matrix);
    }
}
