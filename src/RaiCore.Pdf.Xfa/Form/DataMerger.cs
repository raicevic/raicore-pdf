using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Packets;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Form;

/// <summary>The merged form: the root subform's instances and the page set's page areas with their content.</summary>
internal sealed class MergedForm
{
    public required FormNode Root { get; init; }

    /// <summary>The page areas in document order (nested page sets flattened), each an area node with its boilerplate.</summary>
    public required List<FormNode> PageAreas { get; init; }

    /// <summary>The page set's relation: <c>orderedOccurrence</c>, <c>simplexPaginated</c> or <c>duplexPaginated</c>.</summary>
    public string Relation { get; init; } = "orderedOccurrence";

    /// <summary>The objects with an id, for references (<c>#id</c>) from leaders, trailers, breaks and floating fields.</summary>
    public Dictionary<string, FormNode> Ids { get; } = new(StringComparer.Ordinal);

    /// <summary>The template's elements with an id (prototypes included).</summary>
    public Dictionary<string, XElement> TemplateIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes an object from a template element that is not in the form (an overflow leader or trailer kept as a
    /// prototype), with its default values, under a parent it is not a child of.
    /// </summary>
    public required Func<XElement, FormNode, FormNode> Detach { get; init; }
}

/// <summary>
/// Merges data into the template (XFA 3.3, "Basic Data Binding"): subforms bind to data groups and fields to data values
/// of the same name (match once, each data node bound once), repeated subforms (<c>occur</c>) get an instance per data
/// group up to their maximum, global fields take the first data value of their name anywhere in the record, data
/// references (<c>bind match="dataRef"</c>) follow a SOM expression from the current data node, and fields that bind
/// to nothing (<c>match="none"</c>, or no data) keep their default value. Unnamed subforms and subforms that find no
/// data group of their name pass the current data group on to their content.
/// </summary>
internal sealed class DataMerger
{
    private const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

    private readonly XfaXml _xml;
    private readonly Action<XfaDiagnosticKind, string>? _report;
    private readonly HashSet<XElement> _bound = new(ReferenceEqualityComparer.Instance);
    private readonly XElement? _record;
    private readonly XElement? _dataRoot;
    private readonly bool _hasData;
    private MergedForm? _form;

    private DataMerger(XElement? dataRoot, XElement? record, XfaXml xml, Action<XfaDiagnosticKind, string>? report)
    {
        _dataRoot = dataRoot;
        _record = record;
        _xml = xml;
        _report = report;
        _hasData = record is not null && (record.HasElements || record.Value.Trim().Length > 0);
    }

    /// <summary>Merges data (the <c>xfa:data</c> element, or <c>null</c>) into a template whose prototypes are resolved.</summary>
    public static MergedForm Merge(XElement template, XElement? data, XfaXml xml, Action<XfaDiagnosticKind, string>? report)
    {
        var record = data?.Elements().FirstOrDefault();
        var merger = new DataMerger(data, record, xml, report);
        return merger.Merge(template);
    }

    /// <summary>The <c>xfa:data</c> element of a datasets packet or of data given separately (datasets, data, or the record itself).</summary>
    public static XElement? FindData(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (element.Name.LocalName == "datasets")
        {
            return element.Elements().FirstOrDefault(e => e.Name.LocalName == "data");
        }

        if (element.Name.LocalName == "data" && element.Name.NamespaceName == DataNamespace)
        {
            return element;
        }

        // The record itself: wrap it, so the record is the data element's first child.
        return new XElement(XName.Get("data", DataNamespace), element);
    }

