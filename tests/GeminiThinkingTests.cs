using System;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// What a Gemini translation request says about thinking. Gemini 3.x models
    /// reason before they answer and bill it at the output rate; leaving it alone
    /// cost 718 hidden tokens on a 21-token translation (3.8 Flash) and 1,070 on 3.1
    /// Pro, measured against the live API on 21 Oct 2026. The same day showed that
    /// "minimal" is a 400 on both - their lowest level is "low" - so the level is
    /// each listed model's own decision, not a name prefix.
    /// </summary>
    [Tests]
    internal static class GeminiThinkingTests
    {
        private static string Config(string model) => LlmClient.GeminiGenerationConfig(model, 16384);

        public static void EveryListedGeminiModel_DecidesItsThinkingLevel()
        {
            Assert.True(LlmModels.GeminiModels.Length > 0, "the Gemini list is not empty");
            foreach (var m in LlmModels.GeminiModels)
                Assert.True(!string.IsNullOrEmpty(m.GeminiThinkingLevel),
                    m.Id + " says which thinking level it is sent: a Gemini model added without one thinks at Google's default and bills for it");
        }

        public static void Gemini38Flash_IsPinnedToLow_AndKeepsItsTemperature()
        {
            // "low", not "minimal": Google answers 400 "Thinking level MINIMAL is not supported for this model".
            Assert.Equal("{\"maxOutputTokens\":16384,\"thinkingConfig\":{\"thinkingLevel\":\"low\"},\"temperature\":0.3}",
                Config("gemini-3.8-flash"), "gemini-3.8-flash");
        }

        public static void Gemini31Pro_IsPinnedToLow_AndKeepsItsTemperature()
        {
            Assert.Equal("{\"maxOutputTokens\":16384,\"thinkingConfig\":{\"thinkingLevel\":\"low\"},\"temperature\":0.3}",
                Config("gemini-3.1-pro-preview"), "gemini-3.1-pro-preview");
        }

        public static void Gemini35FlashLite_IsSentWhatItAlwaysWas()
        {
            Assert.Equal("{\"maxOutputTokens\":16384,\"thinkingConfig\":{\"thinkingLevel\":\"minimal\"}}",
                Config("gemini-3.5-flash-lite"), "gemini-3.5-flash-lite");
        }

        public static void NoListedModel_IsSentALevelItRefuses()
        {
            // Only the levels Google documents for these two; "minimal" is rejected.
            foreach (var id in new[] { "gemini-3.8-flash", "gemini-3.1-pro-preview" })
                Assert.True(LlmModels.FindModel(id).GeminiThinkingLevel != "minimal", id + " does not accept minimal");
        }

        public static void AnUnlistedModel_KeepsTheRuleItHadBeforeTheFlag()
        {
            // Typed in, or fetched in Advanced mode: no entry to ask.
            Assert.Equal("{\"maxOutputTokens\":16384,\"thinkingConfig\":{\"thinkingLevel\":\"minimal\"}}",
                Config("gemini-3.5-flash"), "an unlisted 3.5 is pinned as before");
            Assert.Equal("{\"maxOutputTokens\":16384,\"temperature\":0.3}",
                Config("gemini-2.5-flash"), "an unlisted older model is sent what it always was");
        }

        public static void AnUnlistedNewModel_IsNotGuessedAt()
        {
            // A level it might not accept is a 400, which is worse than the default.
            Assert.Equal("{\"maxOutputTokens\":16384,\"temperature\":0.3}",
                Config("gemini-3.9-flash"), "unlisted, not 3.5");
            Assert.Equal(null, LlmClient.GeminiThinkingLevelFor(null), "no model");
            Assert.Equal(null, LlmClient.GeminiThinkingLevelFor(""), "empty model");
        }

        public static void TheConfig_IsAlwaysAnObject_WithTheCeilingFirst()
        {
            foreach (var id in LlmModels.GeminiModels.Select(m => m.Id).Concat(new[] { "gemini-3.5-flash", "gemini-2.5-flash", "gemini-3.9-flash" }))
            {
                var c = Config(id);
                Assert.True(c.StartsWith("{\"maxOutputTokens\":16384", StringComparison.Ordinal), id + ": " + c);
                Assert.True(c.EndsWith("}", StringComparison.Ordinal), id + " is closed: " + c);
                Assert.True(c.Count(ch => ch == '{') == c.Count(ch => ch == '}'), id + " has balanced braces: " + c);
            }
        }
    }
}
