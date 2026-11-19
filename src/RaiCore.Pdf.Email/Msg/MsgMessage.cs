using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Email.Cfb;

namespace RaiCore.Pdf.Email.Msg;

/// <summary>A recipient of an Outlook message: To, Cc or Bcc, with a display name and an address (either may be empty).</summary>
internal sealed record MsgRecipient(int Type, string Name, string Address);

/// <summary>A file attached to an Outlook message, or an attached message (<see cref="Message"/>).</summary>
internal sealed class MsgAttachment
{
    public string FileName { get; init; } = string.Empty;

    public string? MimeType { get; init; }

    public string? ContentId { get; init; }

    public string? ContentLocation { get; init; }

    public byte[] Data { get; init; } = [];

    /// <summary>Whether Outlook hides it from the attachment list (<c>PidTagAttachmentHidden</c>, images shown in the body).</summary>
    public bool IsHidden { get; init; }

    /// <summary>An attached message (attach method 5), read like the message itself.</summary>
    public MsgMessage? Message { get; init; }
}

/// <summary>
/// An Outlook message ([MS-OXMSG]) read from its compound file: the properties that make up an email (sender,
/// recipients, date, subject, body, attachments), from the fixed-size values of <c>__properties_version1.0</c> and the
/// <c>__substg1.0_IIIITTTT</c> streams of the variable-size ones.
/// </summary>
internal sealed class MsgMessage
{
    // Property ids ([MS-OXPROPS]).
    private const ushort PidSubject = 0x0037;
    private const ushort ClientSubmitTime = 0x0039;
    private const ushort SentRepresentingName = 0x0042;
    private const ushort SentRepresentingSmtpAddress = 0x5D02;
    private const ushort SentRepresentingEmailAddress = 0x0065;
    private const ushort SenderName = 0x0C1A;
    private const ushort SenderEmailAddress = 0x0C1F;
    private const ushort SenderSmtpAddress = 0x5D01;
    private const ushort MessageDeliveryTime = 0x0E06;
    private const ushort PidBody = 0x1000;
    private const ushort RtfCompressed = 0x1009;
    private const ushort PidHtml = 0x1013;
    private const ushort InternetMessageId = 0x1035;
    private const ushort InternetCodepage = 0x3FDE;
    private const ushort MessageCodepage = 0x3FFD;
    private const ushort DisplayName = 0x3001;
    private const ushort EmailAddress = 0x3003;
    private const ushort SmtpAddress = 0x39FE;
    private const ushort RecipientType = 0x0C15;
    private const ushort AttachDataObject = 0x3701;
    private const ushort AttachFilename = 0x3704;
    private const ushort AttachMethod = 0x3705;
    private const ushort AttachLongFilename = 0x3707;
    private const ushort AttachMimeTag = 0x370E;
    private const ushort AttachContentId = 0x3712;
    private const ushort AttachContentLocation = 0x3713;
    private const ushort AttachmentHidden = 0x7FFE;
    private const ushort DisplayNameAttachment = 0x3001;

    // Property types.
    private const ushort Int16 = 0x0002;
    private const ushort Int32 = 0x0003;
    private const ushort Boolean = 0x000B;
    private const ushort Object = 0x000D;
    private const ushort String8 = 0x001E;
    private const ushort Unicode = 0x001F;
    private const ushort SysTime = 0x0040;
    private const ushort Binary = 0x0102;

    private readonly Dictionary<ushort, (ushort Type, object Value)> _properties = [];

    public string Subject { get; private set; } = string.Empty;

    public string FromName { get; private set; } = string.Empty;

    public string FromAddress { get; private set; } = string.Empty;

    public DateTime? Date { get; private set; }

    public string? MessageId { get; private set; }

    /// <summary>The HTML body (from <c>PidTagHtml</c> or the RTF's encapsulated HTML), or <c>null</c>.</summary>
    public string? HtmlBody { get; private set; }

    /// <summary>The plain text body (from <c>PidTagBody</c> or the RTF's text), or <c>null</c>.</summary>
    public string? TextBody { get; private set; }

    public List<MsgRecipient> Recipients { get; } = [];

    public List<MsgAttachment> Attachments { get; } = [];

