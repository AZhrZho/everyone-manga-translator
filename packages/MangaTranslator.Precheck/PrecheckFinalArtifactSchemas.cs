using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Precheck;

internal static class PrecheckFinalArtifactSchemas
{
    public static JsonElement Chapters() => Serialize(ArtifactArray(Chapter()));

    public static JsonElement Characters() => Serialize(ArtifactArray(Character()));

    public static JsonElement CulturalLocalizations() =>
        Serialize(ArtifactArray(CulturalLocalization()));

    public static JsonElement Glossary() => Serialize(ArtifactArray(GlossaryItem()));

    public static JsonElement StoryBibleCore() => Serialize(StrictObject(
        ("schema_version", String()),
        ("book_id", String()),
        ("title_original", String()),
        ("title_translation", String()),
        ("target", String()),
        ("genre", StringArray()),
        ("setting", String()),
        ("worldview", StringArray()),
        ("themes", StringArray()),
        ("overall_synopsis", String()),
        ("tone_arc", String()),
        ("timeline", StringArray()),
        ("spoiler_policy", String()),
        ("reveal_sensitive_facts", Array(RevealSensitiveFact())),
        ("style_guide", StyleGuide()),
        ("typography", Typography()),
        ("unresolved_questions", StringArray())));

    private static JsonObject ArtifactArray(JsonObject item) => StrictObject(
        ("items", Array(item)));

    private static JsonObject Chapter() => StrictObject(
        ("chapter_id", String()),
        ("title", String()),
        ("page_start", String()),
        ("page_end", String()),
        ("summary", String()),
        ("key_events", StringArray()),
        ("character_changes", StringArray()),
        ("translation_notes", StringArray()));

    private static JsonObject Character() => StrictObject(
        ("character_id", String()),
        ("original_name", String()),
        ("reading", String()),
        ("translated_name", String()),
        ("name_status", String()),
        ("valid_from_order", Integer()),
        ("valid_to_order", NullableInteger()),
        ("reveal_sensitive", Boolean()),
        ("aliases_original", StringArray()),
        ("aliases_translated", StringArray()),
        ("role", String()),
        ("traits", StringArray()),
        ("background", String()),
        ("story_function", String()),
        ("name_meaning", String()),
        ("name_reference_or_pun", String()),
        ("candidates", Array(CharacterNameCandidate())),
        ("selection_rationale", String()),
        ("speech_profile", SpeechProfile()),
        ("voice_states", Array(VoiceState())),
        ("relationships", Array(CharacterRelationship())),
        ("relationship_notes", StringArray()),
        ("evidence", Array(Evidence())),
        ("confidence", Number()),
        ("locked", Boolean()),
        ("needs_human_review", Boolean()));

    private static JsonObject CharacterNameCandidate() => StrictObject(
        ("translation", String()),
        ("strategy", String()),
        ("rationale", String()),
        ("strengths", StringArray()),
        ("risks", StringArray()));

    private static JsonObject SpeechProfile() => StrictObject(
        ("self_reference", String()),
        ("address_patterns", StringArray()),
        ("politeness", String()),
        ("language_register", String()),
        ("rhythm", String()),
        ("sentence_endings", StringArray()),
        ("catchphrases", StringArray()),
        ("dialect_or_idiolect", String()),
        ("emotional_variation", String()),
        ("chinese_voice_rules", StringArray()),
        ("avoid", StringArray()));

    private static JsonObject VoiceState() => StrictObject(
        ("page_start", String()),
        ("page_end", String()),
        ("page_start_order", Integer()),
        ("page_end_order", Integer()),
        ("scene_or_trigger", String()),
        ("rules", StringArray()));

    private static JsonObject CharacterRelationship() => StrictObject(
        ("other_character_id", String()),
        ("relationship", String()),
        ("page_start", String()),
        ("page_end", String()),
        ("page_start_order", Integer()),
        ("page_end_order", Integer()),
        ("speaker_address_for_other", String()),
        ("other_address_for_speaker", String()),
        ("change_trigger", String()));

    private static JsonObject CulturalLocalization() => StrictObject(
        ("localization_id", String()),
        ("recurrence_key", String()),
        ("source_expression", String()),
        ("literal_meaning", String()),
        ("cultural_context", String()),
        ("strategy", String()),
        ("preferred_rendering", String()),
        ("usage_rule", String()),
        ("candidate_renderings", StringArray()),
        ("preserved_effect", String()),
        ("lost_effect", String()),
        ("compensation", String()),
        ("confidence", Number()),
        ("needs_human_review", Boolean()),
        ("evidence", Array(Evidence())));

    private static JsonObject GlossaryItem() => StrictObject(
        ("source", String()),
        ("translation", String()),
        ("category", String()),
        ("reading", String()),
        ("variants", StringArray()),
        ("rationale", String()),
        ("usage_rule", String()),
        ("valid_from_order", Integer()),
        ("valid_to_order", NullableInteger()),
        ("reveal_sensitive", Boolean()),
        ("avoid_translations", StringArray()),
        ("evidence", Array(Evidence())),
        ("locked", Boolean()),
        ("needs_human_review", Boolean()));

    private static JsonObject Evidence() => StrictObject(
        ("page_id", String()),
        ("region_id", String()),
        ("quote", String()));

    private static JsonObject RevealSensitiveFact() => StrictObject(
        ("fact", String()),
        ("revealed_at", String()),
        ("revealed_order", Integer()),
        ("safe_wording_before_reveal", String()),
        ("translation_risk", String()),
        ("evidence", Array(Evidence())));

    private static JsonObject StyleGuide() => StrictObject(
        ("overall_voice", String()),
        ("target_readership", String()),
        ("dialogue_rules", StringArray()),
        ("narration_rules", StringArray()),
        ("humor_rules", StringArray()),
        ("honorific_policy", String()),
        ("name_policy", String()),
        ("idiom_policy", String()),
        ("culture_policy", String()),
        ("punctuation_policy", String()),
        ("line_length_policy", String()),
        ("sfx_policy", String()),
        ("prohibited_tendencies", StringArray()));

    private static JsonObject Typography() => StrictObject(
        ("dialogue_style", String()),
        ("narration_style", String()),
        ("emphasis_style", String()),
        ("default_orientation_policy", String()),
        ("size_policy", String()),
        ("line_break_policy", String()),
        ("notes", StringArray()));

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

    private static JsonObject Integer() => new() { ["type"] = "integer" };

    private static JsonObject NullableInteger() => new()
    {
        ["type"] = new JsonArray("integer", "null")
    };

    private static JsonObject Number() => new() { ["type"] = "number" };

    private static JsonObject Boolean() => new() { ["type"] = "boolean" };

    private static JsonObject StringArray() => Array(String());

    private static JsonObject Array(JsonNode item) => new()
    {
        ["type"] = "array",
        ["items"] = item
    };

    private static JsonElement Serialize(JsonNode schema)
    {
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }
}
