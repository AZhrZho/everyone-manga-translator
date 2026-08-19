using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MangaTranslator.ModelProvider.OpenAI;
using MangaTranslator.Precheck;
using SkiaSharp;

return await PrecheckRunner.RunAsync(args).ConfigureAwait(false);

internal static partial class PrecheckRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var arguments = CommandLineArguments.Parse(args);
        if (arguments.HasFlag("help") || !arguments.TryGetValue("input", out var inputValue))
        {
            PrintUsage();
            return arguments.HasFlag("help") ? 0 : 2;
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? Environment.GetEnvironmentVariable("KITCODING_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine(
                "Set OPENAI_API_KEY or KITCODING_API_KEY in the runner process environment.");
            return 2;
        }

        var inputDirectory = Path.GetFullPath(inputValue);
        if (!Directory.Exists(inputDirectory))
        {
            Console.Error.WriteLine($"Input directory does not exist: {inputDirectory}");
            return 2;
        }

        var pageLimit = arguments.GetInt32("max-pages", null);
        var discovered = await DiscoverPagesAsync(inputDirectory, pageLimit).ConfigureAwait(false);
        if (discovered.Pages.Count == 0)
        {
            Console.Error.WriteLine($"No supported page images were found in: {inputDirectory}");
            return 2;
        }

        var endpoint = NormalizeResponsesEndpoint(arguments.GetValue(
            "endpoint",
            "https://api.openai.com/v1/responses"));
        var runId = arguments.GetValue(
            "run-id",
            $"precheck_{DateTimeOffset.Now:yyyyMMdd_HHmmss}");
        var outputDirectory = Path.GetFullPath(arguments.GetValue(
            "output",
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "precheck-runs",
                $"{Path.GetFileName(inputDirectory)}_{runId}")));
        Directory.CreateDirectory(outputDirectory);
        using var progress = new HostPrecheckProgressReporter(outputDirectory);
        var options = new PrecheckOptions
        {
            RunId = runId,
            Model = arguments.GetValue("model", "gpt-5.4"),
            TargetLanguage = arguments.GetValue("target-language", "zh-Hans"),
            MaxBatchPageCount = arguments.GetInt32("batch-pages", 8)!.Value,
            MaxBatchCost = arguments.GetDouble("batch-cost", 24)!.Value,
            ContextPagesPerSide = arguments.GetInt32("context-pages", 1)!.Value,
            MaxConcurrentBatches = arguments.GetInt32("batch-concurrency", 5)!.Value,
            MaxAutomaticReviewAttempts = arguments.GetInt32("review-attempts", 2)!.Value,
            AutomaticReviewConfidenceThreshold = arguments.GetDouble("review-threshold", 0.70)!.Value,
            ContactSheetPageCount = arguments.GetInt32("contact-pages", 12)!.Value,
            ContactSheetColumns = arguments.GetInt32("contact-columns", 4)!.Value,
            MaxConcurrentThumbnailCalls = arguments.GetInt32("thumbnail-concurrency", 1)!.Value,
            MaxConcurrentSemanticMergeCalls = arguments.GetInt32(
                "semantic-concurrency",
                4)!.Value
        };

        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var provider = new OpenAIModelProvider(
            httpClient,
            new OpenAIModelProviderOptions
            {
                ApiKey = apiKey,
                Endpoint = endpoint,
                SendRequestMetadata = false,
                Timeouts = new MangaTranslator.ModelProvider.Abstractions.ModelProviderOptions
                {
                    DefaultTotalTimeout = TimeSpan.FromMinutes(20),
                    DefaultTimeToFirstEventTimeout = TimeSpan.FromMinutes(3),
                    DefaultIdleEventTimeout = TimeSpan.FromMinutes(3)
                }
            });

        var pageAssets = new FileSystemPageAssetProvider(discovered.Paths);
        var request = new PrecheckRequest(
            Path.GetFileName(inputDirectory),
            discovered.Pages,
            pageAssets,
            options);
        var imageComposer = new CapturingImageComposer(new SkiaPrecheckImageComposer());
        var engine = new PrecheckEngine(
            new ModelBackedThumbnailPrecheckAnalyzer(provider, imageComposer, progress),
            new DynamicBatchPlanner(),
            new ModelBackedBatchAnalyzer(provider, progress),
            new DeterministicPrecheckMerger(),
            new ModelBackedAutomaticReviewProcessor(provider, progress),
            new ModelBackedGlobalSemanticConsolidator(provider, progress));

        progress.Log($"Input pages: {discovered.Pages.Count}");
        progress.Log($"Endpoint: {endpoint}");
        progress.Log($"Model: {options.Model}");
        progress.Log($"Target language: {options.TargetLanguage}");
        progress.Log($"Output: {outputDirectory}");

        PrecheckResult? result = null;
        try
        {
            await foreach (var @event in engine.RunStreamingAsync(request))
            {
                switch (@event)
                {
                    case PrecheckStageStarted started:
                        progress.Log($"start {started.Stage}");
                        break;
                    case PrecheckStageCompleted completed:
                        progress.Log($"done  {completed.Stage}");
                        break;
                    case ThumbnailPrecheckReady thumbnail:
                        await WriteJsonAsync(
                            Path.Combine(outputDirectory, "thumbnail_precheck.json"),
                            thumbnail.Document).ConfigureAwait(false);
                        break;
                    case BatchPlanReady plan:
                        await WriteJsonAsync(
                            Path.Combine(outputDirectory, "batch_plan.json"),
                            plan.Document).ConfigureAwait(false);
                        break;
                    case BatchCompleted batch:
                        var observationDirectory = Path.Combine(
                            outputDirectory,
                            "batch_observations");
                        Directory.CreateDirectory(observationDirectory);
                        await WriteJsonAsync(
                            Path.Combine(observationDirectory, $"{batch.Document.BatchId}.json"),
                            batch.Document).ConfigureAwait(false);
                        progress.Log($"batch {batch.Document.BatchId} completed");
                        break;
                    case PrecheckMergeReady merge:
                        await WriteJsonAsync(
                            Path.Combine(outputDirectory, "precheck_conflicts.json"),
                            merge.Result.Conflicts).ConfigureAwait(false);
                        break;
                    case AutomaticReviewCompleted review:
                        await WriteJsonAsync(
                            Path.Combine(outputDirectory, "auto_review.json"),
                            review.Result).ConfigureAwait(false);
                        break;
                    case GlobalSemanticMergeCompleted semantic:
                        await WriteSemanticArtifactsAsync(outputDirectory, semantic.Result)
                            .ConfigureAwait(false);
                        break;
                    case BookManifestReady manifest:
                        await WriteJsonAsync(
                            Path.Combine(outputDirectory, "book_manifest.json"),
                            manifest.Document).ConfigureAwait(false);
                        break;
                    case PrecheckCompleted completed:
                        result = completed.Result;
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Log($"Precheck failed: {exception.GetType().Name}: {exception.Message}", error: true);
            return 1;
        }

        if (result is null)
        {
            progress.Log("Precheck completed without a result.", error: true);
            return 1;
        }

        await WriteResultAsync(outputDirectory, result, imageComposer.Images).ConfigureAwait(false);
        progress.Log(
            $"Completed with {result.Merge.Conflicts.Count} conflicts, "
            + $"{result.AutoReview.UnresolvedConflicts.Count} unresolved_auto items, "
            + $"and {result.Warnings.Count} warnings.");
        return 0;
    }

    private static async Task<DiscoveredPages> DiscoverPagesAsync(
        string directory,
        int? maximumPages)
    {
        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedImage)
            .Select(path => new
            {
                Path = path,
                SourceIndex = ReadSourceIndex(Path.GetFileNameWithoutExtension(path))
            })
            .OrderBy(item => item.SourceIndex ?? int.MaxValue)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (maximumPages is > 0)
        {
            files = files.Take(maximumPages.Value).ToArray();
        }

        var descriptors = new List<PageDescriptor>(files.Length);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicateCounters = new Dictionary<int, int>();

        for (var ordinal = 0; ordinal < files.Length; ordinal++)
        {
            var file = files[ordinal];
            var sourceIndex = file.SourceIndex ?? ordinal + 1;
            duplicateCounters.TryGetValue(sourceIndex, out var duplicateNumber);
            duplicateCounters[sourceIndex] = duplicateNumber + 1;
            var pageId = duplicateNumber == 0
                ? $"p{sourceIndex:0000}"
                : $"p{sourceIndex:0000}_{duplicateNumber + 1:00}";
            var bytes = await File.ReadAllBytesAsync(file.Path).ConfigureAwait(false);
            var (width, height) = ReadDimensions(bytes, file.Path);
            var mediaType = GetMediaType(file.Path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            descriptors.Add(new PageDescriptor(
                pageId,
                sourceIndex,
                Path.GetFileName(file.Path),
                width,
                height,
                mediaType,
                hash));
            paths[pageId] = file.Path;
        }

        return new DiscoveredPages(descriptors, paths);
    }

    private static async Task WriteResultAsync(
        string outputDirectory,
        PrecheckResult result,
        IReadOnlyList<GeneratedImage> images)
    {
        Directory.CreateDirectory(outputDirectory);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "source_manifest.json"),
            result.SourceManifest).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "thumbnail_precheck.json"),
            result.ThumbnailPrecheck).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "cross_page_relations.json"),
            result.CrossPageRelations).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "batch_plan.json"),
            result.BatchPlan).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "auto_review.json"),
            result.AutoReview).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "precheck_conflicts.json"),
            result.Merge.Conflicts).ConfigureAwait(false);
        await WriteSemanticArtifactsAsync(outputDirectory, result.SemanticMerge)
            .ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "book_manifest.json"),
            result.BookManifest).ConfigureAwait(false);
        await WriteJsonElementAsync(
            Path.Combine(outputDirectory, "processing_hints.json"),
            result.Merge.ProcessingHints).ConfigureAwait(false);

        var observationDirectory = Path.Combine(outputDirectory, "batch_observations");
        Directory.CreateDirectory(observationDirectory);
        foreach (var observation in result.BatchObservations)
        {
            await WriteJsonAsync(
                Path.Combine(observationDirectory, $"{observation.BatchId}.json"),
                observation).ConfigureAwait(false);
        }

        var thumbnailDirectory = Path.Combine(outputDirectory, "thumbnails");
        Directory.CreateDirectory(thumbnailDirectory);
        foreach (var image in images)
        {
            var extension = StringComparer.Ordinal.Equals(image.MediaType, "image/png")
                ? ".png"
                : ".jpg";
            await File.WriteAllBytesAsync(
                Path.Combine(thumbnailDirectory, SanitizeFileName(image.AssetId) + extension),
                image.Data.ToArray()).ConfigureAwait(false);
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
    }

    private static async Task WriteJsonElementAsync(string path, JsonElement value)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
    }

    private static async Task WriteSemanticArtifactsAsync(
        string outputDirectory,
        GlobalSemanticMergeResult semanticMerge)
    {
        await WriteJsonElementAsync(
            Path.Combine(outputDirectory, "chapters.json"),
            semanticMerge.Chapters).ConfigureAwait(false);
        await WriteJsonElementAsync(
            Path.Combine(outputDirectory, "characters.json"),
            semanticMerge.Characters).ConfigureAwait(false);
        await WriteJsonElementAsync(
            Path.Combine(outputDirectory, "cultural_localizations.json"),
            semanticMerge.CulturalLocalizations).ConfigureAwait(false);
        await WriteJsonElementAsync(
            Path.Combine(outputDirectory, "glossary.json"),
            semanticMerge.Glossary).ConfigureAwait(false);
        await WriteJsonElementAsync(
            Path.Combine(outputDirectory, "story_bible.json"),
            semanticMerge.StoryBible).ConfigureAwait(false);
    }

    private static (int Width, int Height) ReadDimensions(byte[] bytes, string path)
    {
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException($"Unable to decode image: {path}");
        return (bitmap.Width, bitmap.Height);
    }

    private static Uri NormalizeResponsesEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        if (uri.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var baseText = endpoint.TrimEnd('/');
        return baseText.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(baseText + "/responses")
            : new Uri(baseText + "/v1/responses");
    }

    private static bool IsSupportedImage(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";
    }

    private static string GetMediaType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
    }

    private static int? ReadSourceIndex(string fileName)
    {
        var matches = NumberPattern().Matches(fileName);
        return matches.Count > 0
            && int.TryParse(matches[^1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? index
                : null;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: dotnet run --project tools/MangaTranslator.Precheck.Runner -- "
            + "--input <book-directory> [--output <run-directory>] [--endpoint <base-or-responses-url>] "
            + "[--model <model>] [--max-pages <count>] [--batch-pages <count>] "
            + "[--batch-concurrency <count>] [--thumbnail-concurrency <count>]");
    }

    [GeneratedRegex("\\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    private sealed record DiscoveredPages(
        IReadOnlyList<PageDescriptor> Pages,
        IReadOnlyDictionary<string, string> Paths);
}

internal sealed class FileSystemPageAssetProvider(
    IReadOnlyDictionary<string, string> pagePaths) : IPageAssetProvider
{
    private readonly ConcurrentDictionary<(string PageId, ImageQuality Quality), byte[]> _resizedCache = new();

    public async ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
        string pageId,
        ImageQuality quality,
        CancellationToken cancellationToken = default)
    {
        if (!pagePaths.TryGetValue(pageId, out var path))
        {
            throw new KeyNotFoundException($"Unknown page ID: {pageId}");
        }

        if (quality == ImageQuality.Original)
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (_resizedCache.TryGetValue((pageId, quality), out var cached))
        {
            return cached;
        }

        var original = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var maximumLongEdge = quality == ImageQuality.Thumbnail ? 1200 : 2000;
        var resized = ResizePreservingMediaType(original, maximumLongEdge, Path.GetExtension(path));
        _resizedCache.TryAdd((pageId, quality), resized);
        return resized;
    }

    private static byte[] ResizePreservingMediaType(
        byte[] bytes,
        int maximumLongEdge,
        string extension)
    {
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("Unable to decode a page image.");
        var longEdge = Math.Max(bitmap.Width, bitmap.Height);
        if (longEdge <= maximumLongEdge)
        {
            return bytes;
        }

        var scale = maximumLongEdge / (double)longEdge;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidDataException("Unable to resize a page image.");
        using var image = SKImage.FromBitmap(resized);
        var format = extension.ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg
        };
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}

