namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Creates the structure elements of a flow document's blocks, table cells, tagged runs and links in document order,
/// before it is drawn, and records them in <see cref="PdfDocument.LayoutStructure"/>: each layout object maps to the
/// element its content belongs to (its own, or its nearest tagged ancestor's), or to <c>null</c> for an artifact.
/// </summary>
internal sealed class StructureBuilder(Dictionary<object, PdfStructureElement?> map)
{
    /// <summary>Adds the elements of <paramref name="blocks"/> under <paramref name="parent"/>.</summary>
    public static void Build(IEnumerable<Block> blocks, PdfStructureElement parent, Dictionary<object, PdfStructureElement?> map)
    {
        var builder = new StructureBuilder(map);
        foreach (var block in blocks)
        {
            builder.Visit(block, parent, null);
        }
    }

    /// <summary>
    /// Adds a block's element (if it has a tag, or a default one) and its content's.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="parent">The element its element goes in (or that its content belongs to, if it adds none).</param>
    /// <param name="labelParent">The list item that list labels (<c>Lbl</c> runs) inside the block go in, if any.</param>
    private void Visit(Block block, PdfStructureElement? parent, PdfStructureElement? labelParent)
    {
        var tag = block.Tag ?? block switch
        {
            ParagraphBlock => new StructureTag(PdfStructureTypes.Paragraph),
            ImageBlock => new StructureTag(PdfStructureTypes.Figure),
            TableBlock => new StructureTag(PdfStructureTypes.Table),
            _ => null,
        };

        var content = parent;
        if (tag is { IsArtifact: true } || parent is null)
        {
            content = null; // Everything inside an artifact is an artifact.
        }
        else if (tag is not null)
        {
            (var element, content) = tag.CreateElement(parent);
            if (tag.ContentType is not null)
            {
                labelParent = element;
            }
        }

        map[block] = content;
        switch (block)
        {
            case ParagraphBlock paragraph:
                VisitParagraph(paragraph.Paragraph, content, labelParent);
                break;
            case ContainerBlock container:
                foreach (var child in container.Children)
                {
                    Visit(child, content, labelParent);
                    labelParent = null; // Only the first paragraph of a list item has its label.
                }

                break;
            case FlexBlock flex:
                foreach (var item in flex.Items)
                {
                    Visit(item.Content, content, null);
                }

                break;
            case TableBlock table:
                VisitTable(table, content);
                break;
        }
    }

    /// <summary>
    /// A table's rows (in row groups when it has header or footer rows) and cells: data cells unless tagged otherwise.
    /// </summary>
    private void VisitTable(TableBlock table, PdfStructureElement? element)
    {
        var grouped = table.Rows.Any(row => row.IsHeader || row.IsFooter);
        PdfStructureElement? group = null;
        string? groupType = null;
        foreach (var row in table.Rows)
        {
            var parent = element;
            if (element is not null && grouped)
            {
                var type = row.IsHeader ? PdfStructureTypes.TableHead : row.IsFooter ? PdfStructureTypes.TableFoot : PdfStructureTypes.TableBody;
                if (type != groupType)
                {
                    (group, groupType) = (element.AddChild(type), type);
                }

                parent = group;
            }

            var rowElement = parent?.AddChild(PdfStructureTypes.TableRow);
            map[row] = rowElement;
            foreach (var cell in row.Cells)
            {
                var tag = cell.Tag ?? new StructureTag(row.IsHeader ? PdfStructureTypes.TableHeaderCell : PdfStructureTypes.TableDataCell)
                {
                    Scope = row.IsHeader ? PdfTableScope.Column : null,
                };

                PdfStructureElement? content = null;
                if (rowElement is not null && !tag.IsArtifact)
                {
                    (var cellElement, content) = tag.CreateElement(rowElement);
                    cellElement.RowSpan = cell.RowSpan;
                    cellElement.ColumnSpan = cell.ColumnSpan;
                }

                map[cell] = content;
                foreach (var block in cell.Content)
                {
                    Visit(block, content, null);
                }
            }
        }
    }

    /// <summary>
    /// A paragraph's tagged runs and links, nested by their ranges, and the blocks of its inline boxes. Runs tagged
    /// <c>Lbl</c> go first in the enclosing list item.
    /// </summary>
    private void VisitParagraph(Paragraph paragraph, PdfStructureElement? element, PdfStructureElement? labelParent)
    {
        var items = paragraph.Tags.Select(tag => (tag.Start, tag.End, Owner: (object)tag, Tag: tag.Tag))
            .Concat(paragraph.Links.Select(link => (link.Start, link.End, Owner: (object)link, Tag: new StructureTag(PdfStructureTypes.Link))))
            .OrderBy(item => item.Start)
            .ThenByDescending(item => item.End)
            .ToList();

        var open = new List<(int End, PdfStructureElement? Element)>();
        foreach (var (start, end, owner, tag) in items)
        {
            open.RemoveAll(o => o.End <= start);
            var parent = open.Count > 0 ? open[^1].Element : element;
            PdfStructureElement? content = null;
            if (parent is not null && !tag.IsArtifact)
            {
                content = tag.Type == PdfStructureTypes.Label && labelParent is not null && open.Count == 0
                    ? labelParent.InsertChild(0, PdfStructureTypes.Label)
                    : tag.CreateElement(parent, inline: true).Content;
                CopyProperties(tag, content);
            }

            map[owner] = content;
            open.Add((end, content));
        }

        foreach (var span in paragraph.Spans)
        {
            if (span.Box is { } box)
            {
                Visit(box.Content, element, null);
            }
        }
    }

    private static void CopyProperties(StructureTag tag, PdfStructureElement element)
    {
        element.AlternateText ??= tag.AlternateText;
        element.ActualText ??= tag.ActualText;
        element.Language ??= tag.Language;
        element.Title ??= tag.Title;
    }
}
