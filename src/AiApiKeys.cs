using System;
using System.Runtime.Serialization;

namespace Supervertaler.Core
{
    /// <summary>
    /// Per-provider API keys, as stored in a plugin's own settings.
    ///
    /// Lifted out of the Trados plugin's AiSettings so that the key-resolution
    /// chain in <see cref="LlmClient"/> — plugin-local key, then the shared
    /// Supervertaler desktop settings — can be shared rather than reimplemented
    /// per plugin.
    ///
    /// <para><b>Keys are encrypted at rest</b> with Windows DPAPI from
    /// 18.20.190 (issue #115). Each provider has two on-disk members: the
    /// original plaintext name, which is READ but never written again, and a
    /// "<c>_protected</c>" sibling holding the ciphertext. The property callers
    /// use (<see cref="Claude"/> and friends) is deliberately NOT a DataMember:
    /// reading it decrypts, writing it encrypts and drops the legacy value. No
    /// call site had to change.</para>
    ///
    /// <para><b>The failure path is the risk.</b> Ciphertext is bound to the
    /// Windows user and machine that wrote it, so a settings file restored from
    /// a backup or carried to a new laptop will not decrypt. That is intended —
    /// but it must reach the user as "please enter your key again", never as an
    /// empty field, which presents as an authentication failure from the
    /// provider and sends them debugging the wrong thing. Use
    /// <see cref="HasUnreadableKeys"/> to tell the two apart.</para>
    ///
    /// The DataMember names are the on-disk contract of settings files already in
    /// the field. Do not rename one without a migration.
    /// </summary>
    [DataContract]
    public class AiApiKeys
    {
        // ── On disk ──────────────────────────────────────────────────────────
        // "<name>"           legacy plaintext: read for migration, never written
        //                    again (EmitDefaultValue=false + nulled on migrate)
        // "<name>_protected" DPAPI ciphertext, what is written from now on

        [DataMember(Name = "openai", EmitDefaultValue = false)]
        public string OpenAiPlain { get; set; }
        [DataMember(Name = "openai_protected", EmitDefaultValue = false)]
        public string OpenAiProtected { get; set; }

        [DataMember(Name = "claude", EmitDefaultValue = false)]
        public string ClaudePlain { get; set; }
        [DataMember(Name = "claude_protected", EmitDefaultValue = false)]
        public string ClaudeProtected { get; set; }

        [DataMember(Name = "gemini", EmitDefaultValue = false)]
        public string GeminiPlain { get; set; }
        [DataMember(Name = "gemini_protected", EmitDefaultValue = false)]
        public string GeminiProtected { get; set; }

        [DataMember(Name = "grok", EmitDefaultValue = false)]
        public string GrokPlain { get; set; }
        [DataMember(Name = "grok_protected", EmitDefaultValue = false)]
        public string GrokProtected { get; set; }

        [DataMember(Name = "mistral", EmitDefaultValue = false)]
        public string MistralPlain { get; set; }
        [DataMember(Name = "mistral_protected", EmitDefaultValue = false)]
        public string MistralProtected { get; set; }

        [DataMember(Name = "deepseek", EmitDefaultValue = false)]
        public string DeepSeekPlain { get; set; }
        [DataMember(Name = "deepseek_protected", EmitDefaultValue = false)]
        public string DeepSeekProtected { get; set; }

        [DataMember(Name = "openrouter", EmitDefaultValue = false)]
        public string OpenRouterPlain { get; set; }
        [DataMember(Name = "openrouter_protected", EmitDefaultValue = false)]
        public string OpenRouterProtected { get; set; }

        [DataMember(Name = "custom_openai", EmitDefaultValue = false)]
        public string CustomOpenAiPlain { get; set; }
        [DataMember(Name = "custom_openai_protected", EmitDefaultValue = false)]
        public string CustomOpenAiProtected { get; set; }

        // ── What callers use ─────────────────────────────────────────────────
        // Not DataMembers, so DataContractJsonSerializer ignores them entirely.

        public string OpenAi
        {
            get { return Reveal(OpenAiProtected, OpenAiPlain); }
            set { OpenAiProtected = DpapiSecret.Protect(value); OpenAiPlain = null; }
        }

