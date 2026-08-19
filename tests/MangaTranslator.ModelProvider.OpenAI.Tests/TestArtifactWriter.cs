using System.Text.Json;

namespace MangaTranslator.ModelProvider.OpenAI.Tests;

internal static class TestArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string AssemblyRoot = Path.Combine(
        FindWorkspaceRoot(),
        "TestResults",
        "MangaTranslator.ModelProvider.OpenAI.Tests");

    public static void WriteJson<T>(string testName, string relativePath, T value)
    {
        WriteText(testName, relativePath, JsonSerializer.Serialize(value, JsonOptions));
    }

    public static void WriteText(string testName, string relativePath, string content)
    {
        var path = Path.Combine(AssemblyRoot, testName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
}
