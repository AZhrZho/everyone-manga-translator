using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Precheck;

public static class MinimalPrecheckSchemas
{
    public static JsonElement Scan() => Serialize(StrictObject(
        ("pages", Array(StrictObject(
            ("pageId", String()),
            ("pageType", String()),
            ("confidence", Number()),
            ("evidence", Array(String()))))),
        ("contextDelta", StrictObject(
            ("batchSynopsis", String()),
            ("characters", Array(StrictObject(
                ("originalName", String()),
                ("translatedName", String()),
                ("aliases", Array(String())),
                ("role", String()),
                ("speechRules", Array(String())),
                ("relationships", Array(String())),
                ("firstSeenOrder", Integer()),
                ("evidencePages", Array(String()))))),
            ("glossary", Array(StrictObject(
                ("source", String()),
                ("translation", String()),
                ("category", String()),
                ("usageRule", String()),
                ("firstSeenOrder", Integer()),
                ("evidencePages", Array(String()))))),
            ("continuityFacts", Array(StrictObject(
                ("fact", String()),
                ("revealedAtOrder", Integer()),
                ("safeWordingBeforeReveal", String()),
                ("evidencePages", Array(String()))))),
            ("translationStyleSignals", Array(String())))),
        ("unresolvedQuestions", Array(String()))));

    public static JsonElement TranslationContext() => Serialize(StrictObject(
        ("synopsis", StrictObject(
            ("overall", String()),
            ("tone", String()),
            ("themes", Array(String())))),
        ("translationStyle", StrictObject(
            ("overallVoice", String()),
            ("dialogueRules", Array(String())),
            ("honorificPolicy", String()),
            ("namePolicy", String()),
            ("prohibitedTendencies", Array(String())))),
        ("characters", Array(StrictObject(
            ("characterId", String()),
            ("originalName", String()),
            ("translatedName", String()),
            ("aliases", Array(String())),
            ("role", String()),
            ("speechRules", Array(String())),
            ("relationships", Array(String())),
            ("validFromOrder", Integer()),
            ("validToOrder", NullableInteger()),
            ("revealSensitive", Boolean()),
            ("confidence", Number()),
            ("evidencePages", Array(String()))))),
        ("glossary", Array(StrictObject(
            ("source", String()),
            ("translation", String()),
            ("category", String()),
            ("usageRule", String()),
            ("validFromOrder", Integer()),
            ("validToOrder", NullableInteger()),
            ("revealSensitive", Boolean()),
            ("confidence", Number()),
            ("evidencePages", Array(String()))))),
        ("continuityFacts", Array(StrictObject(
            ("fact", String()),
            ("revealedAtOrder", Integer()),
            ("safeWordingBeforeReveal", String()),
            ("evidencePages", Array(String()))))),
        ("unresolvedQuestions", Array(String()))));

    private static JsonObject StrictObject(params (string Name, JsonNode Schema)[] properties)
    {
        var schemas = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            schemas[name] = schema;
            required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = schemas,
            ["required"] = required
        };
    }

    private static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject String() => new() { ["type"] = "string" };

    private static JsonObject Number() => new() { ["type"] = "number" };

    private static JsonObject Integer() => new() { ["type"] = "integer" };

    private static JsonObject Boolean() => new() { ["type"] = "boolean" };

    private static JsonObject NullableInteger() => new() { ["type"] = new JsonArray("integer", "null") };

    private static JsonElement Serialize(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
