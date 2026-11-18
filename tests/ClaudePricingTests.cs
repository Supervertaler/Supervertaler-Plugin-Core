using System;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The Claude short list and the prices the cost figures rest on, as
    /// Anthropic's pricing page gives them (checked 2026-09-28). A cost shown in
    /// Reports, the usage log or the budget warning is only as good as these.
    /// </summary>
    [Tests]
    internal static class ClaudePricingTests
    {
        private static void Costs(string model, int regular, int read, int write, int output, decimal expected, string what) =>
            Assert.Equal(expected, TokenEstimator.ComputeActualCost(model, regular, read, write, output), model + ": " + what);

        public static void TheShortList_IsTheNewestHaikuSonnetOpusAndFable_CheapestFirst()
        {
            Assert.True(LlmModels.ClaudeModels.Select(m => m.Id).SequenceEqual(new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-2" }),
                "Haiku 5.5, Sonnet 5.5, Opus 5.5, Fable 5.2: " + string.Join(", ", LlmModels.ClaudeModels.Select(m => m.Id)));
        }

        public static void TheDefault_IsSonnet_NotTheFirstOfTheList()
        {
            // The list runs cheapest first; a new installation must not start
            // translating with the smallest model because it happens to be on top.
            Assert.Equal("claude-sonnet-5-5", LlmModels.DefaultClaudeModelId, "Claude default");
            Assert.Equal("claude-sonnet-5-5", LlmModels.DefaultModelId(LlmModels.ProviderClaude), "DefaultModelId for Claude");
            Assert.True(LlmModels.FindModel(LlmModels.DefaultClaudeModelId) != null, "the default is a listed model");
            Assert.True(LlmModels.ClaudeModels[0].Id != LlmModels.DefaultClaudeModelId, "and it is not the first entry");
        }

        public static void Haiku55_IsPricedAsPublished()
        {
            // Anthropic's pricing page, checked 2026-11-18, for prompts up to
            // 100,000 tokens. Above that Haiku 5.5 costs five times as much, which
            // this flat price does not know: such a request is under-costed.
            Costs("claude-haiku-5-5", 1_000_000, 0, 0, 0, 0.10m, "input $0.10/MTok");
            Costs("claude-haiku-5-5", 0, 0, 0, 1_000_000, 0.50m, "output $0.50/MTok");
            Costs("claude-haiku-5-5", 0, 1_000_000, 0, 0, 0.01m, "cache read $0.01/MTok (0.1x)");
            Costs("claude-haiku-5-5", 0, 0, 1_000_000, 0, 0.125m, "5-minute cache write $0.125/MTok");
            Assert.True(LlmClient.SupportsAdaptiveThinking("claude-haiku-5-5"), "Haiku 5.5 takes adaptive thinking");
        }

        public static void SupersededModels_StayPriced()
        {
            foreach (var model in new[] { "claude-sonnet-5", "claude-opus-5", "claude-haiku-4-5-20251001", "claude-fable-5-1" })
                Assert.True(TokenEstimator.HasPricing(model), model + " is still costed for anyone who has it saved");
        }

        public static void Sonnet55_IsPricedAsPublished()
        {
            Costs("claude-sonnet-5-5", 1_000_000, 0, 0, 0, 2.00m, "input $2/MTok");
            Costs("claude-sonnet-5-5", 0, 0, 0, 1_000_000, 10.00m, "output $10/MTok");
            Costs("claude-sonnet-5-5", 0, 0, 1_000_000, 0, 2.50m, "5-minute cache write $2.50/MTok");
        }

        public static void Fable52_IsPricedAsPublished()
        {
            Costs("claude-fable-5-2", 1_000_000, 0, 0, 0, 8.00m, "input $8/MTok");
            Costs("claude-fable-5-2", 0, 0, 0, 1_000_000, 40.00m, "output $40/MTok");
            Costs("claude-fable-5-2", 0, 1_000_000, 0, 0, 0.20m, "cache read $0.20/MTok (0.025x)");
            Costs("claude-fable-5-2", 0, 0, 1_000_000, 0, 10.00m, "5-minute cache write $10/MTok (1.25x)");
        }

        public static void CacheReads_AreCostedAtEachModelsOwnRate()
        {
            Costs("claude-sonnet-5-5", 0, 1_000_000, 0, 0, 0.10m, "cache read $0.10/MTok (0.05x)");            Costs("claude-sonnet-5", 0, 1_000_000, 0, 0, 0.20m, "cache read $0.20/MTok (0.1x)");
            Costs("claude-opus-5", 0, 1_000_000, 0, 0, 0.50m, "cache read $0.50/MTok (0.1x)");
            Costs("claude-opus-5-5", 0, 1_000_000, 0, 0, 0.20m, "cache read $0.20/MTok (0.05x)");
            Costs("claude-fable-5-1", 0, 1_000_000, 0, 0, 0.25m, "cache read $0.25/MTok (0.025x)");
            Costs("claude-opus-5-5", 0, 0, 1_000_000, 0, 5.00m, "5-minute cache write $5/MTok (1.25x)");
            Costs("claude-fable-5-1", 0, 0, 1_000_000, 0, 12.50m, "5-minute cache write $12.50/MTok (1.25x)");
        }
    }
}
