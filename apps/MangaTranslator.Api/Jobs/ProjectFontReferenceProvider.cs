using MangaTranslator.Dialogue;

namespace MangaTranslator.Api.Jobs;

internal sealed class ProjectFontReferenceProvider(
    IFontReferenceImageProvider inner,
    IReadOnlyCollection<string> enabledFontIds) : IFontReferenceImageProvider
{
    private readonly HashSet<string> enabled = enabledFontIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<FontReferenceSnapshot> GetFontReferenceImageAsync(
        FontReferenceImageRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await inner.GetFontReferenceImageAsync(request, cancellationToken);
        if (enabled.Count == 0) return snapshot;
        var fonts = snapshot.Fonts.Where(font => enabled.Contains(font.FontId)).ToArray();
        if (fonts.Length == 0)
        {
            throw new InvalidOperationException(
                "The project font set does not contain any font registered in the managed font library.");
        }
        return snapshot with { Fonts = fonts };
    }
}
