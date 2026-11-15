using System.Collections.Generic;
using Supervertaler.Core.Models;

namespace Supervertaler.Core
{
    /// <summary>
    /// Estimates token counts and API costs for AI calls.
    /// Uses chars/4 heuristic for token estimation (no external library).
    /// Per-model prices come from <see cref="PricingTable"/> (the canonical
    /// pricing.json shared with Supervertaler Workbench); the per-provider cache
    /// discount multipliers below are applied on top.
    /// </summary>
    public static class TokenEstimator
    {
        /// <summary>
        /// Estimates token count from a string using chars/4 heuristic.
        /// Returns 0 for null/empty strings.
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (text.Length + 3) / 4; // ceil division
        }

        /// <summary>
        /// Estimates total input tokens for a SendPromptAsync call.
        /// </summary>
        public static int EstimateInputTokens(string userPrompt, string systemPrompt)
        {
            return EstimateTokens(userPrompt) + EstimateTokens(systemPrompt);
        }

        /// <summary>
        /// Estimates total input tokens for a SendChatAsync call.
        /// </summary>
        public static int EstimateInputTokens(List<ChatMessage> messages, string systemPrompt)
        {
            int total = EstimateTokens(systemPrompt);
            if (messages != null)
            {
                foreach (var msg in messages)
                    total += EstimateTokens(msg.Content);
            }
            return total;
        }

        /// <summary>
        /// Estimates the cost of an API call in USD.
        /// Returns 0 for unknown models or Ollama (local).
        /// </summary>
        public static decimal EstimateCost(string model, int inputTokens, int outputTokens)
        {
            if (string.IsNullOrEmpty(model)) return 0m;

            (decimal inputPer1M, decimal outputPer1M) rates;
            if (!PricingTable.TryGet(model, out rates))
                return 0m;

            return (inputTokens * rates.inputPer1M / 1_000_000m)
                 + (outputTokens * rates.outputPer1M / 1_000_000m);
        }

        /// <summary>
        /// Estimates input-only cost in USD for a given model and token count.
        /// Useful for pre-send cost warnings.
        /// </summary>
        public static decimal EstimateInputCost(string model, int inputTokens)
        {
            if (string.IsNullOrEmpty(model)) return 0m;

            (decimal inputPer1M, decimal outputPer1M) rates;
            if (!PricingTable.TryGet(model, out rates))
                return 0m;

            return inputTokens * rates.inputPer1M / 1_000_000m;
        }

        /// <summary>
        /// Returns true if pricing information is available for the given model.
        /// </summary>
        public static bool HasPricing(string model)
        {
            (decimal input, decimal output) rates;
            return PricingTable.TryGet(model, out rates);
        }

        /// <summary>
        /// The most a call to a model MISSING from the price list can have cost:
        /// its tokens at the dearest input and output rates the list holds
        /// (<see cref="PricingTable.Dearest"/>), with the provider's cache
        /// discounts as the model's name implies them. Shown as "up to $X" rather
        /// than "unknown", which reads as nothing, or as a figure that could be
        /// mistaken for the real one. It holds for any model priced no higher than
        /// the dearest one listed - a rarer, dearer model is the one case it can
        /// understate. Zero for Ollama, which runs on the translator's machine.
        /// For estimated counts pass the estimated input as regular input.
        /// </summary>
        public static decimal CostCeiling(string provider, string model,
            int regularInputTokens, int cacheReadTokens, int cacheWriteTokens, int outputTokens,
            int cacheWrite1hTokens = 0)
        {
            if (string.Equals(provider, LlmModels.ProviderOllama, System.StringComparison.OrdinalIgnoreCase))
                return 0m;
            // A machine-translation engine charges by the character and is priced
            // by MachineTranslation.Cost. Counted here, its characters would be
            // taken for tokens at the dearest AI rate.
            if (MachineTranslation.IsEngine(provider))
                return 0m;

            var (inputPer1M, outputPer1M) = PricingTable.Dearest;
            return Price(model, inputPer1M, outputPer1M,
                regularInputTokens, cacheReadTokens, cacheWriteTokens, outputTokens, cacheWrite1hTokens);
        }

