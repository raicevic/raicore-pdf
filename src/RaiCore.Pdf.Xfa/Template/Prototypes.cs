using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Packets;

namespace RaiCore.Pdf.Xfa.Template;

/// <summary>
/// Resolves prototypes (XFA 3.3, "Prototypes"): an element with <c>use="#id"</c> or <c>usehref="#id"</c> inherits the
/// attributes and properties it does not set itself from the element with that id, recursively, and its content when it
/// has none. References to other documents and SOM expressions are not followed (they are reported).
/// </summary>
internal static class Prototypes
{
    /// <summary>The most passes over the template (a prototype's content may itself use prototypes).</summary>
    private const int MaxPasses = 8;

    /// <summary>The longest chain of prototypes using prototypes.</summary>
    private const int MaxChain = 16;

    /// <summary>The elements that are containers (content), not properties.</summary>
    internal static readonly HashSet<string> Containers =
        ["subform", "subformSet", "field", "draw", "exclGroup", "area", "pageSet", "pageArea", "contentArea", "proto"];

    /// <summary>Properties an element may have several of: copied from a prototype only when the element has none.</summary>
    private static readonly HashSet<string> Multiple =
        ["edge", "corner", "items", "event", "setProperty", "traverse", "exObject", "connect", "medium", "variables", "speak", "extras"];

    public static void Resolve(XElement template, XfaXml xml, Action<XfaDiagnosticKind, string>? report)
    {
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in template.DescendantsAndSelf())
        {
            if (element.Attr("id") is { Length: > 0 } id)
            {
                ids.TryAdd(id, element);
            }
        }

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var users = template.DescendantsAndSelf().Where(e => e.Attribute("use") is not null || e.Attribute("usehref") is not null).ToList();
            if (users.Count == 0)
            {
                return;
            }

            foreach (var element in users)
            {
                ResourceLimits.Checkpoint();
                Apply(element, ids, xml, report, 0);
            }
        }
    }

    private static void Apply(XElement element, Dictionary<string, XElement> ids, XfaXml xml, Action<XfaDiagnosticKind, string>? report, int chain)
    {
        var reference = (element.Attr("usehref") ?? element.Attr("use") ?? string.Empty).Trim();
        element.Attribute("use")?.Remove();
        element.Attribute("usehref")?.Remove();
        if (reference.Length == 0)
        {
            return;
        }

        if (!reference.StartsWith('#') || !ids.TryGetValue(reference[1..], out var prototype))
        {
            report?.Invoke(XfaDiagnosticKind.UnsupportedContent, $"The prototype '{reference}' cannot be resolved (only references by id within the form are).");
            return;
        }

        if (ReferenceEquals(prototype, element) || prototype.AncestorsAndSelf().Contains(element) || element.Ancestors().Contains(prototype))
        {
            return; // A prototype cannot contain or be inside what uses it.
        }

        if (chain < MaxChain && (prototype.Attribute("use") is not null || prototype.Attribute("usehref") is not null))
        {
            Apply(prototype, ids, xml, report, chain + 1);
        }

        Merge(element, prototype, xml);
    }

    /// <summary>Copies what <paramref name="target"/> does not set from <paramref name="prototype"/>.</summary>
    private static void Merge(XElement target, XElement prototype, XfaXml xml)
    {
        foreach (var attribute in prototype.Attributes())
        {
            if (attribute.Name.LocalName is not ("id" or "use" or "usehref") && target.Attribute(attribute.Name) is null)
            {
                target.SetAttributeValue(attribute.Name, attribute.Value);
            }
        }

        var hasContent = target.Elements().Any(e => Containers.Contains(e.Name.LocalName));
        if (!target.Elements().Any() && !target.Nodes().OfType<XText>().Any())
        {
            foreach (var text in prototype.Nodes().OfType<XText>())
            {
                target.Add(new XText(text.Value));
            }
        }

        foreach (var child in prototype.Elements().ToList())
        {
            var name = child.Name.LocalName;
            if (Containers.Contains(name))
            {
                if (!hasContent && name != "proto")
                {
                    Add(target, child, xml);
                }
            }
            else if (Multiple.Contains(name))
            {
                if (!target.Elements().Any(e => e.Name.LocalName == name))
                {
                    foreach (var item in prototype.Elements().Where(e => e.Name.LocalName == name))
                    {
                        Add(target, item, xml);
                    }
                }
            }
            else if (target.Child(name) is { } existing)
            {
                Merge(existing, child, xml);
            }
            else
            {
                Add(target, child, xml);
            }
        }
    }

    private static void Add(XElement target, XElement child, XfaXml xml)
    {
        var copy = new XElement(child);
        xml.Count(copy.DescendantsAndSelf().Count());
        foreach (var element in copy.DescendantsAndSelf())
        {
            element.Attribute("id")?.Remove(); // Ids stay unique: the copy is not the prototype.
        }

        target.Add(copy);
    }
}
