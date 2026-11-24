using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RaiCore.Pdf.Xfa.Packets;

/// <summary>
/// Reads XFA XML into an element tree within the limits: no DTDs, nothing external resolved, at most
/// <see cref="ResourceLimits.MaxElements"/> elements (counted across everything one conversion reads) nested at most
/// <see cref="ResourceLimits.MaxNestingDepth"/> deep. Comments and processing instructions are dropped.
/// </summary>
internal sealed class XfaXml(ResourceLimits limits)
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        MaxCharactersFromEntities = 1024,
    };

    /// <summary>The elements read so far.</summary>
    public int Elements { get; private set; }

    /// <summary>Counts elements made by the conversion (prototype copies, form instances) against <see cref="ResourceLimits.MaxElements"/>.</summary>
    public void Count(int elements)
    {
        Elements += elements;
        if (Elements > limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), limits.MaxElements,
                $"The form has more than ResourceLimits.MaxElements ({limits.MaxElements}) elements.");
        }
    }

    /// <summary>Reads a document's root element from bytes (UTF-8 unless the declaration or a byte order mark says otherwise).</summary>
    /// <exception cref="XfaFormatException">The data is not well-formed XML or uses a DTD.</exception>
    public XElement Load(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return Load(() => XmlReader.Create(stream, Settings));
    }

    /// <summary>Reads a document's root element from text.</summary>
    /// <exception cref="XfaFormatException">The text is not well-formed XML or uses a DTD.</exception>
    public XElement Load(string text) => Load(() => XmlReader.Create(new StringReader(text), Settings));

    private XElement Load(Func<XmlReader> open)
    {
        try
        {
            // Creating the reader already reads the start of the data (its encoding), which may fail.
            using var reader = open();
            XElement? root = null;
            var stack = new Stack<XElement>();
            var text = new StringBuilder();
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        Flush(stack, text);
                        Count(1);
                        if (stack.Count >= limits.MaxNestingDepth)
                        {
                            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), limits.MaxNestingDepth,
                                $"The form's XML is nested deeper than ResourceLimits.MaxNestingDepth ({limits.MaxNestingDepth}).");
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

                        break;
                    }

                    case XmlNodeType.EndElement:
                        Flush(stack, text);
                        stack.Pop();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (stack.Count > 0)
                        {
                            text.Append(reader.Value);
                        }

                        break;
                }
            }

            return root ?? throw new XfaFormatException("The XFA data has no root element.");
        }
        catch (XmlException e)
        {
            throw new XfaFormatException($"The XFA data is not well-formed XML: {e.Message}", e);
        }
    }

    private static void Flush(Stack<XElement> stack, StringBuilder text)
    {
        if (text.Length > 0 && stack.Count > 0)
        {
            stack.Peek().Add(new XText(text.ToString()));
        }

        text.Clear();
    }
}
