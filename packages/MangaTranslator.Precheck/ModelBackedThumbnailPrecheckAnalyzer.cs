using System.Diagnostics;
using System.Text.Json;
using MangaTranslator.ModelProvider.Abstractions;

namespace MangaTranslator.Precheck;

public sealed class ModelBackedThumbnailPrecheckAnalyzer : IThumbnailPrecheckAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IModelProvider _modelProvider;
    private readonly IPrecheckImageComposer _imageComposer;
    private readonly IPrecheckProgressReporter _progress;

    public ModelBackedThumbnailPrecheckAnalyzer(
        IModelProvider modelProvider,
        IPrecheckImageComposer imageComposer,
        IPrecheckProgressReporter? progress = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _imageComposer = imageComposer ?? throw new ArgumentNullException(nameof(imageComposer));
        _progress = progress ?? NullPrecheckProgressReporter.Instance;
    }

    public async Task<ThumbnailPrecheckDocument> AnalyzeAsync(
        PrecheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pages = request.Pages.OrderBy(page => page.SourceIndex).ToArray();
        if (pages.Length == 0)
        {
            return new ThumbnailPrecheckDocument(
                "1.0.0",
                new GlobalObservations(null, null, 0, ["No pages were supplied."]),
                [],
                []);
        }

        var chunkSize = Math.Max(1, request.Options.ContactSheetPageCount);
        var chunks = pages.Chunk(chunkSize).ToArray();
        _progress.Report(new PrecheckProgress(
            "thumbnail_precheck",
            "chunks_planned",
            Total: chunks.Length,
            Detail: $"{pages.Length} pages; up to {chunkSize} pages per chunk"));
        var maxConcurrency = Math.Max(1, request.Options.MaxConcurrentThumbnailCalls);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = chunks.Select((chunk, index) => AnalyzeChunkWithLimitAsync(
            request,
            pages,
            chunk,
            index,
            chunks.Length,
            semaphore,
            cancellationToken));
        var chunkResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        var pageObservations = chunkResults
            .SelectMany(result => result.Pages)
            .GroupBy(page => page.PageId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(page => page.Confidence).First())
            .OrderBy(page => pages.First(source => source.PageId == page.PageId).SourceIndex)
            .ToArray();

        var pageIds = pages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal);
        var relations = chunkResults
            .SelectMany(result => result.Relations)
            .Where(relation => relation.Pages.All(pageIds.Contains))
            .Where(relation => relation.Pages.Count > 1)
            .GroupBy(GetRelationKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(relation => relation.Confidence).First())
            .ToArray();

        var missingPages = pages
            .Where(page => !pageObservations.Any(observation =>
                StringComparer.Ordinal.Equals(observation.PageId, page.PageId)))
            .ToArray();
        if (missingPages.Length > 0)
        {
            pageObservations = pageObservations
                .Concat(missingPages.Select(CreateUnknownObservation))
                .OrderBy(observation => pages.First(page => page.PageId == observation.PageId).SourceIndex)
                .ToArray();
        }

        var globalCandidates = chunkResults
            .Select(result => result.Global)
            .Where(global => global is not null)
            .ToArray();
        var direction = SelectGlobalValue(globalCandidates, item => item!.ReadingDirection);
        var language = SelectGlobalValue(globalCandidates, item => item!.PrimaryLanguage);
        var directionConfidence = globalCandidates
            .Where(global => global!.ReadingDirection == direction)
            .Select(global => global!.ReadingDirectionConfidence)
            .DefaultIfEmpty(0)
            .Max();
        var uncertainties = globalCandidates
            .SelectMany(global => global!.Uncertainties)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ThumbnailPrecheckDocument(
            "1.0.0",
            new GlobalObservations(language, direction, directionConfidence, uncertainties),
            pageObservations,
            relations);
    }

    private async Task<ThumbnailChunkResult> AnalyzeChunkWithLimitAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> allPages,
        PageDescriptor[] chunk,
        int index,
        int totalChunks,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var chunkId = $"thumbnail_chunk_{index + 1:000}";
        _progress.Report(new PrecheckProgress(
            "thumbnail_precheck",
            "chunk_started",
            chunkId,
            index + 1,
            totalChunks,
            $"pages {chunk[0].PageId}..{chunk[^1].PageId}"));
        try
        {
            var result = await AnalyzeChunkAsync(request, allPages, chunk, index, cancellationToken)
                .ConfigureAwait(false);
            _progress.Report(new PrecheckProgress(
                "thumbnail_precheck",
                "chunk_completed",
                chunkId,
                index + 1,
                totalChunks,
                $"{result.Pages.Count} page observations; {result.Relations.Count} relations",
                stopwatch.Elapsed));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _progress.Report(new PrecheckProgress(
                "thumbnail_precheck",
                "chunk_failed",
                chunkId,
                index + 1,
                totalChunks,
                exception.Message,
                stopwatch.Elapsed,
                GetErrorCode(exception)));
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<ThumbnailChunkResult> AnalyzeChunkAsync(
        PrecheckRequest request,
        IReadOnlyList<PageDescriptor> allPages,
        PageDescriptor[] chunk,
        int index,
        CancellationToken cancellationToken)
    {
        var firstIndex = chunk[0].SourceIndex;
        var lastIndex = chunk[^1].SourceIndex;
        var adjacencyPages = allPages
            .Where(page => page.SourceIndex >= firstIndex - 1 && page.SourceIndex <= lastIndex + 1)
            .ToArray();
        var contactSheets = await _imageComposer
            .CreateContactSheetsAsync(request, chunk, cancellationToken)
            .ConfigureAwait(false);
        var adjacencySheets = await _imageComposer
            .CreateAdjacencySheetsAsync(request, adjacencyPages, cancellationToken)
            .ConfigureAwait(false);

        var content = new List<ModelContent>
        {
            new TextContent(PrecheckPromptCatalog.ThumbnailUser(chunk, []))
        };
        content.AddRange(contactSheets.Select(sheet => new ImageContent(
            sheet.Data,
            sheet.MediaType,
            request.Options.ThumbnailImageDetail)));
        content.AddRange(adjacencySheets.Select(sheet => new ImageContent(
            sheet.Data,
            sheet.MediaType,
            request.Options.ThumbnailImageDetail)));

        ModelResponse response;
        try
        {
            response = await _modelProvider.GenerateAsync(new ModelRequest(
                request.Options.ManifestModel ?? request.Options.Model,
                [
                    new ModelMessage(
                        ModelRole.System,
                        PrecheckPromptCatalog.ThumbnailSystem(request.Options.TargetLanguage)),
                    new ModelMessage(ModelRole.User, content)
                ],
                new JsonSchemaOutputFormat("thumbnail_precheck_chunk", PrecheckSchemas.ThumbnailChunk()),
                new ModelGenerationOptions
                {
                    MaxOutputTokens = 8_000,
                    TotalTimeout = TimeSpan.FromMinutes(10)
                },
                new Dictionary<string, string>
                {
                    ["run_id"] = request.Options.RunId,
                    ["chunk_index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException(
                $"The model output for thumbnail chunk {index} was not valid JSON.",
                exception);
        }
        catch (ModelProviderException exception)
        {
            throw new PrecheckAnalysisException(
                $"The model provider failed thumbnail chunk {index} "
                + $"[{exception.Error.Code}]: {exception.Error.Message} "
                + FormatProviderMetadata(exception.Error),
                exception);
        }

        EnsureCompleted(response, $"thumbnail chunk {index}");
        var modelResult = Deserialize<ThumbnailChunkModelResult>(response);
        var validPageIds = adjacencyPages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal);
        var corePageIds = chunk.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal);
        var pages = modelResult.Pages
            .Where(page => corePageIds.Contains(page.PageId))
            .Select(MapPage)
            .ToArray();
        var relations = modelResult.Relations
            .Where(relation => relation.Pages.All(validPageIds.Contains))
            .Select(MapRelation)
            .ToArray();

        return new ThumbnailChunkResult(MapGlobal(modelResult.Global), pages, relations);
    }

    private static PageThumbnailObservation MapPage(ThumbnailPageModel page)
    {
        return new PageThumbnailObservation(
            page.PageId,
            page.PageType,
            page.ChapterCandidateId,
            page.SceneCandidateId,
            Math.Clamp(page.LayoutComplexity, 0, 5),
            Math.Clamp(page.TextRiskScore, 0, 5),
            Math.Clamp(page.MaskRiskScore, 0, 5),
            Math.Clamp(page.UncertaintyScore, 0, 5),
            page.NeedsHighResolutionReview,
            Math.Clamp(page.Confidence, 0, 1),
            page.Evidence);
    }

    private static PageRelation MapRelation(ThumbnailRelationModel relation)
    {
        return new PageRelation(
            relation.RelationId,
            relation.Type,
            relation.Pages,
            relation.HardConstraint,
            relation.MustSameBatch,
            relation.RequiresOcrCoordination,
            relation.RequiresMaskCoordination,
            Math.Clamp(relation.Confidence, 0, 1),
            relation.Evidence,
            relation.Status);
    }

    private static GlobalObservations MapGlobal(ThumbnailGlobalModel global)
    {
        return new GlobalObservations(
            global.PrimaryLanguage,
            global.ReadingDirection,
            Math.Clamp(global.ReadingDirectionConfidence, 0, 1),
            global.Uncertainties);
    }

    private static PageThumbnailObservation CreateUnknownObservation(PageDescriptor page)
    {
        return new PageThumbnailObservation(
            page.PageId,
            "unknown",
            null,
            null,
            5,
            5,
            5,
            5,
            true,
            0,
            ["The model did not return an observation for this page."]);
    }

    private static string? SelectGlobalValue(
        IReadOnlyList<GlobalObservations?> values,
        Func<GlobalObservations?, string?> selector)
    {
        return values
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private static string GetRelationKey(PageRelation relation)
    {
        return $"{relation.Type}|{string.Join(",", relation.Pages.Order(StringComparer.Ordinal))}";
    }

    private static T Deserialize<T>(ModelResponse response)
    {
        var json = response.Json?.GetRawText() ?? response.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PrecheckAnalysisException("The model returned no structured output.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new PrecheckAnalysisException("The model returned a null structured output.");
        }
        catch (JsonException exception)
        {
            throw new PrecheckAnalysisException(
                "The model output did not match the thumbnail schema.",
                exception);
        }
    }

    private static void EnsureCompleted(ModelResponse response, string stage)
    {
        if (response.Status == ModelResponseStatus.Refused)
        {
            throw new PrecheckAnalysisException($"The model refused {stage}: {response.Refusal}");
        }

        if (response.Status != ModelResponseStatus.Completed)
        {
            throw new PrecheckAnalysisException($"The model did not complete {stage}: {response.Status}.");
        }
    }

    private static string GetErrorCode(Exception exception)
    {
        return exception is ModelProviderException providerException
            ? providerException.Error.Code
            : exception.InnerException is ModelProviderException innerProviderException
                ? innerProviderException.Error.Code
                : exception.GetType().Name;
    }

    private static string FormatProviderMetadata(ModelProviderError error)
    {
        return error.ProviderMetadata is { } metadata
            ? $"Metadata: {metadata.GetRawText()}"
            : string.Empty;
    }

    private sealed record ThumbnailChunkResult(
        GlobalObservations Global,
        IReadOnlyList<PageThumbnailObservation> Pages,
        IReadOnlyList<PageRelation> Relations);

    private sealed record ThumbnailChunkModelResult(
        ThumbnailGlobalModel Global,
        IReadOnlyList<ThumbnailPageModel> Pages,
        IReadOnlyList<ThumbnailRelationModel> Relations);

    private sealed record ThumbnailGlobalModel(
        string? PrimaryLanguage,
        string? ReadingDirection,
        double ReadingDirectionConfidence,
        IReadOnlyList<string> Uncertainties);

    private sealed record ThumbnailPageModel(
        string PageId,
        string PageType,
        string? ChapterCandidateId,
        string? SceneCandidateId,
        int LayoutComplexity,
        int TextRiskScore,
        int MaskRiskScore,
        int UncertaintyScore,
        bool NeedsHighResolutionReview,
        double Confidence,
        IReadOnlyList<string> Evidence);

    private sealed record ThumbnailRelationModel(
        string RelationId,
        string Type,
        IReadOnlyList<string> Pages,
        bool HardConstraint,
        bool MustSameBatch,
        bool RequiresOcrCoordination,
        bool RequiresMaskCoordination,
        double Confidence,
        string Evidence,
        string Status);
}

public sealed class PrecheckAnalysisException : Exception
{
    public PrecheckAnalysisException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
