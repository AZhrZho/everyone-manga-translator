namespace MangaTranslator.ProjectSystem.Local;

public sealed record LocalProjectSystemOptions(string UserDataRoot)
{
    public string NormalizedUserDataRoot { get; } = Path.GetFullPath(UserDataRoot);

    public string RecentProjectsPath => Path.Combine(NormalizedUserDataRoot, "recent-projects.json");
}
