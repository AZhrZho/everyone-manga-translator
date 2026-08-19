using MangaTranslator.Api.Storage;
using MangaTranslator.LocalDetection;

namespace MangaTranslator.Api.Jobs;

public interface ITextDetectorModelLocator
{
    string DefaultInstallPath(TextMaskDetectorDescriptor detector);

    string EnvironmentVariableName(TextMaskDetectorDescriptor detector);

    string? TryResolve(TextMaskDetectorDescriptor detector);
}

public sealed class TextDetectorModelLocator(
    IHostEnvironment environment,
    AppDataPaths paths) : ITextDetectorModelLocator
{
    public string DefaultInstallPath(TextMaskDetectorDescriptor detector) =>
        Path.Combine(paths.UserDataRoot, "models", detector.DefaultModelFileName);

    public string EnvironmentVariableName(TextMaskDetectorDescriptor detector) => detector.DetectorId switch
    {
        TextDetectorIds.Craft => "MANGA_TRANSLATOR_CRAFT_MODEL",
        TextDetectorIds.Ctd => "MANGA_TRANSLATOR_CTD_MODEL",
        _ => throw new ArgumentException($"Unknown detector: {detector.DetectorId}", nameof(detector))
    };

    public string? TryResolve(TextMaskDetectorDescriptor detector)
    {
        var environmentVariable = EnvironmentVariableName(detector);
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(configured)
            && detector.DetectorId == TextDetectorIds.Ctd)
        {
            configured = Environment.GetEnvironmentVariable("MANGA_TRANSLATOR_DETECTOR_MODEL");
            environmentVariable = "MANGA_TRANSLATOR_DETECTOR_MODEL";
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"{environmentVariable} 指向的文字检测模型不存在。",
                    fullPath);
            }

            return fullPath;
        }

        var candidates = new List<string>
        {
            DefaultInstallPath(detector),
            Path.Combine(environment.ContentRootPath, "resources", "models", detector.DefaultModelFileName)
        };
        for (var current = new DirectoryInfo(environment.ContentRootPath);
             current is not null;
             current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "resources", "models", detector.DefaultModelFileName));
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .FirstOrDefault(File.Exists);
    }
}