    private MergedForm Merge(XElement template)
    {
        var rootTemplate = template.Child("subform") ?? throw new XfaFormatException("The XFA template has no root subform.");
        var root = new FormNode(NodeKind.Subform, rootTemplate, null) { Data = _record };
        _xml.Count(1);
        var pageAreas = new List<FormNode>();
        var pageSet = rootTemplate.Child("pageSet");
        _form = new MergedForm
        {
            Root = root,
            PageAreas = pageAreas,
            Relation = pageSet.Attr("relation") ?? "orderedOccurrence",
            Detach = Detach,
        };
        foreach (var element in template.Descendants())
        {
            if (element.Attr("id") is { Length: > 0 } id)
            {
                _form.TemplateIds.TryAdd(id, element);
            }
        }

        Register(root);
        if (pageSet is not null)
        {
            CollectPageAreas(pageSet, pageAreas, 0);
        }

        // The root binds to the record whatever its name, as the data's top level.
        Content(root, _record);
        ReportScripts(template);
        ReportUnbound();
        return _form;
    }

    private FormNode Detach(XElement element, FormNode parent)
    {
        _xml.Count(1);
        var kind = element.Name.LocalName switch
        {
            "field" => NodeKind.Field,
            "draw" => NodeKind.Draw,
            "exclGroup" => NodeKind.ExclGroup,
            "area" => NodeKind.Area,
            _ => NodeKind.Subform,
        };
        var node = new FormNode(kind, element, parent) { IsLeaderOrTrailer = true };
        if (kind is NodeKind.Field or NodeKind.Draw)
        {
            Default(node);
        }
        else
        {
            Content(node, null);
        }

        return node;
    }

    private void CollectPageAreas(XElement pageSet, List<FormNode> pageAreas, int depth)
    {
        foreach (var child in pageSet.Elements())
        {
            if (child.Name.LocalName == "pageArea")
            {
                var area = new FormNode(NodeKind.Area, child, null);
                _xml.Count(1);
                Register(area);
                Content(area, _record);
                pageAreas.Add(area);
            }
            else if (child.Name.LocalName == "pageSet" && depth < 8)
            {
                CollectPageAreas(child, pageAreas, depth + 1);
            }
        }
    }

    private void Register(FormNode node)
    {
        if (node.Template.Attr("id") is { Length: > 0 } id)
        {
            _form!.Ids.TryAdd(id, node);
        }
    }

    /// <summary>Instantiates a container's content with <paramref name="scope"/> as the current data group.</summary>
    private void Content(FormNode parent, XElement? scope)
    {
        ResourceLimits.Checkpoint();
        using var nesting = ResourceLimits.EnterNesting();
        foreach (var child in parent.Template.Elements())
        {
            Child(parent, child, scope);
        }
    }

    private void Child(FormNode parent, XElement child, XElement? scope)
    {
        switch (child.Name.LocalName)
        {
            case "subform":
                Subform(parent, child, scope);
                break;
            case "subformSet":
                SubformSet(parent, child, scope);
                break;
            case "area":
            {
                var area = Add(parent, NodeKind.Area, child);
                Content(area, scope);
                break;
            }

            case "exclGroup":
                ExclusionGroup(parent, child, scope);
                break;
            case "field":
                Field(Add(parent, NodeKind.Field, child), scope);
                break;
            case "draw":
            {
                var draw = Add(parent, NodeKind.Draw, child);
                Default(draw);
                break;
            }
        }
    }

    private FormNode Add(FormNode parent, NodeKind kind, XElement template)
    {
        _xml.Count(1);
        var node = new FormNode(kind, template, parent);
        parent.Children.Add(node);
        Register(node);
        return node;
    }

    private void SubformSet(FormNode parent, XElement set, XElement? scope)
    {
        var members = set.Elements().Where(e => e.Name.LocalName is "subform" or "subformSet").ToList();
        if (set.Attr("relation") == "choice" && members.Count > 1)
        {
            // One of the members: the first whose data is there, else the first.
            var chosen = members.FirstOrDefault(m => m.Attr("name") is { } name && Groups(scope, name).Any()) ?? members[0];
            members = [chosen];
        }

        foreach (var member in members)
        {
            Child(parent, member, scope);
        }
    }

