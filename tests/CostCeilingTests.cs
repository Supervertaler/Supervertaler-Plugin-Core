using System;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// A model missing from the price list is shown at "up to $X": its tokens at
    /// the dearest listed rates. The one thing that must hold is that X is never
    /// below what a listed model would have cost for the same tokens - an upper
    /// bound that can come out lower is worse than no figure at all.
    /// </summary>
    [Tests]
    internal static class CostCeilingTests
    {
        private static readonly string[] Listed =
        {
            "claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5", "gpt-5.5", "gpt-5.4-mini", "gemini-3.1-pro-preview",
        };

        public static void TheCeiling_IsNeverBelowAListedModelsCost()
        {
            foreach (var model in Listed)
            {
                Assert.True(TokenEstimator.HasPricing(model), model + " is listed");
                foreach (var (reg, read, write, output) in new[] { (40_000, 0, 0, 300), (500, 38_000, 0, 300), (500, 0, 38_000, 300), (0, 0, 0, 20_000) })
                {
                    var cost = TokenEstimator.ComputeActualCost(model, reg, read, write, output);
                    // Same name, so the same cache multipliers: only the rates differ.
                    var ceiling = TokenEstimator.CostCeiling("claude", model, reg, read, write, output);
                    Assert.True(ceiling >= cost, $"{model} {reg}/{read}/{write}/{output}: ceiling {ceiling} >= cost {cost}");
                }
            }
        }

        public static void TheCeiling_IsZeroForOllama_AndPositiveElsewhere()
        {
            Assert.Equal(0m, TokenEstimator.CostCeiling(LlmModels.ProviderOllama, "some-local-model:7b", 10_000, 0, 0, 1_000), "ollama");
            Assert.True(TokenEstimator.CostCeiling(LlmModels.ProviderOpenRouter, "vendor/unlisted-model", 10_000, 0, 0, 1_000) > 0m, "openrouter");
        }

        public static void AnUnlistedModel_ShowsUpTo_NotUnknown()
        {
            var actual = Entry(LlmModels.ProviderOpenRouter, "vendor/unlisted-model");
            actual.ActualRegularInputTokens = 40_000; actual.ActualCacheReadTokens = 0;
            actual.ActualCacheWriteTokens = 0; actual.ActualOutputTokens = 300; actual.ActualCost = 0m;
            Assert.True(actual.SummaryLine.Contains("up to $"), "actual: " + actual.SummaryLine);
            Assert.True(!actual.SummaryLine.Contains("unknown"), "actual: no 'unknown'");
            Assert.True(actual.ToFullText().Contains("up to $"), "actual full text");

            var estimate = Entry(LlmModels.ProviderCustomOpenAi, "my-endpoint-model");
            estimate.EstimatedInputTokens = 40_000; estimate.EstimatedOutputTokens = 300;
            Assert.True(estimate.SummaryLine.Contains("up to ~$"), "estimate: " + estimate.SummaryLine);
            Assert.True(estimate.ToFullText().Contains("up to $"), "estimate full text");
        }

        public static void AnUnlistedOllamaModel_IsStillFree()
        {
            var e = Entry(LlmModels.ProviderOllama, "some-local-model:7b");
            e.EstimatedInputTokens = 40_000; e.EstimatedOutputTokens = 300;
            Assert.True(e.SummaryLine.Contains("free"), e.SummaryLine);
            Assert.True(!e.SummaryLine.Contains("up to"), "no ceiling for a local model");
        }

        private static PromptLogEntry Entry(string provider, string model) => new PromptLogEntry
        {
            Provider = provider, Model = model, IsCostKnown = TokenEstimator.HasPricing(model),
            Duration = TimeSpan.FromSeconds(2),
        };
    }
}
