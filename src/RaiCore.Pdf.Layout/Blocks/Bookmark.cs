namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Makes a block an entry in the PDF outline (<see cref="Block.Bookmark"/>): when the document is rendered, the entry
/// jumps to where the block starts. Entries nest by level, like headings: a level 2 entry after a level 1 entry is its
/// child, and a skipped level nests under the nearest shallower entry.
/// </summary>
public sealed class Bookmark
{
    /// <summary>Creates a bookmark.</summary>
    /// <param name="title">The title shown in the viewer's outline panel.</param>
    /// <param name="level">The nesting level: 1 for top-level entries (like <c>h1</c>), 2 for their children, and so on.</param>
    public Bookmark(string title, int level = 1)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        Title = title;
        Level = level;
    }

    /// <summary>The title shown in the viewer's outline panel.</summary>
    public string Title { get; }

    /// <summary>The nesting level, 1 or more.</summary>
    public int Level { get; }

    /// <summary>Whether the entry starts expanded, showing the entries nested under it. Defaults to <c>true</c>.</summary>
    public bool IsOpen { get; init; } = true;
}
