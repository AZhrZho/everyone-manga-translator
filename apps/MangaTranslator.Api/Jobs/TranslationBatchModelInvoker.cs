using System.Text.Json;
using System.Text.Json.Nodes;
using MangaTranslator.Contracts;
using MangaTranslator.Dialogue;
using MangaTranslator.LocalDetection;
using MangaTranslator.ModelProvider.Abstractions;
using SkiaSharp;

namespace MangaTranslator.Api.Jobs;

internal static class TranslationBatchModelInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<IReadOnlyList<DialogueTranslationResult>> TranslateAsync(
        IModelProvider provider,
        IDialoguePromptProvider prompts,
        IReadOnlyList<DialogueTranslationRequest> requests,
        TranslationInputResolution resolution,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        var prepared = new List<PreparedPage>(requests.Count);
        foreach (var request in requests)
        {
            var image = await request.PageImages.GetOriginalPageAsync(
                request.Page.PageId,
                cancellationToken);
            var scaled = Resize(image, resolution);
            var originalPerInputX = request.Page.Width / (double)scaled.Width;
            var originalPerInputY = request.Page.Height / (double)scaled.Height;
            var inputPerOriginalX = scaled.Width / (double)request.Page.Width;
            var inputPerOriginalY = scaled.Height / (double)request.Page.Height;
            var scaledPage = request.Page with
            {
                Width = scaled.Width,
                Height = scaled.Height,
                MediaType = scaled.MediaType,
                DetectedTextRegions = request.Page.DetectedTextRegions
                    .Select(region => ScaleRegion(region, inputPerOriginalX, inputPerOriginalY))
                    .ToArray()
            };
            var scaledRequest = request with
            {
                Page = scaledPage,
                PageImages = new FixedPageImageProvider(scaled),
                PageScopedPrecheckGuidance = ScaleGuidance(
                    request.PageScopedPrecheckGuidance,
                    inputPerOriginalX,
                    inputPerOriginalY)
            };
            prepared.Add(new PreparedPage(
                scaledRequest,
                originalPerInputX,
                originalPerInputY));
        }

        var translator = new ModelBackedMultimodalDialogueBatchTranslator(provider, prompts);
        var translated = await translator.TranslateAsync(
            new DialogueTranslationBatchRequest(
                requests[0].RunId,
                requests[0].BookId,
                prepared.Select(item => item.Request).ToArray()),
            cancellationToken);
        return translated.Select(result =>
        {
            var page = prepared.Single(item => string.Equals(
                item.Request.Page.PageId,
                result.PageId,
                StringComparison.Ordinal));
            return ScaleResult(result, page.OriginalPerInputX, page.OriginalPerInputY);
        }).ToArray();
    }

    private static DialogueImageAsset Resize(
        DialogueImageAsset source,
        TranslationInputResolution resolution)
    {
        var divisor = resolution switch
        {
            TranslationInputResolution.Original => 1,
            TranslationInputResolution.Half => 2,
            TranslationInputResolution.Quarter => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null)
        };
        if (divisor == 1) return source;
        var width = Math.Max(1, (int)Math.Round(source.Width / (double)divisor));
        var height = Math.Max(1, (int)Math.Round(source.Height / (double)divisor));
        using var decoded = SKBitmap.Decode(source.Data.ToArray())
            ?? throw new InvalidDataException($"Unable to decode page image {source.AssetId} for batch resizing.");
        using var resized = decoded.Resize(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException($"Unable to resize page image {source.AssetId}.");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95)
            ?? throw new InvalidOperationException($"Unable to encode resized page image {source.AssetId}.");
        return new DialogueImageAsset(
            source.AssetId + $":{width}x{height}",
            "image/png",
            data.ToArray(),
            width,
            height);
    }

    private static DialogueDetectedRegion ScaleRegion(
        DialogueDetectedRegion source,
        double scaleX,
        double scaleY) => source with
        {
            Region = source.Region with
            {
                X0 = ScaleInt(source.Region.X0, scaleX),
                Y0 = ScaleInt(source.Region.Y0, scaleY),
                X1 = ScaleInt(source.Region.X1, scaleX),
                Y1 = ScaleInt(source.Region.Y1, scaleY),
                FontSize = Math.Max(1, ScaleInt(source.Region.FontSize, Mean(scaleX, scaleY))),
                Lines = source.Region.Lines.Select(line => line with
                {
                    Polygon = line.Polygon.Select(point => new ImagePoint(
                        ScaleInt(point.X, scaleX),
                        ScaleInt(point.Y, scaleY))).ToArray()
                }).ToArray()
            }
        };

    private static DialogueTranslationResult ScaleResult(
        DialogueTranslationResult result,
        double scaleX,
        double scaleY) => result with
        {
            Segments = result.Segments.Select(segment => segment with
            {
                Layout = ScaleLayout(segment.Layout, scaleX, scaleY)
            }).ToArray()
        };

    private static DialogueTextLayout ScaleLayout(
        DialogueTextLayout layout,
        double scaleX,
        double scaleY)
    {
        var fontScale = Mean(scaleX, scaleY);
        return layout with
        {
            TextBox = ScaleBox(layout.TextBox, scaleX, scaleY),
            FontSizePx = ScaleFloat(layout.FontSizePx, fontScale),
            LetterSpacingPx = ScaleFloat(layout.LetterSpacingPx, fontScale),
            Stroke = layout.Stroke is null ? null : layout.Stroke with
            {
                WidthPx = ScaleFloat(layout.Stroke.WidthPx, fontScale)
            },
            Rotation = layout.Rotation is null ? null : layout.Rotation with
            {
                CenterXPx = ScaleFloat(layout.Rotation.CenterXPx, scaleX),
                CenterYPx = ScaleFloat(layout.Rotation.CenterYPx, scaleY)
            },
            Advanced = layout.Advanced is null ? null : layout.Advanced with
            {
                PlacementGroups = layout.Advanced.PlacementGroups.Select(group => group with
                {
                    TextBox = ScaleBox(group.TextBox, scaleX, scaleY)
                }).ToArray(),
                LineAdjustments = layout.Advanced.LineAdjustments.Select(item => item with
                {
                    OffsetXPx = ScaleFloat(item.OffsetXPx, scaleX),
                    OffsetYPx = ScaleFloat(item.OffsetYPx, scaleY),
                    FontSizePx = item.FontSizePx is null ? null : ScaleFloat(item.FontSizePx.Value, fontScale)
                }).ToArray(),
                CharacterAdjustments = layout.Advanced.CharacterAdjustments.Select(item => item with
                {
                    OffsetXPx = ScaleFloat(item.OffsetXPx, scaleX),
                    OffsetYPx = ScaleFloat(item.OffsetYPx, scaleY),
                    FontSizePx = item.FontSizePx is null ? null : ScaleFloat(item.FontSizePx.Value, fontScale)
                }).ToArray(),
                RubyAnnotations = layout.Advanced.RubyAnnotations.Select(item => item with
                {
                    OffsetXPx = ScaleFloat(item.OffsetXPx, scaleX),
                    OffsetYPx = ScaleFloat(item.OffsetYPx, scaleY),
                    FontSizePx = item.FontSizePx is null ? null : ScaleFloat(item.FontSizePx.Value, fontScale)
                }).ToArray()
            }
        };
    }

    private static TextLayoutBox ScaleBox(TextLayoutBox box, double scaleX, double scaleY) => new(
        ScaleInt(box.X0, scaleX),
        ScaleInt(box.Y0, scaleY),
        ScaleInt(box.X1, scaleX),
        ScaleInt(box.Y1, scaleY));

    private static JsonElement ScaleGuidance(JsonElement guidance, double scaleX, double scaleY)
    {
        if (guidance.ValueKind == JsonValueKind.Undefined) return default;
        if (guidance.ValueKind != JsonValueKind.Object) return guidance.Clone();
        var root = JsonNode.Parse(guidance.GetRawText()) as JsonObject;
        var segments = root?["user_lettering_overrides"]?["segments"] as JsonArray;
        if (root is null || segments is null) return guidance.Clone();
        foreach (var segment in segments.OfType<JsonObject>())
        {
            if (segment["layout"] is JsonObject layout) ScaleLayoutJson(layout, scaleX, scaleY);
        }
        using var document = JsonDocument.Parse(root.ToJsonString(JsonOptions));
        return document.RootElement.Clone();
    }

    private static void ScaleLayoutJson(JsonObject layout, double scaleX, double scaleY)
    {
        var fontScale = Mean(scaleX, scaleY);
        ScaleBoxJson(layout["text_box"] as JsonObject, scaleX, scaleY);
        ScaleNumber(layout, "font_size_px", fontScale);
        ScaleNumber(layout, "letter_spacing_px", fontScale);
        if (layout["stroke"] is JsonObject stroke) ScaleNumber(stroke, "width_px", fontScale);
        if (layout["rotation"] is JsonObject rotation)
        {
            ScaleNumber(rotation, "center_x_px", scaleX);
            ScaleNumber(rotation, "center_y_px", scaleY);
        }
        if (layout["advanced"] is not JsonObject advanced) return;
        foreach (var group in (advanced["placement_groups"] as JsonArray)?.OfType<JsonObject>() ?? [])
            ScaleBoxJson(group["text_box"] as JsonObject, scaleX, scaleY);
        foreach (var item in (advanced["line_adjustments"] as JsonArray)?.OfType<JsonObject>() ?? [])
            ScaleAdjustmentJson(item, scaleX, scaleY, fontScale);
        foreach (var item in (advanced["character_adjustments"] as JsonArray)?.OfType<JsonObject>() ?? [])
            ScaleAdjustmentJson(item, scaleX, scaleY, fontScale);
        foreach (var item in (advanced["ruby_annotations"] as JsonArray)?.OfType<JsonObject>() ?? [])
            ScaleAdjustmentJson(item, scaleX, scaleY, fontScale);
    }

    private static void ScaleAdjustmentJson(JsonObject value, double scaleX, double scaleY, double fontScale)
    {
        ScaleNumber(value, "offset_x_px", scaleX);
        ScaleNumber(value, "offset_y_px", scaleY);
        ScaleNumber(value, "font_size_px", fontScale);
    }

    private static void ScaleBoxJson(JsonObject? box, double scaleX, double scaleY)
    {
        if (box is null) return;
        ScaleNumber(box, "x0", scaleX, integer: true);
        ScaleNumber(box, "y0", scaleY, integer: true);
        ScaleNumber(box, "x1", scaleX, integer: true);
        ScaleNumber(box, "y1", scaleY, integer: true);
    }

    private static void ScaleNumber(JsonObject value, string name, double scale, bool integer = false)
    {
        if (value[name] is not JsonValue number || !TryReadNumber(number, out var raw)) return;
        value[name] = integer ? ScaleInt(raw, scale) : ScaleFloat((float)raw, scale);
    }

    private static bool TryReadNumber(JsonValue value, out double result)
    {
        if (value.TryGetValue<double>(out result)) return true;
        if (value.TryGetValue<float>(out var single))
        {
            result = single;
            return true;
        }
        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }
        if (value.TryGetValue<long>(out var largeInteger))
        {
            result = largeInteger;
            return true;
        }
        result = 0;
        return false;
    }

    private static int ScaleInt(double value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private static float ScaleFloat(float value, double scale) =>
        (float)(value * scale);

    private static double Mean(double x, double y) => (x + y) / 2d;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new DialogueSnakeCaseEnumConverter());
        return options;
    }

    private sealed record PreparedPage(
        DialogueTranslationRequest Request,
        double OriginalPerInputX,
        double OriginalPerInputY);

    private sealed class FixedPageImageProvider(DialogueImageAsset image) : IPageImageProvider
    {
        public ValueTask<DialogueImageAsset> GetOriginalPageAsync(
            string pageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(image);
        }
    }
}
