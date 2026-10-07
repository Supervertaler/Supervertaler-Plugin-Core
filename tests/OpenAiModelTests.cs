using System;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// GPT-6.1 Sol (released 2026-09-29) as OpenAI's model page gives it
    /// (checked 2026-10-07): $2 input, $0.10 cached input, $10 output per 1M
    /// tokens; reasoning always on; no function tools on Chat Completions.
    /// </summary>
    [Tests]
    internal static class OpenAiModelTests
    {
        private static void Costs(string model, int regular, int read, int output, decimal expected, string what) =>
            Assert.Equal(expected, TokenEstimator.ComputeActualCost(model, regular, read, 0, output), model + ": " + what);

        public static void Gpt61Sol_IsOnBothShortLists_AsAReasoningModel()
        {
            foreach (var (list, id) in new[] { (LlmModels.OpenAiModels, "gpt-6.1-sol"), (LlmModels.OpenRouterModels, "openai/gpt-6.1-sol") })
            {
                var m = list.FirstOrDefault(x => x.Id == id);
                Assert.True(m != null, id + " is on its short list");
                Assert.True(m.IsReasoningModel, id + " gets the reasoning timeout");
                Assert.True(!m.SupportsTemperature, id + " is sent no temperature");
            }
        }

        public static void Gpt61Sol_IsTheFirstOpenAiEntry_WithGpt56SolKept()
        {
            // memoQ offers the first entry when nothing is configured.
            Assert.Equal("gpt-6.1-sol", LlmModels.OpenAiModels[0].Id, "first OpenAI entry");
            Assert.True(LlmModels.OpenAiModels.Any(m => m.Id == "gpt-5.6-sol"), "GPT-5.6 Sol stays: it keeps the chat's tools");
        }

        public static void Gpt61Sol_IsPricedAsPublished()
        {
            Costs("gpt-6.1-sol", 1_000_000, 0, 0, 2.00m, "input $2/MTok");
            Costs("gpt-6.1-sol", 0, 0, 1_000_000, 10.00m, "output $10/MTok");
            Costs("gpt-6.1-sol", 0, 1_000_000, 0, 0.10m, "cached input $0.10/MTok (0.05x)");
        }

        public static void OtherOpenAiModels_KeepTheirHalfPriceCacheReads()
        {
            Costs("gpt-5.6-sol", 0, 1_000_000, 0, 2.50m, "cached input 0.5x of $5");
        }

        public static void Gpt6_GoesOutWithoutTools_Gpt56KeepsThem()
        {
            Assert.True(LlmClient.RefusesToolsOnChatCompletions("gpt-6.1-sol"), "direct id");
            Assert.True(LlmClient.RefusesToolsOnChatCompletions("openai/gpt-6.1-sol"), "OpenRouter id");
            Assert.True(LlmClient.RefusesToolsOnChatCompletions("GPT-6-Sol"), "case-insensitive, whole GPT-6 family");
            Assert.True(!LlmClient.RefusesToolsOnChatCompletions("gpt-5.6-sol"), "GPT-5.6 keeps tools (reasoning_effort none)");
            Assert.True(!LlmClient.RefusesToolsOnChatCompletions("claude-opus-5-5"), "Claude keeps tools");
            Assert.True(!LlmClient.RefusesToolsOnChatCompletions(null), "no model, no change");
        }
    }
}
