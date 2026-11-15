using System;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The Gemini short list and Gemini 4 Argon's prices, as Google's pricing page
    /// gives them (checked 2026-10-29). Argon is $2/$10 until 30 Nov 2026 and
    /// $4/$20 from 1 Dec; the list carries the later price, so the figures err high.
    /// </summary>
    [Tests]
    internal static class GeminiPricingTests
    {
        private static void Costs(string model, int regular, int read, int write, int output, decimal expected, string what) =>
            Assert.Equal(expected, TokenEstimator.ComputeActualCost(model, regular, read, write, output), model + ": " + what);

        public static void TheShortList_IsFlashFlashLiteAndArgon()
        {
            Assert.True(LlmModels.GeminiModels.Select(m => m.Id).SequenceEqual(new[] { "gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-4-argon" }),
                "3.8 Flash, 3.5 Flash-Lite, 4 Argon: " + string.Join(", ", LlmModels.GeminiModels.Select(m => m.Id)));
            Assert.Equal("gemini-3.8-flash", LlmModels.DefaultModelId(LlmModels.ProviderGemini), "the default stays 3.8 Flash");
            Assert.True(LlmModels.OpenRouterModels.Any(m => m.Id == "google/gemini-4-argon"), "Argon is on the OpenRouter list");
            Assert.True(!LlmModels.OpenRouterModels.Any(m => m.Id == "google/gemini-3.1-pro-preview"), "3.1 Pro has left the OpenRouter list");
        }

        public static void Gemini31Pro_StaysPriced()
        {
            Assert.True(TokenEstimator.HasPricing("gemini-3.1-pro-preview"), "gemini-3.1-pro-preview is still costed for anyone who has it saved");
        }

        public static void Argon_IsPricedAtItsDecemberRate()
        {
            Costs("gemini-4-argon", 1_000_000, 0, 0, 0, 4.00m, "input $4/MTok");
            Costs("gemini-4-argon", 0, 0, 0, 1_000_000, 20.00m, "output $20/MTok");
        }

        public static void ArgonCacheReads_AreCostedAt95PercentOff()
        {
            // Google: $0.10 against $2 now, $0.20 against $4 from 1 Dec - 0.05x either way.
            Costs("gemini-4-argon", 0, 1_000_000, 0, 0, 0.20m, "cache read $0.20/MTok (0.05x)");
        }

        public static void Gemini3CacheReads_AreCostedAtATenth()
        {
            // Google's pricing page, checked 2026-11-15: context caching at a tenth of input.
            Costs("gemini-3.8-flash", 0, 1_000_000, 0, 0, 0.075m, "3.8 Flash cache read $0.075/MTok (0.1x)");
            Costs("gemini-3.5-flash-lite", 0, 1_000_000, 0, 0, 0.03m, "3.5 Flash-Lite cache read $0.03/MTok (0.1x)");
            Costs("gemini-3.1-flash-lite", 0, 1_000_000, 0, 0, 0.025m, "3.1 Flash-Lite cache read $0.025/MTok (0.1x)");
            Costs("gemini-3.1-pro-preview", 0, 1_000_000, 0, 0, 0.20m, "3.1 Pro cache read $0.20/MTok (0.1x)");
        }
    }
}
