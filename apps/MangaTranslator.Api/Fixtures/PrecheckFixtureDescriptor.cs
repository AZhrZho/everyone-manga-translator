namespace MangaTranslator.Api.Fixtures;

public sealed record PrecheckFixtureDescriptor(
    string RunId,
    string RootPath,
    int PageCount,
    int BatchCount,
    IReadOnlyList<string> AvailableArtifacts,
    DateTimeOffset LastModifiedAt,
    IReadOnlyDictionary<string, string>? ArtifactPaths = null)
{
    public string GetArtifactPath(string artifactName) =>
        ArtifactPaths is not null && ArtifactPaths.TryGetValue(artifactName, out var path)
            ? path
            : Path.Combine(RootPath, artifactName);
}
