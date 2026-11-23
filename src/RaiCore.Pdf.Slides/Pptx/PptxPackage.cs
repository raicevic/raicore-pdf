using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>A relationship of a part: its id, type (the last segment of its URI), target, and whether it is external.</summary>
/// <param name="Id">The id.</param>
/// <param name="Type">The type, such as <c>slide</c> (strict and transitional files share these).</param>
/// <param name="Target">The resolved part name, or the target as written for an external one.</param>
/// <param name="External">Whether the target is outside the package (a URL).</param>
internal readonly record struct Relationship(string Id, string Type, string Target, bool External);

/// <summary>
/// The ZIP package of a presentation (Open Packaging Conventions): its parts read with the decompressed bytes bounded
/// by <see cref="ResourceLimits.MaxDecompressedBytes"/> (all parts together), and parsed as XML with no DTDs and
/// nothing external resolved, the elements of all parts together bounded by <see cref="ResourceLimits.MaxElements"/>
/// and their nesting by <see cref="ResourceLimits.MaxNestingDepth"/>. Malformed ZIP or XML data throws
/// <see cref="PresentationFormatException"/>.
/// </summary>
internal sealed class PptxPackage : IDisposable
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        CheckCharacters = false,
        MaxCharactersFromEntities = 1024,
    };

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XElement?> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Relationship>> _relationships = new(StringComparer.OrdinalIgnoreCase);
    private readonly ResourceLimits _limits;
    private readonly Action? _checkpoint;
    private long _read;
    private long _elements;

    private PptxPackage(ZipArchive zip, ResourceLimits limits, Action? checkpoint)
    {
        _zip = zip;
        _limits = limits;
        _checkpoint = checkpoint;
        foreach (var entry in zip.Entries)
        {
            _entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }
    }

    /// <summary>Opens a package.</summary>
    /// <exception cref="NotSupportedException">The data is an encrypted presentation or a PowerPoint 97-2003 file.</exception>
    /// <exception cref="PresentationFormatException">The data is not a ZIP package.</exception>
    public static PptxPackage Open(byte[] data, ResourceLimits limits, Action? checkpoint = null)
    {
        if (CompoundFile.IsCompoundFile(data))
        {
            throw new NotSupportedException(
                "The data is a Compound File: a password-protected (encrypted) presentation or a PowerPoint 97-2003 (.ppt) file, which are not supported.");
        }

        if (!data.AsSpan().StartsWith("PK"u8))
        {
            throw new PresentationFormatException("The data is not a PPTX presentation (a ZIP package).");
        }

        try
        {
            return new PptxPackage(new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read), limits, checkpoint);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            throw new PresentationFormatException("The data is not a valid ZIP package: " + ex.Message, ex);
        }
    }

    /// <summary>Whether the package has a part.</summary>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>A part's bytes, or <c>null</c> if the package has no such part.</summary>
    /// <exception cref="ResourceLimitExceededException">The parts read decompress to more than the limit.</exception>
    public byte[]? Read(string path)
    {
        if (!_entries.TryGetValue(path, out var entry))
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)Math.Min(Math.Max(entry.Length, 0), 1 << 20));
            var chunk = new byte[81920];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                _read += count;
                if (_read > _limits.MaxDecompressedBytes)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), _limits.MaxDecompressedBytes,
                        $"The presentation decompresses to more than ResourceLimits.MaxDecompressedBytes ({_limits.MaxDecompressedBytes} bytes).");
                }

                buffer.Write(chunk, 0, count);
                _checkpoint?.Invoke();
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new PresentationFormatException($"The presentation part '{path}' cannot be decompressed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A part parsed as XML (once; later calls return the same tree), or <c>null</c> if the package has no such part.
    /// Malformed XML throws <see cref="PresentationFormatException"/>.
    /// </summary>
    public XElement? Xml(string path)
    {
        if (_parsed.TryGetValue(path, out var cached))
        {
            return cached;
        }

        XElement? root = null;
        if (Read(path) is { } data)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(Decode(data)), XmlSettings);
                root = Load(reader);
            }
            catch (XmlException ex)
            {
                throw new PresentationFormatException($"The presentation part '{path}' is not well-formed XML: {ex.Message}", ex);
            }
        }

        _parsed[path] = root;
        return root;
    }

    /// <summary>
    /// Builds the element tree of a reader without recursion, counting the elements against
    /// <see cref="ResourceLimits.MaxElements"/> (all parts together) and their depth against
    /// <see cref="ResourceLimits.MaxNestingDepth"/>. Text is kept; whitespace-only text only where it is content
    /// (inside DrawingML text, <c>a:t</c>).
    /// </summary>
    private XElement? Load(XmlReader reader)
    {
        XElement? root = null;
        var stack = new Stack<XElement>();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    if (++_elements > _limits.MaxElements)
                    {
                        throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _limits.MaxElements,
                            $"The presentation has more XML elements than ResourceLimits.MaxElements ({_limits.MaxElements}).");
                    }

                    if (stack.Count >= _limits.MaxNestingDepth)
                    {
                        throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _limits.MaxNestingDepth,
                            $"The presentation's XML nests deeper than ResourceLimits.MaxNestingDepth ({_limits.MaxNestingDepth}).");
                    }

                    var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.NamespaceURI != "http://www.w3.org/2000/xmlns/")
                            {
                                element.SetAttributeValue(XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value);
                            }
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (stack.Count > 0)
                    {
                        stack.Peek().Add(element);
                    }
                    else
                    {
                        root ??= element;
                    }

                    if (!reader.IsEmptyElement)
                    {
                        stack.Push(element);
                    }

                    if (_elements % 1024 == 0)
                    {
                        _checkpoint?.Invoke();
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }

                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    if (stack.Count > 0)
                    {
                        stack.Peek().Add(new XText(reader.Value));
                    }

                    break;
                case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (stack.Count > 0 && stack.Peek().Name.LocalName == "t")
                    {
                        stack.Peek().Add(new XText(reader.Value));
                    }

                    break;
            }
        }

        return root;
    }

    /// <summary>
    /// Decodes a part's text: UTF-16 or UTF-8 by its byte order mark (or the zero bytes of UTF-16 without one), else
    /// UTF-8, with invalid bytes as U+FFFD. The XML reader gets text, so a declared encoding cannot switch it.
    /// </summary>
    internal static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 4 && data[0] == '<' && data[1] == 0 && data[2] != 0)
        {
            return Encoding.Unicode.GetString(data);
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == '<')
        {
            return Encoding.BigEndianUnicode.GetString(data);
        }

        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// Resolves a relationship's target against the folder of the part that has it (<c>ppt</c> for
    /// <c>ppt/presentation.xml</c>): relative, or absolute from the package root, with <c>..</c> and escapes resolved.
    /// </summary>
    internal static string ResolveTarget(string folder, string target)
    {
        target = target.Replace('\\', '/');
        try
        {
            target = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
        }

        var segments = new List<string>();
        if (!target.StartsWith('/'))
        {
            segments.AddRange(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>The folder of a part (<c>ppt</c> for <c>ppt/presentation.xml</c>, empty at the root).</summary>
    internal static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>The relationships part of a part (<c>ppt/_rels/presentation.xml.rels</c> for <c>ppt/presentation.xml</c>).</summary>
    internal static string RelationshipsOf(string path)
    {
        var folder = FolderOf(path);
        var name = path[(folder.Length == 0 ? 0 : folder.Length + 1)..];
        return (folder.Length == 0 ? string.Empty : folder + "/") + "_rels/" + name + ".rels";
    }

    /// <summary>The relationships of a part (read once).</summary>
    public IReadOnlyList<Relationship> Relationships(string partPath)
    {
        if (_relationships.TryGetValue(partPath, out var cached))
        {
            return cached;
        }

        var folder = FolderOf(partPath);
        var result = new List<Relationship>();
        if (Xml(RelationshipsOf(partPath)) is { } root)
        {
            foreach (var element in root.Elements())
            {
                if (element.Name.LocalName == "Relationship" && (string?)element.Attribute("Target") is { } target
                    && (string?)element.Attribute("Type") is { } type)
                {
                    var external = (string?)element.Attribute("TargetMode") == "External";
                    result.Add(new Relationship(
                        (string?)element.Attribute("Id") ?? string.Empty,
                        type[(type.LastIndexOf('/') + 1)..],
                        external ? target : ResolveTarget(folder, target),
                        external));
                }
            }
        }

        _relationships[partPath] = result;
        return result;
    }

    /// <summary>The relationship of a part with an id, or <c>null</c>.</summary>
    public Relationship? Relationship(string partPath, string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (var relationship in Relationships(partPath))
        {
            if (relationship.Id == id)
            {
                return relationship;
            }
        }

        return null;
    }

    /// <summary>The target of the first internal relationship of a type, or <c>null</c>.</summary>
    public string? Target(string partPath, string type)
    {
        foreach (var relationship in Relationships(partPath))
        {
            if (!relationship.External && relationship.Type == type)
            {
                return relationship.Target;
            }
        }

        return null;
    }

    public void Dispose() => _zip.Dispose();
}