    /// <summary>The occurrence rule of a repeatable container: minimum, maximum (-1 for unbounded) and initial count.</summary>
    /// <remarks>A page area without an <c>occur</c> may be used any number of times.</remarks>
    internal static (int Min, int Max, int Initial) Occurrence(XElement template)
    {
        var occur = template.Child("occur");
        if (occur is null && template.Name.LocalName == "pageArea")
        {
            return (0, -1, 1);
        }

        var min = Math.Max(occur.Int("min", 1), 0);
        var max = occur.Int("max", Math.Max(min, 1));
        if (max >= 0 && max < min)
        {
            max = min;
        }

        var initial = Math.Max(occur.Int("initial", Math.Max(min, 1)), min);
        if (max >= 0)
        {
            initial = Math.Min(initial, max);
        }

        return (min, max, initial);
    }

    private void Subform(FormNode parent, XElement template, XElement? scope)
    {
        var (min, max, initial) = Occurrence(template);
        var bind = template.Child("bind");
        var match = bind.Attr("match") ?? "once";
        var name = template.Attr("name");
        var limit = max < 0 ? int.MaxValue : max;
        List<XElement?> instances;
        if (match == "dataRef" && scope is not null && bind.Attr("ref") is { } reference)
        {
            instances = [.. Som.Resolve(reference, scope, _dataRoot, _record).Where(n => !_bound.Contains(n)).Take(limit)];
            if (instances.Count == 0 && !_hasData)
            {
                instances = [.. Enumerable.Repeat<XElement?>(null, initial)];
            }
        }
        else if (match == "none" || name is null || !_hasData)
        {
            // No data of its own: the initial instances, each passing the current data group on.
            instances = [.. Enumerable.Repeat(scope, initial)];
        }
        else
        {
            instances = [.. Groups(scope, name).Where(g => !_bound.Contains(g)).Take(limit)];
            if (instances.Count == 0 && min > 0)
            {
                // No data group of its name: the content is matched against the current data group.
                instances = [scope];
            }
        }

        while (instances.Count < min)
        {
            instances.Add(null);
        }

        foreach (var data in instances)
        {
            var subform = Add(parent, NodeKind.Subform, template);
            var own = data is not null && !ReferenceEquals(data, scope);
            if (own)
            {
                _bound.Add(data!);
                subform.Data = data;
            }

            Content(subform, data);
        }
    }

    private void ExclusionGroup(FormNode parent, XElement template, XElement? scope)
    {
        var group = Add(parent, NodeKind.ExclGroup, template);
        if (group.Name is null || template.Child("bind").Attr("match") == "none")
        {
            // Unnamed: its fields bind by themselves.
            Content(group, scope);
            return;
        }

        foreach (var child in template.Elements().Where(e => e.Name.LocalName == "field"))
        {
            var field = Add(group, NodeKind.Field, child);
            Default(field);
        }

        if (Bind(group, scope) is { } value)
        {
            var selected = value.Value;
            group.Value = selected;
            group.FromData = true;
            foreach (var field in group.Children)
            {
                var (on, off) = OnOff(field.Template);
                field.Value = on == selected ? on : off;
                field.FromData = true;
            }
        }
    }

    /// <summary>A check button's on and off values (its first and second <c>items</c> values; 1 and 0 by default).</summary>
    internal static (string On, string? Off) OnOff(XElement field)
    {
        var items = field.Child("items")?.Elements().Select(e => e.Value.Trim()).ToList() ?? [];
        return (items.Count > 0 ? items[0] : "1", items.Count > 1 ? items[1] : field.Child("items") is null ? "0" : null);
    }

    private void Field(FormNode field, XElement? scope)
    {
        Default(field);
        if (Bind(field, scope) is not { } data)
        {
            return;
        }

        field.FromData = true;
        if (Rich(data) is { } body)
        {
            field.RichValue = body;
            field.Value = body.Value;
        }
        else
        {
            field.Value = data.Value;
            field.RichValue = null;
        }
    }

