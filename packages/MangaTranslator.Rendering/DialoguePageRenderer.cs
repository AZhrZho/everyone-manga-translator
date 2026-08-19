namespace MangaTranslator.Rendering;

/// <summary>Coordinates block rendering and page composition without hiding either boundary.</summary>
public sealed class DialoguePageRenderer(
    ITextBlockRenderer blockRenderer,
    IPageComposer composer)
{
    public IReadOnlyList<TextBlockPlacement> RenderBlocks(PageRenderData page)
    {
        ValidatePage(page);
        return page.TextBlocks.Select(block =>
        {
            ValidateBlockWithinPage(page, block);
            var rendered = blockRenderer.Render(block);
            return new TextBlockPlacement(
                rendered,
                block.Layout.TextBox.X0,
                block.Layout.TextBox.Y0,
                block.Layout.Rotation);
        }).ToArray();
    }

    public RenderedPage Render(
        PageRenderData page,
        PageBackground? background = null,
        bool debug = false)
    {
        var placements = RenderBlocks(page);
        return composer.Compose(
            new PageCompositionData(page.PageId, page.Width, page.Height, placements),
            background,
            debug);
    }

    private static void ValidatePage(PageRenderData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.PageId);
        ArgumentNullException.ThrowIfNull(page.TextBlocks);
        if (page.Width <= 0 || page.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(page),
                "Page width and height must be positive.");
        }

        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in page.TextBlocks)
        {
            ArgumentNullException.ThrowIfNull(block);
            ArgumentException.ThrowIfNullOrWhiteSpace(block.BlockId);
            if (!blockIds.Add(block.BlockId))
            {
                throw new ArgumentException(
                    $"Page '{page.PageId}' contains duplicate text-block ID '{block.BlockId}'.",
                    nameof(page));
            }
        }
    }

    private static void ValidateBlockWithinPage(
        PageRenderData page,
        TextBlockRenderData block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var box = block.Layout.TextBox;
        if (box.X0 < 0
            || box.Y0 < 0
            || box.X1 <= box.X0
            || box.Y1 <= box.Y0
            || box.X1 > page.Width
            || box.Y1 > page.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(page),
                $"Text block '{block.BlockId}' has an invalid or out-of-page layout box.");
        }
    }
}
