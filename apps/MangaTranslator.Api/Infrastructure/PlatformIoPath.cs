namespace MangaTranslator.Api.Infrastructure;

/// <summary>
/// Keeps Windows extended-length path handling at the file-system boundary.
/// Paths returned to the rest of the application remain ordinary absolute paths.
/// </summary>
internal static class PlatformIoPath
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    internal static string NormalizeForIo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + fullPath[2..]
            : ExtendedPathPrefix + fullPath;
    }

    internal static bool FileExists(string path) => File.Exists(NormalizeForIo(path));

    internal static bool DirectoryExists(string path) => Directory.Exists(NormalizeForIo(path));

    internal static void CreateDirectory(string path) => Directory.CreateDirectory(NormalizeForIo(path));

    internal static IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.EnumerateDirectories(NormalizeForIo(path)).Select(ToApplicationPath);

    internal static IEnumerable<string> EnumerateFiles(
        string path,
        string searchPattern,
        SearchOption searchOption) =>
        Directory.EnumerateFiles(NormalizeForIo(path), searchPattern, searchOption)
            .Select(ToApplicationPath);

    internal static string ReadAllText(string path) => File.ReadAllText(NormalizeForIo(path));

    internal static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(NormalizeForIo(path), cancellationToken);

    internal static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(NormalizeForIo(path), cancellationToken);

    internal static DateTime GetLastWriteTimeUtc(string path) =>
        File.GetLastWriteTimeUtc(NormalizeForIo(path));

    internal static FileStream OpenRead(
        string path,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.SequentialScan) =>
        new(
            NormalizeForIo(path),
            FileMode.Open,
            FileAccess.Read,
            share,
            65536,
            options);

    private static string ToApplicationPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }

        return path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[ExtendedPathPrefix.Length..]
            : path;
    }
}
