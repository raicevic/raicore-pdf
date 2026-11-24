using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>A break before or after an object (XFA <c>breakBefore</c>, <c>breakAfter</c>, or the older <c>break</c>).</summary>
/// <param name="TargetType"><c>pageArea</c>, <c>contentArea</c>, <c>pageEven</c> or <c>pageOdd</c>.</param>
/// <param name="Target">The page area or content area to go to (<c>#id</c> or a name), or <c>null</c> for the next one.</param>
/// <param name="StartNew">Whether to break even when already at the start of the target.</param>
internal sealed record BreakSpec(string TargetType, string? Target, bool StartNew)
{
    /// <summary>The page area, when it is already known.</summary>
    public FormNode? Area { get; init; }
}

/// <summary>
/// An object laid out: its size, its place in its parent's content region, and its children or content. Flowed
/// containers whose height grows can be split between their children across content areas.
/// </summary>
internal sealed class LayoutBox
{
    /// <summary>The form object, or <c>null</c> for a line of an <c>lr-tb</c> or <c>rl-tb</c> flow.</summary>
    public FormNode? Node { get; init; }

    /// <summary>The left of the box's extent (after rotation) in the parent's content region.</summary>
    public double X { get; set; }

    /// <summary>The top of the box's extent (after rotation) in the parent's content region.</summary>
    public double Y { get; set; }

    /// <summary>The nominal width (before rotation).</summary>
    public double W { get; set; }

    /// <summary>The nominal height (before rotation).</summary>
    public double H { get; set; }

    /// <summary>The rotation, counterclockwise: 0, 90, 180 or 270 degrees.</summary>
    public int Rotate { get; init; }

    public double ExtentWidth => Rotate is 90 or 270 ? H : W;

    public double ExtentHeight => Rotate is 90 or 270 ? W : H;

    /// <summary>The container's margin: its children are placed inside it.</summary>
    public Insets Margin { get; init; }

    public List<LayoutBox> Children { get; init; } = [];

    /// <summary>A draw's or field's content, laid out at its width.</summary>
    public WidgetContent? Content { get; init; }

    /// <summary>Whether the box can be split between its children (a flowed container whose height grows).</summary>
    public bool Splittable { get; init; }

    public BreakSpec? BreakBefore { get; set; }

    public BreakSpec? BreakAfter { get; set; }

    /// <summary>Whether a break is inside the box (on a child of a flow, at any depth).</summary>
    public bool ContainsBreak { get; set; }

    /// <summary>Whether the box stays in the same content area as the next one.</summary>
    public bool KeepNext { get; init; }

    /// <summary>Whether the box stays in the same content area as the previous one.</summary>
    public bool KeepPrevious { get; init; }

    /// <summary>The overflow leader, placed at the top of each content area the box continues in.</summary>
    public LayoutBox? Leader { get; init; }

    /// <summary>The overflow trailer, placed at the bottom of each content area the box is split from.</summary>
    public LayoutBox? Trailer { get; init; }

    /// <summary>A copy with other children and height (a fragment of a split box).</summary>
    public LayoutBox Fragment(List<LayoutBox> children, double height) => new()
    {
        Node = Node,
        X = X,
        Y = Y,
        W = W,
        H = height,
        Rotate = Rotate,
        Margin = Margin,
        Children = children,
        Content = Content,
        Splittable = Splittable,
        KeepNext = KeepNext,
        KeepPrevious = KeepPrevious,
        Leader = Leader,
        Trailer = Trailer,
    };

    /// <summary>A copy placed elsewhere (a leader or trailer, placed once per content area).</summary>
    public LayoutBox At(double x, double y) => new()
    {
        Node = Node,
        X = x,
        Y = y,
        W = W,
        H = H,
        Rotate = Rotate,
        Margin = Margin,
        Children = Children,
        Content = Content,
    };

    /// <summary>
    /// The nominal frame (y downwards from its top left) mapped to the parent's content region: the rotation about the
    /// extent, which <see cref="X"/> and <see cref="Y"/> place.
    /// </summary>
    public Affine Frame => Rotate switch
    {
        90 => new Affine(0, -1, 1, 0, X, Y + W),
        180 => new Affine(-1, 0, 0, -1, X + W, Y + H),
        270 => new Affine(0, 1, -1, 0, X + H, Y),
        _ => Affine.Translate(X, Y),
    };

    public override string ToString() => $"{Node?.ToString() ?? "line"} ({X:0.#},{Y:0.#} {W:0.#}x{H:0.#}) {Children.Count} children";
}

/// <summary>A 2D affine transformation: (x, y) maps to (a x + c y + e, b x + d y + f).</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public (double X, double Y) Map(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>This transformation followed by <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);
}

/// <summary>Reading layout rules from the template.</summary>
internal static class LayoutRules
{
    /// <summary>The break before an object: <c>breakBefore</c>, or the older <c>break before</c>.</summary>
    public static BreakSpec? Before(XElement template)
    {
        if (template.Child("breakBefore") is { } before)
        {
            return Spec(before.Attr("targetType") ?? "auto", before.Attr("target"), before.Attr("startNew") == "1");
        }

        return template.Child("break") is { } legacy ? Spec(legacy.Attr("before") ?? "auto", legacy.Attr("beforeTarget"), legacy.Attr("startNew") == "1") : null;
    }

    /// <summary>The break after an object: <c>breakAfter</c>, or the older <c>break after</c>.</summary>
    public static BreakSpec? After(XElement template)
    {
        if (template.Child("breakAfter") is { } after)
        {
            return Spec(after.Attr("targetType") ?? "auto", after.Attr("target"), after.Attr("startNew") == "1");
        }

        return template.Child("break") is { } legacy ? Spec(legacy.Attr("after") ?? "auto", legacy.Attr("afterTarget"), legacy.Attr("startNew") == "1") : null;
    }

    private static BreakSpec? Spec(string type, string? target, bool startNew) => type switch
    {
        "pageArea" or "contentArea" or "pageEven" or "pageOdd" => new BreakSpec(type, string.IsNullOrWhiteSpace(target) ? null : target.Trim(), startNew),
        _ => null,
    };

    /// <summary>The overflow leader and trailer references: <c>overflow</c>, or the older <c>break overflowLeader</c>.</summary>
    public static (string? Leader, string? Trailer) Overflow(XElement template)
    {
        var overflow = template.Child("overflow");
        var legacy = template.Child("break");
        static string? Clean(string? reference) => string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        return (Clean(overflow.Attr("leader") ?? legacy.Attr("overflowLeader")), Clean(overflow.Attr("trailer") ?? legacy.Attr("overflowTrailer")));
    }
}
