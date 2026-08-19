using System.Text.Json;
using MangaTranslator.Contracts;
using MangaTranslator.ProjectSystem;

namespace MangaTranslator.Api.Projects;

public sealed class TranslationAnnotationService(
    IProjectStore projects,
    IProjectArtifactStore artifacts)
{
    private const string ArtifactName = "translation-annotations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TranslationAnnotationsResponse> GetAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        var artifact = (await artifacts.ListAsync(projectId, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Name, ArtifactName, StringComparison.Ordinal));
        return new TranslationAnnotationsResponse(
            projectId,
            project.Revision,
            Parse(artifact?.Json));
    }

    public async Task<TranslationAnnotationsResponse> UpdateAsync(
        string projectId,
        UpdateTranslationAnnotationsRequest request,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(projectId, cancellationToken);
        if (project.Revision != request.ExpectedRevision)
        {
            throw new ProjectRevisionConflictException(project.Revision);
        }

        var pages = (request.Pages ?? new Dictionary<string, string>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(
                item => item.Key.Trim(),
                item => Normalize(item.Value),
                StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new AnnotationDocument(pages), JsonOptions);
        await artifacts.SaveAsync(projectId, ArtifactName, json, cancellationToken);
        return new TranslationAnnotationsResponse(projectId, project.Revision, pages);
    }

    private static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var document = JsonSerializer.Deserialize<AnnotationDocument>(json, JsonOptions);
            return document?.Pages is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : document.Pages
                    .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                    .ToDictionary(item => item.Key, item => Normalize(item.Value), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().Length > 4000
            ? (value ?? string.Empty).Trim()[..4000]
            : (value ?? string.Empty).Trim();

    private sealed record AnnotationDocument(IReadOnlyDictionary<string, string> Pages);
}
