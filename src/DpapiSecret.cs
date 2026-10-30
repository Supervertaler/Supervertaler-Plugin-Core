using System;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// Encrypts secrets at rest with Windows DPAPI (CurrentUser scope).
    ///
    /// <para>Used for the GroupShare server password (issue #35), the AI
    /// provider API keys in Trados's settings (issue #115), and the shared
    /// locked key file (<see cref="ApiKeyStore"/>). The ciphertext opens only
    /// for the Windows account that produced it: Windows locks it with that
    /// account's own key, kept in the roaming part of the profile
    /// (<c>%APPDATA%\Microsoft\Protect</c>). A roaming profile carries that key,
    /// and so the secret, to the account's other computers; another account,
    /// or a file copied to another computer without the profile, gets nothing
    /// usable. An administrator resetting a local account's password also makes
    /// it unreadable (Windows error 0x8009000B).</para>
    ///
    /// <para><b>What this does not do.</b> Any process running as the same user
    /// can call <see cref="Unprotect"/> with no prompt. It is not a defence
    /// against malware running under the user's own account, and nothing that
    /// stores a secret locally on a single-user desktop is. It defends against
    /// the file moving, which is how these secrets actually leak.</para>
    ///
    /// <para><b>Two ways to fail.</b> <see cref="Protect"/> and
    /// <see cref="Unprotect"/> return "" rather than throwing, because a
    /// settings load must not die on an undecryptable value - so "" means both
    /// "nothing" and "refused". <see cref="TryUnprotect"/> tells the two apart
    /// and gives Windows' error code, for callers that must report an
    /// unreadable secret as such; see <see cref="AiApiKeys.HasUnreadableKeys"/>
    /// for why that distinction matters to the user.</para>
    ///
    /// <para>Lives in core rather than in one plugin so that Trados and memoQ
    /// share a single implementation. <c>ProtectedData</c> is a framework
    /// assembly (System.Security), so this respects core's no-NuGet rule.</para>
    /// </summary>
    public static class DpapiSecret
    {
        /// <summary>
        /// Encrypts <paramref name="plain"/> and returns base64, or "" on empty/failure.
        ///
        /// <para>This is the form every Supervertaler product must produce for the
        /// shared locked key file, or a key locked by one will not open in another
        /// (docs/design/api-key-protection.md §3): the text as UTF-8 with no
        /// byte-order mark and no terminating zero; <c>CryptProtectData</c>,
        /// current user, <c>CRYPTPROTECT_UI_FORBIDDEN</c>, no entropy; standard
        /// base64 with padding and no line breaks. <c>ProtectedData</c> passes an
        /// empty description and an empty entropy blob, which Windows treats as
        /// none: the core test <c>ContractMatchesTheRawWindowsCall</c> opens this
        /// method's output with the raw call and the raw call's output here.</para>
        /// </summary>
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
        /// <see cref="Unprotect"/> that tells "nothing" from "refused". False when
        /// the value is empty, is not base64, or Windows will not unlock it;
        /// <paramref name="windowsError"/> is then Windows' error code (an
        /// HRESULT such as 0x8009000B), or 0 when the value never reached Windows.
        /// The code is for a log: the user's remedy is the same whatever it is.
        /// </summary>
        public static bool TryUnprotect(string protectedBase64, out string plain, out int windowsError)
        {
            plain = null;
            windowsError = 0;
            if (string.IsNullOrEmpty(protectedBase64)) return false;

            byte[] enc;
            try { enc = Convert.FromBase64String(protectedBase64); }
            catch (FormatException) { return false; }

            try
            {
                var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                plain = Encoding.UTF8.GetString(dec);
                return true;
            }
            catch (Exception ex)
            {
                // CryptographicException carries the code Windows set.
                windowsError = ex.HResult;
                return false;
            }
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
