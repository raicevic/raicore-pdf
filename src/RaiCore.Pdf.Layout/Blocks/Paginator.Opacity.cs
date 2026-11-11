using System.Collections.ObjectModel;

namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Opacity (CSS <c>opacity</c>): everything a block with an opacity below 1 paints becomes one transparency group per
/// page. Drawing operations remember the opacity scope they were added in; when a page's operations are handed out,
/// consecutive operations of a scope are wrapped in <see cref="PdfCanvas.BeginGroup"/>/<see cref="PdfCanvas.EndGroup"/>.
/// </summary>
/// <remarks>
/// Tagging operations as they are added, rather than marking where a block's operations start and end, keeps this
/// independent of where they go: backgrounds are inserted under content that is already laid out, pages created
/// inside a block get operations at their start, and flex items revisit pages.
/// </remarks>
internal sealed partial class Paginator
{
    /// <summary>The opacity scope operations are added in: the innermost block with an opacity below 1, if any.</summary>
    private OpacityScope? _opacity;

    /// <summary>
    /// Lays out one block; returns the bottom margin that collapses out of it (from its last child). A block with an
    /// opacity below 1 opens an opacity scope for everything it draws.
    /// </summary>
    private CollapsedMargin LayoutBlock(Block block, double x, double width, BoxSizing sizing = default)
    {
        if (block.Style.Opacity >= 1)
        {
            return LayoutBlockInScope(block, x, width, sizing);
        }

        var outer = _opacity;
        _opacity = new OpacityScope(outer, Math.Max(0, block.Style.Opacity));
        try
        {
            return LayoutBlockInScope(block, x, width, sizing);
        }
        finally
        {
            _opacity = outer;
        }
    }

    /// <summary>
    /// The paint layer operations are added in: floats go over the flow, positioned boxes over floats (CSS 2.1
    /// appendix E, simplified to one stacking order per page).
    /// </summary>
    private PaintLayer _layer;

    /// <summary>The offset of relatively positioned boxes around the current position, applied to what is drawn.</summary>
    private (double X, double Y) _shift;

    /// <summary>
    /// Wraps deferred drawing (e.g. a flex item's background, added once the line's height is known) so that it runs
    /// in the opacity scope, paint layer and offset current now.
    /// </summary>
    private Action InCurrentOpacityScope(Action draw)
    {
        var (scope, layer, shift) = (_opacity, _layer, _shift);
        return () =>
        {
            var outer = (_opacity, _layer, _shift);
            (_opacity, _layer, _shift) = (scope, layer, shift);
            try
            {
                draw();
            }
            finally
            {
                (_opacity, _layer, _shift) = outer;
            }
        };
    }

    /// <summary>The pages' drawing operations, with transparency groups for the opacity scopes.</summary>
    private List<IReadOnlyList<DrawOperation>> GroupedPages() => [.. _pages.Select(page => (IReadOnlyList<DrawOperation>)page.Grouped())];

    /// <summary>A block's opacity scope, nested in the scopes of the blocks around it.</summary>
    private sealed class OpacityScope(OpacityScope? parent, double opacity)
    {
        public OpacityScope? Parent { get; } = parent;

        public double Opacity { get; } = opacity;

        public int Depth { get; } = parent is null ? 1 : parent.Depth + 1;
    }

    /// <summary>The drawing operations of a page; each remembers the opacity scope it was added in.</summary>
    private sealed class PageOperations(Paginator owner) : Collection<DrawOperation>
    {
        private readonly List<OpacityScope?> _scopes = [];

        /// <summary>The paint layer of each operation (see <see cref="Ordered"/>).</summary>
        private readonly List<PaintLayer> _layers = [];

