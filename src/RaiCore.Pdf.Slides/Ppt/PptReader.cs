using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>
/// Reads a PowerPoint 97-2003 file into a <see cref="Presentation"/>: the document (slide size, fonts, default text
/// styles, the drawing group's default shape properties and picture store, headers and footers, hyperlinks), the
/// slides in the order of the slide list with their masters (and title masters), color schemes, backgrounds and
/// OfficeArt drawings: shapes, groups, pictures and text.
/// </summary>
/// <remarks>Partial: shapes (<c>PptReader.Shapes.cs</c>), text (<c>PptReader.Text.cs</c>).</remarks>
internal sealed partial class PptReader
{
    /// <summary>Master units (576 per inch) per point.</summary>
    private const double MasterUnits = 8;

    /// <summary>The most masters a slide's master chain is followed through.</summary>
    private const int MaxMasterDepth = 4;

    /// <summary>PowerPoint's default color scheme: background, text, shadow, title, fill, accent, hyperlink, followed.</summary>
    private static readonly PdfColor[] DefaultScheme =
    [
        PdfColor.White, PdfColor.Black, PdfColor.FromRgb(0x80, 0x80, 0x80), PdfColor.Black,
        PdfColor.FromRgb(0xBB, 0xE0, 0xE3), PdfColor.FromRgb(0x33, 0x33, 0x99), PdfColor.FromRgb(0x00, 0x99, 0x99),
        PdfColor.FromRgb(0x99, 0xCC, 0x00),
    ];

    private readonly PptFile _file;
    private readonly RecordStream _stream;
    private readonly ResourceLimits _limits;
    private readonly Action<int, string, string>? _report;
    private readonly Action? _checkpoint;
    private readonly List<string> _fonts = [];
    private readonly HashSet<int> _symbolFonts = [];
    private readonly Dictionary<int, (ParagraphProps? Paragraph, CharacterProps? Character)[]> _environmentStyles = [];
    private readonly Dictionary<int, int> _masterPersist = [];
    private readonly Dictionary<int, Sheet?> _masters = [];
    private readonly Dictionary<int, Uri> _hyperlinks = [];
    private readonly List<Record> _blipStore = [];
    private readonly Dictionary<int, byte[]?> _blips = [];
    private ParagraphProps _defaultParagraph = new() { Align = 0, LineSpacing = 100, SpaceBefore = 0, SpaceAfter = 0, TextOffset = 0, BulletOffset = 0, HasBullet = false };
    private CharacterProps _defaultCharacter = new() { Bold = false, Italic = false, Underline = false, Font = 0, Size = 18, Position = 0 };
    private Dictionary<int, ShapeProperty>? _drawingDefaults;
    private HeadersFooters? _headersFooters;
    private long _decompressed;
    private int _slideNumber;

    private PptReader(PptFile file, ResourceLimits limits, Action<int, string, string>? report, Action? checkpoint)
    {
        _file = file;
        _stream = file.Stream;
        _limits = limits;
        _report = report;
        _checkpoint = checkpoint;
    }

    /// <summary>The headers and footers shown on slides.</summary>
    private sealed record HeadersFooters(bool Date, bool UserDate, bool SlideNumber, bool Footer, string? DateText, string? FooterText);

    /// <summary>A slide, master or title master: its records, master, colors, text styles and drawing.</summary>
    private sealed class Sheet
    {
        public required Record Container { get; init; }

        /// <summary>The slide atom's flags: follow the master's objects (1), scheme (2) and background (4).</summary>
        public int Flags { get; init; }

        public Sheet? Master { get; set; }

        public PdfColor[] Scheme { get; set; } = DefaultScheme;

        /// <summary>A main master's text styles by text type.</summary>
        public Dictionary<int, (ParagraphProps? Paragraph, CharacterProps? Character)[]> Styles { get; } = [];

        public EscherShape? Root { get; set; }

        public EscherShape? BackgroundShape { get; set; }

        public Dictionary<int, EscherShape> ShapesById { get; } = [];

        /// <summary>The text of the slide's placeholders (its slide list entry), for outline text references.</summary>
        public List<TextGroup> OutlineText { get; set; } = [];

        public bool FollowMasterObjects => (Flags & 0x1) != 0;

        public bool FollowMasterScheme => (Flags & 0x2) != 0;

