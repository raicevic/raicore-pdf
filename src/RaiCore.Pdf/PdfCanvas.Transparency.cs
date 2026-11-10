using System.Globalization;
using System.Text;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf;

/// <summary>
/// Transparency: the fill and stroke alpha that colors carry (PDF extended graphics states with <c>/ca</c> and
/// <c>/CA</c>), and transparency groups, which composite a whole drawing at once with one opacity.
/// </summary>
public sealed partial class PdfCanvas
{
    private readonly Stack<(double Fill, double Stroke)> _savedAlphas = new();
    private readonly Stack<GroupFrame> _groups = new();

    /// <summary>The resources (by page-local name) the page's own content stream uses; groups have their own sets.</summary>
    private readonly HashSet<string> _pageResources = new(StringComparer.Ordinal);

    /// <summary>The resources the content being drawn (the page, or the innermost open group) uses.</summary>
    private HashSet<string> _resources;

    /// <summary>The alpha that fills (and text and images) are painted with, as set in the content stream.</summary>
    private double _fillAlpha = 1;

    /// <summary>The alpha that strokes are painted with, as set in the content stream.</summary>
    private double _strokeAlpha = 1;

    /// <summary>The names of the resources the page's own content stream uses (not those used only inside groups).</summary>
    internal IReadOnlySet<string> PageResources => _pageResources;

    /// <summary>
    /// Starts a transparency group: everything drawn until the matching <see cref="EndGroup"/> is composited first
    /// on its own, and the result is then painted with <paramref name="opacity"/>, like CSS <c>opacity</c> or SVG
    /// group opacity. Overlapping parts of the group therefore do not show through each other; translucent colors
    /// would instead blend every shape separately.
    /// </summary>
    /// <remarks>
    /// The group is a form XObject with a <c>/Group</c> of subtype <c>/Transparency</c> (isolated), painted under
    /// an extended graphics state with <c>/ca</c> and <c>/CA</c> set to the opacity. Groups may nest. Inside a
    /// group, every <see cref="SaveState"/> must be restored before <see cref="EndGroup"/>. The drawing starts with
    /// full alpha and keeps the current transformation and clip. Transparency requires PDF 1.4; PDF/A-1 forbids it.
    /// </remarks>
    /// <param name="opacity">The group's opacity, from 0 (invisible) to 1 (opaque).</param>
    /// <exception cref="ArgumentOutOfRangeException">The opacity is not a number.</exception>
    public PdfCanvas BeginGroup(double opacity)
    {
        if (double.IsNaN(opacity))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "The opacity must be a number from 0 to 1.");
        }

        _groups.Push(new GroupFrame(_ops, _resources, RoundAlpha(opacity), _savedTransforms.Count, _fillAlpha, _strokeAlpha, _transform));
        _ops = new StringBuilder();
        _resources = new HashSet<string>(StringComparer.Ordinal);
        _fillAlpha = _strokeAlpha = 1; // A transparency group starts with full alpha.

        // The group's space is the page's (or form's) own: its matrix undoes the current transformation, which the
        // group's content then applies again. Patterns drawn inside therefore keep their page-space matrices.
        if (!_transform.IsIdentity)
        {
            var t = _transform;
            Op($"{N(t.A)} {N(t.B)} {N(t.C)} {N(t.D)} {N(t.E)} {N(t.F)} cm");
        }

        return this;
    }

    /// <summary>Ends the innermost transparency group started with <see cref="BeginGroup"/> and paints it.</summary>
    /// <exception cref="InvalidOperationException">No group is open, or a state saved inside it was not restored.</exception>
    public PdfCanvas EndGroup()
    {
        if (!_groups.TryPeek(out var frame))
        {
            throw new InvalidOperationException("EndGroup was called without a matching BeginGroup.");
        }

        if (_savedTransforms.Count != frame.SavedStates)
        {
            throw new InvalidOperationException("Every SaveState inside a transparency group must be restored before EndGroup.");
        }

        _groups.Pop();
        var content = Encoding.Latin1.GetBytes(_ops.ToString());
        var resources = _resources;

        // Transformations and clips inside the group end with it, like the alpha it started with.
        (_ops, _resources, _fillAlpha, _strokeAlpha, _transform) = (frame.Ops, frame.Resources, frame.FillAlpha, frame.StrokeAlpha, frame.Transform);
        if (frame.Transform.Invert() is not { } inverse)
        {
            return this; // A singular transformation: nothing drawn would be visible.
        }

        var name = UseResource(_page.AddGroup(content, resources, inverse));
        var state = UseResource(_page.UseExtGState(frame.Opacity, frame.Opacity));
        return Op($"q /{state} gs /{name} Do Q");
    }

    /// <summary>
    /// Sets the fill and stroke alpha (<c>/ca</c> and <c>/CA</c>), writing an extended graphics state when either
    /// changes. Values are rounded to 1/10000, which also keeps the number of distinct states small.
    /// </summary>
    internal PdfCanvas SetAlpha(double fill, double stroke)
    {
        fill = RoundAlpha(fill);
        stroke = RoundAlpha(stroke);
        if (fill == _fillAlpha && stroke == _strokeAlpha)
        {
            return this;
        }

        (_fillAlpha, _strokeAlpha) = (fill, stroke);
        return Op($"/{UseResource(_page.UseExtGState(fill, stroke))} gs");
    }

    /// <summary>Rounds an alpha to the precision extended graphics states are written with.</summary>
    internal static double RoundAlpha(double alpha) => double.IsNaN(alpha) ? 0 : Math.Round(Math.Clamp(alpha, 0, 1), 4);

    /// <summary>
    /// The operators that paint an image or a form at full alpha inside a <c>q</c>/<c>Q</c> pair: images are drawn
    /// opaque whatever the alpha of the current fill color (use <see cref="BeginGroup"/> for a translucent image).
    /// </summary>
    private string OpaqueImagePrefix() =>
        _fillAlpha == 1 && _strokeAlpha == 1 ? string.Empty : $"/{UseResource(_page.UseExtGState(1, 1))} gs ";

    /// <summary>Records that the content being drawn uses a page resource.</summary>
    private string UseResource(string name)
    {
        _resources.Add(name);
        return name;
    }

    private void PushState()
    {
        PushTransform();
        _savedAlphas.Push((_fillAlpha, _strokeAlpha));
    }

    private void PopState()
    {
        PopTransform();
        if (_savedAlphas.TryPop(out var alphas))
        {
            (_fillAlpha, _strokeAlpha) = alphas;
        }
    }

    private void EnsureGroupsClosed()
    {
        if (_groups.Count > 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture, $"{_groups.Count} transparency group(s) were not ended with EndGroup."));
        }
    }

    /// <summary>What <see cref="BeginGroup"/> set aside: the enclosing content and its state, restored by <see cref="EndGroup"/>.</summary>
    private sealed record GroupFrame(
        StringBuilder Ops, HashSet<string> Resources, double Opacity, int SavedStates, double FillAlpha, double StrokeAlpha, SvgMatrix Transform);
}