internal sealed class CapturingImageComposer(IPrecheckImageComposer inner) : IPrecheckImageComposer
{
    private readonly ConcurrentQueue<GeneratedImage> _images = new();
    private int _sequence;

    public IReadOnlyList<GeneratedImage> Images => _images.ToArray();

    public async Task<IReadOnlyList<GeneratedImage>> CreateContactSheetsAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> pages,
        CancellationToken cancellationToken = default)
    {
        var images = await inner
            .CreateContactSheetsAsync(request, pages, cancellationToken)
            .ConfigureAwait(false);
        return Capture("contact", images);
    }

    public async Task<IReadOnlyList<GeneratedImage>> CreateAdjacencySheetsAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> pages,
        CancellationToken cancellationToken = default)
    {
        var images = await inner
            .CreateAdjacencySheetsAsync(request, pages, cancellationToken)
            .ConfigureAwait(false);
        return Capture("adjacency", images);
    }

    private GeneratedImage[] Capture(string kind, IReadOnlyList<GeneratedImage> images)
    {
        var callSequence = Interlocked.Increment(ref _sequence);
        var captured = images
            .Select((image, index) => image with
            {
                AssetId = $"{kind}_{callSequence:000}_{index:000}"
            })
            .ToArray();
        foreach (var image in captured)
        {
            _images.Enqueue(image);
        }

        return captured;
    }
}

