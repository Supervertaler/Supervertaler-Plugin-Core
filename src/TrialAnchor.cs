using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Supervertaler.Core
{
    /// <summary>
    /// The trial's anchor outside the data folder, shared by every Supervertaler
    /// product on this computer and Windows account. It is why a lost, moved or
    /// edited licence file never reads as a fresh install.
    ///
    /// It is shared rather than per product on purpose: one anchor per product
    /// would give every computer one trial per product, which contradicts "an
    /// activation is a computer" and could not be undone once people had used it.
    ///
    /// It holds two values that move in OPPOSITE directions:
    ///
    ///   - start: when the trial began. Only ever moves EARLIER.
    ///   - seen:  the latest time any product has observed. Only ever moves
    ///            LATER, and trial expiry is measured against it.
    ///
    /// Each value is guarded separately, by merging it with what is already
    /// stored in its own direction. One rule for both would get one right and
    /// quietly disable the other while still passing a naive test.
    ///
    /// Known and accepted: a clock that is ever wrong in the FUTURE direction,
    /// even briefly, leaves "seen" there, and a trial measured against it ends
    /// early for good. It affects trials only - the licensed offline window
    /// runs on the real clock. Do not "fix" it by capping "seen": that is the
    /// whole of what makes winding the clock back useless.
    ///
    /// Two products can write at once and the registry has no compare-and-swap,
    /// so two simultaneous writes can leave the second one's values. Both are
    /// legitimate and the next write heals it. The guard narrows that race; it
    /// does not close it, and no lock is taken for it.
    ///
    /// The value the Trados plugin wrote before the anchor was shared is still
    /// read and merged, so nobody's anchor is lost. It is never written: nothing
    /// here writes to a product-named location.
    ///
    /// Every failure degrades to the caller's own inputs. A registry problem
    /// costs the extra protection, never a legitimate user's access.
    /// </summary>
    internal static class TrialAnchor
    {
        internal const string SharedSubKey = @"Software\Supervertaler";
        internal const string LegacySubKey = @"Software\Supervertaler\Trados";

        private const string ValueName = "ts";

        // Folded into the signing key. Not a secret - it ships in the binary -
        // and kept as it was so the legacy value still verifies.
        private static readonly byte[] Pepper =
            Encoding.UTF8.GetBytes("sv-trados-trial-anchor-v1");

        /// <summary>Result of <see cref="Reconcile(string, DateTime, DateTime)"/>.</summary>
        internal struct Anchored
        {
            /// <summary>Authoritative trial start (earliest known).</summary>
            public DateTime Start;

            /// <summary>
            /// The "now" to measure expiry against: never earlier than the
            /// latest time any product has observed. Equal to the real clock for
            /// every honest user.
            /// </summary>
            public DateTime EffectiveNow;
        }

        internal struct Record
        {
            public DateTime Start;
            public DateTime Seen;
        }

        /// <summary>
        /// Reconciles a candidate trial start and the real clock with the
        /// anchor, returning the authoritative start and the effective "now".
        /// <paramref name="candidateStart"/> is what the caller would otherwise
        /// use: the licence file's start, or "now" for a brand-new trial.
        /// </summary>
        public static Anchored Reconcile(string fingerprint, DateTime candidateStart, DateTime realNow)
            => Reconcile(SharedSubKey, LegacySubKey, fingerprint, candidateStart, realNow);

        /// <summary>The same, against given keys. For the tests, which must never touch the real anchor.</summary>
        internal static Anchored Reconcile(
            string sharedSubKey, string legacySubKey,
            string fingerprint, DateTime candidateStart, DateTime realNow)
        {
            candidateStart = candidateStart.ToUniversalTime();
            realNow = realNow.ToUniversalTime();

            try
            {
                // Legacy first, so the shared read sits as close to the write
                // as it can. A legacy value that cannot be read is simply absent.
                Record? legacy = null;
                try { legacy = Read(legacySubKey, fingerprint); }
                catch { }

                var shared = Read(sharedSubKey, fingerprint);

                // Each value in its own direction. Neither rule may be applied
                // to the other value.
                var start = candidateStart;
                if (shared.HasValue && shared.Value.Start < start) start = shared.Value.Start;
                if (legacy.HasValue && legacy.Value.Start < start) start = legacy.Value.Start;

                var seen = realNow;
                if (shared.HasValue && shared.Value.Seen > seen) seen = shared.Value.Seen;
                if (legacy.HasValue && legacy.Value.Seen > seen) seen = legacy.Value.Seen;

                if (!shared.HasValue || shared.Value.Start != start || shared.Value.Seen != seen)
                    Write(sharedSubKey, fingerprint, start, seen);

                return new Anchored { Start = start, EffectiveNow = seen };
            }
            catch
            {
                return new Anchored { Start = candidateStart, EffectiveNow = realNow };
            }
        }

        /// <summary>
        /// The stored values, or null when there are none or they do not verify
        /// (edited, or copied from another computer or account).
        /// </summary>
        internal static Record? Read(string subKey, string fingerprint)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                if (!(key?.GetValue(ValueName) is string raw) || string.IsNullOrEmpty(raw))
                    return null;

                // "<payload>.<signature>", payload "start" (the first format) or
                // "start|seen".
                var dot = raw.LastIndexOf('.');
                if (dot <= 0 || dot >= raw.Length - 1)
                    return null;

                var payload = raw.Substring(0, dot);
                var sig = raw.Substring(dot + 1);

                if (!ConstantTimeEquals(sig, Sign(payload, fingerprint)))
                    return null;

                var parts = payload.Split('|');
                if (!TryParseUtc(parts[0], out var start))
                    return null;

                // The first format had no high-water mark; seed it from the start.
                var seen = parts.Length > 1 && TryParseUtc(parts[1], out var s) ? s : start;

                return new Record { Start = start, Seen = seen };
            }
        }

        /// <summary>
        /// Writes both values as given. Unguarded - <see cref="Reconcile(string, string, string, DateTime, DateTime)"/>
        /// is the only caller in the product; the tests use it to set a scene.
        /// </summary>
        internal static void Write(string subKey, string fingerprint, DateTime start, DateTime seen)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                if (key == null) return;
                var payload =
                    start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" +
                    seen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                key.SetValue(ValueName, payload + "." + Sign(payload, fingerprint),
                    RegistryValueKind.String);
            }
        }

        private static bool TryParseUtc(string s, out DateTime utc)
        {
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts))
            {
                utc = ts.ToUniversalTime();
                return true;
            }
            utc = default(DateTime);
            return false;
        }

        private static string Sign(string payload, string fingerprint)
        {
            using (var hmac = new HMACSHA256(DeriveKey(fingerprint)))
            {
                var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var sb = new StringBuilder(mac.Length * 2);
                foreach (var b in mac)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static byte[] DeriveKey(string fingerprint)
        {
            using (var sha = SHA256.Create())
            {
                var fp = Encoding.UTF8.GetBytes(fingerprint ?? "");
                var buf = new byte[fp.Length + Pepper.Length];
                Buffer.BlockCopy(fp, 0, buf, 0, fp.Length);
                Buffer.BlockCopy(Pepper, 0, buf, fp.Length, Pepper.Length);
                return sha.ComputeHash(buf);
            }
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
