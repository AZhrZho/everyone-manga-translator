using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Dialogue;

internal static class DialogueSchemas
{
    public static JsonElement Result(
        IReadOnlyCollection<string> fontIds)
    {
        var fontEnum = new JsonArray();
        foreach (var id in fontIds.Order(StringComparer.Ordinal))
        {
            fontEnum.Add(id);
        }

        var regionId = String();
        var fontId = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = fontEnum
        };

        var textBox = StrictObject(
            ("x0", Integer(0)),
            ("y0", Integer(0)),
            ("x1", Integer(1)),
            ("y1", Integer(1)));

        var lineAdjustment = StrictObject(
            ("line_index", Integer(0)),
            ("offset_x_px", Number()),
            ("offset_y_px", Number()),
            ("font_size_px", NullableNumber()),
            ("color", NullableColor()));
        var characterAdjustment = StrictObject(
            ("character_index", Integer(0)),
            ("offset_x_px", Number()),
            ("offset_y_px", Number()),
            ("font_size_px", NullableNumber()),
            ("color", NullableColor()));
        var ruby = StrictObject(
            ("base_character_index", Integer(0)),
            ("base_character_length", Integer(1)),
            ("text", String()),
            ("placement", Enum("auto", "before", "after")),
            ("offset_x_px", Number()),
            ("offset_y_px", Number()),
            ("font_size_px", NullableNumber()),
            ("color", NullableColor()));
        var placementGroup = StrictObject(
            ("line_indices", NonEmptyPlacementArray(Integer(0))),
            ("source_region_ids", NonEmptyPlacementArray(regionId.DeepClone())),
            ("text_box", textBox.DeepClone()));
        var advanced = StrictObject(
            ("line_adjustments", Array(lineAdjustment)),
            ("character_adjustments", Array(characterAdjustment)),
            ("ruby_annotations", Array(ruby)),
            ("placement_groups", Array(placementGroup)));
        var rotation = StrictObject(
            ("center_x_px", Number()),
            ("center_y_px", Number()),
            ("angle_deg", Number()));
        var stroke = StrictObject(
            ("width_px", Number(0)),
            ("color", Color()),
            ("round_corners", Boolean()));

        var layout = StrictObject(
            ("text_box", textBox),
            ("orientation", Enum("horizontal", "vertical")),
            ("reading_direction", Enum("left_to_right", "right_to_left")),
            ("alignment", Enum("start", "center", "end", "justify")),
            ("font_id", fontId),
            ("font_size_px", Number(1)),
            ("font_weight", Integer(1, 1000)),
            ("italic", Boolean()),
            ("color", Color()),
            ("letter_spacing_px", Number()),
            ("advanced", NullableObject(advanced)),
            ("rotation", NullableObject(rotation)),
            ("stroke", NullableObject(stroke)));

        var segment = StrictObject(
            ("segment_id", String()),
            ("kind", Enum(
                "dialogue",
                "narration",
                "caption",
                "sign",
                "title",
                "sound_effect",
                "other")),
            ("status", Enum("translated", "unreadable")),
            ("reading_order", Integer(0)),
            ("source_region_ids", NonEmptyArray(regionId.DeepClone())),
            ("source_text", String()),
            ("translated_text", String()),
            ("layout", layout),
            ("speaker_id", NullableString()),
            ("confidence", Number(0, 1)),
            ("notes", Array(String())),
            ("needs_priority_review", Boolean()));

        var ignored = StrictObject(
            ("region_ids", new JsonObject
            {
                ["type"] = "array",
                ["items"] = regionId.DeepClone(),
                ["minItems"] = 1
            }),
            ("reason", String()),
            ("evidence", String()));

        var warning = StrictObject(
            ("code", String()),
            ("message", String()),
            ("segment_id", NullableString()),
            ("region_id", NullableString()));

        var root = StrictObject(
            ("segments", Array(segment)),
            ("ignored_regions", Array(ignored)),
            ("warnings", Array(warning)));
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    public static JsonElement BatchResult(IReadOnlyCollection<string> fontIds)
    {
        var pageResult = JsonNode.Parse(Result(fontIds).GetRawText())!.AsObject();
        var properties = pageResult["properties"]!.AsObject();
        properties["page_id"] = String();
        pageResult["required"]!.AsArray().Add("page_id");
        var root = StrictObject(("pages", Array(pageResult)));
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject StrictObject(params (string Name, JsonNode Schema)[] properties)
    {
        var propertySchemas = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            propertySchemas[name] = schema;
            required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = propertySchemas,
            ["required"] = required
        };
    }

    private static JsonObject String() => new() { ["type"] = "string" };

    private static JsonObject NullableString() => new()
    {
        ["type"] = new JsonArray("string", "null")
    };

    private static JsonObject Integer(int? minimum = null, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (minimum is not null)
        {
            schema["minimum"] = minimum.Value;
        }

        if (maximum is not null)
        {
            schema["maximum"] = maximum.Value;
        }

        return schema;
    }

    private static JsonObject Number(double? minimum = null, double? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (minimum is not null)
        {
            schema["minimum"] = minimum.Value;
        }

        if (maximum is not null)
        {
            schema["maximum"] = maximum.Value;
        }

        return schema;
    }

    private static JsonObject NullableNumber() => new()
    {
        ["type"] = new JsonArray("number", "null")
    };

    private static JsonObject Boolean() => new() { ["type"] = "boolean" };

    private static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(value => (JsonNode?)value).ToArray())
    };

    private static JsonObject Array(JsonNode item) => new()
    {
        ["type"] = "array",
        ["items"] = item
    };

    private static JsonObject NonEmptyArray(JsonNode item) => new()
    {
        ["type"] = "array",
        ["items"] = item,
        ["minItems"] = 1
    };

    private static JsonObject NonEmptyPlacementArray(JsonNode item) => new()
    {
        ["type"] = "array",
        ["items"] = item,
        // Strict structured-output providers do not consistently support the JSON Schema
        // uniqueItems keyword.  Uniqueness is enforced by the host validator so the wire schema
        // stays within the provider-compatible subset.
        ["minItems"] = 1
    };

    private static JsonObject NullableObject(JsonNode schema) => new()
    {
        ["type"] = new JsonArray("object", "null"),
        ["properties"] = schema["properties"]?.DeepClone(),
        ["required"] = schema["required"]?.DeepClone(),
        ["additionalProperties"] = false
    };

    private static JsonObject Color() => new()
    {
        ["type"] = "string",
        ["pattern"] = "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"
    };

    private static JsonObject NullableColor() => new()
    {
        ["type"] = new JsonArray("string", "null"),
        ["pattern"] = "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"
    };
}
