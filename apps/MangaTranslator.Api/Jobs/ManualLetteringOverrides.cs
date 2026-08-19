using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Api.Jobs;

public static class ManualLetteringOverrides
{
    private static readonly string[] BasicFormatFields =
    [
        "font_id",
        "font_size_px",
        "font_weight",
        "italic",
        "color",
        "letter_spacing_px",
        "stroke"
    ];

    private static readonly string[] FlowFields =
    [
        "orientation",
        "reading_direction",
        "alignment"
    ];

    public static JsonObject? CreateContext(string? currentLayoutJson, bool protectAllFallback)
    {
        if (string.IsNullOrWhiteSpace(currentLayoutJson)) return null;
        var root = JsonNode.Parse(currentLayoutJson) as JsonObject;
        var segments = root?["segments"] as JsonArray;
        if (root is null || segments is null) return null;

        var protectedSegments = new JsonArray();
        var hasExplicitOverrides = false;
        foreach (var segment in segments.OfType<JsonObject>())
        {
            if (HasExplicitOverride(segment))
            {
                hasExplicitOverrides = true;
                protectedSegments.Add(segment.DeepClone());
            }
        }

        var deleted = root["user_deleted_segment_ids"] as JsonArray;
        var hasDeletedSegments = deleted is { Count: > 0 };
        var protectAll = protectAllFallback && !hasExplicitOverrides && !hasDeletedSegments;
        if (protectAll)
        {
            protectedSegments.Clear();
            foreach (var segment in segments)
            {
                if (segment is not null) protectedSegments.Add(segment.DeepClone());
            }
        }

        if (!protectAll && protectedSegments.Count == 0 && !hasDeletedSegments) return null;
        return new JsonObject
        {
            ["schema_version"] = "1.0.0",
            ["protect_all"] = protectAll,
            ["segments"] = protectedSegments,
            ["deleted_segment_ids"] = deleted?.DeepClone() ?? new JsonArray()
        };
    }

    public static JsonObject? ReadContext(JsonElement guidance)
    {
        if (guidance.ValueKind != JsonValueKind.Object
            || !TryGetProperty(guidance, "user_lettering_overrides", out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonNode.Parse(value.GetRawText()) as JsonObject;
    }

    public static string Apply(string generatedLayoutJson, JsonObject? context)
    {
        if (context is null) return generatedLayoutJson;
        var generated = JsonNode.Parse(generatedLayoutJson) as JsonObject
            ?? throw new InvalidDataException("AI 嵌字布局不是 JSON 对象。");
        var generatedSegments = generated["segments"] as JsonArray ?? new JsonArray();
        generated["segments"] = generatedSegments;
        var protectedSegments = context["segments"] as JsonArray ?? new JsonArray();
        var deletedIds = (context["deleted_segment_ids"] as JsonArray)?.OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var id) ? id : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? [];

        for (var index = generatedSegments.Count - 1; index >= 0; index--)
        {
            if (generatedSegments[index] is JsonObject segment
                && deletedIds.Contains(ReadString(segment, "segment_id")))
            {
                generatedSegments.RemoveAt(index);
            }
        }

        var protectAll = context["protect_all"]?.GetValue<bool>() == true;
        if (protectAll)
        {
            generated["segments"] = protectedSegments.DeepClone();
        }
        else
        {
            foreach (var protectedSegment in protectedSegments.OfType<JsonObject>())
            {
                MergeSegment(generatedSegments, protectedSegment);
            }
        }

        generated["user_deleted_segment_ids"] = new JsonArray(
            deletedIds.OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => JsonValue.Create(id))
                .ToArray());
        return generated.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
    }

    private static void MergeSegment(JsonArray generatedSegments, JsonObject protectedSegment)
    {
        var segmentId = ReadString(protectedSegment, "segment_id");
        if (string.IsNullOrWhiteSpace(segmentId)) return;
        var overrides = protectedSegment["user_overrides"] as JsonObject;
        var generatedSegment = generatedSegments.OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(ReadString(item, "segment_id"), segmentId, StringComparison.Ordinal));
        var userCreated = ReadBoolean(overrides, "user_created");
        if (generatedSegment is null || userCreated)
        {
            var replacement = protectedSegment.DeepClone();
            if (generatedSegment is null) generatedSegments.Add(replacement);
            else generatedSegments[generatedSegments.IndexOf(generatedSegment)] = replacement;
            return;
        }

        if (ReadBoolean(overrides, "translated_text"))
        {
            CopyProperty(generatedSegment, protectedSegment, "translated_text");
        }

        var generatedLayout = generatedSegment["layout"] as JsonObject ?? new JsonObject();
        var protectedLayout = protectedSegment["layout"] as JsonObject;
        generatedSegment["layout"] = generatedLayout;
        if (protectedLayout is not null)
        {
            if (ReadBoolean(overrides, "text_box")) CopyProperty(generatedLayout, protectedLayout, "text_box");
            if (ReadBoolean(overrides, "rotation")) CopyProperty(generatedLayout, protectedLayout, "rotation");
            if (ReadBoolean(overrides, "advanced_layout")) CopyProperty(generatedLayout, protectedLayout, "advanced");
            if (ReadBoolean(overrides, "basic_format")) CopyProperties(generatedLayout, protectedLayout, BasicFormatFields);
            if (ReadBoolean(overrides, "flow")) CopyProperties(generatedLayout, protectedLayout, FlowFields);
        }

        if (overrides is not null) generatedSegment["user_overrides"] = overrides.DeepClone();
    }

    private static bool HasExplicitOverride(JsonObject segment)
    {
        if (segment["user_overrides"] is not JsonObject overrides) return false;
        return overrides.Any(property => property.Value is JsonValue value
                                         && value.TryGetValue<bool>(out var enabled)
                                         && enabled);
    }

    private static void CopyProperties(JsonObject target, JsonObject source, IEnumerable<string> names)
    {
        foreach (var name in names) CopyProperty(target, source, name);
    }

    private static void CopyProperty(JsonObject target, JsonObject source, string name)
    {
        if (source.TryGetPropertyValue(name, out var value)) target[name] = value?.DeepClone();
    }

    private static string? ReadString(JsonObject value, string name) =>
        value[name] is JsonValue property && property.TryGetValue<string>(out var result) ? result : null;

    private static bool ReadBoolean(JsonObject? value, string name) =>
        value?[name] is JsonValue property
        && property.TryGetValue<bool>(out var result)
        && result;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
