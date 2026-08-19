namespace MangaTranslator.Rendering.Tests;

internal static class TestArtifactWriter
{
    public static string WritePage(string testName, RenderedPage page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);
        ArgumentNullException.ThrowIfNull(page);
        var path = Path.Combine(
            FindWorkspaceRoot(),
            "TestResults",
            "MangaTranslator.Rendering.Tests",
            Sanitize(testName),
            $"{Sanitize(page.PageId)}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, page.Data.ToArray());
        return path;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the workspace root containing MangaTranslator.slnx.");
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }
}
