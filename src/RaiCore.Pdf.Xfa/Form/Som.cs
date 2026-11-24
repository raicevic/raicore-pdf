using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.Xfa.Form;

/// <summary>
/// The subset of SOM expressions (XFA 3.3, "Scripting Object Model") that data references use: <c>$</c> (the current
/// data node), <c>$data</c>, <c>$record</c>, names separated by dots, each with an optional index (<c>[2]</c>) or all
/// of them (<c>[*]</c>), and <c>..</c>-free relative paths from the current data node.
/// </summary>
internal static class Som
{
    /// <summary>The most nodes one expression yields.</summary>
    private const int MaxResults = 100_000;

    /// <summary>The data nodes an expression refers to, in document order.</summary>
    public static List<XElement> Resolve(string expression, XElement current, XElement? data, XElement? record)
    {
        var steps = expression.Trim().Split('.', StringSplitOptions.None);
        IEnumerable<XElement> nodes = [current];
        var start = 0;
        if (steps.Length > 0)
        {
            switch (steps[0])
            {
                case "$":
                    start = 1;
                    break;
                case "$data" or "!data" or "xfa.datasets.data":
                    nodes = data is null ? [] : [data];
                    start = 1;
                    break;
                case "$record":
                    nodes = record is null ? [] : [record];
                    start = 1;
                    break;
                case "":
                    start = 1;
                    break;
            }
        }

        for (var i = start; i < steps.Length; i++)
        {
            var step = steps[i].Trim();
            if (step.Length == 0)
            {
                continue;
            }

            var (name, index) = Parse(step);
            if (name.Length == 0 || name.StartsWith('$') || name.StartsWith('#'))
            {
                return [];
            }

            var next = new List<XElement>();
            foreach (var node in nodes)
            {
                var matches = node.Elements().Where(e => e.Name.LocalName == name);
                if (index == -1)
                {
                    next.AddRange(matches);
                }
                else if (matches.Skip(index).FirstOrDefault() is { } match)
                {
                    next.Add(match);
                }

                if (next.Count > MaxResults)
                {
                    break;
                }
            }

            nodes = next;
        }

        return [.. nodes.Take(MaxResults)];
    }

    /// <summary>A step's name and index: 0 without one, -1 for <c>[*]</c>.</summary>
    private static (string Name, int Index) Parse(string step)
    {
        var open = step.IndexOf('[', StringComparison.Ordinal);
        if (open < 0)
        {
            return (step, 0);
        }

        var name = step[..open].Trim();
        var inside = step[(open + 1)..].TrimEnd(']').Trim();
        if (inside == "*")
        {
            return (name, -1);
        }

        return int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 ? (name, index) : (name, 0);
    }
}
