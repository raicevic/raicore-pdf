using System.Buffers.Binary;

namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>The record types of [MS-PPT] and [MS-ODRAW] the reader uses.</summary>
internal static class Rt
{
    public const int Document = 0x03E8;
    public const int DocumentAtom = 0x03E9;
    public const int Slide = 0x03EE;
    public const int SlideAtom = 0x03EF;
    public const int SlidePersistAtom = 0x03F3;
    public const int Environment = 0x03F2;
    public const int MainMaster = 0x03F8;
    public const int SlideShowSlideInfoAtom = 0x03F9;
    public const int ExObjList = 0x0409;
    public const int DrawingGroup = 0x040B;
    public const int Drawing = 0x040C;
    public const int ColorSchemeAtom = 0x07F0;
    public const int FontCollection = 0x07D5;
    public const int OutlineTextRefAtom = 0x0F9E;
    public const int TextHeaderAtom = 0x0F9F;
    public const int TextCharsAtom = 0x0FA0;
    public const int StyleTextPropAtom = 0x0FA1;
    public const int TextMasterStyleAtom = 0x0FA3;
    public const int TextCFExceptionAtom = 0x0FA4;
    public const int TextPFExceptionAtom = 0x0FA5;
    public const int TextRulerAtom = 0x0FA6;
    public const int TextBytesAtom = 0x0FA8;
    public const int FontEntityAtom = 0x0FB7;
    public const int CString = 0x0FBA;
    public const int ExHyperlinkAtom = 0x0FD3;
    public const int ExHyperlink = 0x0FD7;
    public const int SlideNumberMCAtom = 0x0FD8;
    public const int HeadersFooters = 0x0FD9;
    public const int HeadersFootersAtom = 0x0FDA;
    public const int TextInteractiveInfoAtom = 0x0FDF;
    public const int SlideListWithText = 0x0FF0;
    public const int InteractiveInfo = 0x0FF2;
    public const int InteractiveInfoAtom = 0x0FF3;
    public const int UserEditAtom = 0x0FF5;
    public const int CurrentUserAtom = 0x0FF6;
    public const int DateTimeMCAtom = 0x0FF7;
    public const int GenericDateMCAtom = 0x0FF8;
    public const int FooterMCAtom = 0x0FFA;
    public const int PlaceholderAtom = 0x0BC3;
    public const int PersistDirectoryAtom = 0x1772;
    public const int RtfDateTimeMCAtom = 0x1018;
    public const int CryptSession10 = 0x2F14;

    // OfficeArt ([MS-ODRAW]).
    public const int DggContainer = 0xF000;
    public const int BStoreContainer = 0xF001;
    public const int DgContainer = 0xF002;
    public const int SpgrContainer = 0xF003;
    public const int SpContainer = 0xF004;
    public const int Fbse = 0xF007;
    public const int Fspgr = 0xF009;
    public const int Fsp = 0xF00A;
    public const int Fopt = 0xF00B;
    public const int ClientTextbox = 0xF00D;
    public const int ChildAnchor = 0xF00F;
    public const int ClientAnchor = 0xF010;
    public const int ClientData = 0xF011;
    public const int BlipFirst = 0xF018;
    public const int BlipLast = 0xF117;
    public const int SecondaryFopt = 0xF121;
    public const int TertiaryFopt = 0xF122;
}

/// <summary>
/// A record of a PowerPoint binary stream: its header (version, instance, type) and where its body is. Containers
/// (version 0xF) hold other records.
/// </summary>
/// <param name="Type">The record type.</param>
/// <param name="Instance">The record instance.</param>
/// <param name="Version">The record version (0xF for a container).</param>
/// <param name="Offset">Where the body starts in its stream.</param>
/// <param name="Length">The body's length, cut to what the stream (and its parent) holds.</param>
internal readonly record struct Record(int Type, int Instance, int Version, int Offset, int Length)
{
    /// <summary>Whether the record is a container.</summary>
    public bool IsContainer => Version == 0xF;

    /// <summary>Where the body ends.</summary>
    public int End => Offset + Length;
}

/// <summary>
/// A stream of records (the PowerPoint Document stream, or a picture): reads headers and walks containers, the bodies
/// cut to their parents (a record claiming more than its parent holds is cut, not trusted) and every record read
/// counted against <see cref="ResourceLimits.MaxElements"/>.
/// </summary>
internal sealed class RecordStream(byte[] data, ResourceLimits limits)
{
    private long _records;

    /// <summary>The stream's bytes.</summary>
    public byte[] Data { get; } = data;

    /// <summary>The record whose header is at an offset, its body cut to <paramref name="end"/>; <c>null</c> when there is no room for a header.</summary>
    public Record? At(long offset, int end)
    {
        end = Math.Min(end, Data.Length);
        if (offset < 0 || offset + 8 > end)
        {
            return null;
        }

        if (++_records > limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), limits.MaxElements,
                $"The presentation has more records than ResourceLimits.MaxElements allows ({limits.MaxElements}).");
        }

        var at = (int)offset;
        var versionInstance = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at + 2));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at + 4));
        var body = at + 8;
        return new Record(type, versionInstance >> 4, versionInstance & 0xF, body, (int)Math.Min(length, (uint)(end - body)));
    }

    /// <summary>The record at an offset of the stream.</summary>
    public Record? At(long offset) => At(offset, Data.Length);

    /// <summary>The records of a container (none for an atom), in order.</summary>
    public IEnumerable<Record> Children(Record parent)
    {
        if (!parent.IsContainer)
        {
            yield break;
        }

        // Each record ends at least 8 bytes (its header) after it starts, so the walk ends.
        var offset = parent.Offset;
        while (At(offset, parent.End) is { } child)
        {
            yield return child;
            offset = child.End;
        }
    }

    /// <summary>The first record of a type (and instance) in a container, or <c>null</c>.</summary>
    public Record? Child(Record parent, int type, int instance = -1)
    {
        foreach (var child in Children(parent))
        {
            if (child.Type == type && (instance < 0 || child.Instance == instance))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>The body of a record.</summary>
    public ReadOnlySpan<byte> Body(Record record) => Data.AsSpan(record.Offset, record.Length);
}

/// <summary>Little-endian reads that give 0 past the end of the data (a short atom reads as its defaults).</summary>
internal static class Bytes
{
    public static int U8(ReadOnlySpan<byte> data, int at) => at >= 0 && at < data.Length ? data[at] : 0;

    public static int U16(ReadOnlySpan<byte> data, int at) =>
        at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : 0;

    public static int I16(ReadOnlySpan<byte> data, int at) =>
        at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadInt16LittleEndian(data[at..]) : 0;

    public static int I32(ReadOnlySpan<byte> data, int at) =>
        at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data[at..]) : 0;

    public static uint U32(ReadOnlySpan<byte> data, int at) =>
        at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) : 0;
}
