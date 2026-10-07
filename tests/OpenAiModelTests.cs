using System;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The OpenAI short list - OpenAI's three current tiers - and what the cost
    /// figures and the tool-using chat rest on, as OpenAI's model pages give them
    /// (checked 2026-10-07).
    /// </summary>
    [Tests]
    internal static class OpenAiModelTests
    {
        private static void Costs(string model, int regular, int read, int output, decimal expected, string what) =>
            Assert.Equal(expected, TokenEstimator.ComputeActualCost(model, regular, read, 0, output), model + ": " + what);

        public static void TheShortList_IsAstraSolLuna_MostCapableFirst()
        {
            Assert.True(LlmModels.OpenAiModels.Select(m => m.Id).SequenceEqual(new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6-luna" }),
                "Astra, 6.1 Sol, Luna: " + string.Join(", ", LlmModels.OpenAiModels.Select(m => m.Id)));
            foreach (var id in new[] { "openai/gpt-6-astra", "openai/gpt-6.1-sol", "openai/gpt-6-luna" })
                Assert.True(LlmModels.OpenRouterModels.Any(m => m.Id == id), id + " is on the OpenRouter list");
            Assert.True(!LlmModels.OpenRouterModels.Any(m => m.Id.StartsWith("openai/gpt-5", StringComparison.Ordinal)),
                "no GPT-5 left on the OpenRouter list");
        }

        public static void TheDefault_IsGpt61Sol_AndOnTheList()
        {
            Assert.Equal("gpt-6.1-sol", LlmModels.DefaultOpenAiModelId, "OpenAI default");
            Assert.True(LlmModels.FindModel(LlmModels.DefaultOpenAiModelId) != null, "the default is a listed model");
        }

        public static void DefaultModelId_IsNamedForOpenAi_AndTheFirstEntryElsewhere()
        {
            // A provider switch selects this, so for OpenAI it must not be Astra,
            // the first - and dearest - entry.
            Assert.Equal("gpt-6.1-sol", LlmModels.DefaultModelId(LlmModels.ProviderOpenAi), "OpenAI");
            foreach (var key in LlmModels.AllProviderKeys)
            {
                if (key == LlmModels.ProviderOpenAi) continue;
                var models = LlmModels.GetModelsForProvider(key);
                Assert.Equal(models.Length > 0 ? models[0].Id : null, LlmModels.DefaultModelId(key), key);
            }
            Assert.Equal(null, LlmModels.DefaultModelId("no-such-provider"), "unknown provider");
        }

        public static void EveryGpt6Entry_IsAReasoningModel_SentNoTemperature()
        {
            foreach (var m in LlmModels.OpenAiModels.Concat(LlmModels.OpenRouterModels.Where(x => x.Id.StartsWith("openai/", StringComparison.Ordinal))))
            {
                Assert.True(m.IsReasoningModel, m.Id + " gets the reasoning timeout");
                Assert.True(!m.SupportsTemperature, m.Id + " is sent no temperature");
            }
        }

        public static void SupersededModels_StayPriced()
        {
            foreach (var model in new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.4-mini" })
                Assert.True(TokenEstimator.HasPricing(model), model + " is still costed for anyone who has it saved");
        }

        public static void TheThree_ArePricedAsPublished()
        {
            Costs("gpt-6-astra", 1_000_000, 0, 0, 10.00m, "input $10/MTok");
            Costs("gpt-6-astra", 0, 0, 1_000_000, 50.00m, "output $50/MTok");
            Costs("gpt-6-astra", 0, 1_000_000, 0, 1.00m, "cached input $1/MTok (0.1x)");
            Costs("gpt-6.1-sol", 1_000_000, 0, 0, 2.00m, "input $2/MTok");
            Costs("gpt-6.1-sol", 0, 0, 1_000_000, 10.00m, "output $10/MTok");
            Costs("gpt-6.1-sol", 0, 1_000_000, 0, 0.10m, "cached input $0.10/MTok (0.05x)");
            Costs("gpt-6-luna", 1_000_000, 0, 0, 0.10m, "input $0.10/MTok");
            Costs("gpt-6-luna", 0, 0, 1_000_000, 0.50m, "output $0.50/MTok");
            Costs("gpt-6-luna", 0, 1_000_000, 0, 0.01m, "cached input $0.01/MTok (0.1x)");
        }

        public static void Gpt56_IsPricedAsOpenAiListsItNow()
        {
            // Off the short list, but costed for anyone who kept one. Sol's is a
            // promotional price, at least until 21 November 2026.
            Costs("gpt-5.6-sol", 1_000_000, 0, 0, 4.00m, "input $4/MTok");
            Costs("gpt-5.6-sol", 0, 0, 1_000_000, 20.00m, "output $20/MTok");
            Costs("gpt-5.6-sol", 0, 1_000_000, 0, 0.40m, "cached input $0.40/MTok (0.1x)");
            Costs("gpt-5.6-terra", 1_000_000, 0, 0, 2.00m, "input $2/MTok");
            Costs("gpt-5.6-terra", 0, 0, 1_000_000, 12.00m, "output $12/MTok");
            Costs("gpt-5.6-terra", 0, 1_000_000, 0, 0.20m, "cached input $0.20/MTok (0.1x)");
            Costs("gpt-5.6-luna", 1_000_000, 0, 0, 0.20m, "input $0.20/MTok");
            Costs("gpt-5.6-luna", 0, 0, 1_000_000, 1.20m, "output $1.20/MTok");
            Costs("gpt-5.6-luna", 0, 1_000_000, 0, 0.02m, "cached input $0.02/MTok (0.1x)");
        }

        public static void OlderOpenAiModels_KeepTheirHalfPriceCacheReads()
        {
            Costs("gpt-5.4-mini", 0, 1_000_000, 0, 0.375m, "cached input 0.5x of $0.75");
        }

        public static void ToolRules_MatchWhatEachModelAccepts()
        {
            // Takes tools as they are: no opt-out, no fallback.
            foreach (var id in new[] { "gpt-6-astra", "openai/gpt-6-astra", "claude-opus-5-5" })
            {
                Assert.True(!LlmClient.RefusesToolsOnChatCompletions(id), id + " keeps its tools");
                Assert.True(!LlmClient.RequiresReasoningEffortNoneWithTools(id), id + " is not sent reasoning_effort none");
            }
            // Takes tools only with reasoning_effort "none".
            foreach (var id in new[] { "gpt-6-luna", "openai/gpt-6-luna", "gpt-6-sol", "gpt-5.6-sol", "GPT-5.6-Terra" })
            {
                Assert.True(!LlmClient.RefusesToolsOnChatCompletions(id), id + " keeps its tools");
                Assert.True(LlmClient.RequiresReasoningEffortNoneWithTools(id), id + " is sent reasoning_effort none with tools");
            }
            // Takes no tools on Chat Completions, and refuses "none".
            foreach (var id in new[] { "gpt-6.1-sol", "openai/gpt-6.1-sol", "openai/gpt-6.1-sol-pro" })
            {
                Assert.True(LlmClient.RefusesToolsOnChatCompletions(id), id + " goes out as plain chat");
                Assert.True(!LlmClient.RequiresReasoningEffortNoneWithTools(id), id + " is never sent reasoning_effort none");
            }
            Assert.True(!LlmClient.RefusesToolsOnChatCompletions(null), "no model, no change");
            Assert.True(!LlmClient.RequiresReasoningEffortNoneWithTools(null), "no model, no change");
        }
    }
}
