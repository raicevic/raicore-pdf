namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Checks how deeply blocks nest before layout recurses over them.</summary>
internal static class BlockDepth
{
    /// <summary>
    /// Throws <see cref="ResourceLimitExceededException"/> when blocks nest deeper than twice
    /// <see cref="ResourceLimits.MaxNestingDepth"/> (an HTML element can make two levels of blocks: an inline-block is
    /// a box in an anonymous paragraph). Walks the tree without recursion, so it is safe for any depth.
    /// </summary>
    public static void Check(IEnumerable<Block> blocks)
    {
        var limit = 2L * ResourceLimits.Current.MaxNestingDepth;
        var pending = new Stack<(Block Block, int Depth)>();
        foreach (var block in blocks)
        {
            pending.Push((block, 1));
        }

        while (pending.Count > 0)
        {
            var (block, depth) = pending.Pop();
            if (depth > limit)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), ResourceLimits.Current.MaxNestingDepth, $"The blocks are nested more than {limit} deep.");
            }

            foreach (var child in Children(block))
            {
                pending.Push((child, depth + 1));
            }
        }
    }

    private static IEnumerable<Block> Children(Block block) => block switch
    {
        ContainerBlock container => container.Children,
        TableBlock table => table.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Content),
        FlexBlock flex => flex.Items.Select(item => item.Content),
        ParagraphBlock paragraph => paragraph.Paragraph.Spans.Where(span => span.Box is not null).Select(span => span.Box!.Content),
        _ => [],
    };
}
