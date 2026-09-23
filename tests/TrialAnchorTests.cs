using System;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The anchor holds two values that move in opposite directions. Each test
    /// asserts that a value did not move the forbidden way, never that it holds
    /// the best value ever written: two simultaneous writers can legitimately
    /// leave the second one's value, and a test chasing that race would be
    /// flaky.
    /// </summary>
    [Tests]
    internal static class TrialAnchorTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Seen = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        private static TrialAnchor.Record Stored(Scene s) =>
            TrialAnchor.Read(s.AnchorKey, Scene.Fingerprint) ?? throw new AssertFailed("no anchor stored");

        public static void Start_NeverMovesLater()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, Start, Seen);

                var a = TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint,
                    candidateStart: Start.AddDays(5), realNow: Seen.AddHours(1));

                Assert.Equal(Start, a.Start, "returned start");
                Assert.Equal(Start, Stored(s).Start, "stored start");
            }
        }

        public static void Seen_NeverMovesEarlier()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, Start, Seen);

                var a = TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint,
                    candidateStart: Start, realNow: Seen.AddDays(-5));

                Assert.Equal(Seen, a.EffectiveNow, "effective now (a clock wound back)");
                Assert.Equal(Seen, Stored(s).Seen, "stored seen");
            }
        }

        /// <summary>
        /// One write that moves both values their allowed way, and one that
        /// tries both forbidden ways. A single rule for both values - "keep the
        /// earlier" or "keep the later" - fails one of these two.
        /// </summary>
        public static void TheTwoValues_AreGuardedInOppositeDirections()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, Start, Seen);

                // Earlier start: allowed. Earlier clock: not.
                TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint,
                    candidateStart: Start.AddDays(-2), realNow: Seen.AddDays(-3));
                Assert.Equal(Start.AddDays(-2), Stored(s).Start, "start moved earlier");
                Assert.Equal(Seen, Stored(s).Seen, "seen did not move earlier");

                // Later clock: allowed. Later start: not.
                TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint,
                    candidateStart: Start.AddDays(2), realNow: Seen.AddDays(3));
                Assert.Equal(Start.AddDays(-2), Stored(s).Start, "start did not move later");
                Assert.Equal(Seen.AddDays(3), Stored(s).Seen, "seen moved later");
            }
        }

        public static void TheTradosAnchor_IsHonoured_AndNeverWritten()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.LegacyAnchorKey, Scene.Fingerprint, Start, Seen);
                var before = RawValue(s.LegacyAnchorKey);

                var now = Seen.AddDays(1);
                var a = TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint, now, now);

                Assert.Equal(Start, a.Start, "the Trados start is the start");
                Assert.Equal(Start, Stored(s).Start, "and is now in the shared anchor");
                Assert.Equal(before, RawValue(s.LegacyAnchorKey), "the Trados value is unchanged");

                // And a Trados high-water mark later than the clock still counts.
                TrialAnchor.Write(s.LegacyAnchorKey, Scene.Fingerprint, Start, Seen.AddDays(20));
                var b = TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint, now, now);
                Assert.Equal(Seen.AddDays(20), b.EffectiveNow, "the Trados high-water mark counts");
            }
        }

        public static void AnAnchorFromAnotherComputer_IsIgnored()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, "some-other-computer", Start, Seen);

                var now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
                var a = TrialAnchor.Reconcile(s.AnchorKey, s.LegacyAnchorKey, Scene.Fingerprint, now, now);

                Assert.Equal(now, a.Start, "a value that does not verify is not trusted");
            }
        }

        public static void ARegistryFailure_FailsOpen()
        {
            // A key name the registry refuses, standing in for "registry unavailable".
            var bad = new string('x', 300);
            var now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            var a = TrialAnchor.Reconcile(bad, bad, Scene.Fingerprint, Start, now);

            Assert.Equal(Start, a.Start, "the caller's start comes back");
            Assert.Equal(now, a.EffectiveNow, "the caller's clock comes back");
        }

        /// <summary>
        /// Four processes reconcile the same anchor at once, half of the time
        /// trying to move each value the wrong way. However the writes
        /// interleave, neither value may ever pass where it started in its
        /// forbidden direction.
        /// </summary>
        public static void FourProcessHammer_NeitherValueEverMovesTheWrongWay()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, Start, Seen);

                var results = Program.RunChildren(4, i =>
                    $"--anchor-hammer \"{s.AnchorKey}\" \"{s.LegacyAnchorKey}\" \"{s.GoFile}\" 400 " +
                    Start.Ticks.ToString(CultureInfo.InvariantCulture) + " " +
                    Seen.Ticks.ToString(CultureInfo.InvariantCulture) + " " + i,
                    s.GoFile);

                Console.WriteLine("      " + string.Join(" | ", results));
                Assert.True(results.All(r => r == "violations=0 unreadable=0"),
                    "every process saw both values within bounds: " + string.Join(" | ", results));

                var final = Stored(s);
                Assert.True(final.Start <= Start, "final start is not later than it began");
                Assert.True(final.Seen >= Seen, "final seen is not earlier than it began");
            }
        }

        private static string RawValue(string subKey)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                return key?.GetValue("ts") as string;
        }
    }
}
