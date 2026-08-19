using System.Runtime.CompilerServices;
using System.Text.Json;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;

namespace MangaTranslator.Precheck.Tests;

public sealed class PrecheckComponentTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task ImageComposerCreatesLabeledContactAndAdjacencySheetsInMemory()
    {
        var pages = new[]
        {
            CreatePage("p0001", 1),
            CreatePage("p0002", 2),
            CreatePage("p0003", 3)
        };
        var request = CreateRequest(
            pages,
            new RecordingPageAssetProvider(),
            new PrecheckOptions
            {
                ContactSheetPageCount = 4,
                ContactSheetColumns = 2,
                ContactSheetThumbnailWidth = 80,
                ContactSheetThumbnailHeight = 120
            });
        var composer = new SkiaPrecheckImageComposer();

        var contactSheets = await composer.CreateContactSheetsAsync(request, pages);
        var adjacencySheets = await composer.CreateAdjacencySheetsAsync(request, pages);

        var contact = Assert.Single(contactSheets);
        Assert.Equal("image/jpeg", contact.MediaType);
        Assert.True(contact.Width > 0);
        Assert.True(contact.Height > 0);
        Assert.True(contact.Data.Length > 100);

        var adjacency = Assert.Single(adjacencySheets);
        Assert.Equal("image/jpeg", adjacency.MediaType);
        Assert.True(adjacency.Width > 0);
        Assert.True(adjacency.Height > 0);
        Assert.True(adjacency.Data.Length > 100);

        TestArtifactWriter.WriteBytes(
            nameof(ImageComposerCreatesLabeledContactAndAdjacencySheetsInMemory),
            "images/contact_sheets/contact_000.jpg",
            contact.Data);
        TestArtifactWriter.WriteBytes(
            nameof(ImageComposerCreatesLabeledContactAndAdjacencySheetsInMemory),
            "images/adjacency_sheets/adjacency_000.jpg",
            adjacency.Data);
    }

    [Fact]
    public async Task ThumbnailAnalyzerUsesStructuredMultimodalRequestAndMapsRelations()
    {
        var pages = new[]
        {
            CreatePage("p0001", 1),
            CreatePage("p0002", 2)
        };
        var provider = new QueueModelProvider(
            """
            {
              "global": {
                "primaryLanguage": "ja",
                "readingDirection": "right_to_left",
                "readingDirectionConfidence": 0.96,
                "uncertainties": []
              },
              "pages": [
                {
                  "pageId": "p0001",
                  "pageType": "story",
                  "chapterCandidateId": "chapter_1",
                  "sceneCandidateId": "scene_1",
                  "layoutComplexity": 3,
                  "textRiskScore": 2,
                  "maskRiskScore": 1,
                  "uncertaintyScore": 1,
                  "needsHighResolutionReview": false,
                  "confidence": 0.91,
                  "evidence": ["story panels"]
                },
                {
                  "pageId": "p0002",
                  "pageType": "story",
                  "chapterCandidateId": "chapter_1",
                  "sceneCandidateId": "scene_1",
                  "layoutComplexity": 4,
                  "textRiskScore": 3,
                  "maskRiskScore": 2,
                  "uncertaintyScore": 2,
                  "needsHighResolutionReview": true,
                  "confidence": 0.84,
                  "evidence": ["dense text"]
                }
              ],
              "relations": [
                {
                  "relationId": "rel_1",
                  "type": "spread",
                  "pages": ["p0001", "p0002"],
                  "hardConstraint": true,
                  "mustSameBatch": true,
                  "requiresOcrCoordination": true,
                  "requiresMaskCoordination": true,
                  "confidence": 0.93,
                  "evidence": "continuous composition",
                  "status": "candidate"
                }
              ]
            }
            """);
        var analyzer = new ModelBackedThumbnailPrecheckAnalyzer(
            provider,
            new StubImageComposer());
        var request = CreateRequest(
            pages,
            new RecordingPageAssetProvider(),
            new PrecheckOptions
            {
                Model = "fallback-model",
                ManifestModel = "manifest-model",
                AnalysisModel = "analysis-model",
                ContactSheetPageCount = 2,
                MaxConcurrentThumbnailCalls = 1
            });

        var result = await analyzer.AnalyzeAsync(request);

        Assert.Equal("ja", result.Global.PrimaryLanguage);
        Assert.Equal("right_to_left", result.Global.ReadingDirection);
        Assert.Equal(2, result.Pages.Count);
        Assert.True(result.Pages[1].NeedsHighResolutionReview);
        Assert.Equal("spread", Assert.Single(result.RelationCandidates).Type);

        var modelRequest = Assert.Single(provider.Requests);
        Assert.Equal("manifest-model", modelRequest.Model);
        Assert.IsType<JsonSchemaOutputFormat>(modelRequest.OutputFormat);
        var userContent = modelRequest.Messages.Single(message => message.Role == ModelRole.User).Content;
        Assert.Contains(userContent, content => content is ImageContent);
        Assert.Contains(userContent, content => content is TextContent text
            && text.Text.Contains("p0001", StringComparison.Ordinal));

        WriteModelRequestArtifacts(
            nameof(ThumbnailAnalyzerUsesStructuredMultimodalRequestAndMapsRelations),
            modelRequest,
            "thumbnail");
        TestArtifactWriter.WriteJson(
            nameof(ThumbnailAnalyzerUsesStructuredMultimodalRequestAndMapsRelations),
            "results/thumbnail_precheck.json",
            result);
    }

    [Fact]
    public async Task BatchAnalyzerLoadsOwnerOriginalAndContextStandardAndReturnsAllSections()
    {
        var pages = new[]
        {
            CreatePage("p0001", 1),
            CreatePage("p0002", 2),
            CreatePage("p0003", 3)
        };
        var assets = new RecordingPageAssetProvider();
        var provider = new QueueModelProvider(
            """
            {
              "pageObservations": [
                {
                  "pageId": "p0002",
                  "pageType": "story",
                  "textContent": "contains_non_sfx_text",
                  "sceneBoundaryBefore": false,
                  "facts": ["chapter story page"],
                  "ocrRisks": ["small text"],
                  "maskRisks": ["text over art"],
                  "confidence": 0.88,
                  "evidence": ["visible on page"]
                }
              ],
              "relationVerifications": [],
              "artifactDeltas": {
                "chapters": [],
                "characters": [],
                "culturalLocalizations": [],
                "glossary": [],
                "storyBible": []
              },
              "processingHints": [
                {
                  "pageId": "p0002",
                  "priority": "high",
                  "ocrHints": ["try vertical OCR"],
                  "maskHints": ["review borderless text"],
                  "translationHints": ["Send to manual review under the editorial style guide."],
                  "reviewRequiredAfterLocalProcessing": true
                }
              ],
              "uncertainties": [],
              "warnings": []
            }
            """);
        var request = CreateRequest(
            pages,
            assets,
            new PrecheckOptions
            {
                Model = "fallback-model",
                ManifestModel = "manifest-model",
                AnalysisModel = "analysis-model"
            });
        var batch = new BatchPlan(
            "batch_001",
            ["p0002"],
            ["p0001"],
            ["p0003"],
            [],
            3,
            "high",
            null,
            null);
        var thumbnail = CreateThumbnail(pages);

        var result = await new ModelBackedBatchAnalyzer(provider)
            .AnalyzeAsync(request, batch, thumbnail);

        Assert.Equal(JsonValueKind.Array, result.Observations.ValueKind);
        Assert.Equal(JsonValueKind.Array, result.RelationVerifications.ValueKind);
        Assert.Equal(JsonValueKind.Object, result.ArtifactDeltas.ValueKind);
        Assert.Equal(JsonValueKind.Array, result.ProcessingHints.ValueKind);
        Assert.Equal(JsonValueKind.Array, result.Uncertainties.ValueKind);
        Assert.DoesNotContain("manual review", result.ProcessingHints.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("editorial style guide", result.ProcessingHints.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("automatic multimodal review", result.ProcessingHints.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(("p0002", ImageQuality.Original), assets.Requests);
        Assert.Contains(("p0001", ImageQuality.Standard), assets.Requests);
        Assert.Contains(("p0003", ImageQuality.Standard), assets.Requests);
        var schema = Assert.IsType<JsonSchemaOutputFormat>(Assert.Single(provider.Requests).OutputFormat);
        Assert.Equal("analysis-model", provider.Requests[0].Model);
        Assert.DoesNotContain("$ref", schema.Schema.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("$defs", schema.Schema.GetRawText(), StringComparison.Ordinal);
        var systemPrompt = provider.Requests[0].Messages
            .Single(message => message.Role == ModelRole.System)
            .Content
            .OfType<TextContent>()
            .Single()
            .Text;
        Assert.Contains("no human approval gate", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("contains_non_sfx_text", systemPrompt, StringComparison.Ordinal);

        WriteModelRequestArtifacts(
            nameof(BatchAnalyzerLoadsOwnerOriginalAndContextStandardAndReturnsAllSections),
            provider.Requests[0],
            "batch");
        TestArtifactWriter.WriteJson(
            nameof(BatchAnalyzerLoadsOwnerOriginalAndContextStandardAndReturnsAllSections),
            "results/batch_observation.json",
            result);
    }

    [Fact]
    public void BookManifestBuilderReturnsRoutingDataAndHostWritesInspectableArtifact()
    {
        var pages = Enumerable.Range(1, 6)
            .Select(index => CreatePage($"p{index:0000}", index))
            .ToArray();
        var thumbnail = new ThumbnailPrecheckDocument(
            "1.0.0",
            new GlobalObservations("ja", "right_to_left", 0.98, []),
            [
                ThumbnailPage("p0001", "front_cover", null),
                ThumbnailPage("p0002", "chapter_title", null),
                ThumbnailPage("p0003", "story", "scene_a"),
                ThumbnailPage("p0004", "story", "scene_a"),
                ThumbnailPage("p0005", "story", "scene_b"),
                ThumbnailPage("p0006", "afterword", null)
            ],
            []);
        var observations = new[]
        {
            new BatchObservationDocument(
                "1.0.0",
                "batch_001",
                ["p0003", "p0004", "p0005"],
                Json("""
                    [
                      {"pageId":"p0003","pageType":"story","textContent":"contains_non_sfx_text","sceneBoundaryBefore":true},
                      {"pageId":"p0004","pageType":"story","textContent":"sfx_only","sceneBoundaryBefore":false},
                      {"pageId":"p0005","pageType":"story","textContent":"contains_non_sfx_text","sceneBoundaryBefore":true}
                    ]
                    """),
                Json("[]"),
                Json("{}"),
                Json("[]"),
                Json("[]"),
                [])
        };
        var semantic = new GlobalSemanticMergeResult(
            "zh-Hans",
            Json("""
                [
                  {"chapter_id":"ch01","page_start":"p0002","page_end":"p0005"},
                  {"chapter_id":"afterword","page_start":"p0006","page_end":"p0006"}
                ]
                """),
            Json("[]"),
            Json("[]"),
            Json("[]"),
            Json("{}"));

        var manifest = new DeterministicBookManifestBuilder().Build(
            CreateRequest(pages, new RecordingPageAssetProvider()),
            thumbnail,
            observations,
            semantic);

        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("book-test", manifest.BookId);
        Assert.Equal(6, manifest.Pages.Count);
        Assert.Equal("cover", manifest.Pages[0].PageType);
        Assert.Null(manifest.Pages[1].ChapterId);
        Assert.Equal("ch01", manifest.Pages[2].ChapterId);
        Assert.True(manifest.Pages[2].IncludeInMask);
        Assert.False(manifest.Pages[3].IncludeInMask);
        Assert.True(manifest.Pages[4].IncludeInMask);
        Assert.Null(manifest.Pages[5].ChapterId);
        Assert.Equal(manifest.Pages[2].SceneId, manifest.Pages[3].SceneId);
        Assert.NotEqual(manifest.Pages[3].SceneId, manifest.Pages[4].SceneId);

        TestArtifactWriter.WriteJson(
            nameof(BookManifestBuilderReturnsRoutingDataAndHostWritesInspectableArtifact),
            "results/final_artifacts/book_manifest.json",
            manifest);
    }

    [Fact]
    public async Task ThumbnailAnalyzerWrapsMalformedStructuredOutputAsPrecheckFailure()
    {
        var pages = new[] { CreatePage("p0001", 1) };
        var analyzer = new ModelBackedThumbnailPrecheckAnalyzer(
            new QueueModelProvider("{not-json"),
            new StubImageComposer());

        var exception = await Assert.ThrowsAsync<PrecheckAnalysisException>(() =>
            analyzer.AnalyzeAsync(CreateRequest(pages, new RecordingPageAssetProvider())));

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicMergerDetectsConflictingClaimsByMergeKey()
    {
        var pages = new[] { CreatePage("p0001", 1), CreatePage("p0002", 2) };
        var request = CreateRequest(pages, new RecordingPageAssetProvider());
        var observations = new[]
        {
            CreateObservation(
                "batch_001",
                """
                {
                  "chapters": [
                    {
                      "operation": "upsert",
                      "mergeKey": "chapter:1",
                      "candidateId": "chapter_1",
                      "value": "Chapter One",
                      "pages": ["p0001"],
                      "evidence": "title page",
                      "confidence": 0.92
                    }
                  ],
                  "characters": [],
                  "culturalLocalizations": [],
                  "glossary": [],
                  "storyBible": []
                }
                """),
            CreateObservation(
                "batch_002",
                """
                {
                  "chapters": [
                    {
                      "operation": "upsert",
                      "mergeKey": "chapter:1",
                      "candidateId": "chapter_1",
                      "value": "Episode One",
                      "pages": ["p0002"],
                      "evidence": "alternate title",
                      "confidence": 0.80
                    }
                  ],
                  "characters": [],
                  "culturalLocalizations": [],
                  "glossary": [],
                  "storyBible": []
                }
                """)
        };

        var result = new DeterministicPrecheckMerger().Merge(
            request,
            CreateThumbnail(pages),
            new BatchPlanDocument("1.0.0", []),
            observations);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("chapters", conflict.Artifact);
        Assert.Equal("chapter:1", conflict.MergeKey);
        Assert.Equal(2, conflict.Claims.Count);
        Assert.Equal(
            "conflicted",
            result.Chapters.GetProperty("chapters")[0].GetProperty("status").GetString());

        TestArtifactWriter.WriteJsonElement(
            nameof(DeterministicMergerDetectsConflictingClaimsByMergeKey),
            "merge/chapters.json",
            result.Chapters);
        TestArtifactWriter.WriteJson(
            nameof(DeterministicMergerDetectsConflictingClaimsByMergeKey),
            "merge/conflicts.json",
            result.Conflicts);
    }

    [Fact]
    public void DeterministicMergerQueuesLowConfidenceClaimForAutomaticReview()
    {
        var pages = new[] { CreatePage("p0001", 1) };
        var request = CreateRequest(
            pages,
            new RecordingPageAssetProvider(),
            new PrecheckOptions { AutomaticReviewConfidenceThreshold = 0.70 });
        var observation = CreateObservation(
            "batch_001",
            """
            {
              "chapters": [],
              "characters": [
                {
                  "operation": "add",
                  "mergeKey": "character:unknown-cover-girl",
                  "candidateId": null,
                  "value": "Unknown cover character",
                  "pages": ["p0001"],
                  "evidence": "visual candidate only",
                  "confidence": 0.55
                }
              ],
              "culturalLocalizations": [],
              "glossary": [],
              "storyBible": []
            }
            """);

        var result = new DeterministicPrecheckMerger().Merge(
            request,
            CreateThumbnail(pages),
            new BatchPlanDocument("1.0.0", []),
            [observation]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("low_confidence_artifact_claim", conflict.Type);
        Assert.Equal("characters", conflict.Artifact);
        Assert.Single(conflict.Claims);

        TestArtifactWriter.WriteJsonElement(
            nameof(DeterministicMergerQueuesLowConfidenceClaimForAutomaticReview),
            "merge/characters.json",
            result.Characters);
        TestArtifactWriter.WriteJson(
            nameof(DeterministicMergerQueuesLowConfidenceClaimForAutomaticReview),
            "merge/auto_review_queue.json",
            result.Conflicts);
    }

    [Fact]
    public async Task AutomaticReviewerAppliesSelectedClaimToFinalArtifact()
    {
        var pages = new[] { CreatePage("p0001", 1), CreatePage("p0002", 2) };
        var assets = new RecordingPageAssetProvider();
        var merge = CreateConflictedMerge(pages, assets);
        var provider = new QueueModelProvider(
            """
            {
              "decision": "resolved",
              "selectedClaimIndex": 1,
              "confidence": 0.97,
              "evidence": "The second title is visibly printed on the chapter page.",
              "conservativeStrategy": ""
            }
            """);
        var request = CreateRequest(
            pages,
            assets,
            new PrecheckOptions { MaxAutomaticReviewAttempts = 2 });

        var result = await new ModelBackedAutomaticReviewProcessor(provider)
            .ReviewAsync(request, merge);

        Assert.Single(result.ResolvedConflicts);
        Assert.Empty(result.UnresolvedConflicts);
        Assert.Equal("resolved", Assert.Single(result.Items).Status);
        Assert.Equal("resolved", Assert.Single(result.FinalMerge.Conflicts).Status);
        var chapter = result.FinalMerge.Chapters.GetProperty("chapters")[0];
        Assert.Equal("Episode One", chapter.GetProperty("value").GetString());
        Assert.Equal("confirmed", chapter.GetProperty("status").GetString());
        Assert.All(assets.Requests, requestItem => Assert.Equal(ImageQuality.Original, requestItem.Quality));

        TestArtifactWriter.WriteJson(
            nameof(AutomaticReviewerAppliesSelectedClaimToFinalArtifact),
            "review/auto_review.json",
            result);
        TestArtifactWriter.WriteJsonElement(
            nameof(AutomaticReviewerAppliesSelectedClaimToFinalArtifact),
            "review/final_chapters.json",
            result.FinalMerge.Chapters);
    }

    [Fact]
    public async Task AutomaticReviewerFallsBackToUnresolvedAutoAfterAttemptBudget()
    {
        var pages = new[] { CreatePage("p0001", 1), CreatePage("p0002", 2) };
        var assets = new RecordingPageAssetProvider();
        var merge = CreateConflictedMerge(pages, assets);
        var unresolvedResponse =
            """
            {
              "decision": "unresolved_auto",
              "selectedClaimIndex": null,
              "confidence": 0.42,
              "evidence": "The lettering is too ambiguous to choose safely.",
              "conservativeStrategy": "Keep both chapter-title candidates and avoid a hard boundary."
            }
            """;
        var provider = new QueueModelProvider(unresolvedResponse, unresolvedResponse);
        var request = CreateRequest(
            pages,
            assets,
            new PrecheckOptions { MaxAutomaticReviewAttempts = 2 });

        var result = await new ModelBackedAutomaticReviewProcessor(provider)
            .ReviewAsync(request, merge);

        Assert.Empty(result.ResolvedConflicts);
        Assert.Equal("unresolved_auto", Assert.Single(result.UnresolvedConflicts).Status);
        var item = Assert.Single(result.Items);
        Assert.Equal(2, item.Attempts);
        Assert.Equal("unresolved_auto", item.Status);
        var chapter = result.FinalMerge.Chapters.GetProperty("chapters")[0];
        Assert.Equal("unresolved_auto", chapter.GetProperty("status").GetString());
        Assert.Contains("Keep both", chapter.GetProperty("conservativeStrategy").GetString(), StringComparison.Ordinal);

        TestArtifactWriter.WriteJson(
            nameof(AutomaticReviewerFallsBackToUnresolvedAutoAfterAttemptBudget),
            "review/auto_review.json",
            result);
        TestArtifactWriter.WriteJsonElement(
            nameof(AutomaticReviewerFallsBackToUnresolvedAutoAfterAttemptBudget),
            "review/final_chapters.json",
            result.FinalMerge.Chapters);
    }

    [Fact]
    public async Task GlobalSemanticConsolidatorRestoresPersistedArtifactsWithoutModelCalls()
    {
        var pages = new[] { CreatePage("p0001", 1) };
        var assets = new RecordingPageAssetProvider();
        var request = CreateRequest(
            pages,
            assets,
            new PrecheckOptions { RunId = "run-semantic-resume" });
        var store = new InMemorySemanticArtifactStore();
        store.Set("chapters", "[]");
        store.Set("characters", "[]");
        store.Set("cultural_localizations", "[]");
        store.Set("glossary", "[]");
        store.Set("story_bible", "{}");
        var provider = new QueueModelProvider();

        var result = await new ModelBackedGlobalSemanticConsolidator(
                provider,
                artifactStore: store)
            .ConsolidateAsync(
                request,
                CreateThumbnail(pages),
                new BatchPlanDocument("1.0.0", []),
                [],
                new AutoReviewResult(
                    new PrecheckMergeResult(
                        Json("{}"),
                        Json("{}"),
                        Json("{}"),
                        Json("{}"),
                        Json("{}"),
                        Json("{}"),
                        []),
                    [],
                    [],
                    []));

        Assert.Empty(provider.Requests);
        Assert.Equal("zh-Hans", result.TargetLanguage);
        Assert.Equal(JsonValueKind.Array, result.Chapters.ValueKind);
        Assert.Equal(JsonValueKind.Object, result.StoryBible.ValueKind);
        TestArtifactWriter.WriteJson(
            nameof(GlobalSemanticConsolidatorRestoresPersistedArtifactsWithoutModelCalls),
            "semantic-resume/result.json",
            result);
    }

    private static PrecheckMergeResult CreateConflictedMerge(
        IReadOnlyList<PageDescriptor> pages,
        IPageAssetProvider assets)
    {
        var request = CreateRequest(pages, assets);
        return new DeterministicPrecheckMerger().Merge(
            request,
            CreateThumbnail(pages),
            new BatchPlanDocument("1.0.0", []),
            [
                CreateObservation(
                    "batch_001",
                    """
                    {
                      "chapters": [{"operation":"upsert","mergeKey":"chapter:1","candidateId":"chapter_1","value":"Chapter One","pages":["p0001"],"evidence":"title","confidence":0.80}],
                      "characters": [], "culturalLocalizations": [], "glossary": [], "storyBible": []
                    }
                    """),
                CreateObservation(
                    "batch_002",
                    """
                    {
                      "chapters": [{"operation":"upsert","mergeKey":"chapter:1","candidateId":"chapter_1","value":"Episode One","pages":["p0002"],"evidence":"alternate title","confidence":0.85}],
                      "characters": [], "culturalLocalizations": [], "glossary": [], "storyBible": []
                    }
                    """)
            ]);
    }

    private static BatchObservationDocument CreateObservation(string batchId, string artifactDeltas)
    {
        return new BatchObservationDocument(
            "1.0.0",
            batchId,
            [],
            Json("[]"),
            Json("[]"),
            Json(artifactDeltas),
            Json("[]"),
            Json("[]"),
            []);
    }

    private static ThumbnailPrecheckDocument CreateThumbnail(IReadOnlyList<PageDescriptor> pages)
    {
        return new ThumbnailPrecheckDocument(
            "1.0.0",
            new GlobalObservations("ja", "rtl", 0.95, []),
            pages.Select(page => new PageThumbnailObservation(
                page.PageId,
                "story",
                null,
                null,
                2,
                1,
                1,
                1,
                false,
                0.9,
                [])).ToArray(),
            []);
    }

    private static PageThumbnailObservation ThumbnailPage(
        string pageId,
        string pageType,
        string? sceneCandidateId)
    {
        return new PageThumbnailObservation(
            pageId,
            pageType,
            null,
            sceneCandidateId,
            2,
            1,
            1,
            1,
            false,
            0.9,
            []);
    }

    private static PrecheckRequest CreateRequest(
        IReadOnlyList<PageDescriptor> pages,
        IPageAssetProvider assets,
        PrecheckOptions? options = null)
    {
        return new PrecheckRequest(
            "book-test",
            pages,
            assets,
            options ?? new PrecheckOptions { RunId = "run-test" });
    }

    private static PageDescriptor CreatePage(string pageId, int sourceIndex)
    {
        return new PageDescriptor(
            pageId,
            sourceIndex,
            $"page_{sourceIndex:0000}.png",
            1000,
            1400,
            "image/png",
            $"hash-{sourceIndex}");
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void WriteModelRequestArtifacts(
        string testName,
        ModelRequest request,
        string prefix)
    {
        foreach (var message in request.Messages)
        {
            var text = string.Join(
                Environment.NewLine + Environment.NewLine,
                message.Content.OfType<TextContent>().Select(content => content.Text));
            TestArtifactWriter.WriteText(
                testName,
                $"requests/{prefix}_{message.Role.ToString().ToLowerInvariant()}_prompt.txt",
                text);
        }

        if (request.OutputFormat is JsonSchemaOutputFormat schema)
        {
            TestArtifactWriter.WriteJsonElement(
                testName,
                $"schemas/{prefix}_{schema.Name}.schema.json",
                schema.Schema);
        }
    }

    private sealed class StubImageComposer : IPrecheckImageComposer
    {
        public Task<IReadOnlyList<GeneratedImage>> CreateContactSheetsAsync(
            PrecheckRequest request,
            IReadOnlyList<PageDescriptor> pages,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<GeneratedImage>>(
                [new GeneratedImage("contact", "image/png", OnePixelPng, 1, 1)]);
        }

        public Task<IReadOnlyList<GeneratedImage>> CreateAdjacencySheetsAsync(
            PrecheckRequest request,
            IReadOnlyList<PageDescriptor> pages,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<GeneratedImage>>(
                [new GeneratedImage("adjacency", "image/png", OnePixelPng, 1, 1)]);
        }
    }

    private sealed class RecordingPageAssetProvider : IPageAssetProvider
    {
        public List<(string PageId, ImageQuality Quality)> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> GetPageAsync(
            string pageId,
            ImageQuality quality,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((pageId, quality));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(OnePixelPng);
        }
    }

    private sealed class QueueModelProvider(params string[] responses) : IModelProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public string ProviderId => "test";

        public ModelProviderCapabilities Capabilities { get; } = new(
            SupportsVision: true,
            SupportsMultipleImages: true,
            SupportsStructuredOutputs: true,
            SupportsStreaming: true,
            SupportsOriginalImageDetail: true,
            SupportsReasoningConfiguration: true);

        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var response = _responses.Dequeue();
            yield return new ResponseCreated($"response_{Requests.Count}");
            yield return new OutputTextDelta(response);
            yield return new ResponseCompleted(new ModelResponseMetadata(
                $"response_{Requests.Count}",
                TimeSpan.Zero));
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class InMemorySemanticArtifactStore : IGlobalSemanticArtifactStore
    {
        private readonly Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);

        public ValueTask<JsonElement?> TryLoadAsync(
            string runId,
            string artifactName,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<JsonElement?>(
                values.TryGetValue(artifactName, out var value) ? value.Clone() : null);
        }

        public ValueTask SaveAsync(
            string runId,
            string artifactName,
            JsonElement value,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            cancellationToken.ThrowIfCancellationRequested();
            values[artifactName] = value.Clone();
            return ValueTask.CompletedTask;
        }

        public void Set(string artifactName, string json)
        {
            values[artifactName] = Json(json);
        }
    }
}
