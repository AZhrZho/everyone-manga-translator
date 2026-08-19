using System.Text.Encodings.Web;
using System.Text.Json;

namespace MangaTranslator.FontReference.FileSystem.Tests;

internal static class TestArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string AssemblyRoot = Path.Combine(
        FindRepositoryRoot(),
        "TestResults",
        "MangaTranslator.FontReference.FileSystem.Tests");

    public static void WriteJson<T>(string testName, string relativePath, T value)
    {
        WriteText(
            testName,
            relativePath,
            JsonSerializer.Serialize(value, JsonOptions));
    }

    public static void WriteBytes(
        string testName,
        string relativePath,
        ReadOnlyMemory<byte> content)
    {
        string path = GetArtifactPath(testName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content.ToArray());
    }

    private static void WriteText(string testName, string relativePath, string content)
    {
        string path = GetArtifactPath(testName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string GetArtifactPath(string testName, string relativePath)
    {
        return Path.Combine(AssemblyRoot, testName, relativePath);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MangaTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the repository root containing MangaTranslator.slnx.");
    }
}