        /// <summary>
        /// Anthropic's one-hour cache write: 2x the input rate on every Claude
        /// model (the five-minute write is 1.25x). Only Claude is ever asked for
        /// the hour, so no other model reports such writes.
        /// </summary>
        private const decimal OneHourWriteMultiplier = 2m;

        /// <summary>
        /// Tokens at the given rates. <paramref name="cacheWrite1hTokens"/> is the
        /// part of <paramref name="cacheWriteTokens"/> written for an hour, priced
        /// at <see cref="OneHourWriteMultiplier"/>; the rest at the model's own
        /// write multiplier.
        /// </summary>
        private static decimal Price(string model, decimal inputPer1M, decimal outputPer1M,
            int regularInputTokens, int cacheReadTokens, int cacheWriteTokens, int outputTokens, int cacheWrite1hTokens)
        {
            var (readMul, writeMul) = GetCacheMultipliers(model);
            var oneHour = System.Math.Max(0, System.Math.Min(cacheWrite1hTokens, cacheWriteTokens));
            var shortWrites = cacheWriteTokens - oneHour;

            return (regularInputTokens * inputPer1M / 1_000_000m)
                 + (cacheReadTokens * inputPer1M * readMul / 1_000_000m)
                 + (shortWrites * inputPer1M * writeMul / 1_000_000m)
                 + (oneHour * inputPer1M * OneHourWriteMultiplier / 1_000_000m)
                 + (outputTokens * outputPer1M / 1_000_000m);
        }