        public string Claude
        {
            get { return Reveal(ClaudeProtected, ClaudePlain); }
            set { ClaudeProtected = DpapiSecret.Protect(value); ClaudePlain = null; }
        }

        public string Gemini
        {
            get { return Reveal(GeminiProtected, GeminiPlain); }
            set { GeminiProtected = DpapiSecret.Protect(value); GeminiPlain = null; }
        }

        public string Grok
        {
            get { return Reveal(GrokProtected, GrokPlain); }
            set { GrokProtected = DpapiSecret.Protect(value); GrokPlain = null; }
        }

        public string Mistral
        {
            get { return Reveal(MistralProtected, MistralPlain); }
            set { MistralProtected = DpapiSecret.Protect(value); MistralPlain = null; }
        }

        public string DeepSeek
        {
            get { return Reveal(DeepSeekProtected, DeepSeekPlain); }
            set { DeepSeekProtected = DpapiSecret.Protect(value); DeepSeekPlain = null; }
        }

        public string OpenRouter
        {
            get { return Reveal(OpenRouterProtected, OpenRouterPlain); }
            set { OpenRouterProtected = DpapiSecret.Protect(value); OpenRouterPlain = null; }
        }

        public string CustomOpenAi
        {
            get { return Reveal(CustomOpenAiProtected, CustomOpenAiPlain); }
            set { CustomOpenAiProtected = DpapiSecret.Protect(value); CustomOpenAiPlain = null; }
        }

        /// <summary>
        /// Ciphertext wins where present; the legacy plaintext is the fallback
        /// until migration runs. Returns "" for an undecryptable value rather
        /// than throwing — a settings load must survive a file from another
        /// machine.
        /// </summary>
        private static string Reveal(string protectedValue, string plainValue)
        {
            if (!string.IsNullOrEmpty(protectedValue))
                return DpapiSecret.Unprotect(protectedValue);
            return plainValue ?? "";
        }

        /// <summary>
        /// Encrypts any plaintext key found on disk and clears the plaintext
        /// field, so the next save writes ciphertext only. Returns true when it
        /// changed something, so the caller can save exactly once instead of on
        /// every startup.
        ///
        /// <para>One-way and idempotent: a second run finds nothing to do.</para>
        /// </summary>
        public bool MigrateToProtected()
        {
            var changed = false;
            changed |= MigrateOne(OpenAiPlain,       v => OpenAi       = v);
            changed |= MigrateOne(ClaudePlain,       v => Claude       = v);
            changed |= MigrateOne(GeminiPlain,       v => Gemini       = v);
            changed |= MigrateOne(GrokPlain,         v => Grok         = v);
            changed |= MigrateOne(MistralPlain,      v => Mistral      = v);
            changed |= MigrateOne(DeepSeekPlain,     v => DeepSeek     = v);
            changed |= MigrateOne(OpenRouterPlain,   v => OpenRouter   = v);
            changed |= MigrateOne(CustomOpenAiPlain, v => CustomOpenAi = v);
            return changed;
        }

        private static bool MigrateOne(string plain, Action<string> assign)
        {
            if (string.IsNullOrEmpty(plain)) return false;
            assign(plain);          // the setter encrypts and nulls the plaintext
            return true;
        }

        /// <summary>
        /// True when at least one provider holds ciphertext this machine cannot
        /// decrypt — the settings came from another Windows user or another PC.
        /// The distinction from "no key entered" is what the UI needs in order to
        /// say "please enter your key again" instead of showing a blank box.
        /// </summary>
        public bool HasUnreadableKeys
        {
            get
            {
                return DpapiSecret.IsUnreadable(OpenAiProtected)
                    || DpapiSecret.IsUnreadable(ClaudeProtected)
                    || DpapiSecret.IsUnreadable(GeminiProtected)
                    || DpapiSecret.IsUnreadable(GrokProtected)
                    || DpapiSecret.IsUnreadable(MistralProtected)
                    || DpapiSecret.IsUnreadable(DeepSeekProtected)
                    || DpapiSecret.IsUnreadable(OpenRouterProtected)
                    || DpapiSecret.IsUnreadable(CustomOpenAiProtected);
            }
        }
    }
}
