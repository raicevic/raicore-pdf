using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>Fields (<c>\field</c>), pictures (<c>\pict</c>) and shape properties.</summary>
internal sealed partial class RtfReader
{
    /// <summary>The most fields nested in each other that are tracked; deeper ones are left out.</summary>
    private const int MaxFieldDepth = 32;

    private readonly StringBuilder _shapeProperty = new();
    private int _pictures;

    /// <summary>What a field does with its result.</summary>
    private enum FieldAction
    {
        /// <summary>Its result is shown as it is.</summary>
        Keep,

        /// <summary>Its value is computed by the renderer; the result is the fallback.</summary>
        Compute,

        /// <summary>Its result is a hyperlink.</summary>
        Link,
    }

    /// <summary>A field being read: its instruction, what it does, and its result.</summary>
    private sealed class FieldState(RunFormat format)
    {
        public StringBuilder Instruction { get; } = new();

        public bool Classified { get; set; }

        public bool InResult { get; set; }

        public FieldAction Action { get; set; }

        public FieldKind Kind { get; set; }

        public string? Argument { get; set; }

        public NumberFormat? NumberFormat { get; set; }

        public Uri? Uri { get; set; }

        public string? Anchor { get; set; }

        public RunFormat Format { get; } = format;

        public RunFormat? ResultFormat { get; set; }

        public StringBuilder Result { get; } = new();

        /// <summary>The hyperlink the result goes into in the current paragraph, and that paragraph.</summary>
        public HyperlinkInline? Link { get; set; }

        public ParagraphBuilder? LinkOwner { get; set; }
    }

    private FieldState? CurrentField() => _stories.Count > 0 && CurrentStory.Fields.Count > 0 ? CurrentStory.Fields[^1] : null;

    private void StartField(Group group)
    {
        EmitRun();
        if (CurrentStory.Fields.Count >= MaxFieldDepth || group.Field is not null || group.Story is not null)
        {
            group.Destination = RtfDestination.Skip;
            return;
        }

        var field = new FieldState(Format(group.Chars));
        group.Field = field;
        CurrentStory.Fields.Add(field);
    }

    /// <summary>Starts a field's result: shown (or a link) as text, or kept aside as the fallback of a computed value.</summary>
    private void StartFieldResult(Group group)
    {
        EmitRun();
        if (CurrentField() is not { } field)
        {
            return;
        }

        Classify(field);
        field.InResult = true;
        group.Destination = field.Action == FieldAction.Compute ? RtfDestination.FieldResult : RtfDestination.Text;
    }

    /// <summary>Ends a field: a computed one becomes a field the renderer fills in (its result the fallback).</summary>
    private void EndField(FieldState field)
    {
        var fields = CurrentStory.Fields;
        var index = fields.LastIndexOf(field);
        if (index < 0)
        {
            return;
        }

        fields.RemoveAt(index);
        Classify(field);
        if (field.Action == FieldAction.Compute)
        {
            var result = field.Result.ToString().Trim();
            AddInline(new FieldInline(field.Kind, field.ResultFormat ?? field.Format, result.Length > 0 ? Clean(result) : null)
            {
                Argument = field.Argument,
                NumberFormat = field.NumberFormat,
            });
        }
    }

    /// <summary>Decides what a field does from its instruction (<c>PAGE</c>, <c>HYPERLINK "..."</c>), once.</summary>
    private static void Classify(FieldState field)
    {
        if (field.Classified)
        {
            return;
        }

        field.Classified = true;
        var tokens = DocxReader.Tokenize(field.Instruction.ToString());
        if (tokens.Count == 0)
        {
            return;
        }

        switch (tokens[0].ToUpperInvariant())
        {
            case "PAGE":
                (field.Action, field.Kind, field.NumberFormat) = (FieldAction.Compute, FieldKind.Page, DocxReader.FormatSwitch(tokens));
                break;
            case "NUMPAGES":
                (field.Action, field.Kind, field.NumberFormat) = (FieldAction.Compute, FieldKind.NumPages, DocxReader.FormatSwitch(tokens) ?? NumberFormat.Decimal);
                break;
            case "SECTIONPAGES":
                (field.Action, field.Kind, field.NumberFormat) = (FieldAction.Compute, FieldKind.SectionPages, DocxReader.FormatSwitch(tokens) ?? NumberFormat.Decimal);
                break;
            case "PAGEREF":
                if (tokens.Count > 1 && !tokens[1].StartsWith('\\'))
                {
                    (field.Action, field.Kind, field.Argument, field.NumberFormat) = (FieldAction.Compute, FieldKind.PageRef, tokens[1], DocxReader.FormatSwitch(tokens));
                }

                break;
            case "HYPERLINK":
                string? address = null, anchor = null;
                for (var i = 1; i < tokens.Count; i++)
                {
                    if (tokens[i] == "\\l" && i + 1 < tokens.Count)
                    {
                        anchor = tokens[++i];
                    }
                    else if (tokens[i].StartsWith('\\'))
                    {
                        i += tokens[i] is "\\o" or "\\t" ? 1 : 0;
                    }
                    else
                    {
                        address ??= tokens[i];
                    }
                }

                var uri = address is null ? null : DocxReader.LinkUri(address, anchor);
                if (uri is not null || anchor is not null)
                {
                    (field.Action, field.Uri, field.Anchor) = (FieldAction.Link, uri, uri is null ? anchor : null);
                }

                break;
        }
    }

    /// <summary>The state of a picture being read.</summary>
    private sealed class PictureState
    {
        public string Format { get; set; } = string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }

        public int GoalWidth { get; set; }

        public int GoalHeight { get; set; }

        public int ScaleX { get; set; } = 100;

        public int ScaleY { get; set; } = 100;

        public List<byte> Data { get; } = [];