    /// <summary>Reads the message of an Outlook <c>.msg</c> file.</summary>
    /// <exception cref="PdfFormatException">It is not an Outlook message.</exception>
    /// <exception cref="ResourceLimitExceededException">It nests attached messages too deep, or has too many parts (see <see cref="CompoundFile"/>).</exception>
    public static MsgMessage Read(ReadOnlyMemory<byte> data, ResourceLimits limits)
    {
        var file = CompoundFile.Open(data, limits);
        if (file.Root["__properties_version1.0"] is null)
        {
            throw new PdfFormatException("The compound file is not an Outlook message (no __properties_version1.0 stream).");
        }

        return Read(file, file.Root, 32, limits);
    }

    /// <summary>
    /// Reads a message storage. How deep attached messages nest and how many recipients and attachments there are is
    /// bounded by the compound file's limits: each attached message is two storages deeper, each part an entry.
    /// </summary>
    private static MsgMessage Read(CompoundFile file, CfbEntry storage, int headerSize, ResourceLimits limits)
    {
        var message = new MsgMessage();
        message.ReadProperties(file, storage, headerSize);
        var codePage = message.Int(MessageCodepage) ?? message.Int(InternetCodepage) ?? 1252;
        message.Subject = message.String(PidSubject, codePage) ?? string.Empty;
        message.FromName = message.String(SentRepresentingName, codePage) ?? message.String(SenderName, codePage) ?? string.Empty;
        message.FromAddress = Address(
            message.String(SentRepresentingSmtpAddress, codePage) ?? message.String(SenderSmtpAddress, codePage),
            message.String(SentRepresentingEmailAddress, codePage) ?? message.String(SenderEmailAddress, codePage));
        message.Date = message.Time(ClientSubmitTime) ?? message.Time(MessageDeliveryTime);
        message.MessageId = message.String(InternetMessageId, codePage);
        message.ReadBody(codePage, limits);

        foreach (var child in storage.Children.Where(c => c.Kind == CfbEntryKind.Storage).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (child.Name.StartsWith("__recip_version1.0_", StringComparison.OrdinalIgnoreCase))
            {
                var recipient = new MsgMessage();
                recipient.ReadProperties(file, child, 8);
                message.Recipients.Add(new MsgRecipient(
                    recipient.Int(RecipientType) is { } type ? type & 0xFF : 1,
                    recipient.String(DisplayName, codePage) ?? string.Empty,
                    Address(recipient.String(SmtpAddress, codePage), recipient.String(EmailAddress, codePage))));
            }
            else if (child.Name.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase))
            {
                var attachment = new MsgMessage();
                attachment.ReadProperties(file, child, 8);
                MsgMessage? embedded = null;
                if (attachment.Int(AttachMethod) == 5 && child[$"__substg1.0_{AttachDataObject:X4}{Object:X4}"] is { Kind: CfbEntryKind.Storage } inner)
                {
                    embedded = Read(file, inner, 24, limits);
                }

                message.Attachments.Add(new MsgAttachment
                {
                    FileName = attachment.String(AttachLongFilename, codePage) ?? attachment.String(AttachFilename, codePage)
                        ?? attachment.String(DisplayNameAttachment, codePage) ?? string.Empty,
                    MimeType = attachment.String(AttachMimeTag, codePage),
                    ContentId = attachment.String(AttachContentId, codePage),
                    ContentLocation = attachment.String(AttachContentLocation, codePage),
                    Data = attachment.Bytes(AttachDataObject) ?? [],
                    IsHidden = attachment.Int(AttachmentHidden) is { } hidden && hidden != 0,
                    Message = embedded,
                });
            }
        }

