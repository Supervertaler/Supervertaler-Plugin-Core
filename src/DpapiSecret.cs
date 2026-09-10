using System;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// Encrypts secrets at rest with Windows DPAPI (CurrentUser scope).
    ///
    /// <para>Used for the GroupShare server password (issue #35) and, from
    /// 18.20.190, for the AI provider API keys (issue #115). The ciphertext is
    /// bound to the Windows user AND the machine that produced it, so a settings
    /// file copied elsewhere - to a backup, a sync folder, a support bundle, a
    /// new laptop - carries nothing usable. That is the point of it.</para>
    ///
    /// <para><b>What this does not do.</b> Any process running as the same user
    /// can call <see cref="Unprotect"/> with no prompt. It is not a defence
    /// against malware running under the user's own account, and nothing that
    /// stores a secret locally on a single-user desktop is. It defends against
    /// the file moving, which is how these secrets actually leak.</para>
    ///
    /// <para><b>Failure is silent by design here</b> - both methods return ""
    /// rather than throwing, because a settings load must not die on an
    /// undecryptable value. Callers that need to tell "no secret" from "secret
    /// present but unreadable" have to check the ciphertext field themselves;
    /// see <see cref="AiApiKeys.HasUnreadableKeys"/> for why that distinction
    /// matters to the user.</para>
    ///
    /// <para>Lives in core rather than in one plugin so that Trados and memoQ
    /// share a single implementation. <c>ProtectedData</c> is a framework
    /// assembly (System.Security), so this respects core's no-NuGet rule.</para>
    /// </summary>
    public static class DpapiSecret
    {
        /// <summary>Encrypts <paramref name="plain"/> and returns base64, or "" on empty/failure.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        /// <summary>Decrypts a base64 value produced by <see cref="Protect"/>, or "" on failure.</summary>
        public static string Unprotect(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return "";
            try
            {
                var enc = Convert.FromBase64String(protectedBase64);
                var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }

        /// <summary>
        /// True when <paramref name="protectedBase64"/> holds something that was
        /// meant to be a secret but cannot be decrypted here - the settings file
        /// came from another Windows user or another machine. Distinguishing this
        /// from "no key was ever entered" is what lets the UI say "please enter
        /// your key again" instead of showing an empty box and letting the user
        /// discover the problem as a 401 from the provider.
        /// </summary>
        public static bool IsUnreadable(string protectedBase64)
        {
            return !string.IsNullOrEmpty(protectedBase64)
                && string.IsNullOrEmpty(Unprotect(protectedBase64));
        }
    }
}
