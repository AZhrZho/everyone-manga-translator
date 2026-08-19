using System.Text.Json.Nodes;
using MangaTranslator.Api.Jobs;

namespace MangaTranslator.Api.Tests;

public sealed class ManualLetteringOverridesTests
{
    [Fact]
    public void AppliesOnlyTheExplicitBasicFormatOverride()
    {
        var context = ManualLetteringOverrides.CreateContext(
            """
            {
              "segments": [{
                "segment_id": "s1",
                "translated_text": "手动文本",
                "user_overrides": {"translated_text": true, "basic_format": true},
                "layout": {
                  "text_box": {"x0": 10, "y0": 20, "x1": 110, "y1": 120},
                  "font_id": "F0001", "font_size_px": 32, "font_weight": 700,
                  "bold": true, "italic": false, "color": "#ffffff", "letter_spacing_px": 2,
                  "rotation": {"center_x_px": 50, "center_y_px": 50, "angle_deg": 8},
                  "advanced": {"line_adjustments": [], "character_adjustments": [], "ruby_annotations": [], "placement_groups": [{"line_indices": [0], "source_region_ids": ["r1"], "text_box": {"x0": 10, "y0": 20, "x1": 110, "y1": 120}}]}
                }
              }]
            }
            """,
            protectAllFallback: false);
        var generated = JsonNode.Parse(
            """
            {
              "segments": [{
                "segment_id": "s1",
                "translated_text": "AI 文本",
                "layout": {
                  "text_box": {"x0": 1, "y0": 2, "x1": 3, "y1": 4},
                  "font_id": "F0002", "font_size_px": 18, "font_weight": 400,
                  "bold": false, "italic": true, "color": "#000000", "letter_spacing_px": 0,
                  "rotation": {"center_x_px": 20, "center_y_px": 20, "angle_deg": 25},
                  "advanced": {"line_adjustments": [], "character_adjustments": [], "ruby_annotations": [], "placement_groups": []}
                }
              }]
            }
            """ )!.AsObject();

        var result = JsonNode.Parse(ManualLetteringOverrides.Apply(generated.ToJsonString(), context))!.AsObject();
        var segment = result["segments"]!.AsArray()[0]!.AsObject();
        var layout = segment["layout"]!.AsObject();

        Assert.Equal("手动文本", segment["translated_text"]!.GetValue<string>());
        Assert.Equal("F0001", layout["font_id"]!.GetValue<string>());
        Assert.Equal(32, layout["font_size_px"]!.GetValue<int>());
        Assert.Equal("#ffffff", layout["color"]!.GetValue<string>());
        Assert.Equal(1, layout["text_box"]!["x0"]!.GetValue<int>());
        Assert.Equal(25, layout["rotation"]!["angle_deg"]!.GetValue<int>());
        Assert.Empty(layout["advanced"]!["placement_groups"]!.AsArray());
    }

    [Fact]
    public void PreservesCreatedAndDeletedSegmentsDuringRerun()
    {
        var context = ManualLetteringOverrides.CreateContext(
            """
            {
              "segments": [{"segment_id":"new","translated_text":"新增","user_overrides":{"user_created":true},"layout":{"text_box":{"x0":0,"y0":0,"x1":20,"y1":20}}}],
              "user_deleted_segment_ids": ["deleted"]
            }
            """,
            protectAllFallback: false);
        var generated = JsonNode.Parse(
            """
            {"segments":[{"segment_id":"deleted","translated_text":"不要"},{"segment_id":"keep","translated_text":"保留"}]}
            """ )!.AsObject();

        var result = JsonNode.Parse(ManualLetteringOverrides.Apply(generated.ToJsonString(), context))!.AsObject();
        var segments = result["segments"]!.AsArray().OfType<JsonObject>().ToArray();

        Assert.DoesNotContain(segments, item => item["segment_id"]?.GetValue<string>() == "deleted");
        Assert.Contains(segments, item => item["segment_id"]?.GetValue<string>() == "keep");
        Assert.Contains(segments, item => item["segment_id"]?.GetValue<string>() == "new");
        Assert.Contains("deleted", result["user_deleted_segment_ids"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void ProtectAllFallbackKeepsLegacyCurrentLayout()
    {
        var context = ManualLetteringOverrides.CreateContext(
            """
            {"segments":[{"segment_id":"legacy","translated_text":"旧布局","layout":{"text_box":{"x0":0,"y0":0,"x1":30,"y1":30}}}]}
            """,
            protectAllFallback: true);
        var generated = """{"segments":[{"segment_id":"ai","translated_text":"新结果"}]}""";

        var result = JsonNode.Parse(ManualLetteringOverrides.Apply(generated, context))!.AsObject();

        Assert.True(context!["protect_all"]!.GetValue<bool>());
        Assert.Equal("legacy", result["segments"]!.AsArray()[0]!["segment_id"]!.GetValue<string>());
    }
}
