namespace MangaTranslator.ProjectSystem.Local;

public sealed class LocalProjectAssetStore(LocalRecentProjectRegistry recentProjects)
    : IProjectAssetStore, IProjectAssetWriter
{
    public async Task<bool> ExistsAsync(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        return File.Exists(LocalProjectLayout.ResolveAssetPath(projectRoot, assetKey));
    }

    public async Task<ProjectAsset> OpenReadAsync(
        string projectId,
        string assetKey,
        CancellationToken cancellationToken)
    {
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        var path = LocalProjectLayout.ResolveAssetPath(projectRoot, assetKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到项目资源。", path);
        }

        var fileInfo = new FileInfo(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new ProjectAsset(stream, MediaTypeForExtension(extension), Path.GetFileName(path), fileInfo.Length);
    }

    public async Task SaveAsync(
        string projectId,
        string assetKey,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var projectRoot = await recentProjects.GetRootAsync(projectId, cancellationToken);
        var path = LocalProjectLayout.ResolveAssetPath(projectRoot, assetKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65536,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string MediaTypeForExtension(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
