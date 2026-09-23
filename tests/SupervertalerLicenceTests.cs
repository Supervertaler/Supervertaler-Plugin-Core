using System;
using System.IO;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The licence as a product sees it at startup: the copy from the Trados
    /// location, the trial from the anchor, the states, and every way the file
    /// can be missing or unreadable. The network calls (activate, deactivate,
    /// validate) are not exercised here: they need a real key and are part of
    /// the manual test.
    /// </summary>
    [Tests]
    internal static class SupervertalerLicenceTests
    {
        private const string Key = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";

        private static LicenceRecord Activated(DateTime lastValidated, string status = "active") => new LicenceRecord
        {
            LicenseKey = Key,
            InstanceId = "instance-1",
            VariantName = "Annual",
            Status = status,
            ActivatedAt = lastValidated,
            LastValidatedAt = lastValidated,
            TrialStartedAt = DateTime.UtcNow.AddDays(-200),
            MachineFingerprint = Scene.Fingerprint,
        };

        // ─── The copy from the Trados location ──────────────────────

        public static void TheTradosLicence_IsCopiedByteForByte_AndLeftInPlace()
        {
            using (var s = new Scene())
            {
                Scene.WriteTradosStyle(s.LegacyPath, Activated(DateTime.UtcNow.AddDays(-1)));
                var legacyBefore = File.ReadAllBytes(s.LegacyPath);
                var legacyTime = File.GetLastWriteTimeUtc(s.LegacyPath);

                var licence = s.Open();

                Assert.Equal(LicenceState.Licensed, licence.State, "state");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(legacyBefore), "the shared file is a byte-for-byte copy");
                Assert.True(File.ReadAllBytes(s.LegacyPath).SequenceEqual(legacyBefore), "the Trados file is unchanged");
                Assert.Equal(legacyTime, File.GetLastWriteTimeUtc(s.LegacyPath), "the Trados file was not rewritten");
                Assert.Equal("AAAAAAAA...EEEE", licence.MaskedKey, "masked key");
            }
        }

        public static void OnceTheSharedFileExists_TheTradosFileIsNotReadAgain()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(new LicenceRecord
                {
                    TrialStartedAt = DateTime.UtcNow.AddDays(-2),
                    MachineFingerprint = Scene.Fingerprint,
                }), replaceExisting: false);
                Scene.WriteTradosStyle(s.LegacyPath, Activated(DateTime.UtcNow.AddDays(-1)));

                Assert.Equal(LicenceState.Trial, s.Open().State, "the shared file decides");
            }
        }

        public static void TwoProductsStarting_OnAnExistingTradosMachine_BothFindTheLicence()
        {
            using (var s = new Scene())
            {
                Scene.WriteTradosStyle(s.LegacyPath, Activated(DateTime.UtcNow.AddDays(-1)));
                var legacy = File.ReadAllBytes(s.LegacyPath);

                var results = Program.RunChildren(2, i =>
                    $"--open \"{s.SharedPath}\" \"{s.LegacyPath}\" \"{s.AnchorKey}\" \"{s.LegacyAnchorKey}\" \"{s.GoFile}\"",
                    s.GoFile);

                Assert.True(results.All(r => r.StartsWith("Licensed ") && r.EndsWith(" True")),
                    "both products licensed: " + string.Join(" | ", results));
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(legacy), "one copy, intact");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        public static void TwoProductsStarting_OnANewMachine_AgreeOnOneTrial()
        {
            using (var s = new Scene())
            {
                var results = Program.RunChildren(2, i =>
                    $"--open \"{s.SharedPath}\" \"{s.LegacyPath}\" \"{s.AnchorKey}\" \"{s.LegacyAnchorKey}\" \"{s.GoFile}\"",
                    s.GoFile);

                Assert.True(results.All(r => r.StartsWith("Trial ")), "both on trial: " + string.Join(" | ", results));
                var ends = results.Select(r => long.Parse(r.Split(' ')[1])).ToArray();
                Assert.True(Math.Abs(ends[0] - ends[1]) < TimeSpan.FromSeconds(5).Ticks,
                    "one trial, not two: the ends differ by " + TimeSpan.FromTicks(Math.Abs(ends[0] - ends[1])));
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        // ─── The trial ──────────────────────────────────────────────

        public static void ANewComputer_StartsATrial()
        {
            using (var s = new Scene())
            {
                var licence = s.Open();

                Assert.Equal(LicenceState.Trial, licence.State, "state");
                Assert.Equal(14, licence.TrialDaysRemaining, "days remaining");
                Assert.True(Math.Abs((licence.TrialEndsUtc - DateTime.UtcNow.AddDays(14)).TotalMinutes) < 1, "trial ends in 14 days");
                Assert.True(!licence.HasKey, "no key");
                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.SharedPath, out _, out _), "the shared file was created");
                Assert.True(!File.Exists(s.LegacyPath), "nothing was written to the Trados location");
            }
        }

        public static void ALostLicenceFile_DoesNotStartANewTrial()
        {
            using (var s = new Scene())
            {
                var started = DateTime.UtcNow.AddDays(-20);
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, started, DateTime.UtcNow.AddDays(-1));

                var licence = s.Open();

                Assert.Equal(LicenceState.Expired, licence.State, "state");
                Assert.Equal(0, licence.TrialDaysRemaining, "days remaining");
                Assert.True(Math.Abs((licence.TrialEndsUtc - started.AddDays(14)).TotalSeconds) < 1, "the original trial's end");
            }
        }

        /// <summary>A memoQ-only start on a computer whose trial was begun by an older Trados build.</summary>
        public static void AnOldTradosTrial_CountsForEveryProduct()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.LegacyAnchorKey, Scene.Fingerprint, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow.AddDays(-1));

                Assert.Equal(LicenceState.Expired, s.Open().State, "one trial per computer, not per product");
            }
        }

        public static void ATrialStartEditedLater_IsPulledBackByTheAnchor()
        {
            using (var s = new Scene())
            {
                var real = DateTime.UtcNow.AddDays(-20);
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, real, DateTime.UtcNow);
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(new LicenceRecord
                {
                    TrialStartedAt = DateTime.UtcNow.AddDays(-1),
                    MachineFingerprint = Scene.Fingerprint,
                }), replaceExisting: false);

                Assert.Equal(LicenceState.Expired, s.Open().State, "state");
                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.SharedPath, out var r, out _), "read back");
                Assert.True(Math.Abs((r.TrialStartedAt - real).TotalSeconds) < 1, "the file now carries the real start");
            }
        }

        // ─── Licensed and expired ───────────────────────────────────

        public static void TheOfflineWindow_IsADeadline_NotAnUnknown()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(Activated(DateTime.UtcNow.AddDays(-29))), false);
                var inside = s.Open();
                Assert.Equal(LicenceState.Licensed, inside.State, "29 days since the last confirmation");
                Assert.Equal(0, inside.TrialDaysRemaining, "no trial days when licensed");

                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(Activated(DateTime.UtcNow.AddDays(-31))), true);
                Assert.Equal(LicenceState.Expired, s.Open().State, "31 days since the last confirmation");
            }
        }

        public static void ALicenceTheServerCalledExpired_IsExpired()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(Activated(DateTime.UtcNow.AddDays(-1), "expired")), false);
                Assert.Equal(LicenceState.Expired, s.Open().State, "state");
            }
        }

        // ─── Unknown ────────────────────────────────────────────────

        public static void ADamagedFile_IsUnknownThisSession_AndReplacedForTheNext()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(s.SharedPath));
                File.WriteAllText(s.SharedPath, "not a licence");

                var first = s.Open();
                Assert.Equal(LicenceState.Unknown, first.State, "this session");
                Assert.True(first.DamagedFileFound, "the product is told, so it can ask for the key again");
                Assert.Equal(0, first.TrialDaysRemaining, "no trial days while unknown");
                Assert.Equal("not a licence", File.ReadAllText(s.SharedPath + ".damaged"), "the damaged file is kept, not overwritten");

                var next = s.Open();
                Assert.Equal(LicenceState.Expired, next.State, "the next session knows, from the anchor");
                Assert.True(next.DamagedFileFound, "and still says why, until a key is activated");
            }
        }

        /// <summary>Only the first damaged file can hold a key; a second must not replace it.</summary>
        public static void ASecondDamage_KeepsTheFirstDamagedFile()
        {
            using (var s = new Scene())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s.SharedPath));
                File.WriteAllText(s.SharedPath, "first damage, with the key in it");
                s.Open();

                File.WriteAllText(s.SharedPath, "second damage");
                var licence = s.Open();

                Assert.Equal(LicenceState.Unknown, licence.State, "state");
                Assert.Equal("first damage, with the key in it", File.ReadAllText(s.SharedPath + ".damaged"), "the first is kept");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        /// <summary>
        /// An older Trados build writes its file in place. A new-core product
        /// starting while it saves must copy the whole file, not the empty one
        /// it happened to see - or the activation never migrates.
        /// </summary>
        public static void ATradosFileMidSave_IsCopiedWhole()
        {
            using (var s = new Scene())
            {
                Scene.WriteTradosStyle(s.LegacyPath, Activated(DateTime.UtcNow.AddDays(-1)));
                var whole = File.ReadAllBytes(s.LegacyPath);
                File.WriteAllBytes(s.LegacyPath, new byte[0]);   // truncated, the save not yet written

                var save = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(300);
                    File.WriteAllBytes(s.LegacyPath, whole);
                });
                var licence = s.Open();
                save.Wait();

                Assert.Equal(LicenceState.Licensed, licence.State, "state");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(whole), "the whole file was copied");
                Assert.True(!licence.DamagedFileFound, "nothing damaged to report");
            }
        }

        /// <summary>
        /// A licence file exactly as the Trados plugin wrote it, as fixed text,
        /// so renaming a field in LicenceRecord cannot pass unnoticed: every
        /// existing customer's copy would read back empty.
        /// </summary>
        public static void AFileWrittenByTheTradosPlugin_ReadsBackEveryField()
        {
            const string written =
                "﻿{\"activatedAt\":\"\\/Date(1757000000000)\\/\",\"expiresAt\":null," +
                "\"instanceId\":\"inst-0001\",\"lastValidatedAt\":\"\\/Date(1758500000000)\\/\"," +
                "\"licenseKey\":\"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\",\"machineFingerprint\":\"abc123\"," +
                "\"status\":\"active\",\"trialStartedAt\":\"\\/Date(1756000000000)\\/\",\"variantName\":\"Annual\"}";

            Assert.True(LicenceFile.TryParse(System.Text.Encoding.UTF8.GetBytes(written), out var r), "parses");
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(epoch.AddMilliseconds(1757000000000), r.ActivatedAt.ToUniversalTime(), "activatedAt");
            Assert.Equal(null, r.ExpiresAt, "expiresAt");
            Assert.Equal("inst-0001", r.InstanceId, "instanceId");
            Assert.Equal(epoch.AddMilliseconds(1758500000000), r.LastValidatedAt.ToUniversalTime(), "lastValidatedAt");
            Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", r.LicenseKey, "licenseKey");
            Assert.Equal("abc123", r.MachineFingerprint, "machineFingerprint");
            Assert.Equal("active", r.Status, "status");
            Assert.Equal(epoch.AddMilliseconds(1756000000000), r.TrialStartedAt.ToUniversalTime(), "trialStartedAt");
            Assert.Equal("Annual", r.VariantName, "variantName");
        }

        /// <summary>A failed attempt to re-enter the key re-reads the file; that must not end the grace session.</summary>
        public static void AfterADamagedFile_TheSessionStaysUnknown_ThroughARefresh()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(s.SharedPath));
                File.WriteAllText(s.SharedPath, "not a licence");

                var licence = s.Open();
                Assert.True(!licence.IsActivatedHereWith(Key), "not activated");
                Assert.Equal(LicenceState.Unknown, licence.State, "still unknown after re-reading the fresh file");
            }
        }

        /// <summary>
        /// The licence is created lazily, so an exception while loading would be
        /// cached and thrown at every feature gate. Whatever goes wrong, it must
        /// fail open.
        /// </summary>
        public static void APathWindowsRejects_FailsOpen()
        {
            using (var s = new Scene())
            {
                var bad = Path.Combine(s.Dir, "bad") + "|<>\"\\licence.json";
                var licence = new SupervertalerLicence(bad, bad, s.AnchorKey, s.LegacyAnchorKey);

                Assert.Equal(LicenceState.Unknown, licence.State, "state");
                Assert.True(!licence.IsActivatedHereWith(Key), "a refresh does not throw either");
            }
        }

        public static void AFileThatCannotBeOpened_IsUnknown_AndLeftAlone()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(Activated(DateTime.UtcNow.AddDays(-1))), false);
                var before = File.ReadAllBytes(s.SharedPath);

                SupervertalerLicence licence;
                using (new FileStream(s.SharedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    licence = s.Open();

                Assert.Equal(LicenceState.Unknown, licence.State, "state");
                Assert.True(!licence.DamagedFileFound, "not damaged, only unreadable");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(before), "nothing was written over it");
                Assert.Equal(LicenceState.Licensed, s.Open().State, "the next start reads it");
            }
        }

        public static void ATradosFileThatCannotBeOpened_IsUnknown_AndNotHidden()
        {
            using (var s = new Scene())
            {
                Scene.WriteTradosStyle(s.LegacyPath, Activated(DateTime.UtcNow.AddDays(-1)));

                SupervertalerLicence licence;
                using (new FileStream(s.LegacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    licence = s.Open();

                Assert.Equal(LicenceState.Unknown, licence.State, "state");
                Assert.True(!File.Exists(s.SharedPath), "no shared file that would hide the Trados one");
                Assert.Equal(LicenceState.Licensed, s.Open().State, "the next start copies it");
            }
        }

        public static void ADamagedTradosFile_IsUnknown_AndNeverWritten()
        {
            using (var s = new Scene())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s.LegacyPath));
                File.WriteAllText(s.LegacyPath, "not a licence");

                var licence = s.Open();

                Assert.Equal(LicenceState.Unknown, licence.State, "state");
                Assert.True(licence.DamagedFileFound, "the product is told");
                Assert.Equal("not a licence", File.ReadAllText(s.LegacyPath), "the Trados file is untouched");
                Assert.Equal("not a licence", File.ReadAllText(s.SharedPath + ".damaged"), "and a copy is kept beside the shared file");
                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.SharedPath, out _, out _), "a fresh shared record exists");
            }
        }

        // ─── Telling the products ───────────────────────────────────

        /// <summary>
        /// A subscriber that throws - the Trados AI Assistant touching a pane
        /// that was never opened did, on 2026-09-23 - must not break the licence
        /// call that raised the event, nor keep later subscribers from hearing.
        /// </summary>
        public static void ASubscriberThatThrows_BreaksNothing()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(Activated(DateTime.UtcNow)), false);
                var licence = s.Open();
                Assert.Equal(LicenceState.Licensed, licence.State, "before");

                bool laterSubscriberHeard = false;
                licence.StateChanged += (o, e) => throw new InvalidOperationException("no window handle yet");
                licence.StateChanged += (o, e) => laterSubscriberHeard = true;

                var logged = new System.Collections.Generic.List<string>();
                var previousLog = SupervertalerLicence.Log;
                SupervertalerLicence.Log = m => logged.Add(m);
                try
                {
                    // The other product deactivates; this one finds out on its next
                    // validation, which then needs no network call.
                    LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(new LicenceRecord
                    {
                        TrialStartedAt = DateTime.UtcNow.AddDays(-100),
                        MachineFingerprint = Scene.Fingerprint,
                    }), replaceExisting: true);

                    var (ok, message) = licence.ValidateOnlineAsync().GetAwaiter().GetResult();

                    Assert.True(!ok && message.Contains("No active licence"), "the call completed normally: " + message);
                    Assert.True(laterSubscriberHeard, "the later subscriber still heard");
                    Assert.True(logged.Any(m => m.Contains("subscriber failed")), "the failure was logged");
                    Assert.Equal(LicenceState.Expired, licence.State, "and the state followed the file");
                }
                finally
                {
                    SupervertalerLicence.Log = previousLog;
                }
            }
        }

        // ─── Two products open at once ──────────────────────────────

        /// <summary>
        /// Entering a key in one product that the other has already activated
        /// on this computer must be recognised, so it is adopted rather than
        /// activated a second time.
        /// </summary>
        public static void AnActivationByTheOtherProduct_IsSeen()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow);
                var trados = s.Open();
                Assert.Equal(LicenceState.Expired, trados.State, "before");

                // memoQ activates.
                LicenceFile.Write(s.SharedPath, LicenceFile.Serialise(Activated(DateTime.UtcNow)), replaceExisting: true);

                Assert.True(trados.IsActivatedHereWith(" " + Key.ToLowerInvariant() + " "), "the same key, as a person might paste it");
                Assert.Equal(LicenceState.Licensed, trados.State, "and the state follows");
                Assert.True(!trados.IsActivatedHereWith("SOME-OTHER-KEY"), "a different key is not adopted");
            }
        }
    }
}
