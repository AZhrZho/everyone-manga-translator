namespace MangaTranslator.Api.Storage;

public sealed class AppDataPaths
{
    public AppDataPaths() : this(
        Environment.GetEnvironmentVariable("EMT_USER_DATA_ROOT")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Everyone Manga Translator"),
        Environment.GetEnvironmentVariable("EMT_CACHE_ROOT"),
        Environment.GetEnvironmentVariable("EMT_LOGS_ROOT"),
        Environment.GetEnvironmentVariable("EMT_TEMP_ROOT"))
    {
    }

    public AppDataPaths(
        string userDataRoot,
        string? cacheRoot = null,
        string? logsRoot = null,
        string? tempRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        UserDataRoot = Path.GetFullPath(userDataRoot);
        CacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(UserDataRoot, "cache"));
        LogsRoot = Path.GetFullPath(logsRoot ?? Path.Combine(UserDataRoot, "logs"));
        TempRoot = Path.GetFullPath(tempRoot ?? Path.Combine(UserDataRoot, "temp"));
        Directory.CreateDirectory(UserDataRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(TempRoot);
    }

    public string UserDataRoot { get; }

    public string CacheRoot { get; }

    public string LogsRoot { get; }

    public string TempRoot { get; }

    // Kept as a compatibility name for the system-info UI. It is machine data,
    // not the location of any project content.
    public string DataRoot => UserDataRoot;
}
