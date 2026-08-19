using System.Text.Json;
using MangaTranslator.Api.Jobs;
using MangaTranslator.Contracts;
using MangaTranslator.Dialogue;
using MangaTranslator.LocalDetection;
using MangaTranslator.ModelProvider.Abstractions;
using MangaTranslator.Precheck;
using SkiaSharp;

namespace MangaTranslator.Api.Tests;

public sealed class TranslationBatchModelInvokerTests
{
    private static readonly string[] RegionIds = ["page-1:region:0001"];
    [Theory]
    [InlineData(TranslationInputResolution.Original, 80, 60)]
    [InlineData(TranslationInputResolution.Half, 40, 30)]
    [InlineData(TranslationInputResolution.Quarter, 20, 15)]
    public async Task ResizesInputsAndRestoresEveryLayoutPixelField(
        TranslationInputResolution resolution,
        int expectedWidth,
        int expectedHeight)
    {
        var provider = new RecordingProvider(request => ResponseJson(expectedWidth, expectedHeight));
        var request = CreateRequest();

        var result = Assert.Single(await TranslationBatchModelInvoker.TranslateAsync(
            provider,
            ExpertDialoguePromptProvider.Instance,
            [request],
            resolution,
            CancellationToken.None));

        var modelRequest = Assert.Single(provider.Requests);
        var pageImage = Assert.Single(modelRequest.Messages
            .Single(item => item.Role == ModelRole.User)
            .Content.OfType<ImageContent>()
            .Where(item => item.Data.Length > 0)
            .Take(1));
        using var bitmap = SKBitmap.Decode(pageImage.Data.ToArray());
        Assert.Equal(expectedWidth, bitmap.Width);
        Assert.Equal(expectedHeight, bitmap.Height);

        var layout = Assert.Single(result.Segments).Layout;
        Assert.Equal(new TextLayoutBox(8, 8, 72, 56), layout.TextBox);
        Assert.Equal(4, layout.FontSizePx);
        Assert.Equal(4, layout.LetterSpacingPx);
        Assert.Equal(2, layout.Stroke?.WidthPx);
        Assert.Equal(16, layout.Rotation?.CenterXPx);
        Assert.Equal(10, layout.Rotation?.CenterYPx);
        var advanced = Assert.IsType<DialogueAdvancedLayout>(layout.Advanced);
        Assert.Empty(advanced.PlacementGroups);
        Assert.Equal(1, Assert.Single(advanced.LineAdjustments).OffsetXPx);
        Assert.Equal(1, Assert.Single(advanced.LineAdjustments).OffsetYPx);
        Assert.Equal(3, Assert.Single(advanced.LineAdjustments).FontSizePx);
        Assert.Equal(2, Assert.Single(advanced.CharacterAdjustments).OffsetXPx);
        Assert.Equal(2, Assert.Single(advanced.CharacterAdjustments).OffsetYPx);
        Assert.Equal(3.5f, Assert.Single(advanced.CharacterAdjustments).FontSizePx);
        Assert.Equal(3, Assert.Single(advanced.RubyAnnotations).OffsetXPx);
        Assert.Equal(3, Assert.Single(advanced.RubyAnnotations).OffsetYPx);
        Assert.Equal(2, Assert.Single(advanced.RubyAnnotations).FontSizePx);
    }

    private static DialogueTranslationRequest CreateRequest()
    {
        var image = CreatePng(80, 60);
        using var arrays = JsonDocument.Parse("[]");
        using var objects = JsonDocument.Parse("{}");
        var knowledge = new GlobalSemanticMergeResult(
            "zh-Hans",
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            arrays.RootElement.Clone(),
            objects.RootElement.Clone());
        return new DialogueTranslationRequest(
            "run-page-1",
            "book-1",
            new DialoguePageInput(
                "page-1", 1, null, "page-1", "story", true,
                80, 60, "image/png",
                [new DialogueDetectedRegion(
                    "page-1:region:0001", 0,
                    new DetectedTextRegion(
                        8, 12, 40, 48,
                        DetectedTextLanguage.Japanese,
                        DetectedTextOrientation.Horizontal,
                        20, .95f, []))]),
            knowledge,
            new FixedImages(image),
            new FixedImages(image),
            new DialogueTranslationOptions
            {
                Model = "vision-model",
                TargetLanguage = "zh-Hans",
                MaxModelAttempts = 1,
                RetryBaseDelay = TimeSpan.Zero
            })
        {
            BookPageIndex = [new DialogueBookPageIndexEntry("page-1", 1)]
        };
    }

    private static string ResponseJson(int width, int height)
    {
        var box = new { x0 = width / 10, y0 = height * 2 / 15, x1 = width * 9 / 10, y1 = height * 14 / 15 };
        return JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new
                {
                    page_id = "page-1",
                    segments = new[]
                    {
                        new
                        {
                            segment_id = "seg-1",
                            kind = "dialogue",
                            status = "translated",
                            reading_order = 0,
                            source_region_ids = RegionIds,
                            source_text = "a",
                            translated_text = "甲\n乙",
                            layout = new
                            {
                                text_box = box,
                                orientation = "horizontal",
                                reading_direction = "left_to_right",
                                alignment = "center",
                                font_id = "font-1",
                                font_size_px = width / 20d,
                                font_weight = 400,
                                italic = false,
                                color = "#000000",
                                letter_spacing_px = width / 20d,
                                advanced = new
                                {
                                    line_adjustments = new[] { new { line_index = 0, offset_x_px = width / 80d, offset_y_px = height / 60d, font_size_px = width * 3 / 80d, color = (string?)null } },
                                    character_adjustments = new[] { new { character_index = 0, offset_x_px = width / 40d, offset_y_px = height / 30d, font_size_px = width * 7 / 160d, color = (string?)null } },
                                    ruby_annotations = new[] { new { base_character_index = 0, base_character_length = 1, text = "ruby", placement = "auto", offset_x_px = width * 3 / 80d, offset_y_px = height / 20d, font_size_px = width / 40d, color = (string?)null } },
                                    placement_groups = Array.Empty<object>()
                                },
                                rotation = new { center_x_px = width / 5d, center_y_px = height / 6d, angle_deg = 12d },
                                stroke = new { width_px = width / 40d, color = "#FFFFFF", round_corners = true }
                            },
                            speaker_id = (string?)null,
                            confidence = .99,
                            notes = Array.Empty<string>(),
                            needs_priority_review = false
                        }
                    },
                    ignored_regions = Array.Empty<object>(),
                    warnings = Array.Empty<object>()
                }
            }
        });
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class FixedImages(byte[] pageImage) : IDialogueImageProvider
    {
        public ValueTask<DialogueImageAsset> GetOriginalPageAsync(string pageId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DialogueImageAsset("page", "image/png", pageImage, 80, 60));

        public ValueTask<FontReferenceSnapshot> GetFontReferenceImageAsync(FontReferenceImageRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FontReferenceSnapshot(
                "fonts", 1,
                new DialogueImageAsset("fonts", "image/png", CreatePng(1, 1), 1, 1),
                [new FontReferenceFont("font-1", "Font", "Font", null, [], [new FontReferenceStyle("regular", "native", "regular", "Regular", 0, [])])]));
    }

    private sealed class RecordingProvider(Func<ModelRequest, string> response) : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = [];
        public string ProviderId => "recording";
        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new ResponseCreated("response-1");
            yield return new OutputTextDelta(response(request));
            yield return new ResponseCompleted(new ModelResponseMetadata("response-1", TimeSpan.Zero));
            await Task.CompletedTask;
        }
    }
}
