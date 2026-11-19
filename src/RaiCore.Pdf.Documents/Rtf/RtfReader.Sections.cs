using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>Document and section formatting: page size, margins, section breaks, page numbers, headers and footers.</summary>
internal sealed partial class RtfReader
{
    private SectionSetup _documentSetup = SectionSetup.Default;
    private SectionSetup _setup = SectionSetup.Default;
    private WordSection _section = new();

    /// <summary>A section's page setup in twips, as RTF gives it (document defaults, then the section's own).</summary>
    private record struct SectionSetup
    {
        public static SectionSetup Default => new()
        {
            Width = 12240,
            Height = 15840,
            Left = 1800,
            Right = 1800,
            Top = 1440,
            Bottom = 1440,
            Header = 720,
            Footer = 720,
            Columns = 1,
        };

        public int Width;
        public int Height;
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
        public int Gutter;
        public int Header;
        public int Footer;
        public bool Landscape;
        public SectionStart Start;
        public bool TitlePage;
        public int PageStart;
        public bool Restart;
        public NumberFormat PageFormat;
        public int Columns;
    }

    private void BeginDocument() => _stories.Add(new Story(StoryKind.Body));

    /// <summary>Applies a document formatting control word, returning whether it was one.</summary>
    private bool DocumentWord(string word, int parameter, bool hasParameter)
    {
        var twips = Math.Clamp(parameter, 0, 14400 * 20);
        switch (word)
        {
            case "paperw":
                (_documentSetup.Width, _setup.Width) = (twips, twips);
                break;
            case "paperh":
                (_documentSetup.Height, _setup.Height) = (twips, twips);
                break;
            case "margl":
                (_documentSetup.Left, _setup.Left) = (twips, twips);
                break;
            case "margr":
                (_documentSetup.Right, _setup.Right) = (twips, twips);
                break;
            case "margt":
                (_documentSetup.Top, _setup.Top) = (twips, twips);
                break;
            case "margb":
                (_documentSetup.Bottom, _setup.Bottom) = (twips, twips);
                break;
            case "gutter":
                (_documentSetup.Gutter, _setup.Gutter) = (twips, twips);
                break;
            case "landscape":
                (_documentSetup.Landscape, _setup.Landscape) = (true, true);
                break;
            case "facingp":
                _document.EvenAndOddHeaders = true;
                break;
            case "deftab" when parameter > 0:
                _document.DefaultTabStop = Math.Min(parameter / 20.0, 1584);
                break;
            case "deff":
                _defaultFont = parameter;
                break;
            case "ansicpg" when parameter > 0:
                _codePage = parameter;
                break;
            case "mac":
                _codePage = 10000;
                break;
            case "pc":
                _codePage = 437;
                break;
            case "pca":
                _codePage = 850;
                break;
            case "ftnnar":
            case "ftnnalc":
            case "ftnnauc":
            case "ftnnrlc":
            case "ftnnruc":
                _document.FootnoteFormat = NoteFormat(word[4..]);
                break;
            case "aftnnar":
            case "aftnnalc":
            case "aftnnauc":
            case "aftnnrlc":
            case "aftnnruc":
                _document.EndnoteFormat = NoteFormat(word[5..]);
                break;
            default:
                return false;
        }

        _ = hasParameter;
        return true;

        static NumberFormat NoteFormat(string code) => code switch
        {
            "alc" => NumberFormat.LowerLetter,
            "auc" => NumberFormat.UpperLetter,
            "rlc" => NumberFormat.LowerRoman,
            "ruc" => NumberFormat.UpperRoman,
            _ => NumberFormat.Decimal,
        };
    }

