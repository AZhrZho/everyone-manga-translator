using System.Security.Cryptography;
using System.Text;
using MangaTranslator.Api.Projects;

namespace MangaTranslator.Api.Tests;

public sealed class PrecheckReviewServiceTests
{
    [Fact]
    public void CompletedStoryBibleProjectsExistingDetailsIntoReviewItems()
    {
        var termKey = StableTermKey("王都");
        Dictionary<string, string> artifacts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["characters.json"] =
                """
                {
                  "runId":"run-1",
                  "characters":[{
                    "mergeKey":"character_hero",
                    "value":"阿凛",
                    "pages":["p0001","p0004"],
                    "evidence":"p0001: 凛",
                    "confidence":0.91
                  }]
                }
                """,
            ["glossary.json"] =
                $$"""
                {
                  "runId":"run-1",
                  "terms":[{
                    "mergeKey":"{{termKey}}",
                    "value":"王都",
                    "pages":["p0002"],
                    "evidence":"p0002: 王都",
                    "confidence":0.88
                  }]
                }
                """,
            ["translation_book_context.json"] = CompletedStoryBible()
        };

        var result = PrecheckReviewService.CreateResponse(artifacts, 7);

        Assert.Equal(7, result.ProjectRevision);
        Assert.NotNull(result.Context);
        Assert.Equal("紧张而温暖", result.Context.Tone);
        Assert.Equal(["成长", "归属"], result.Context.Themes);
        Assert.Equal("避免网络流行语", Assert.Single(result.Context.ProhibitedTendencies));
        var fact = Assert.Single(result.Context.ContinuityFacts);
        Assert.Equal(4, fact.RevealedAtOrder);
        Assert.Equal("神秘的旧伤", fact.SafeWordingBeforeReveal);

        var character = Assert.Single(result.Characters);
        Assert.NotNull(character.Details);
        Assert.Equal("阿凛", character.Details.TranslatedName);
        Assert.Equal("凛", character.Details.OriginalName);
        Assert.Equal(["小凛"], character.Details.Aliases);
        Assert.Equal(["句子简短", "生气时省略敬语"], character.Details.SpeechRules);
        Assert.True(character.Details.RevealSensitive);

        var term = Assert.Single(result.Terms);
        Assert.NotNull(term.Details);
        Assert.Equal("王都", term.Details.Source);
        Assert.Equal("王都", term.Details.Translation);
        Assert.Equal("place", term.Details.Category);
        Assert.Equal("始终译为王都", term.Details.UsageRule);
    }

    [Fact]
    public void IncompleteStoryBibleKeepsLegacyReviewProjection()
    {
        Dictionary<string, string> artifacts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["characters.json"] =
                """
                {"runId":"run-1","characters":[{"mergeKey":"character_hero","value":"阿凛","pages":[],"evidence":"","confidence":0.9}]}
                """,
            ["glossary.json"] = """{"runId":"run-1","terms":[]}""",
            ["translation_book_context.json"] =
                """
                {"runId":"run-1","synopsis":{"overall":"尚未完成"},"characters":[],"glossary":[]}
                """
        };

        var result = PrecheckReviewService.CreateResponse(artifacts, 3);

        Assert.Null(result.Context);
        Assert.Null(Assert.Single(result.Characters).Details);
    }

    [Fact]
    public void StoryBibleFromAnotherRunDoesNotAddDetails()
    {
        Dictionary<string, string> artifacts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["characters.json"] =
                """
                {"runId":"run-older","characters":[{"mergeKey":"character_hero","value":"阿凛","pages":[],"evidence":"","confidence":0.9}]}
                """,
            ["glossary.json"] = """{"runId":"run-older","terms":[]}""",
            ["translation_book_context.json"] = CompletedStoryBible()
        };

        var result = PrecheckReviewService.CreateResponse(artifacts, 4);

        Assert.Null(result.Context);
        Assert.Null(Assert.Single(result.Characters).Details);
    }

    private static string CompletedStoryBible() =>
        """
        {
          "schemaVersion":"1.0.0",
          "runId":"run-1",
          "bookId":"book-1",
          "targetLanguage":"zh-Hans",
          "synopsis":{
            "overall":"凛寻找失踪的姐姐。",
            "tone":"紧张而温暖",
            "themes":["成长","归属"]
          },
          "translationStyle":{
            "overallVoice":"自然、克制",
            "dialogueRules":["对白口语化"],
            "honorificPolicy":"保留关系信息",
            "namePolicy":"人名统一使用既定译名",
            "prohibitedTendencies":["避免网络流行语"]
          },
          "characters":[{
            "characterId":"character_hero",
            "originalName":"凛",
            "translatedName":"林",
            "aliases":["小凛"],
            "role":"主角",
            "speechRules":["句子简短","生气时省略敬语"],
            "relationships":["结衣的妹妹"],
            "validFromOrder":1,
            "validToOrder":null,
            "revealSensitive":true,
            "confidence":0.91,
            "evidencePages":["p0001","p0004"]
          }],
          "glossary":[{
            "source":"王都",
            "translation":"皇家首都",
            "category":"place",
            "usageRule":"始终译为王都",
            "validFromOrder":2,
            "validToOrder":null,
            "revealSensitive":false,
            "confidence":0.88,
            "evidencePages":["p0002"]
          }],
          "continuityFacts":[{
            "fact":"凛的伤来自姐姐",
            "revealedAtOrder":4,
            "safeWordingBeforeReveal":"神秘的旧伤",
            "evidencePages":["p0004"]
          }],
          "unresolvedQuestions":["姐姐是否仍然活着"]
        }
        """;

    private static string StableTermKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "term_" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
