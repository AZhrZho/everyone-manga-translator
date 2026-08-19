using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace MangaTranslator.Dialogue.Tests;

internal static class TestArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string WriteDialogueTranslation(DialogueTranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var path = Path.Combine(
            FindWorkspaceRoot(),
            "TestResults",
            "dialogue",
            "unit",
            "dialogue_translation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        return path;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
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
