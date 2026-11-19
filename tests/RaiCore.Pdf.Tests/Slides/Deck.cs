using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RaiCore.Pdf.Tests.Slides;

/// <summary>Builds small PPTX packages for tests: a master, a layout, an optional theme and slides from XML snippets.</summary>
internal sealed class Deck
{
    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The slides' shape trees (inner XML), in order.</summary>
    public List<string> Slides { get; } = [];

    /// <summary>Extra XML in each slide's <c>p:cSld</c> before the shape tree (such as a background).</summary>
    public string SlideBackground { get; init; } = string.Empty;

    /// <summary>Attributes of every slide's root (such as <c>show="0"</c>), by slide index.</summary>
    public Dictionary<int, string> SlideAttributes { get; } = [];

    /// <summary>Extra relationships of every slide (Relationship elements).</summary>
    public string SlideRelationships { get; init; } = string.Empty;

    /// <summary>The master's shape tree (inner XML).</summary>
    public string MasterShapes { get; init; } = string.Empty;

    /// <summary>The master's text styles (inner XML of <c>p:txStyles</c>).</summary>
    public string MasterTextStyles { get; init; } = string.Empty;

    /// <summary>Extra XML in the master's <c>p:cSld</c> before the shape tree.</summary>
    public string MasterBackground { get; init; } = string.Empty;

    /// <summary>The master's color map attributes.</summary>
    public string ColorMap { get; init; } = "bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"";

    /// <summary>The layout's shape tree (inner XML).</summary>
    public string LayoutShapes { get; init; } = string.Empty;

    /// <summary>Attributes of the layout's root.</summary>
    public string LayoutAttributes { get; init; } = string.Empty;

    /// <summary>The theme's elements (inner XML of <c>a:themeElements</c>), or <c>null</c> for no theme part.</summary>
    public string? Theme { get; init; }

    /// <summary>The presentation's default text style (inner XML), or empty.</summary>
    public string DefaultTextStyle { get; init; } = string.Empty;

    /// <summary>The slide size in points.</summary>
    public (double Width, double Height) Size { get; init; } = (720, 540);

    /// <summary>Extra parts (name, content).</summary>
    public List<(string Name, byte[] Content)> Parts { get; } = [];

    /// <summary>A length in points as EMUs.</summary>
    public static string Emu(double points) => ((long)Math.Round(points * 12700)).ToString(CultureInfo.InvariantCulture);

    /// <summary>A transform (<c>a:xfrm</c>) of a box in points.</summary>
    public static string Xfrm(double x, double y, double w, double h, string attributes = "") =>
        $"<a:xfrm {attributes}><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>";

    /// <summary>A shape with a box, properties and text body (inner XML of <c>p:txBody</c> after <c>a:bodyPr</c>).</summary>
    public static string Shape(double x, double y, double w, double h, string text = "", string spPr = "", string bodyPr = "<a:bodyPr/>", string ph = "", string style = "", string name = "Shape", string attributes = "", string xfrmAttributes = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"{name}\" {attributes}/><p:cNvSpPr/><p:nvPr>{ph}</p:nvPr></p:nvSpPr>"
        + $"<p:spPr>{(w > 0 || h > 0 ? Xfrm(x, y, w, h, xfrmAttributes) : string.Empty)}{spPr}</p:spPr>{style}"
        + (text.Length > 0 ? $"<p:txBody>{bodyPr}<a:lstStyle/>{text}</p:txBody>" : string.Empty) + "</p:sp>";

    /// <summary>A paragraph of one run.</summary>
    public static string Para(string text, string rPr = "", string pPr = "") =>
        $"<a:p>{(pPr.Length > 0 ? pPr : string.Empty)}<a:r><a:rPr lang=\"en-US\" {rPr}/><a:t>{text}</a:t></a:r></a:p>";

    /// <summary>The package's bytes.</summary>
    public byte[] Build()
    {
        var parts = new List<(string, byte[])>();
        void Add(string name, string content) => parts.Add((name, Encoding.UTF8.GetBytes(content)));
        var ns = $"xmlns:a=\"{A}\" xmlns:p=\"{P}\" xmlns:r=\"{R}\"";
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        Add("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");
        var ids = string.Concat(Slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{10 + i}\"/>"));
        Add("ppt/presentation.xml", $"<p:presentation {ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>"
            + $"<p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx=\"{Emu(Size.Width)}\" cy=\"{Emu(Size.Height)}\"/>"
            + (DefaultTextStyle.Length > 0 ? $"<p:defaultTextStyle>{DefaultTextStyle}</p:defaultTextStyle>" : string.Empty) + "</p:presentation>");
        Add("ppt/_rels/presentation.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>"
            + string.Concat(Slides.Select((_, i) => $"<Relationship Id=\"rId{10 + i}\" Type=\"{R}/slide\" Target=\"slides/slide{i + 1}.xml\"/>"))
            + "</Relationships>");
        Add("ppt/slideMasters/slideMaster1.xml", $"<p:sldMaster {ns}><p:cSld>{MasterBackground}<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{MasterShapes}</p:spTree></p:cSld>"
            + $"<p:clrMap {ColorMap}/><p:txStyles>{MasterTextStyles}</p:txStyles></p:sldMaster>");
        Add("ppt/slideMasters/_rels/slideMaster1.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\">"
            + (Theme is null ? string.Empty : $"<Relationship Id=\"rId2\" Type=\"{R}/theme\" Target=\"../theme/theme1.xml\"/>") + "</Relationships>");
        Add("ppt/slideLayouts/slideLayout1.xml", $"<p:sldLayout {ns} {LayoutAttributes}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{LayoutShapes}</p:spTree></p:cSld></p:sldLayout>");
        Add("ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        if (Theme is not null)
        {
            Add("ppt/theme/theme1.xml", $"<a:theme {ns} name=\"Test\"><a:themeElements>{Theme}</a:themeElements></a:theme>");
        }

        for (var i = 0; i < Slides.Count; i++)
        {
            Add($"ppt/slides/slide{i + 1}.xml", $"<p:sld {ns} {SlideAttributes.GetValueOrDefault(i, string.Empty)}><p:cSld>{SlideBackground}<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{Slides[i]}</p:spTree></p:cSld></p:sld>");
            Add($"ppt/slides/_rels/slide{i + 1}.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>{SlideRelationships}</Relationships>");
        }

        parts.AddRange(Parts);
        return Zip(parts);
    }

    /// <summary>A ZIP package of parts.</summary>
    public static byte[] Zip(IEnumerable<(string Name, byte[] Content)> parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A deck of the default parts with slides.</summary>
    public static Deck Of(params string[] slides) => new Deck().With(slides);

    /// <summary>Adds slides; returns the deck.</summary>
    public Deck With(params string[] slides)
    {
        Slides.AddRange(slides);
        return this;
    }
}
