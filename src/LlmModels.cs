using System;
using System.Collections.Generic;

namespace Supervertaler.Core
{
    public enum LlmProvider
    {
        OpenAi,
        Claude,
        Gemini,
        Grok,
        Mistral,
        DeepSeek,
        OpenRouter,
        Ollama,
        CustomOpenAi
    }

    public class LlmModelInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public LlmProvider Provider { get; set; }
        public bool IsReasoningModel { get; set; }

        /// <summary>
        /// Whether the model accepts a custom "temperature" value. Some models
        /// (e.g. GPT-5.5) only accept the API default and 400 if any explicit
        /// temperature is sent, so the request builder must omit it. Defaults
        /// to true; set false only for models that reject custom temperatures.
        /// </summary>
        public bool SupportsTemperature { get; set; } = true;
        public int DefaultTimeoutMs { get; set; } = 120_000;
        public int DefaultMaxTokens { get; set; } = 16384;
    }

    /// <summary>
    /// Static catalog of all supported LLM models and provider metadata.
    /// Mirrors Python Supervertaler's model definitions in modules/llm_clients.py.
    /// </summary>
    public static class LlmModels
    {
        // Provider key strings – match Python Supervertaler and JSON settings
        public const string ProviderOpenAi = "openai";
        public const string ProviderClaude = "claude";
        public const string ProviderGemini = "gemini";
        public const string ProviderOllama = "ollama";
        public const string ProviderGrok = "grok";
        public const string ProviderMistral = "mistral";
        public const string ProviderDeepSeek = "deepseek";
        public const string ProviderOpenRouter = "openrouter";
        public const string ProviderCustomOpenAi = "custom_openai";

        // The OpenAI model a new installation starts on. Named, not taken from the
        // list's position: the list below is ordered by price, most capable
        // first, so its first entry is not the one to recommend.
        public const string DefaultOpenAiModelId = "gpt-6.1-sol";

        // OpenAI's three current tiers, most capable to cheapest - the three it
        // offers as its own main choices (developers.openai.com/api/docs/models,
        // checked 2026-10-07; Michael chose them the same day). All 1.05M context,
        // 128k max output, reasoning models that are sent no temperature.
        // Superseded models leave this list but not the price list: anyone with
        // GPT-5.6 Sol, Terra or Luna, or GPT-5.4 Mini, saved keeps it - it shows in
        // the custom model ID field and still works - and is still costed.
        //
        // They differ in how the tool-using chat can reach them on
        // /v1/chat/completions (see LlmClient): Astra takes tools as they are,
        // Luna only with reasoning_effort "none", 6.1 Sol not at all.
        public static readonly LlmModelInfo[] OpenAiModels =
        {
            new LlmModelInfo
            {
                Id = "gpt-6-astra", DisplayName = "GPT-6 Astra",
                Description = "Premium – OpenAI's most capable model ($10/$50), 1M context. For the hardest work when cost is secondary",
                Provider = LlmProvider.OpenAi,
                SupportsTemperature = false,
                IsReasoningModel = true
            },
            new LlmModelInfo
            {
                Id = "gpt-6.1-sol", DisplayName = "GPT-6.1 Sol",
                Description = "Recommended – close to Astra's quality at a fifth of its price ($2/$10), 1M context",
                Provider = LlmProvider.OpenAi,
                SupportsTemperature = false,
                IsReasoningModel = true
            },
            new LlmModelInfo
            {
                Id = "gpt-6-luna", DisplayName = "GPT-6 Luna",
                Description = "Budget – $0.10/$0.50 per 1M tokens, for high-volume work where cost matters most",
                Provider = LlmProvider.OpenAi,
                SupportsTemperature = false,
                IsReasoningModel = true
            }
        };

        // The newest Sonnet, Opus and Fable, in that order (Michael, 2026-09-28).
        // The first is Trados's default for a new install. Superseded models leave
        // this list but not the price list: anyone with one saved keeps it - a
        // model missing from here shows in the custom model ID field and still
        // works - and their runs are still costed. So Sonnet 5 (superseded by
        // Sonnet 5.5, same price, released 2026-09-28), Opus 5 (by Opus 5.5,
        // 2026-09-24) and Haiku 4.5 (out of the short list, same day as Sonnet
        // 5.5) all still run.
        public static readonly LlmModelInfo[] ClaudeModels =
        {
            new LlmModelInfo
            {
                Id = "claude-sonnet-5-5", DisplayName = "Claude Sonnet 5.5",
                Description = "Recommended – newest Sonnet, near-Opus quality at Sonnet cost ($2/$10), 1M context",
                Provider = LlmProvider.Claude
            },
            new LlmModelInfo
            {
                Id = "claude-opus-5-5", DisplayName = "Claude Opus 5.5",
                Description = "Premium – Anthropic's newest Opus ($4/$20), 1M context. Top choice for hard legal/technical work",
                Provider = LlmProvider.Claude
            },
            new LlmModelInfo
            {
                Id = "claude-fable-5-1", DisplayName = "Claude Fable 5.1",
                Description = "Maximum capability – Anthropic's most capable model, always-on reasoning at double Opus pricing; for the hardest legal/technical work when cost is secondary",
                Provider = LlmProvider.Claude
            }
        };

        // Short list re-judged 2026-09-06 against Google's pricing page: the newest of
        // each tier, superseded models dropped (3.5 Flash costs twice 3.8 Flash; 3.1
        // Flash-Lite and 2.5 Pro are generations behind). Gemma is out: Google hosts it
        // on the free tier only - paid tier "not available" - so a batch on a paid key
        // hits the rate limits; it lives under Ollama, where open weights belong.
        public static readonly LlmModelInfo[] GeminiModels =
        {
            new LlmModelInfo
            {
                Id = "gemini-3.8-flash", DisplayName = "Gemini 3.8 Flash",
                Description = "Recommended – newest Flash, strong quality at $0.75/$3.75 per 1M tokens (until end 2026), 1M context",
                Provider = LlmProvider.Gemini
            },
            new LlmModelInfo
            {
                Id = "gemini-3.5-flash-lite", DisplayName = "Gemini 3.5 Flash-Lite",
                Description = "Budget – $0.30/$2.50 per 1M tokens, for high-volume work where cost matters most",
                Provider = LlmProvider.Gemini
            },
            new LlmModelInfo
            {
                Id = "gemini-3.1-pro-preview", DisplayName = "Gemini 3.1 Pro (Preview)",
                Description = "Top quality – Google's most capable, $2/$12 per 1M tokens; still a preview",
                Provider = LlmProvider.Gemini
            }
        };

        public static readonly LlmModelInfo[] MistralModels =
        {
            new LlmModelInfo
            {
                Id = "mistral-large-latest", DisplayName = "Mistral Large",
                Description = "Flagship – best quality, ideal for complex translation tasks",
                Provider = LlmProvider.Mistral
            },
            new LlmModelInfo
            {
                Id = "mistral-small-latest", DisplayName = "Mistral Small",
                Description = "Fast and cost-effective – great for large batch jobs",
                Provider = LlmProvider.Mistral
            }
        };

        public static readonly LlmModelInfo[] DeepSeekModels =
        {
            new LlmModelInfo
            {
                Id = "deepseek-v4-pro", DisplayName = "DeepSeek V4 Pro",
                Description = "Flagship – top-tier reasoning and multilingual quality",
                Provider = LlmProvider.DeepSeek
            },
            new LlmModelInfo
            {
                Id = "deepseek-v4-flash", DisplayName = "DeepSeek V4 Flash",
                Description = "Fast and cost-effective – great for high-volume translation",
                Provider = LlmProvider.DeepSeek
            }
        };

        public static readonly LlmModelInfo[] OllamaModels =
        {
            new LlmModelInfo
            {
                Id = "translategemma:12b", DisplayName = "TranslateGemma 12B",
                Description = "Best translation quality/size ratio (12 GB RAM)",
                Provider = LlmProvider.Ollama
            },
            new LlmModelInfo
            {
                Id = "translategemma:4b", DisplayName = "TranslateGemma 4B",
                Description = "Lightweight translation model (6 GB RAM)",
                Provider = LlmProvider.Ollama
            },
            new LlmModelInfo
            {
                Id = "qwen3:14b", DisplayName = "Qwen 3 14B",
                Description = "General-purpose, 100+ languages (10 GB RAM)",
                Provider = LlmProvider.Ollama
            },
            new LlmModelInfo
            {
                Id = "aya-expanse:8b", DisplayName = "Aya Expanse 8B",
                Description = "Top Dutch support, high fidelity (8 GB RAM)",
                Provider = LlmProvider.Ollama
            }
        };

        public static readonly LlmModelInfo[] GrokModels =
        {
            new LlmModelInfo
            {
                Id = "grok-4.3", DisplayName = "Grok 4.3",
                Description = "xAI's latest flagship – fast and capable",
                Provider = LlmProvider.Grok
            }
        };

        // The direct short lists' current models under one key, then open-weight
        // models offered here only. IDs checked against openrouter.ai/api/v1/models
        // on 2026-10-02, the GPT-6 ones on 2026-10-07. Superseded entries (Claude
        // Sonnet 5 and Opus 5, GPT-5.5, GPT-5.6, GPT-5.4 Mini, Gemini 3 Flash)
        // left the list but not the price list, so anyone with one saved keeps it. Qwen 3.6 Plus (Free) left because OpenRouter withdrew it:
        // choosing it failed.
        public static readonly LlmModelInfo[] OpenRouterModels =
        {
            new LlmModelInfo
            {
                Id = "anthropic/claude-sonnet-5.5", DisplayName = "Claude Sonnet 5.5",
                Description = "Recommended – newest Sonnet, near-Opus quality at Sonnet cost, 1M context",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "anthropic/claude-opus-5.5", DisplayName = "Claude Opus 5.5",
                Description = "Premium – Anthropic's newest Opus, 1M context. Top choice for hard legal/technical work",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "anthropic/claude-fable-5.1", DisplayName = "Claude Fable 5.1",
                Description = "Maximum capability – Anthropic's most capable model, at double Opus pricing",
                Provider = LlmProvider.OpenRouter
            },
            // The GPT-6 routes reach the same reasoning models as the direct ones,
            // so they need the same flags: no custom temperature, long timeout.
            new LlmModelInfo
            {
                Id = "openai/gpt-6-astra", DisplayName = "GPT-6 Astra",
                Description = "OpenAI's most capable model, 1M context. For the hardest work when cost is secondary",
                Provider = LlmProvider.OpenRouter,
                SupportsTemperature = false,
                IsReasoningModel = true
            },
            new LlmModelInfo
            {
                Id = "openai/gpt-6.1-sol", DisplayName = "GPT-6.1 Sol",
                Description = "Close to Astra's quality at a fifth of its price, 1M context",
                Provider = LlmProvider.OpenRouter,
                SupportsTemperature = false,
                IsReasoningModel = true
            },
            new LlmModelInfo
            {
                Id = "openai/gpt-6-luna", DisplayName = "GPT-6 Luna",
                Description = "OpenAI's cheapest – for high-volume work",
                Provider = LlmProvider.OpenRouter,
                SupportsTemperature = false,
                IsReasoningModel = true
            },
            new LlmModelInfo
            {
                Id = "google/gemini-3.8-flash", DisplayName = "Gemini 3.8 Flash",
                Description = "Google's newest Flash – strong quality at low cost, 1M context",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "google/gemini-3.1-pro-preview", DisplayName = "Gemini 3.1 Pro (Preview)",
                Description = "Google's most capable model; still a preview",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "google/gemma-4-31b-it", DisplayName = "Gemma 4 31B",
                Description = "Open-source – strong multilingual quality, 256K context",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "google/gemma-4-26b-a4b-it", DisplayName = "Gemma 4 26B MoE",
                Description = "Open-source – near-31B quality at a fraction of the cost",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "mistralai/mistral-small-2603", DisplayName = "Mistral Small 4",
                Description = "Very fast and cheap – good multilingual support",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "qwen/qwen3.8-27b:free", DisplayName = "Qwen 3.8 27B (Free)",
                Description = "Free and rate-limited – providers of free models may keep what you send, so not for confidential work",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "deepseek/deepseek-v4-pro", DisplayName = "DeepSeek V4 Pro",
                Description = "DeepSeek flagship – strong multilingual, competitive pricing",
                Provider = LlmProvider.OpenRouter
            },
            new LlmModelInfo
            {
                Id = "deepseek/deepseek-v4-flash", DisplayName = "DeepSeek V4 Flash",
                Description = "DeepSeek fast – great for high-volume translation",
                Provider = LlmProvider.OpenRouter
            }
        };

        /// <summary>
        /// Returns the model array for a given provider key string.
        /// </summary>
        public static LlmModelInfo[] GetModelsForProvider(string providerKey)
        {
            switch (providerKey)
            {
                case ProviderOpenAi: return OpenAiModels;
                case ProviderClaude: return ClaudeModels;
                case ProviderGemini: return GeminiModels;
                case ProviderGrok: return GrokModels;
                case ProviderMistral: return MistralModels;
                case ProviderDeepSeek: return DeepSeekModels;
                case ProviderOpenRouter: return OpenRouterModels;
                case ProviderOllama: return OllamaModels;
                case ProviderCustomOpenAi: return new LlmModelInfo[0]; // Custom models are user-defined
                default: return new LlmModelInfo[0];
            }
        }

        /// <summary>
        /// The model to select for a provider when none is chosen: the first of its
        /// short list, except OpenAI, whose list runs most capable - and dearest -
        /// first. Null for a provider without a list.
        /// </summary>
        public static string DefaultModelId(string providerKey)
        {
            if (providerKey == ProviderOpenAi) return DefaultOpenAiModelId;
            var models = GetModelsForProvider(providerKey);
            return models.Length > 0 ? models[0].Id : null;
        }

        /// <summary>
        /// Looks up a model by ID across all providers. Returns null if not found.
        /// </summary>
        public static LlmModelInfo FindModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;

            var allArrays = new[] { OpenAiModels, ClaudeModels, GeminiModels, GrokModels, MistralModels, DeepSeekModels, OpenRouterModels, OllamaModels };
            foreach (var arr in allArrays)
            {
                foreach (var m in arr)
                {
                    if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the JSON-compatible provider key string for an enum value.
        /// </summary>
        public static string GetProviderKey(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.OpenAi: return ProviderOpenAi;
                case LlmProvider.Claude: return ProviderClaude;
                case LlmProvider.Gemini: return ProviderGemini;
                case LlmProvider.Grok: return ProviderGrok;
                case LlmProvider.Mistral: return ProviderMistral;
                case LlmProvider.DeepSeek: return ProviderDeepSeek;
                case LlmProvider.OpenRouter: return ProviderOpenRouter;
                case LlmProvider.Ollama: return ProviderOllama;
                case LlmProvider.CustomOpenAi: return ProviderCustomOpenAi;
                default: return ProviderOpenAi;
            }
        }

        /// <summary>
        /// Returns the display name for a provider key.
        /// </summary>
        public static string GetProviderDisplayName(string providerKey)
        {
            switch (providerKey)
            {
                case ProviderOpenAi: return "OpenAI";
                case ProviderClaude: return "Claude (Anthropic)";
                case ProviderGemini: return "Gemini (Google)";
                case ProviderGrok: return "Grok (xAI)";
                case ProviderMistral: return "Mistral AI";
                case ProviderDeepSeek: return "DeepSeek";
                case ProviderOpenRouter: return "OpenRouter";
                case ProviderOllama: return "Ollama (Local)";
                case ProviderCustomOpenAi: return "Custom (OpenAI-compatible)";
                default: return providerKey;
            }
        }

        /// <summary>
        /// All provider keys in display order.
        /// </summary>
        public static readonly string[] AllProviderKeys =
        {
            ProviderOpenAi, ProviderClaude, ProviderGemini, ProviderGrok, ProviderMistral, ProviderDeepSeek, ProviderOpenRouter, ProviderOllama, ProviderCustomOpenAi
        };
    }
}