internal sealed class HostPrecheckProgressReporter : IPrecheckProgressReporter, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _log;

    public HostPrecheckProgressReporter(string outputDirectory)
    {
        _log = new StreamWriter(
            Path.Combine(outputDirectory, "precheck-run.log"),
            append: false,
            Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public void Report(PrecheckProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var counter = progress.Current is { } current && progress.Total is { } total
            ? $" ({current}/{total})"
            : string.Empty;
        var item = string.IsNullOrWhiteSpace(progress.ItemId)
            ? string.Empty
            : $" {progress.ItemId}";
        var detail = string.IsNullOrWhiteSpace(progress.Detail)
            ? string.Empty
            : $" - {progress.Detail}";
        var elapsed = progress.Elapsed is { } duration
            ? $" [{duration.TotalSeconds:0.0}s]"
            : string.Empty;
        var error = string.IsNullOrWhiteSpace(progress.ErrorCode)
            ? string.Empty
            : $" error={progress.ErrorCode}";
        WriteLine(
            $"[{progress.Stage}] {progress.Action}{item}{counter}{detail}{elapsed}{error}",
            error: progress.Action.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    public void Log(string message, bool error = false)
    {
        WriteLine(message, error);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _log.Dispose();
        }
    }

    private void WriteLine(string message, bool error)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
        lock (_gate)
        {
            if (error)
            {
                Console.Error.WriteLine(line);
            }
            else
            {
                Console.WriteLine(line);
            }

            Console.Out.Flush();
            Console.Error.Flush();
            _log.WriteLine(line);
        }
    }
}

internal sealed class CommandLineArguments
{
    private readonly Dictionary<string, string?> _values;

    private CommandLineArguments(Dictionary<string, string?> values)
    {
        _values = values;
    }

    public static CommandLineArguments Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            var value = index + 1 < args.Count
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++index]
                    : null;
            values[key] = value;
        }

        return new CommandLineArguments(values);
    }

    public bool HasFlag(string name) => _values.ContainsKey(name);

    public bool TryGetValue(string name, out string value)
    {
        if (_values.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public string GetValue(string name, string fallback)
    {
        return TryGetValue(name, out var value) ? value : fallback;
    }

    public int? GetInt32(string name, int? fallback)
    {
        return TryGetValue(name, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
    }

    public double? GetDouble(string name, double? fallback)
    {
        return TryGetValue(name, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
    }
}
