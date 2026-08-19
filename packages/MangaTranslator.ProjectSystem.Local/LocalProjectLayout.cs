namespace MangaTranslator.ProjectSystem.Local;

internal static class LocalProjectLayout
{
    public const string ManifestFileName = "project.json";
    public const string DatabaseFileName = "project.db";

    public static readonly string[] AssetDirectories =
    [
        "source",
        "clean",
        "masks",
        "rendered",
        "thumbnails",
        "versions",
        "cache"
    ];

    public static string GetDatabasePath(string projectRoot) => Path.Combine(projectRoot, DatabaseFileName);

    public static string ResolveAssetPath(string projectRoot, string assetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetKey);
        var normalizedRoot = Path.GetFullPath(projectRoot);
        var relativePath = assetKey.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ProjectFormatException("项目资源键不能是绝对路径。");
        }

        var resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!resolved.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectFormatException("项目资源键超出了项目目录。");
        }

        return resolved;
    }
}
