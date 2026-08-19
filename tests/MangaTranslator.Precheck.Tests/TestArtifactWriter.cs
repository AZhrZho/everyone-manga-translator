using System.Text.Json;

namespace MangaTranslator.Precheck.Tests;

internal static class TestArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string AssemblyRoot = Path.Combine(
        FindWorkspaceRoot(),
        "TestResults",
        "MangaTranslator.Precheck.Tests");

    public static void WriteJson<T>(string testName, string relativePath, T value)
    {
        WriteText(
            testName,
            relativePath,
            JsonSerializer.Serialize(value, JsonOptions));
    }

    public static void WriteJsonElement(
        string testName,
        string relativePath,
        JsonElement value)
    {
        WriteText(
            testName,
            relativePath,
            JsonSerializer.Serialize(value, JsonOptions));
    }

    public static void WriteText(string testName, string relativePath, string content)
    {
        var path = GetArtifactPath(testName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static void WriteBytes(
        string testName,
        string relativePath,
        ReadOnlyMemory<byte> content)
    {
        var path = GetArtifactPath(testName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content.ToArray());
    }

    private static string GetArtifactPath(string testName, string relativePath)
    {
        return Path.Combine(AssemblyRoot, Sanitize(testName), relativePath);
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
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