        /// <summary>The high nibble of a byte being read from hexadecimal, or -1.</summary>
        public int Nibble { get; set; } = -1;

        public bool TooLarge { get; set; }
    }

    private void StartPicture(Group group)
    {
        group.Data = new PictureState();
        SetDestination(RtfDestination.Picture);
    }

    private void PictureWord(string word, int parameter, bool hasParameter)
    {
        if (Top.Data is not PictureState picture)
        {
            return;
        }

        var value = Math.Clamp(parameter, 0, 1_000_000);
        switch (word)
        {
            case "pngblip":
                picture.Format = "PNG";
                break;
            case "jpegblip":
                picture.Format = "JPEG";
                break;
            case "emfblip":
                picture.Format = "EMF";
                break;
            case "wmetafile":
                picture.Format = "WMF";
                break;
            case "macpict":
                picture.Format = "PICT";
                break;
            case "pmmetafile":
                picture.Format = "OS/2 metafile";
                break;
            case "dibitmap":
                picture.Format = "DIB";
                break;
            case "wbitmap":
                picture.Format = "device-dependent bitmap";
                break;
            case "picw":
                picture.Width = value;
                break;
            case "pich":
                picture.Height = value;
                break;
            case "picwgoal":
                picture.GoalWidth = value;
                break;
            case "pichgoal":
                picture.GoalHeight = value;
                break;
            case "picscalex" when hasParameter && parameter > 0:
                picture.ScaleX = Math.Min(parameter, 10_000);
                break;
            case "picscaley" when hasParameter && parameter > 0:
                picture.ScaleY = Math.Min(parameter, 10_000);
                break;
        }
    }

    /// <summary>Picture data in hexadecimal (white space and other characters between the digits are left out).</summary>
    private void PictureHex(ReadOnlySpan<byte> text)
    {
        if (Top.Data is not PictureState picture || picture.TooLarge)
        {
            return;
        }

        var limit = _context.Limits.MaxResourceBytes;
        foreach (var b in text)
        {
            var digit = RtfLexer.Hex(b);
            if (digit < 0)
            {
                continue;
            }

            if (picture.Nibble < 0)
            {
                picture.Nibble = digit;
                continue;
            }

            if (picture.Data.Count >= limit)
            {
                picture.TooLarge = true;
                picture.Data.Clear();
                return;
            }

            picture.Data.Add((byte)((picture.Nibble << 4) | digit));
            picture.Nibble = -1;
        }
    }

    /// <summary>Picture data from <c>\bin</c>.</summary>
    private void PictureBytes(ReadOnlySpan<byte> data)
    {
        if (Top.Data is not PictureState picture || picture.TooLarge)
        {
            return;
        }

        if (picture.Data.Count + (long)data.Length > _context.Limits.MaxResourceBytes)
        {
            picture.TooLarge = true;
            picture.Data.Clear();
            return;
        }

        picture.Data.AddRange(data);
    }

    /// <summary>
    /// Ends a picture: PNG and JPEG (and device-independent bitmaps, as BMP) become images at their goal size and
    /// scale; metafiles are reported and left out.
    /// </summary>
    private void EndPicture(Group group)
    {
        if (group.Data is not PictureState picture || Parent is not { Destination: RtfDestination.Text or RtfDestination.ShapePropertyValue })
        {
            return;
        }

        var name = $"Picture {++_pictures}";
        byte[]? data = picture.Format switch
        {
            "PNG" or "JPEG" => [.. picture.Data],
            "DIB" => Bitmap(CollectionsMarshal.AsSpan(picture.Data)),
            _ => null,
        };
        if (picture.TooLarge)
        {
            _context.Report(DocumentDiagnosticKind.ImageNotLoaded, $"{name} is larger than ResourceLimits.MaxResourceBytes allows ({_context.Limits.MaxResourceBytes} bytes); it is left out.");
            return;
        }

        if (data is null)
        {
            _context.Report(DocumentDiagnosticKind.UnsupportedContent,
                $"{name} is in {(picture.Format.Length > 0 ? picture.Format + " format" : "an unknown format")}, which cannot be drawn (EMF, WMF and PICT pictures are not supported); it is left out.");
            return;
        }

        if (data.Length == 0)
        {
            return;
        }

        // The goal size is in twips; without one, a bitmap's size in pixels at 96 dpi.
        var width = picture.GoalWidth > 0 ? picture.GoalWidth / 20.0 : picture.Width * 0.75;
        var height = picture.GoalHeight > 0 ? picture.GoalHeight / 20.0 : picture.Height * 0.75;
        width = Math.Min(14400, width * picture.ScaleX / 100.0);
        height = Math.Min(14400, height * picture.ScaleY / 100.0);
        AddInline(new ImageInline(data, width, height) { Name = name });
    }

    /// <summary>A device-independent bitmap (a BMP file without its file header) as a BMP file, or <c>null</c> if damaged.</summary>
    internal static byte[]? Bitmap(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 16)
        {
            return null;
        }

        var bytes = dib.ToArray();
        var header = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (header is < 12 or > 1024 || header > bytes.Length)
        {
            return null;
        }

        int bits, compression = 0, used = 0;
        if (header == 12)
        {
            bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        }
        else
        {
            if (bytes.Length < 36)
            {
                return null;
            }

            bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
            compression = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
            used = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32));
        }

        var colors = used is > 0 and <= 256 ? used : bits is > 0 and <= 8 ? 1 << bits : 0;
        var masks = header == 40 && compression is 3 or 6 ? (compression == 3 ? 12 : 16) : 0;
        var offset = 14 + header + masks + (colors * (header == 12 ? 3 : 4));
        var file = new byte[14 + bytes.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), Math.Min(offset, file.Length));
        bytes.CopyTo(file, 14);
        return file;
    }
}