        /// <summary>
        /// The operations, with a group begun before the first operation of each run of operations in a scope and
        /// ended after the last (nested scopes nest). Without opacity, the operations themselves.
        /// </summary>
        public List<DrawOperation> Grouped()
        {
            var order = Ordered();
            if (_scopes.TrueForAll(scope => scope is null))
            {
                return [.. order.Select(i => Items[i])];
            }

            var result = new List<DrawOperation>(Count + 4);
            var open = new List<OpacityScope>(); // The groups open at this point, outermost first.
            foreach (var i in order)
            {
                var chain = Chain(_scopes[i]);
                var common = 0;
                while (common < open.Count && common < chain.Count && open[common] == chain[common])
                {
                    common++;
                }

                for (var j = open.Count - 1; j >= common; j--)
                {
                    result.Add(static (canvas, _) => canvas.EndGroup());
                    open.RemoveAt(j);
                }

                for (var j = common; j < chain.Count; j++)
                {
                    var opacity = chain[j].Opacity;
                    result.Add((canvas, _) => canvas.BeginGroup(opacity));
                    open.Add(chain[j]);
                }

                result.Add(Items[i]);
            }

            for (var j = open.Count - 1; j >= 0; j--)
            {
                result.Add(static (canvas, _) => canvas.EndGroup());
            }

            return result;
        }

        /// <summary>
        /// The order the operations are drawn in: by paint layer (see <see cref="PaintLayer"/>), each layer in the order
        /// its operations were added. So floats are painted over the backgrounds of the blocks around them (which are
        /// added under the content already laid out), and positioned boxes over both, by z-index.
        /// </summary>
        private List<int> Ordered()
        {
            var order = Enumerable.Range(0, Count).ToList();
            return _layers.TrueForAll(layer => layer == default) ? order : [.. order.OrderBy(i => _layers[i])]; // A stable sort.
        }

        protected override void InsertItem(int index, DrawOperation item)
        {
            _scopes.Insert(index, owner._opacity);
            _layers.Insert(index, owner._layer);
            base.InsertItem(index, Shifted(item));
        }

        protected override void RemoveItem(int index)
        {
            _scopes.RemoveAt(index);
            _layers.RemoveAt(index);
            base.RemoveItem(index);
        }

        /// <summary>The operation moved by the current relative offset, if any.</summary>
        private DrawOperation Shifted(DrawOperation item)
        {
            var (dx, dy) = owner._shift;
            if (dx == 0 && dy == 0)
            {
                return item;
            }

            return (canvas, pageHeight) =>
            {
                canvas.SaveState().Transform(1, 0, 0, 1, dx, -dy);
                item(canvas, pageHeight);
                canvas.RestoreState();
            };
        }

        protected override void SetItem(int index, DrawOperation item)
        {
            _scopes[index] = owner._opacity;
            _layers[index] = owner._layer;
            base.SetItem(index, Shifted(item));
        }

        protected override void ClearItems()
        {
            _scopes.Clear();
            _layers.Clear();
            base.ClearItems();
        }

        /// <summary>The scopes from the outermost to <paramref name="scope"/>.</summary>
        private static List<OpacityScope> Chain(OpacityScope? scope)
        {
            var chain = new List<OpacityScope>(scope?.Depth ?? 0);
            for (var s = scope; s is not null; s = s.Parent)
            {
                chain.Add(s);
            }

            chain.Reverse();
            return chain;
        }
    }
}

/// <summary>
/// Where an operation goes in a page's painting order (CSS 2.1 appendix E, with one stacking order per page): positioned
/// boxes with a negative z-index first, then the flow, then floats, then positioned boxes (z-index auto and 0 in
/// document order, then positive ones in increasing order).
/// </summary>
internal readonly record struct PaintLayer(int Group, int Z) : IComparable<PaintLayer>
{
    /// <summary>Positioned boxes with a negative z-index.</summary>
    public const int Below = -1;

    /// <summary>The flow (the default).</summary>
    public const int Flow = 0;

    /// <summary>Floats.</summary>
    public const int Floats = 1;

    /// <summary>Positioned boxes with z-index auto or not negative.</summary>
    public const int Positioned = 2;

    public int CompareTo(PaintLayer other) => Group != other.Group ? Group.CompareTo(other.Group) : Z.CompareTo(other.Z);
}
