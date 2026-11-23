namespace RaiCore.Pdf.Cfb;

/// <summary>What a directory entry of a compound file is.</summary>
internal enum CfbEntryKind
{
    /// <summary>The root storage (whose data is the mini stream).</summary>
    Root,

    /// <summary>A storage: a folder of other entries.</summary>
    Storage,

    /// <summary>A stream: a file of bytes.</summary>
    Stream,
}

/// <summary>A storage or stream of a <see cref="CompoundFile"/>.</summary>
internal sealed class CfbEntry(string name, CfbEntryKind kind, long size, uint startSector, Guid clsid)
{
    private readonly List<CfbEntry> _children = [];

    /// <summary>The entry's name (at most 31 characters).</summary>
    public string Name { get; } = name;

    /// <summary>Whether it is the root, a storage or a stream.</summary>
    public CfbEntryKind Kind { get; } = kind;

    /// <summary>A stream's size in bytes (the root's: the size of the mini stream).</summary>
    public long Size { get; } = size;

    /// <summary>The class id of a storage (all zeros when not set).</summary>
    public Guid Clsid { get; } = clsid;

    /// <summary>The first sector of the stream's data.</summary>
    internal uint StartSector { get; } = startSector;

    /// <summary>The entries of a storage, in the directory's order.</summary>
    public IReadOnlyList<CfbEntry> Children => _children;

    /// <summary>The entry of this storage with a name, compared without case as compound files do; or <c>null</c>.</summary>
    public CfbEntry? this[string childName] => _children.Find(c => string.Equals(c.Name, childName, StringComparison.OrdinalIgnoreCase));

    internal void Add(CfbEntry child) => _children.Add(child);

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} '{Name}'{(Kind == CfbEntryKind.Stream ? $" ({Size} bytes)" : string.Empty)}";
}