        return message;
    }

    /// <summary>The body: HTML from <c>PidTagHtml</c>, else from the compressed RTF if it encapsulates HTML; plain text from <c>PidTagBody</c>, else the RTF's text.</summary>
    private void ReadBody(int codePage, ResourceLimits limits)
    {
        HtmlBody = _properties.TryGetValue(PidHtml, out var html) ? html.Value switch
        {
            byte[] bytes => Mime.Charsets.Decode(bytes, CharsetOf(Int(InternetCodepage) ?? codePage), html: true),
            string text => text,
            _ => null,
        } : null;
        TextBody = String(PidBody, codePage);
        if (HtmlBody is null && Bytes(RtfCompressed) is { } compressed)
        {
            try
            {
                var rtf = Encoding.Latin1.GetString(CompressedRtf.Decompress(compressed, limits.MaxDecompressedBytes));
                HtmlBody = RtfBody.Html(rtf);
                if (HtmlBody is null && string.IsNullOrWhiteSpace(TextBody))
                {
                    TextBody = RtfBody.Text(rtf);
                }
            }
            catch (PdfFormatException)
            {
                // A damaged RTF body: the plain text one (if any) stands in.
            }
        }
    }

    /// <summary>The properties of a storage: fixed-size values from its property stream, variable-size ones from their streams.</summary>
    private void ReadProperties(CompoundFile file, CfbEntry storage, int headerSize)
    {
        if (storage["__properties_version1.0"] is { Kind: CfbEntryKind.Stream } stream)
        {
            var data = file.Read(stream);
            for (var offset = headerSize; offset + 16 <= data.Length; offset += 16)
            {
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                var (id, type) = ((ushort)(tag >> 16), (ushort)tag);
                var value = data.AsSpan(offset + 8, 8);
                object? fixedValue = type switch
                {
                    Int16 => (int)BinaryPrimitives.ReadInt16LittleEndian(value),
                    Int32 => BinaryPrimitives.ReadInt32LittleEndian(value),
                    Boolean => BinaryPrimitives.ReadUInt16LittleEndian(value) != 0 ? 1 : 0,
                    SysTime => BinaryPrimitives.ReadInt64LittleEndian(value),
                    _ => null,
                };
                if (fixedValue is not null)
                {
                    _properties[id] = (type, fixedValue);
                }
            }
        }

        foreach (var child in storage.Children)
        {
            if (child.Kind != CfbEntryKind.Stream || child.Name.Length != 20 || !child.Name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase)
                || !ushort.TryParse(child.Name.AsSpan(12, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                || !ushort.TryParse(child.Name.AsSpan(16, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var type))
            {
                continue;
            }

            if (type is Unicode or String8 or Binary && !(_properties.TryGetValue(id, out var existing) && existing.Type == Unicode && type != Unicode))
            {
                var bytes = file.Read(child);
                _properties[id] = (type, type == Unicode ? DecodeUnicode(bytes) : bytes);
            }
        }
    }

    private int? Int(ushort id) => _properties.TryGetValue(id, out var p) && p.Value is int i ? i : null;

    private byte[]? Bytes(ushort id) => _properties.TryGetValue(id, out var p) && p.Value is byte[] b ? b : null;

    private DateTime? Time(ushort id) =>
        _properties.TryGetValue(id, out var p) && p.Value is long ticks && ticks > 0 && ticks < DateTime.MaxValue.ToFileTimeUtc()
            ? DateTime.FromFileTimeUtc(ticks)
            : null;

    /// <summary>A string property: Unicode as it is, 8-bit in the message's code page; trailing nulls removed.</summary>
    private string? String(ushort id, int codePage) => _properties.TryGetValue(id, out var p) ? p.Value switch
    {
        string text => text,
        byte[] bytes when p.Type == String8 => CodePageEncoding(codePage).GetString(bytes).TrimEnd('\0'),
        _ => null,
    } : null;

    private static string DecodeUnicode(byte[] bytes) => Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).TrimEnd('\0');

    /// <summary>An SMTP address: the SMTP property if set, else the address if it looks like one (not an Exchange path).</summary>
    private static string Address(string? smtp, string? address) =>
        !string.IsNullOrWhiteSpace(smtp) ? smtp.Trim()
        : address is { } a && a.Contains('@', StringComparison.Ordinal) && !a.StartsWith('/') ? a.Trim()
        : string.Empty;

    private static string CharsetOf(int codePage) => codePage switch
    {
        65001 => "utf-8",
        1200 => "utf-16le",
        20127 => "us-ascii",
        28591 => "iso-8859-1",
        _ => "windows-" + codePage.ToString(CultureInfo.InvariantCulture),
    };

    private static Encoding CodePageEncoding(int codePage) =>
        codePage == 65001 ? Encoding.UTF8
        : codePage == 1200 ? Encoding.Unicode
        : (codePage is > 0 and < 65535 ? CodePagesEncodingProvider.Instance.GetEncoding(codePage) : null)
            ?? CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
}
