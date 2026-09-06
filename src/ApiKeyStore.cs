using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// One API-key file for every Supervertaler product (Trados #108):
    /// <c>&lt;data root&gt;\settings\api-keys.json</c>, one key per provider, plain
    /// text, hand-editable. Trados and memoQ read it through this class; Sidekick
    /// reads the same file with its own parser.
    ///
    /// It is the source of truth: a product's own settings hold a key only as a
    /// legacy fallback, and <see cref="MigrateFrom"/> fills the file from those the
    /// first time, so nothing is typed twice. Plain text on purpose - a key that can
    /// be rotated by pasting a line into a text file is a key that actually gets
    /// rotated, and anyone who can read the file can read the whole profile anyway.
    ///
    /// Keys are stored under the plugin's provider ids (claude, openai, gemini, grok,
    /// mistral, deepseek, openrouter). Common aliases are accepted on read
    /// (anthropic, google, xai), so a file written by hand in either vocabulary works.
    /// </summary>
    public static class ApiKeyStore
    {
        public static string FilePath
        {
            get
            {
                try { return Path.Combine(SupervertalerPaths.Root, "settings", "api-keys.json"); }
                catch { return null; }
            }
        }

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "anthropic", LlmModels.ProviderClaude },
            { "google", LlmModels.ProviderGemini },
            { "xai", LlmModels.ProviderGrok },
            { "x.ai", LlmModels.ProviderGrok },
            { "custom", LlmModels.ProviderCustomOpenAi },
        };

        private static readonly object Gate = new object();

        public static string Canonical(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey)) return providerKey;
            var k = providerKey.Trim().ToLowerInvariant();
            return Aliases.TryGetValue(k, out var c) ? c : k;
        }

        /// <summary>The key for a provider, or null when the file has none. Never throws.</summary>
        public static string Get(string providerKey)
        {
            try
            {
                var map = Load();
                return map.TryGetValue(Canonical(providerKey), out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>Writes one key (empty removes it). Returns false if the file could not be written.</summary>
        public static bool Set(string providerKey, string key)
        {
            lock (Gate)
            {
                try
                {
                    var map = Load();
                    var p = Canonical(providerKey);
                    if (string.IsNullOrWhiteSpace(key)) map.Remove(p);
                    else map[p] = key.Trim();
                    return Save(map);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Adds every non-empty key from a product's own settings that the file does
        /// not have yet. Never overwrites: the file is the source of truth once it
        /// holds a key. Returns how many were added.
        /// </summary>
        public static int MigrateFrom(AiApiKeys local)
        {
            if (local == null) return 0;
            lock (Gate)
            {
                try
                {
                    var map = Load();
                    int added = 0;
                    void Take(string provider, string key)
                    {
                        if (string.IsNullOrWhiteSpace(key)) return;
                        if (map.TryGetValue(provider, out var have) && !string.IsNullOrWhiteSpace(have)) return;
                        map[provider] = key.Trim(); added++;
                    }
                    Take(LlmModels.ProviderOpenAi, local.OpenAi);
                    Take(LlmModels.ProviderClaude, local.Claude);
                    Take(LlmModels.ProviderGemini, local.Gemini);
                    Take(LlmModels.ProviderGrok, local.Grok);
                    Take(LlmModels.ProviderMistral, local.Mistral);
                    Take(LlmModels.ProviderDeepSeek, local.DeepSeek);
                    Take(LlmModels.ProviderOpenRouter, local.OpenRouter);
                    if (added > 0 && !Save(map)) return 0;
                    return added;
                }
                catch { return 0; }
            }
        }

        /// <summary>The whole file as provider → key, canonical ids. Empty when absent.</summary>
        public static Dictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
            var json = File.ReadAllText(path, Encoding.UTF8);
            // A flat object of strings; nothing else is expected, and anything else is ignored.
            foreach (Match m in Regex.Matches(json, "\"(?<k>[^\"\\\\]+)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\""))
            {
                var k = m.Groups["k"].Value;
                if (k.StartsWith("_")) continue;   // "_comment" and the like
                map[Canonical(k)] = Unescape(m.Groups["v"].Value);
            }
            return map;
        }

        private static bool Save(Dictionary<string, string> map)
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"_comment\": \"API keys shared by Supervertaler for Trados, Supervertaler for memoQ and Supervertaler Sidekick. One key per provider; edit by hand or in any product's settings.\",");
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append("  \"").Append(Escape(keys[i])).Append("\": \"").Append(Escape(map[keys[i]] ?? "")).Append("\"");
                sb.AppendLine(i < keys.Count - 1 ? "," : "");
            }
            sb.AppendLine("}");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return true;
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Unescape(string s) => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");

        // ─── Does this look like a key for that provider? ─────────────────────────────

        /// <summary>
        /// A sentence when the key plainly belongs to another service, else null. Only
        /// the prefixes that are stable and public are checked; an unknown shape passes.
        /// </summary>
        public static string CheckShape(string providerKey, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var k = key.Trim();
            switch (Canonical(providerKey))
            {
                case LlmModels.ProviderClaude:
                    return k.StartsWith("sk-ant-") ? null : "This does not look like an Anthropic key - they start with sk-ant-.";
                case LlmModels.ProviderOpenAi:
                    if (k.StartsWith("sk-or-")) return "This is an OpenRouter key (sk-or-), not an OpenAI key.";
                    if (k.StartsWith("sk-ant-")) return "This is an Anthropic key (sk-ant-), not an OpenAI key.";
                    return k.StartsWith("sk-") ? null : "This does not look like an OpenAI key - they start with sk- or sk-proj-.";
                case LlmModels.ProviderGemini:
                    return k.StartsWith("AIza") ? null : "This does not look like a Gemini key - they start with AIza.";
                case LlmModels.ProviderGrok:
                    return k.StartsWith("xai-") ? null : "This does not look like an xAI key - they start with xai-.";
                case LlmModels.ProviderOpenRouter:
                    return k.StartsWith("sk-or-") ? null : "This does not look like an OpenRouter key - they start with sk-or-.";
                default:
                    return null;
            }
        }
    }
}