        /// <summary>
        /// Returns the per-provider cache discount multipliers for a given model:
        /// (cacheReadMultiplier, cacheWriteMultiplier), both relative to the regular
        /// input rate. Multiply input rate by these for the effective cached rate.
        /// </summary>
        private static (decimal readMultiplier, decimal writeMultiplier) GetCacheMultipliers(string model)
        {
            if (string.IsNullOrEmpty(model)) return (1m, 1m);
            var lc = model.ToLowerInvariant();

            // Anthropic native + OpenRouter→Anthropic. A cache read is 0.1x the input
            // rate, except on the newest models: 0.05x on Claude Opus 5.5 and Sonnet
            // 5.5, 0.025x on Claude Fable 5.1 and 5.2 and Mythos 5.1 and 5.2
            // (Anthropic's pricing page, checked 2026-09-28; 5.2's $0.20 against $8
            // checked 2026-11-10; Sonnet 5.5's $0.10 against $2 in the prompt
            // caching documentation, checked 2026-11-15). At 0.1x their cache reads
            // were costed at two and four times what they are billed. Five-minute
            // writes are 1.25x on all of them; one-hour writes 2x (see Price).
            if (lc.Contains("claude") || lc.StartsWith("anthropic/"))
            {
                if (lc.Contains("opus-5-5") || lc.Contains("opus-5.5")
                    || lc.Contains("sonnet-5-5") || lc.Contains("sonnet-5.5"))
                    return (0.05m, 1.25m);
                if (lc.Contains("fable-5-1") || lc.Contains("fable-5.1")
                    || lc.Contains("fable-5-2") || lc.Contains("fable-5.2")
                    || lc.Contains("mythos-5-1") || lc.Contains("mythos-5.1")
                    || lc.Contains("mythos-5-2") || lc.Contains("mythos-5.2"))
                    return (0.025m, 1.25m);
                return (0.1m, 1.25m);
            }

            // OpenAI auto-cache: 50% off cache reads, no separate cache-write surcharge.
            // The GPT-6 and GPT-5.6 models read for much less (OpenAI's model pages,
            // checked 2026-10-07): 0.05x on GPT-6.1 Sol ($0.10 against $2), 0.1x on
            // Astra, Luna, GPT-6 Sol and all three GPT-5.6. At 0.5x their cache
            // reads were costed at five to ten times the bill. Cache writes are
            // 1.25x on every GPT-6 model (OpenAI's pricing page, checked
            // 2026-10-19: $2.50 against $2.00 on 6.1 Sol and GPT-6 Sol, $12.50 on
            // Astra, $0.125 on Luna). The Responses API reports the write count in
            // input_tokens_details.cache_write_tokens and Chat Completions in
            // prompt_tokens_details.cache_write_tokens (OpenRouter uses the same
            // field); both are read (LlmClient.ExtractResponsesUsage,
            // ExtractOpenAiUsage). GPT-5.6 is not on that page any more and keeps 1x.
            if (lc.Contains("gpt-6.1-sol"))
                return (0.05m, 1.25m);
            if (lc.Contains("gpt-6"))
                return (0.1m, 1.25m);
            if (lc.Contains("gpt-5.6"))
                return (0.1m, 1m);
            if (lc.StartsWith("gpt-") || lc.StartsWith("openai/") || lc.StartsWith("o4-"))
                return (0.5m, 1m);

            // DeepSeek automatic disk caching: ~90% off cache reads
            if (lc.StartsWith("deepseek") || lc.Contains("deepseek/"))
                return (0.1m, 1m);

            // Gemini 4 reads its implicit cache at 95% off: $0.10 against $2 on Argon
            // (Google's pricing page, checked 2026-10-29), and Google gives Carbon the
            // same discount. Without this, Argon's cache reads cost full price.
            if (lc.StartsWith("gemini-4") || lc.StartsWith("google/gemini-4"))
                return (0.05m, 1m);

            // Gemini 2.5 and 3.x: cached input at a tenth of the input price on
            // every one of them Google prices (Google's pricing page, checked
            // 2026-11-15: 3.8 Flash $0.075 against $0.75, 3.5 Flash-Lite $0.03
            // against $0.30, 3.1 Flash-Lite $0.025 against $0.25, 3.1 Pro $0.20
            // against $2, 2.5 Flash $0.03 against $0.30). At the 0.25x it had,
            // their cache reads were costed at two and a half times the bill.
            if (lc.StartsWith("gemini-2.5") || lc.StartsWith("gemini-3")
                || lc.StartsWith("google/gemini-2.5") || lc.StartsWith("google/gemini-3"))
                return (0.1m, 1m);

            // Grok 4.3, the one Grok on the list: cached input $0.20 against $1.25
            // (xAI's models page, checked 2026-11-15). Other Grok models read at
            // other rates (0.15x to 0.25x) and keep 1x here, which keeps a
            // "most it can have cost" figure from understating.
            if (lc == "grok-4.3")
                return (0.16m, 1m);

            // No documented caching for this provider/model
            return (1m, 1m);
        }

        /// <summary>
        /// Computes the actual API cost in USD from real token counts returned by
        /// the provider's response usage block, applying per-provider cache
        /// discount rates. Use this when ApiUsage is available; falls back to
        /// EstimateCost when only chars/4 estimates exist.
        /// Returns 0 for unknown models or Ollama (local).
        /// </summary>
        public static decimal ComputeActualCost(string model,
            int regularInputTokens, int cacheReadTokens, int cacheWriteTokens, int outputTokens,
            int cacheWrite1hTokens = 0)
        {
            if (string.IsNullOrEmpty(model)) return 0m;

            (decimal inputPer1M, decimal outputPer1M) rates;
            if (!PricingTable.TryGet(model, out rates))
                return 0m;

            return Price(model, rates.inputPer1M, rates.outputPer1M,
                regularInputTokens, cacheReadTokens, cacheWriteTokens, outputTokens, cacheWrite1hTokens);
        }
    }
}
