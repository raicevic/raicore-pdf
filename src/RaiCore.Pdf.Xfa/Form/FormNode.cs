using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Form;

/// <summary>The kinds of objects of the merged form.</summary>
internal enum NodeKind
{
    Subform,
    Area,
    ExclGroup,
    Field,
    Draw,
}

/// <summary>
/// An object of the merged form (XFA 3.3, "Form DOM"): an instance of a template container, with the value the data
/// merge gave it.
/// </summary>
internal sealed class FormNode(NodeKind kind, XElement template, FormNode? parent)
{
    public NodeKind Kind { get; } = kind;

    /// <summary>The template element, with its prototypes resolved.</summary>
    public XElement Template { get; } = template;

    public FormNode? Parent { get; } = parent;

    public string? Name { get; } = template.Attr("name");

    public List<FormNode> Children { get; } = [];

    /// <summary>The value in its canonical (data) form, or <c>null</c> for none.</summary>
    public string? Value { get; set; }

    /// <summary>A rich text value (an XHTML <c>body</c> element), from the data or the template.</summary>
    public XElement? RichValue { get; set; }

    /// <summary>Whether the value came from the data (rather than the template's default).</summary>
    public bool FromData { get; set; }

    /// <summary>The data the object is bound to, if any.</summary>
    public XElement? Data { get; set; }

    /// <summary>The overflow leader or trailer this object is an instance of (placed by layout, not in the flow).</summary>
    public bool IsLeaderOrTrailer { get; set; }

    /// <summary>Whether the object takes part in layout: its presence is not hidden or inactive, and it is printed.</summary>
    public bool IsLaidOut
    {
        get
        {
            var presence = Template.Attr("presence");
            if (presence is "hidden" or "inactive")
            {
                return false;
            }

            return Template.Attr("relevant") is not { } relevant || !relevant.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("-print");
        }
    }

    /// <summary>Whether the object is drawn (laid out and not invisible).</summary>
    public bool IsVisible => IsLaidOut && Template.Attr("presence") != "invisible";

    /// <summary>A path naming the object, for diagnostics (<c>form1.Page1.Name[2]</c>).</summary>
    public string Path
    {
        get
        {
            var parts = new List<string>();
            for (var node = this; node is not null && parts.Count < 32; node = node.Parent)
            {
                parts.Add(node.Name ?? $"#{node.Kind.ToString().ToLowerInvariant()}");
            }

            parts.Reverse();
            return string.Join('.', parts);
        }
    }

    public override string ToString() => $"{Kind} {Path}";
}
