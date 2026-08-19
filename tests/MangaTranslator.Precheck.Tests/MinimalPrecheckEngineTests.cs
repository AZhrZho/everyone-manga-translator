using System.Runtime.CompilerServices;
using System.Text.Json;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;

namespace MangaTranslator.Precheck.Tests;

public sealed class MinimalPrecheckEngineTests
{
    private static readonly string[] EmptyStrings = [];
    private static readonly string[] StoryEvidence = ["story panels"];

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task TwentyFivePagesUseThreeScansAndOneConsolidationCall()
    {
        var scanResponses = new[]
        {
            ScanResponse(1, 12),
            ScanResponse(13, 24),
            ScanResponse(25, 25)
        };
        var scanProvider = new QueueProvider(scanResponses);
        var consolidationProvider = new QueueProvider(ContextResponse());
        var engine = new MinimalPrecheckEngine(scanProvider, consolidationProvider);
        var request = Request(25);

        var plan = MinimalPrecheckEngine.Plan(request);
        var scans = await engine.ScanAsync(request, plan, null, null, null, CancellationToken.None);
        var manifest = MinimalPrecheckEngine.BuildPageManifest(request, scans);
        var context = await engine.ConsolidateAsync(request, manifest, scans, CancellationToken.None);

        Assert.Equal(3, plan.Count);
        Assert.Equal(["p0013", "p0014", "p0015", "p0016", "p0017", "p0018", "p0019", "p0020", "p0021", "p0022", "p0023", "p0024"], plan[1].OwnerPages);
        Assert.Equal(["p0012"], plan[1].ContextPagesBefore);
        Assert.Equal(["p0025"], plan[1].ContextPagesAfter);
        Assert.Equal(3, scanProvider.Requests.Count);
        Assert.Single(consolidationProvider.Requests);
        Assert.Equal("scan-model", scanProvider.Requests[0].Model);
        Assert.Equal("context-model", consolidationProvider.Requests[0].Model);
        Assert.DoesNotContain(
            consolidationProvider.Requests[0].Messages.SelectMany(message => message.Content),
            content => content is ImageContent);
        Assert.Equal("zh-Hans", context.TargetLanguage);
    }

    [Fact]
    public async Task PageRoutingExcludesOnlyHighConfidenceNonStoryPages()
    {
        var scanProvider = new QueueProvider(
            """
            {
              "pages": [
                {"pageId":"p0001","pageType":"cover","confidence":0.95,"evidence":["front cover"]},
                {"pageId":"p0002","pageType":"advertisement","confidence":0.55,"evidence":["uncertain promotional page"]},
                {"pageId":"p0003","pageType":"story","confidence":0.98,"evidence":["story panels"]}
              ],
              "contextDelta": {
                "batchSynopsis":"",
                "characters":[],
                "glossary":[],
                "continuityFacts":[],
                "translationStyleSignals":[]
              },
              "unresolvedQuestions":[]
            }
            """);
        var engine = new MinimalPrecheckEngine(scanProvider, new QueueProvider(ContextResponse()));
        var request = Request(3);
        var plan = MinimalPrecheckEngine.Plan(request);
        var scans = await engine.ScanAsync(request, plan, null, null, null, CancellationToken.None);

        var manifest = MinimalPrecheckEngine.BuildPageManifest(request, scans);

        Assert.False(manifest.Pages[0].IncludeInWorkflow);
        Assert.True(manifest.Pages[1].IncludeInWorkflow);
        Assert.True(manifest.Pages[2].IncludeInWorkflow);
    }

