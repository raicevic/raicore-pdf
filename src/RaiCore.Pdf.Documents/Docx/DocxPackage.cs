using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>A relationship of a part.</summary>
/// <param name="Id">Its id, which the part refers to.</param>
/// <param name="Type">The last segment of its type URI (<c>styles</c>, <c>image</c>, <c>hyperlink</c>), which strict and transitional files share.</param>
/// <param name="Target">The resolved part path, or the URI as written for an external target.</param>
/// <param name="External">Whether the target is outside the package.</param>
internal readonly record struct Relationship(string Id, string Type, string Target, bool External);

/// <summary>
/// The ZIP package of a document (Open Packaging Conventions): its parts read with the decompressed bytes bounded by
/// <see cref="ResourceLimits.MaxDecompressedBytes"/> (all parts together), and parsed as XML with no DTDs and nothing
/// external resolved, their elements counted against <see cref="ResourceLimits.MaxElements"/> (all parts together) and
/// nested no deeper than <see cref="ResourceLimits.MaxNestingDepth"/>. Malformed ZIP or XML data throws
/// <see cref="DocumentFormatException"/>. The OpenDocument reader uses it too, for ODF packages (<see cref="OpenZip"/>) and
/// flat XML documents (<see cref="ForXml"/>).
/// </summary>
internal sealed class DocxPackage : IDisposable
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        MaxCharactersFromEntities = 1024,
    };

    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConversionContext _context;
    private long _read;
    private long _elements;

    private DocxPackage(ZipArchive? zip, ConversionContext context)
    {
        _zip = zip;
        _context = context;
        foreach (var entry in zip?.Entries ?? [])
        {
            _entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }
    }

    /// <summary>Opens a package.</summary>
    /// <exception cref="NotSupportedException">The data is an encrypted document.</exception>
    /// <exception cref="DocumentFormatException">The data is not a ZIP package.</exception>
    public static DocxPackage Open(byte[] data, ConversionContext context)
    {
        if (CompoundFile.IsCompoundFile(data))
        {
            throw new NotSupportedException("The data is a Compound File without a Word 97-2003 document: a password-protected (encrypted) document, which is not supported.");
        }

        if (!data.AsSpan().StartsWith("PK"u8))
        {
            throw new DocumentFormatException(data.AsSpan().StartsWith(@"{\rtf"u8)
                ? "The data is an RTF document, not a Word document (a ZIP package)."
                : "The data is not a Word document (a ZIP package).");
        }

        return OpenZip(data, context);
    }

    /// <summary>Opens a ZIP package of any kind, without checking what it holds.</summary>
    /// <exception cref="DocumentFormatException">The data is not a valid ZIP package.</exception>
    public static DocxPackage OpenZip(byte[] data, ConversionContext context)
    {
        try
        {
            return new DocxPackage(new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read), context);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            throw new DocumentFormatException("The data is not a valid ZIP package: " + ex.Message, ex);
        }
    }

    /// <summary>A package with no parts, whose <see cref="Parse"/> reads XML documents within the same limits.</summary>
    public static DocxPackage ForXml(ConversionContext context) => new(null, context);

    /// <summary>The paths of the package's parts.</summary>
    public IEnumerable<string> Paths => _entries.Keys;

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

        var limit = _context.Limits.MaxDecompressedBytes;
        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)Math.Clamp(entry.Length, 0, 1 << 20));
            var chunk = new byte[81920];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                _read += count;
                if (_read > limit)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), limit,
                        $"The document decompresses to more than ResourceLimits.MaxDecompressedBytes ({limit} bytes).");
                }

                buffer.Write(chunk, 0, count);
                _context.Checkpoint();
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new DocumentFormatException($"The document part '{path}' cannot be decompressed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses a part as XML into a tree, or returns <c>null</c> if the package has no such part. Malformed XML throws
    /// <see cref="DocumentFormatException"/>.
    /// </summary>
    public XElement? Load(string path)
    {
        return Read(path) is { } data ? Parse(data, path) : null;
    }

    /// <summary>
    /// Parses XML bytes (a part, or a whole flat document) into a tree, its elements counted with the package's.
    /// Malformed XML throws <see cref="DocumentFormatException"/>.
    /// </summary>
    public XElement? Parse(byte[] data, string path)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(Decode(data)), XmlSettings);
            return Build(reader, path);
        }
        catch (XmlException ex)
        {
            throw new DocumentFormatException($"The document part '{path}' is not well-formed XML: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the element tree of a part, counting its elements and checking their depth as it goes (an
    /// <see cref="XDocument"/> load could not stop early).
    /// </summary>
    private XElement? Build(XmlReader reader, string path)
    {
        var maxElements = _context.Limits.MaxElements;
        var maxDepth = _context.Limits.MaxNestingDepth;
        var stack = new Stack<XElement>();
        XElement? root = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (++_elements > maxElements)
                    {
                        throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), maxElements,
                            $"The document has more XML elements than ResourceLimits.MaxElements allows ({maxElements}).");
                    }

                    if (stack.Count >= maxDepth)
                    {
                        throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), maxDepth,
                            $"The document part '{path}' nests XML elements deeper than ResourceLimits.MaxNestingDepth allows ({maxDepth}).");
                    }

                    if ((_elements & 4095) == 0)
                    {
                        _context.Checkpoint();
                    }

                    var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.Prefix != "xmlns" && reader.LocalName != "xmlns")
                            {
                                element.SetAttributeValue(XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value);
                            }
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (stack.TryPeek(out var parent))
                    {
                        parent.Add(element);
                    }
                    else
                    {
                        root ??= element;
                    }

                    if (!reader.IsEmptyElement)
                    {
                        stack.Push(element);
                    }

                    break;
                case XmlNodeType.EndElement:
                    stack.Pop();
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (stack.TryPeek(out var owner))
                    {
                        owner.Add(new XText(reader.Value));
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
    /// Resolves a relationship's target against the folder of the part that has it (<c>word</c> for
    /// <c>word/document.xml</c>): relative, or absolute from the package root, with <c>..</c> and escapes resolved.
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

    /// <summary>The folder of a part (<c>word</c> for <c>word/document.xml</c>, empty at the root).</summary>
    internal static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>The relationships part of a part (<c>word/_rels/document.xml.rels</c> for <c>word/document.xml</c>).</summary>
    internal static string RelationshipsOf(string path)
    {
        var folder = FolderOf(path);
        var name = path[(folder.Length == 0 ? 0 : folder.Length + 1)..];
        return (folder.Length == 0 ? string.Empty : folder + "/") + "_rels/" + name + ".rels";
    }

    /// <summary>Reads the relationships of a part by id (the first of an id wins).</summary>
    public Dictionary<string, Relationship> Relationships(string partPath)
    {
        var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        if (Load(RelationshipsOf(partPath)) is not { } root)
        {
            return result;
        }

        var folder = FolderOf(partPath);
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName == "Relationship" && (string?)element.Attribute("Target") is { } target
                && (string?)element.Attribute("Type") is { } type)
            {
                var external = (string?)element.Attribute("TargetMode") == "External";
                var id = (string?)element.Attribute("Id") ?? string.Empty;
                result.TryAdd(id, new Relationship(id, type[(type.LastIndexOf('/') + 1)..], external ? target : ResolveTarget(folder, target), external));
            }
        }

        return result;
    }

    public void Dispose() => _zip?.Dispose();
}