        public bool FollowMasterBackground => (Flags & 0x4) != 0;
    }

    /// <summary>Reads a presentation: every slide <paramref name="include"/> accepts (by number and hidden flag).</summary>
    public static Presentation Read(PptFile file, Func<int, bool, bool> include, ResourceLimits limits, Action<int, string, string>? report, Action? checkpoint) =>
        new PptReader(file, limits, report, checkpoint).Read(include);

    private Presentation Read(Func<int, bool, bool> include)
    {
        var document = _file.Document;
        var (width, height) = (720.0, 540.0);
        var slides = new List<(int PersistId, List<TextGroup> Text)>();
        foreach (var child in _stream.Children(document))
        {
            _checkpoint?.Invoke();
            switch (child.Type)
            {
                case Rt.DocumentAtom:
                {
                    var body = _stream.Body(child);
                    var (x, y) = (Bytes.I32(body, 0) / MasterUnits, Bytes.I32(body, 4) / MasterUnits);
                    if (x is > 1 and < 1e5 && y is > 1 and < 1e5)
                    {
                        (width, height) = (x, y);
                    }

                    break;
                }

                case Rt.Environment:
                    ReadEnvironment(child);
                    break;
                case Rt.DrawingGroup:
                    ReadDrawingGroup(child);
                    break;
                case Rt.SlideListWithText when child.Instance == 0:
                    slides.AddRange(SlideList(child));
                    break;
                case Rt.SlideListWithText when child.Instance == 1:
                    foreach (var atom in _stream.Children(child))
                    {
                        if (atom.Type == Rt.SlidePersistAtom)
                        {
                            var body = _stream.Body(atom);
                            _masterPersist.TryAdd(Bytes.I32(body, 12), Bytes.I32(body, 0));
                        }
                    }

                    break;
                case Rt.HeadersFooters when child.Instance == 3:
                    _headersFooters = ReadHeadersFooters(child);
                    break;
                case Rt.ExObjList:
                    ReadHyperlinks(child);
                    break;
            }
        }

        var presentation = new Presentation { Width = width, Height = height, Title = _file.Title, Author = _file.Author };
        var number = 0;
        var converted = 0;
        foreach (var (persistId, text) in slides)
        {
            number++;
            _slideNumber = number;
            _checkpoint?.Invoke();
            if (_file.Object(persistId, Rt.Slide) is not { } container)
            {
                Report(number, "slide", "The slide's record is missing from the file; the slide is left out.");
                continue;
            }

            var hidden = _stream.Child(container, Rt.SlideShowSlideInfoAtom) is { } info && (Bytes.U16(_stream.Body(info), 10) & 0x4) != 0;
            if (!include(number, hidden))
            {
                continue;
            }

            if (++converted > _limits.MaxPages)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), _limits.MaxPages,
                    $"The presentation has more slides to convert than ResourceLimits.MaxPages allows ({_limits.MaxPages}).");
            }

            presentation.Slides.Add(ReadSlide(container, text, number, hidden));
        }

        return presentation;
    }

    /// <summary>The slides of a slide list: each slide atom's persist id and the text of its placeholders that follows it.</summary>
    private List<(int PersistId, List<TextGroup> Text)> SlideList(Record list)
    {
        var slides = new List<(int PersistId, List<Record> Records)>();
        foreach (var child in _stream.Children(list))
        {
            if (child.Type == Rt.SlidePersistAtom)
            {
                slides.Add((Bytes.I32(_stream.Body(child), 0), []));
            }
            else if (slides.Count > 0)
            {
                slides[^1].Records.Add(child);
            }
        }

        return [.. slides.Select(s => (s.PersistId, TextGroups(s.Records)))];
    }

    private void ReadEnvironment(Record environment)
    {
        foreach (var child in _stream.Children(environment))
        {
            switch (child.Type)
            {
                case Rt.FontCollection:
                    foreach (var font in _stream.Children(child))
                    {
                        if (font.Type == Rt.FontEntityAtom)
                        {
                            var body = _stream.Body(font);
                            var name = System.Text.Encoding.Unicode.GetString(body[..Math.Min(64, body.Length & ~1)]);
                            var end = name.IndexOf('\0', StringComparison.Ordinal);
                            if (Bytes.U8(body, 64) == 2)
                            {
                                // A font of the symbol character set: its characters are pictures, not letters.
                                _symbolFonts.Add(_fonts.Count);
                            }

                            _fonts.Add((end >= 0 ? name[..end] : name).Trim());
                        }
                    }

                    break;
                case Rt.TextCFExceptionAtom:
                {
                    var c = new Cursor(_stream.Body(child));
                    var props = PptTextStyles.Character(ref c);
                    if (!c.Failed)
                    {
                        _defaultCharacter = _defaultCharacter.Merge(props);
                    }

                    break;
                }

                case Rt.TextPFExceptionAtom:
                {
                    // Two reserved bytes, then the exception.
                    var c = new Cursor(_stream.Body(child));
                    c.Skip(2);
                    var props = PptTextStyles.Paragraph(ref c);
                    if (!c.Failed)
                    {
                        _defaultParagraph = _defaultParagraph.Merge(props);
                    }

                    break;
                }

                case Rt.TextMasterStyleAtom:
                    _environmentStyles[child.Instance] = PptTextStyles.Master(_stream.Body(child), child.Instance);
                    break;
            }
        }
    }

    private void ReadDrawingGroup(Record group)
    {
        if (_stream.Child(group, Rt.DggContainer) is not { } dgg)
        {
            return;
        }

        foreach (var child in _stream.Children(dgg))
        {
            if (child.Type == Rt.Fopt)
            {
                _drawingDefaults = [];
                EscherShape.ReadProperties(_stream.Body(child), child.Instance, _drawingDefaults);
            }
            else if (child.Type == Rt.BStoreContainer)
            {
                _blipStore.AddRange(_stream.Children(child));
            }
        }
    }

    private HeadersFooters ReadHeadersFooters(Record container)
    {
        var flags = 0;
        string? date = null;
        string? footer = null;
        foreach (var child in _stream.Children(container))
        {
            if (child.Type == Rt.HeadersFootersAtom)
            {
                flags = Bytes.U16(_stream.Body(child), 2);
            }
            else if (child.Type == Rt.CString && child.Instance == 0)
            {
                date = CString(child);
            }
            else if (child.Type == Rt.CString && child.Instance == 2)
            {
                footer = CString(child);
            }
        }

        return new HeadersFooters((flags & 0x1) != 0, (flags & 0x4) != 0, (flags & 0x8) != 0, (flags & 0x20) != 0, date, footer);
    }

    /// <summary>The hyperlinks of the external object list, by id.</summary>
    private void ReadHyperlinks(Record list)
    {
        foreach (var child in _stream.Children(list))
        {
            if (child.Type != Rt.ExHyperlink || _stream.Child(child, Rt.ExHyperlinkAtom) is not { } atom)
            {
                continue;
            }

            var id = Bytes.I32(_stream.Body(atom), 0);
            if (_stream.Child(child, Rt.CString, 1) is { } target && CString(target) is { Length: > 0 } url
                && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "ftp" or "file")
            {
                _hyperlinks.TryAdd(id, uri);
            }
        }
    }

    private string CString(Record record)
    {
        var body = _stream.Body(record);
        return System.Text.Encoding.Unicode.GetString(body[..(body.Length & ~1)]).TrimEnd('\0');
    }

    /// <summary>A master (or title master) by its id, read once; <c>null</c> when it is missing.</summary>
    private Sheet? Master(int masterId, int depth)
    {
        if (_masters.TryGetValue(masterId, out var cached))
        {
            return cached;
        }

        _masters[masterId] = null;
        Sheet? sheet = null;
        if (depth < MaxMasterDepth && _masterPersist.TryGetValue(masterId, out var persistId)
            && (_file.Object(persistId, Rt.MainMaster) ?? _file.Object(persistId, Rt.Slide)) is { } container)
        {
            sheet = ReadSheet(container, [], depth + 1);
        }

        _masters[masterId] = sheet;
        return sheet;
    }

    /// <summary>Reads a slide, master or title master (not its shapes' model, which depends on the slide drawn).</summary>
    private Sheet ReadSheet(Record container, List<TextGroup> outlineText, int depth)
    {
        var atom = _stream.Child(container, Rt.SlideAtom);
        var atomBody = atom is { } a ? _stream.Body(a) : default;
        var sheet = new Sheet { Container = container, Flags = atom is null ? 0x7 : Bytes.U16(atomBody, 20), OutlineText = outlineText };
        var masterId = Bytes.I32(atomBody, 12);
        if (masterId != 0)
        {
            sheet.Master = Master(masterId, depth);
        }

        PdfColor[]? scheme = null;
        foreach (var child in _stream.Children(container))
        {
            switch (child.Type)
            {
                case Rt.ColorSchemeAtom when child.Instance == 1:
                {
                    var body = _stream.Body(child);
                    if (body.Length >= 32)
                    {
                        scheme = new PdfColor[8];
                        for (var i = 0; i < 8; i++)
                        {
                            scheme[i] = PdfColor.FromRgb(body[4 * i], body[(4 * i) + 1], body[(4 * i) + 2]);
                        }
                    }

                    break;
                }

                case Rt.TextMasterStyleAtom:
                    sheet.Styles[child.Instance] = PptTextStyles.Master(_stream.Body(child), child.Instance);
                    break;
                case Rt.Drawing:
                    ReadDrawing(child, sheet);
                    break;
            }
        }

        sheet.Scheme = sheet.Master is { } master && (sheet.FollowMasterScheme || scheme is null) ? master.Scheme : scheme ?? DefaultScheme;

        // Shapes that take their properties from a shape of the master.
        foreach (var shape in sheet.ShapesById.Values)
        {
            if ((shape.Flags & ShapeFlags.HaveMaster) != 0 && shape.Properties.TryGetValue(Op.MasterShape, out var id))
            {
                for (var m = sheet.Master; m is not null && shape.Master is null; m = m.Master)
                {
                    shape.Master = m.ShapesById.GetValueOrDefault(id.Value);
                }
            }
        }

        return sheet;
    }

    /// <summary>Reads a sheet's OfficeArt drawing: its shape tree and background shape.</summary>
    private void ReadDrawing(Record drawing, Sheet sheet)
    {
        if (_stream.Child(drawing, Rt.DgContainer) is not { } dg)
        {
            return;
        }

        foreach (var child in _stream.Children(dg))
        {
            if (child.Type == Rt.SpgrContainer && sheet.Root is null)
            {
                sheet.Root = ReadGroup(child, sheet, 0);
            }
            else if (child.Type == Rt.SpContainer && ReadShape(child, sheet) is { } shape && (shape.Flags & ShapeFlags.Background) != 0)
            {
                sheet.BackgroundShape = shape;
            }
        }
    }

    /// <summary>Reads a group (<c>OfficeArtSpgrContainer</c>): its own shape first, then its members.</summary>
    private EscherShape? ReadGroup(Record container, Sheet sheet, int depth)
    {
        if (depth > _limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _limits.MaxNestingDepth,
                $"The presentation's groups nest deeper than ResourceLimits.MaxNestingDepth allows ({_limits.MaxNestingDepth}).");
        }

        EscherShape? group = null;
        foreach (var child in _stream.Children(container))
        {
            if (group is null)
            {
                if (child.Type != Rt.SpContainer || ReadShape(child, sheet) is not { } own)
                {
                    return null;
                }

                group = own;
                group.Flags |= ShapeFlags.Group;
                continue;
            }

            var member = child.Type switch
            {
                Rt.SpContainer => ReadShape(child, sheet),
                Rt.SpgrContainer => ReadGroup(child, sheet, depth + 1),
                _ => null,
            };
            if (member is not null)
            {
                group.Children.Add(member);
            }
        }

        return group;
    }

    /// <summary>Reads a shape (<c>OfficeArtSpContainer</c>).</summary>
    private EscherShape? ReadShape(Record container, Sheet sheet)
    {
        var shape = new EscherShape();
        var hasFsp = false;
        foreach (var child in _stream.Children(container))
        {
            var body = _stream.Body(child);
            switch (child.Type)
            {
                case Rt.Fsp:
                    shape.ShapeType = child.Instance;
                    shape.Id = Bytes.I32(body, 0);
                    shape.Flags = (ShapeFlags)Bytes.I32(body, 4);
                    hasFsp = true;
                    break;
                case Rt.Fspgr:
                    shape.GroupBounds = new AnchorRect(Bytes.I32(body, 0), Bytes.I32(body, 4), Bytes.I32(body, 8), Bytes.I32(body, 12));
                    break;
                case Rt.Fopt or Rt.SecondaryFopt or Rt.TertiaryFopt:
                    EscherShape.ReadProperties(body, child.Instance, shape.Properties);
                    break;
                case Rt.ChildAnchor:
                    shape.ChildAnchor = new AnchorRect(Bytes.I32(body, 0), Bytes.I32(body, 4), Bytes.I32(body, 8), Bytes.I32(body, 12));
                    break;
                case Rt.ClientAnchor:
                    // Top, left, right, bottom: 16-bit values in an 8-byte anchor, 32-bit ones in a 16-byte one.
                    shape.ClientAnchor = body.Length >= 16
                        ? new AnchorRect(Bytes.I32(body, 4), Bytes.I32(body, 0), Bytes.I32(body, 8), Bytes.I32(body, 12))
                        : new AnchorRect(Bytes.I16(body, 2), Bytes.I16(body, 0), Bytes.I16(body, 4), Bytes.I16(body, 6));
                    break;
                case Rt.ClientData:
                    shape.ClientData = child;
                    break;
                case Rt.ClientTextbox:
                    shape.ClientTextbox = child;
                    break;
            }
        }

        if (!hasFsp)
        {
            return null;
        }

        if (shape.Id != 0)
        {
            sheet.ShapesById.TryAdd(shape.Id, shape);
        }

        return shape;
    }

    /// <summary>Reads a slide: its master's background and objects, then its own shapes.</summary>
    private Slide ReadSlide(Record container, List<TextGroup> text, int number, bool hidden)
    {
        var sheet = ReadSheet(container, text, 0);
        var slide = new Slide { Number = number, Hidden = hidden, Background = Background(sheet) };
        var chain = new List<Sheet>();
        for (var s = sheet; s.FollowMasterObjects && s.Master is { } master && chain.Count < MaxMasterDepth; s = master)
        {
            chain.Add(master);
        }

        // The masters' shapes behind the slide's, the farthest first; their placeholders are the slide's to fill,
        // except dates, footers and slide numbers, which the headers and footers settings show.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Root is { } masterRoot)
            {
                foreach (var shape in masterRoot.Children)
                {
                    if (ConvertMasterShape(shape, chain[i], sheet) is { } converted)
                    {
                        converted.Background = true;
                        slide.Shapes.Add(converted);
                    }
                }
            }
        }

        if (sheet.Root is { } root)
        {
            foreach (var shape in root.Children)
            {
                if (Convert(shape, sheet, sheet, false, 0) is { } converted)
                {
                    slide.Shapes.Add(converted);
                }
            }
        }

        return slide;
    }

    /// <summary>A master's shape as drawn on a slide: not its title and body placeholders; footers as set.</summary>
    private Shape? ConvertMasterShape(EscherShape shape, Sheet master, Sheet slide)
    {
        var placeholder = PlaceholderType(shape);
        if (placeholder == 0)
        {
            return Convert(shape, master, slide, false, 0);
        }

        var settings = _headersFooters;
        var field = placeholder switch
        {
            7 when settings is { Date: true } => settings.UserDate ? settings.DateText : null,
            8 when settings is { SlideNumber: true } => _slideNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            9 when settings is { Footer: true } => settings.FooterText,
            _ => null,
        };
        if (placeholder == 7 && settings is { Date: true, UserDate: false })
        {
            Report(_slideNumber, "date", "The automatic date of the slide's footer is not drawn.");
        }

        return field is { Length: > 0 } ? Convert(shape, master, slide, false, 0, field) : null;
    }

    /// <summary>The fill behind a slide: its own background shape's, or (when it follows it, or has none) its master's.</summary>
    private Fill? Background(Sheet sheet)
    {
        var s = sheet;
        for (var depth = 0; depth < MaxMasterDepth && s.Master is { } master && (s.FollowMasterBackground || s.BackgroundShape is null); depth++)
        {
            s = master;
        }

        return s.BackgroundShape is { } shape ? Fill(shape, sheet, sheet, new SolidFill(PdfColor.White)) : null;
    }

    /// <summary>Reports an issue (the converter keeps one of each per slide).</summary>
    private void Report(int slide, string subject, string message) => _report?.Invoke(slide, subject, message);
}