    [Fact]
    public async Task IllustrationRemainsIncludedBecauseItMayContainTranslatableText()
    {
        var scanProvider = new QueueProvider(
            """
            {
              "pages": [
                {"pageId":"p0001","pageType":"illustration","confidence":0.99,"evidence":["full-page illustration"]}
              ],
              "contextDelta": {
                "batchSynopsis":"",
                "characters":[],
                "glossary":[],
                "continuityFacts":[],
                "translationStyleSignals":[]
              },
              "unresolvedQuestions":[]
            }
            """);
        var engine = new MinimalPrecheckEngine(scanProvider, new QueueProvider(ContextResponse()));
        var request = Request(1);
        var scans = await engine.ScanAsync(request, MinimalPrecheckEngine.Plan(request), null, null, null, CancellationToken.None);

        var manifest = MinimalPrecheckEngine.BuildPageManifest(request, scans);

        Assert.True(manifest.Pages[0].IncludeInWorkflow);
    }

    [Fact]
    public async Task RestoredScanIsNotSubmittedAgain()
    {
        var provider = new QueueProvider(ScanResponse(13, 13));
        var engine = new MinimalPrecheckEngine(provider, new QueueProvider(ContextResponse()));
        var request = Request(13);
        var plan = MinimalPrecheckEngine.Plan(request);
        var restored = new MinimalPrecheckScanResult(
            "1.0.0",
            "scan_001",
            plan[0].OwnerPages,
            plan[0].OwnerPages.Select(pageId => new MinimalPageClassification(pageId, "story", 1, [])).ToArray(),
            Json("{\"batchSynopsis\":\"restored\",\"characters\":[],\"glossary\":[],\"continuityFacts\":[],\"translationStyleSignals\":[]}"),
            []);

        var scans = await engine.ScanAsync(
            request,
            plan,
            new Dictionary<string, MinimalPrecheckScanResult> { ["scan_001"] = restored },
            null,
            null,
            CancellationToken.None);

        Assert.Equal(2, scans.Count);
        Assert.Single(provider.Requests);
        Assert.Equal("scan_002", scans[1].BatchId);
    }

    private static MinimalPrecheckRequest Request(int pageCount) => new(
        "book-test",
        Enumerable.Range(1, pageCount).Select(index => new PageDescriptor(
            $"p{index:0000}", index, $"{index:0000}.png", 1000, 1400, "image/png", $"hash-{index}")).ToArray(),
        new AssetProvider(),
        new MinimalPrecheckOptions
        {
            RunId = "run-test",
            ScanModel = "scan-model",
            ConsolidationModel = "context-model",
            PagesPerScan = 12,
            ContextPagesPerSide = 1,
            MaxConcurrentScans = 4
        });

    private static string ScanResponse(int start, int end) => JsonSerializer.Serialize(new
    {
        pages = Enumerable.Range(start, end - start + 1).Select(index => new
        {
            pageId = $"p{index:0000}",
            pageType = "story",
            confidence = 0.95,
            evidence = StoryEvidence
        }),
        contextDelta = new
        {
            batchSynopsis = $"pages {start}-{end}",
            characters = Array.Empty<object>(),
            glossary = Array.Empty<object>(),
            continuityFacts = Array.Empty<object>(),
            translationStyleSignals = EmptyStrings
        },
        unresolvedQuestions = Array.Empty<string>()
    });

    private static string ContextResponse() =>
        """
        {
          "synopsis":{"overall":"A story.","tone":"dramatic","themes":[]},
          "translationStyle":{"overallVoice":"natural","dialogueRules":[],"honorificPolicy":"preserve","namePolicy":"consistent","prohibitedTendencies":[]},
          "characters":[],
          "glossary":[],
          "continuityFacts":[],
          "unresolvedQuestions":[]
        }
        """;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class AssetProvider : IPageAssetProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
            string pageId,
            ImageQuality quality,
            CancellationToken cancellationToken = default)
        {
            _ = pageId;
            _ = quality;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(OnePixelPng);
        }
    }

    private sealed class QueueProvider(params string[] responses) : IModelProvider
    {
        private readonly Queue<string> responses = new(responses);

        public string ProviderId => "test";

        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ResponseCreated($"response-{Requests.Count}");
            yield return new OutputTextDelta(responses.Dequeue());
            yield return new ResponseCompleted(new ModelResponseMetadata($"response-{Requests.Count}", TimeSpan.Zero));
            await Task.CompletedTask;
        }
    }
}
