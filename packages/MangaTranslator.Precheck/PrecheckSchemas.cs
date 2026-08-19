using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaTranslator.Precheck;

internal static class PrecheckSchemas
{
    public static JsonElement ThumbnailChunk()
    {
        return Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "global": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "primaryLanguage": { "type": ["string", "null"] },
                "readingDirection": {
                  "type": ["string", "null"],
                  "enum": ["right_to_left", "left_to_right", "top_to_bottom", "unknown", null]
                },
                "readingDirectionConfidence": { "type": "number" },
                "uncertainties": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["primaryLanguage", "readingDirection", "readingDirectionConfidence", "uncertainties"]
            },
            "pages": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "pageId": { "type": "string" },
                  "pageType": {
                    "type": "string",
                    "enum": ["front_cover", "back_cover", "inside_cover", "title_page", "copyright", "table_of_contents", "chapter_title", "story", "omake", "afterword", "character_profile", "advertisement", "blank", "unknown"]
                  },
                  "chapterCandidateId": { "type": ["string", "null"] },
                  "sceneCandidateId": { "type": ["string", "null"] },
                  "layoutComplexity": { "type": "integer" },
                  "textRiskScore": { "type": "integer" },
                  "maskRiskScore": { "type": "integer" },
                  "uncertaintyScore": { "type": "integer" },
                  "needsHighResolutionReview": { "type": "boolean" },
                  "confidence": { "type": "number" },
                  "evidence": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["pageId", "pageType", "chapterCandidateId", "sceneCandidateId", "layoutComplexity", "textRiskScore", "maskRiskScore", "uncertaintyScore", "needsHighResolutionReview", "confidence", "evidence"]
              }
            },
            "relations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "relationId": { "type": "string" },
                  "type": {
                    "type": "string",
                    "enum": ["spread", "background_continuation", "action_continuation", "dialogue_continuation", "sentence_continuation", "character_continuation", "scene_continuation", "scene_transition", "chapter_boundary", "front_matter_sequence", "back_matter_sequence", "advertisement_group", "adjacent_paratext", "possible_duplicate", "page_order_anomaly", "unknown"]
                  },
                  "pages": { "type": "array", "items": { "type": "string" } },
                  "hardConstraint": { "type": "boolean" },
                  "mustSameBatch": { "type": "boolean" },
                  "requiresOcrCoordination": { "type": "boolean" },
                  "requiresMaskCoordination": { "type": "boolean" },
                  "confidence": { "type": "number" },
                  "evidence": { "type": "string" },
                  "status": { "type": "string", "enum": ["candidate", "confirmed", "uncertain"] }
                },
                "required": ["relationId", "type", "pages", "hardConstraint", "mustSameBatch", "requiresOcrCoordination", "requiresMaskCoordination", "confidence", "evidence", "status"]
              }
            }
          },
          "required": ["global", "pages", "relations"]
        }
        """);
    }

    public static JsonElement BatchAnalysis()
    {
        var schema = Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "pageObservations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "pageId": { "type": "string" },
                  "pageType": {
                    "type": "string",
                    "enum": ["front_cover", "back_cover", "inside_cover", "title_page", "copyright", "table_of_contents", "chapter_title", "story", "omake", "afterword", "character_profile", "advertisement", "blank", "unknown"]
                  },
                  "textContent": {
                    "type": "string",
                    "enum": ["contains_non_sfx_text", "sfx_only", "no_text", "unknown"]
                  },
                  "sceneBoundaryBefore": { "type": "boolean" },
                  "facts": { "type": "array", "items": { "type": "string" } },
                  "ocrRisks": { "type": "array", "items": { "type": "string" } },
                  "maskRisks": { "type": "array", "items": { "type": "string" } },
                  "confidence": { "type": "number" },
                  "evidence": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["pageId", "pageType", "textContent", "sceneBoundaryBefore", "facts", "ocrRisks", "maskRisks", "confidence", "evidence"]
              }
            },
            "relationVerifications": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "relationId": { "type": "string" },
                  "decision": { "type": "string", "enum": ["supported", "rejected", "uncertain"] },
                  "confidence": { "type": "number" },
                  "evidence": { "type": "string" }
                },
                "required": ["relationId", "decision", "confidence", "evidence"]
              }
            },
            "artifactDeltas": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "chapters": { "type": "array", "items": { "$ref": "#/$defs/artifactDelta" } },
                "characters": { "type": "array", "items": { "$ref": "#/$defs/artifactDelta" } },
                "culturalLocalizations": { "type": "array", "items": { "$ref": "#/$defs/artifactDelta" } },
                "glossary": { "type": "array", "items": { "$ref": "#/$defs/artifactDelta" } },
                "storyBible": { "type": "array", "items": { "$ref": "#/$defs/artifactDelta" } }
              },
              "required": ["chapters", "characters", "culturalLocalizations", "glossary", "storyBible"]
            },
            "processingHints": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "pageId": { "type": "string" },
                  "priority": { "type": "string", "enum": ["low", "normal", "high", "critical"] },
                  "ocrHints": { "type": "array", "items": { "type": "string" } },
                  "maskHints": { "type": "array", "items": { "type": "string" } },
                  "translationHints": { "type": "array", "items": { "type": "string" } },
                  "reviewRequiredAfterLocalProcessing": { "type": "boolean" }
                },
                "required": ["pageId", "priority", "ocrHints", "maskHints", "translationHints", "reviewRequiredAfterLocalProcessing"]
              }
            },
            "uncertainties": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "type": { "type": "string" },
                  "pages": { "type": "array", "items": { "type": "string" } },
                  "question": { "type": "string" },
                  "severity": { "type": "string", "enum": ["low", "medium", "high", "critical"] },
                  "confidence": { "type": "number" }
                },
                "required": ["type", "pages", "question", "severity", "confidence"]
              }
            },
            "warnings": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "code": { "type": "string" },
                  "message": { "type": "string" },
                  "pageId": { "type": ["string", "null"] },
                  "batchId": { "type": ["string", "null"] }
                },
                "required": ["code", "message", "pageId", "batchId"]
              }
            }
          },
          "$defs": {
            "artifactDelta": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "operation": { "type": "string", "enum": ["add", "update", "upsert", "confirm"] },
                "mergeKey": { "type": "string" },
                "candidateId": { "type": ["string", "null"] },
                "value": { "type": "string" },
                "pages": { "type": "array", "items": { "type": "string" } },
                "evidence": { "type": "string" },
                "confidence": { "type": "number" }
              },
              "required": ["operation", "mergeKey", "candidateId", "value", "pages", "evidence", "confidence"]
            }
          },
          "required": ["pageObservations", "relationVerifications", "artifactDeltas", "processingHints", "uncertainties", "warnings"]
        }
        """);
        return InlineArtifactDeltaDefinition(schema);
    }

    public static JsonElement AutomaticReview()
    {
        return Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "decision": { "type": "string", "enum": ["resolved", "unresolved_auto"] },
            "selectedClaimIndex": { "type": ["integer", "null"] },
            "confidence": { "type": "number" },
            "evidence": { "type": "string" },
            "conservativeStrategy": { "type": "string" }
          },
          "required": ["decision", "selectedClaimIndex", "confidence", "evidence", "conservativeStrategy"]
        }
        """);
    }

    private static JsonElement Parse(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    private static JsonElement InlineArtifactDeltaDefinition(JsonElement schema)
    {
        var root = JsonNode.Parse(schema.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("The batch schema root must be an object.");
        var definition = root["$defs"]?["artifactDelta"]
            ?? throw new InvalidOperationException("The artifact delta definition is missing.");
        var artifacts = root["properties"]?["artifactDeltas"]?["properties"] as JsonObject
            ?? throw new InvalidOperationException("The artifact delta properties are missing.");

        foreach (var artifact in artifacts)
        {
            if (artifact.Value is JsonObject arraySchema)
            {
                arraySchema["items"] = definition.DeepClone();
            }
        }

        root.Remove("$defs");
        return Parse(root.ToJsonString());
    }
}
