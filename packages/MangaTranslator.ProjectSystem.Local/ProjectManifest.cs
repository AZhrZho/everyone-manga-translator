using System.Text.Json;

namespace MangaTranslator.ProjectSystem.Local;

internal sealed record ProjectManifest(
    string SchemaVersion,
    string ProjectId,
    string Name,
    DateTimeOffset CreatedAt,
    ProjectWorkflowMode WorkflowMode = ProjectWorkflowMode.Expert,
    string TargetLanguage = "zh-Hans",
    IReadOnlyList<string>? EnabledFontIds = null,
    string? DefaultFontId = null,
    string? FallbackFontId = null,
    int PrecheckBatchSize = ProjectPrecheckDefaults.BatchSize,
    int PrecheckBatchConcurrency = ProjectPrecheckDefaults.BatchConcurrency)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<ProjectManifest> ReadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectRoot, LocalProjectLayout.ManifestFileName);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectManifest>(stream, JsonOptions, cancellationToken)
               ?? throw new ProjectFormatException("项目清单为空。");
    }

    public async Task WriteAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectRoot, LocalProjectLayout.ManifestFileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