    /// <summary>The data value a named field or exclusion group binds to, by its <c>bind</c> rule.</summary>
    private XElement? Bind(FormNode node, XElement? scope)
    {
        var bind = node.Template.Child("bind");
        var match = bind.Attr("match") ?? "once";
        if (match == "none" || !_hasData)
        {
            return null;
        }

        XElement? data = null;
        switch (match)
        {
            case "global" when node.Name is { } name:
                data = _record?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == name && IsValue(e));
                break;
            case "dataRef" when bind.Attr("ref") is { } reference && scope is not null:
                data = Som.Resolve(reference, scope, _dataRoot, _record).FirstOrDefault();
                break;
            default:
                if (node.Name is { } own && scope is not null)
                {
                    data = scope.Elements().FirstOrDefault(e => e.Name.LocalName == own && IsValue(e) && !_bound.Contains(e));
                }

                break;
        }

        if (data is not null)
        {
            _bound.Add(data);
            node.Data = data;
        }

        return data;
    }

    /// <summary>The data groups of a name directly in a data group.</summary>
    private static IEnumerable<XElement> Groups(XElement? scope, string name) =>
        scope?.Elements().Where(e => e.Name.LocalName == name && !IsValue(e)) ?? [];

    /// <summary>Whether a data node is a data value (no element content, or rich text, or marked so).</summary>
    internal static bool IsValue(XElement element)
    {
        var kind = element.Attribute(XName.Get("dataNode", DataNamespace))?.Value;
        if (kind is not null)
        {
            return kind == "dataValue";
        }

        return !element.HasElements || Rich(element) is not null;
    }

    /// <summary>The XHTML body of a rich text data value, if it is one.</summary>
    internal static XElement? Rich(XElement element)
    {
        var type = element.Attribute(XName.Get("contentType", DataNamespace))?.Value ?? element.Attr("contentType");
        var body = element.Elements().FirstOrDefault(e => e.Name.LocalName is "body" or "html");
        return type == "text/html" || (body is not null && body.Name.NamespaceName == "http://www.w3.org/1999/xhtml") ? body : null;
    }

    /// <summary>Sets a field's or draw's value from its template default (<c>value</c>).</summary>
    private static void Default(FormNode node)
    {
        var content = node.Template.Child("value")?.Elements().FirstOrDefault();
        if (content is null)
        {
            return;
        }

        if (content.Name.LocalName == "exData")
        {
            if (content.Attr("contentType") == "text/html")
            {
                node.RichValue = content.Elements().FirstOrDefault();
                node.Value = node.RichValue?.Value;
            }
            else
            {
                node.Value = content.Value;
            }
        }
        else if (content.Name.LocalName is "text" or "integer" or "decimal" or "float" or "date" or "time" or "dateTime" or "boolean")
        {
            node.Value = content.Value;
        }
    }

    private void ReportScripts(XElement template)
    {
        var scripts = template.Descendants().Where(e => e.Name.LocalName == "script" && e.Value.Trim().Length > 0).ToList();
        if (scripts.Count == 0)
        {
            return;
        }

        var kinds = scripts.Select(s => s.Parent?.Name.LocalName == "event" ? $"{s.Parent.Attr("activity") ?? "event"} events" : $"{s.Parent?.Name.LocalName ?? "script"}s")
            .Distinct().Order(StringComparer.Ordinal);
        _report?.Invoke(XfaDiagnosticKind.ScriptNotRun,
            $"The form has {scripts.Count} script(s) ({string.Join(", ", kinds)}); scripts are not run, so what they would calculate, show, hide or add is missing.");
    }

    private void ReportUnbound()
    {
        if (!_hasData || _record is null)
        {
            return;
        }

        var unbound = _record.Descendants().Where(e => IsValue(e) && !_bound.Contains(e) && e.Value.Trim().Length > 0
            && !e.Ancestors().Any(a => _bound.Contains(a) && IsValue(a))).ToList();
        if (unbound.Count > 0)
        {
            var names = string.Join(", ", unbound.Take(5).Select(e => e.Name.LocalName));
            _report?.Invoke(XfaDiagnosticKind.Data, $"{unbound.Count} data value(s) are not bound to any field ({names}{(unbound.Count > 5 ? ", ..." : string.Empty)}).");
        }
    }
}