    /// <summary>Applies a section formatting control word, returning whether it was one.</summary>
    private bool SectionWord(string word, int parameter, bool hasParameter)
    {
        var twips = Math.Clamp(parameter, 0, 14400 * 20);
        switch (word)
        {
            case "sectd":
                _setup = _documentSetup with { Start = SectionStart.NextPage, TitlePage = false, PageStart = 0, Restart = false, PageFormat = NumberFormat.Decimal, Columns = 1 };
                break;
            case "pgwsxn":
                _setup.Width = twips;
                break;
            case "pghsxn":
                _setup.Height = twips;
                break;
            case "marglsxn":
                _setup.Left = twips;
                break;
            case "margrsxn":
                _setup.Right = twips;
                break;
            case "margtsxn":
                _setup.Top = twips;
                break;
            case "margbsxn":
                _setup.Bottom = twips;
                break;
            case "guttersxn":
                _setup.Gutter = twips;
                break;
            case "lndscpsxn":
                _setup.Landscape = true;
                break;
            case "headery":
                _setup.Header = twips;
                break;
            case "footery":
                _setup.Footer = twips;
                break;
            case "sbknone":
            case "sbkcol":
                _setup.Start = SectionStart.Continuous;
                break;
            case "sbkpage":
                _setup.Start = SectionStart.NextPage;
                break;
            case "sbkeven":
                _setup.Start = SectionStart.EvenPage;
                break;
            case "sbkodd":
                _setup.Start = SectionStart.OddPage;
                break;
            case "titlepg":
                _setup.TitlePage = !hasParameter || parameter != 0;
                break;
            case "pgnstarts":
                _setup.PageStart = Math.Clamp(parameter, 0, 1_000_000);
                break;
            case "pgnrestart":
                _setup.Restart = true;
                break;
            case "pgncont":
                _setup.Restart = false;
                break;
            case "pgndec":
                _setup.PageFormat = NumberFormat.Decimal;
                break;
            case "pgnucrm":
                _setup.PageFormat = NumberFormat.UpperRoman;
                break;
            case "pgnlcrm":
                _setup.PageFormat = NumberFormat.LowerRoman;
                break;
            case "pgnucltr":
                _setup.PageFormat = NumberFormat.UpperLetter;
                break;
            case "pgnlcltr":
                _setup.PageFormat = NumberFormat.LowerLetter;
                break;
            case "cols":
                _setup.Columns = Math.Clamp(parameter, 1, 45);
                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>Ends a section (<c>\sect</c>, or the end of the document): the body's blocks so far, with the page setup.</summary>
    private void EndSection()
    {
        var story = _stories[0];
        EmitRun();
        if (story.Paragraph is not null && ReferenceEquals(CurrentStory, story))
        {
            EndParagraph();
        }

        CloseTables(story, 0);
        _section.Blocks.AddRange(story.Blocks);
        story.Blocks.Clear();
        Apply(_setup, _section);
        _document.Sections.Add(_section);
        _section = new WordSection();
    }

    /// <summary>Ends the last section when the document's group closes, unless a trailing <c>\sect</c> left it empty.</summary>
    private void EndLastSection()
    {
        var story = _stories[0];
        EmitRun();
        if (story.Paragraph is not null && ReferenceEquals(CurrentStory, story))
        {
            EndParagraph();
        }

        CloseTables(story, 0);
        if (story.Blocks.Count > 0 || _document.Sections.Count == 0)
        {
            EndSection();
        }
    }

    /// <summary>Gives a section its page setup, keeping at least an inch of room for the text both ways.</summary>
    private void Apply(SectionSetup setup, WordSection section)
    {
        var width = Math.Clamp(setup.Width / 20.0, 72, 14400);
        var height = Math.Clamp(setup.Height / 20.0, 72, 14400);
        if (setup.Landscape && width < height)
        {
            (width, height) = (height, width);
        }

        section.PageWidth = width;
        section.PageHeight = height;
        var (left, right) = Fit((setup.Left + setup.Gutter) / 20.0, setup.Right / 20.0, width);
        var (top, bottom) = Fit(setup.Top / 20.0, setup.Bottom / 20.0, height);
        (section.MarginLeft, section.MarginRight, section.MarginTop, section.MarginBottom) = (left, right, top, bottom);
        section.HeaderDistance = Math.Clamp(setup.Header / 20.0, 0, height / 3);
        section.FooterDistance = Math.Clamp(setup.Footer / 20.0, 0, height / 3);
        section.Start = setup.Start;
        section.TitlePage = setup.TitlePage;
        section.PageNumberStart = setup.Restart ? setup.PageStart : null;
        section.PageNumberFormat = setup.PageFormat;
        section.Columns = setup.Columns;
        if (setup.Columns > 1)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent, $"A section with {setup.Columns} text columns is laid out in one column.");
        }

        static (double, double) Fit(double a, double b, double extent)
        {
            var room = extent - 72;
            return a + b > room ? (a * room / (a + b), b * room / (a + b)) : (a, b);
        }
    }

    /// <summary>
    /// Ends the document: the last section (one a trailing <c>\sect</c> left empty is dropped), headers and footers a
    /// section lacks inherited from the section before, and the title and author.
    /// </summary>
    private WordDocument EndDocument()
    {
        if (_document.Sections.Count == 0)
        {
            Apply(_setup, _section);
            _document.Sections.Add(_section);
        }

        var sections = _document.Sections;
        for (var i = 1; i < sections.Count; i++)
        {
            foreach (var kind in Enum.GetValues<HeaderFooterKind>())
            {
                if (!sections[i].Headers.ContainsKey(kind) && sections[i - 1].Headers.TryGetValue(kind, out var header))
                {
                    sections[i].Headers[kind] = header;
                }

                if (!sections[i].Footers.ContainsKey(kind) && sections[i - 1].Footers.TryGetValue(kind, out var footer))
                {
                    sections[i].Footers[kind] = footer;
                }
            }
        }

        _document.Title = _title.ToString().Trim() is { Length: > 0 } title ? Clean(title) : null;
        _document.Author = _author.ToString().Trim() is { Length: > 0 } author ? Clean(author) : null;
        return _document;
    }
}
