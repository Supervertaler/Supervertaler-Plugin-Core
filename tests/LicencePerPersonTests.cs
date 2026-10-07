using System;
using System.IO;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// A licence counts only for the computer and Windows account that
    /// activated it. These are the cases where a data folder is used by more
    /// than one: a folder shared between colleagues, a licence file copied to
    /// another computer, and the customer whose own computer was renamed or
    /// reinstalled - who must always have a way back in, and never be locked
    /// out by it in the middle of a day's work.
    ///
    /// Another account is stood in for by a record with a different
    /// fingerprint, since the tests run as one account.
    /// </summary>
    [Tests]
    internal static class LicencePerPersonTests
    {
        private const string Key = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        private const string OtherKey = "ZZZZZZZZ-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        private const string Someone = "5e1f0000feedface0000000000000000000000000000000000000000someone";

        private static LicenceRecord ActivatedFor(string fingerprint, string key = Key, string instance = "instance-old") => new LicenceRecord
        {
            LicenseKey = key,
            InstanceId = instance,
            VariantName = "Annual",
            Status = "active",
            ActivatedAt = DateTime.UtcNow.AddDays(-100),
            LastValidatedAt = DateTime.UtcNow.AddDays(-1),
            TrialStartedAt = DateTime.UtcNow.AddDays(-200),
            MachineFingerprint = fingerprint,
        };

        private static LicenceRecord OnTrialFor(string fingerprint) => new LicenceRecord
        {
            TrialStartedAt = DateTime.UtcNow.AddDays(-3),
            MachineFingerprint = fingerprint,
        };

        private static void Put(string path, LicenceRecord record) =>
            LicenceFile.Write(path, LicenceFile.Serialise(record), replaceExisting: true);

        private static LicenceRecord Read(string path)
        {
            Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(path, out var rec, out _), "read " + Path.GetFileName(path));
            return rec;
        }

        // ─── The rule ───────────────────────────────────────────────

        /// <summary>
        /// The rule: a folder holding someone else's activation licenses only
        /// them. The first session is Unknown - never a refusal - and every later
        /// one is this account's own: here a fresh trial, as after renaming a computer.
        /// </summary>
        public static void AnotherAccountsActivation_LicensesNobodyElse()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                var theirs = File.ReadAllBytes(s.SharedPath);

                var first = s.Open();
                Assert.Equal(LicenceState.Unknown, first.State, "the session that meets it");
                Assert.True(first.ForeignActivationFound, "and is told why");
                Assert.True(!first.HasKey, "their key is not this account's");

                var next = s.Open();
                Assert.Equal(LicenceState.Trial, next.State, "every later session: this account's own trial");
                Assert.Equal(14, next.TrialDaysRemaining, "a whole one, from a clean anchor");
                Assert.True(next.ForeignActivationFound, "still told, until it activates a key of its own");

                var mine = Read(s.PersonalPath);
                Assert.Equal(Scene.Fingerprint, mine.MachineFingerprint, "its own record, under its own fingerprint");
                Assert.True(!mine.HasLicenseKey, "with no key in it");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs was never written");
            }
        }

        /// <summary>
        /// The customer whose computer was renamed before this build: older
        /// builds re-signed their trial anchor with the old trial start, so
        /// there is no trial left. The first session still works and says why.
        /// </summary>
        public static void ARenamedComputer_WithItsTrialLongOver_IsUnknownOnce_ThenExpired()
        {
            using (var s = new Scene())
            {
                TrialAnchor.Write(s.AnchorKey, Scene.Fingerprint, DateTime.UtcNow.AddDays(-200), DateTime.UtcNow.AddDays(-1));
                Put(s.SharedPath, ActivatedFor(Someone));

                var first = s.Open();
                Assert.Equal(LicenceState.Unknown, first.State, "the first session keeps working");
                Assert.True(first.ForeignActivationFound, "told");

                Assert.True(!first.IsActivatedHereWith(OtherKey), "a failed attempt at a key re-reads the file");
                Assert.Equal(LicenceState.Unknown, first.State, "and does not end the Unknown session");

                var next = s.Open();
                Assert.Equal(LicenceState.Expired, next.State, "after that, the trial it had");
                Assert.True(next.ForeignActivationFound, "and told why, at every start");
            }
        }

        /// <summary>Someone else's trial is not a licence anyone could lose: no Unknown session.</summary>
        public static void AnotherAccountsTrial_IsNotAFirstMeeting()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, OnTrialFor(Someone));

                var licence = s.Open();
                Assert.Equal(LicenceState.Trial, licence.State, "this account's own trial, at once");
                Assert.True(!licence.ForeignActivationFound, "nothing to tell");
                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.PersonalPath, out _, out _), "its own file");
            }
        }

        /// <summary>The single-computer customer - nearly everyone - sees no change at all.</summary>
        public static void TheOwner_KeepsLicenceJson_AndNoSecondFile()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Scene.Fingerprint));
                Put(LicenceFile.PersonalPath(s.SharedPath, Someone), OnTrialFor(Someone));

                var licence = s.Open();
                Assert.Equal(LicenceState.Licensed, licence.State, "state");
                Assert.True(!licence.ForeignActivationFound, "nothing foreign about it");
                Assert.True(!File.Exists(s.PersonalPath), "no file of its own beside it");
            }
        }

        /// <summary>
        /// Two accounts each with their own activation, in one folder: a change
        /// to one leaves the other's file exactly as it was.
        /// </summary>
        public static void TwoAccountsSharingAFolder_DoNotOverwriteEachOther()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                Put(s.PersonalPath, ActivatedFor(Scene.Fingerprint, OtherKey, "instance-mine"));
                var theirs = File.ReadAllBytes(s.SharedPath);

                var licence = s.Open();
                Assert.Equal(LicenceState.Licensed, licence.State, "its own activation");
                Assert.True(!licence.ForeignActivationFound, "it has one of its own");
                Assert.Equal("ZZZZZZZZ...EEEE", licence.MaskedKey, "its own key");

                var reply = SupervertalerLicence.ParseLemonSqueezyResponse(Replies.Validated("active"));
                Assert.Equal(LicenceState.Licensed, licence.ApplyValidationReply(reply, OtherKey, "instance-mine"), "validated");

                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs untouched by its validation");
                Assert.True((DateTime.UtcNow - Read(s.PersonalPath).LastValidatedAt).TotalMinutes < 1, "its own renewed");
            }
        }

        /// <summary>A file renamed by hand to this account's name still names its real owner inside.</summary>
        public static void ACopiedFileUnderThisAccountsName_LicensesNobody()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                Put(s.PersonalPath, ActivatedFor(Someone));

                var licence = s.Open();
                Assert.True(licence.State != LicenceState.Licensed, "not licensed: " + licence.State);
                Assert.True(!licence.HasKey, "no key");
                Assert.Equal(Scene.Fingerprint, Read(s.PersonalPath).MachineFingerprint, "replaced by this account's own record");
            }
        }

        /// <summary>A record with no fingerprint is claimed by its first reader, on disk, so a second cannot claim it too.</summary>
        public static void ARecordWithNoFingerprint_IsClaimedByWritingIt()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(""));

                Assert.Equal(LicenceState.Licensed, s.Open().State, "the first reader's");
                Assert.Equal(Scene.Fingerprint, Read(s.SharedPath).MachineFingerprint, "and written as theirs");
            }
        }

        /// <summary>
        /// licence.json locked at startup: whose it is cannot be known, so the
        /// session is Unknown and writes nothing. Once it can be read and turns
        /// out to be someone else's, this account moves to its own file.
        /// </summary>
        public static void ALicenceJsonUnreadableAtStart_ThatIsSomeoneElses_IsLeftToThem()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                var theirs = File.ReadAllBytes(s.SharedPath);

                SupervertalerLicence licence;
                using (new FileStream(s.SharedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    licence = s.Open();
                Assert.Equal(LicenceState.Unknown, licence.State, "while it cannot be read");

                Assert.True(!licence.IsActivatedHereWith(Key), "their key is not activated here");
                Assert.True(!licence.HasKey, "nor held");
                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.PersonalPath, out _, out _), "its own file now");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs untouched");
                Assert.Equal(LicenceState.Unknown, licence.State, "and the rest of this session is the first meeting");
            }
        }

        /// <summary>Trados and memoQ starting together as a second account: one own file, one trial.</summary>
        public static void TwoProductsMeetingAnotherAccountsLicence_ShareOneOwnRecord()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                var theirs = File.ReadAllBytes(s.SharedPath);

                var results = Program.RunChildren(2, i =>
                    $"--open \"{s.SharedPath}\" \"{s.LegacyPath}\" \"{s.AnchorKey}\" \"{s.LegacyAnchorKey}\" \"{s.GoFile}\"",
                    s.GoFile);

                Assert.True(results.All(r => !r.StartsWith("Licensed") && r.EndsWith(" False")),
                    "neither licensed by it, neither holding its key: " + string.Join(" | ", results));
                var ends = results.Select(r => long.Parse(r.Split(' ')[1])).ToArray();
                Assert.True(Math.Abs(ends[0] - ends[1]) < TimeSpan.FromSeconds(5).Ticks, "one trial, not two");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs untouched");
                Assert.Equal(1, Directory.GetFiles(Path.GetDirectoryName(s.SharedPath), "licence-*.json").Length, "one own file");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        public static void APersonalFileName_IsMadeOnlyOfSafeCharacters()
        {
            var shared = Path.Combine(Path.GetTempPath(), "x", "licence", "licence.json");
            Assert.Equal(Path.Combine(Path.GetDirectoryName(shared), "licence-abcdef0123456789.json"),
                LicenceFile.PersonalPath(shared, "ABCdef0123456789fedcba"), "hex, lowered, sixteen");
            Assert.Equal(Path.Combine(Path.GetDirectoryName(shared), "licence-fallback-1a2b3c.json"),
                LicenceFile.PersonalPath(shared, "fallback-1a2b3c"), "the fallback fingerprint");
            Assert.Equal(Path.Combine(Path.GetDirectoryName(shared), "licence-abc.json"),
                LicenceFile.PersonalPath(shared, "a\\b/c"), "nothing that could leave the folder");
        }

        // ─── Finding and clearing a stranded activation ─────────────

        public static void AStrandedActivation_IsFoundOnlyForTheSameKey_AndNeverThisAccounts()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                Put(s.PersonalPath, ActivatedFor(Scene.Fingerprint, OtherKey, "instance-mine"));
                var licence = s.Open();

                var found = licence.FindStrandedActivation(" " + Key.ToLowerInvariant() + " ");
                Assert.True(found.HasValue, "found, as a person might paste the key");
                Assert.Equal(s.SharedPath, found.Value.Path, "where");
                Assert.Equal("instance-old", found.Value.InstanceId, "which");

                Assert.True(!licence.FindStrandedActivation(OtherKey).HasValue, "this account's own is never stranded");
                Assert.True(!licence.FindStrandedActivation("SOME-OTHER-KEY").HasValue, "nor anyone's with another key");
            }
        }

        public static void ClearingAReleasedActivation_ClearsThatOneOnly()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                var before = File.ReadAllBytes(s.SharedPath);

                SupervertalerLicence.ClearReleasedActivation(s.SharedPath, "instance-other");
                Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(before), "a different activation is left alone");

                SupervertalerLicence.ClearReleasedActivation(s.SharedPath, "instance-old");
                var after = Read(s.SharedPath);
                Assert.True(!after.IsActivated && !after.HasLicenseKey, "cleared");
                Assert.Equal(Someone, after.MachineFingerprint, "still theirs");
                Assert.True(Math.Abs((after.TrialStartedAt - DateTime.UtcNow.AddDays(-200)).TotalMinutes) < 1, "their trial start kept");
            }
        }

        // ─── The licence server's replies ───────────────────────────

        public static void AReplyWithNoActivationsLeft_IsRecognised()
        {
            var full = SupervertalerLicence.ParseLemonSqueezyResponse(Replies.LimitReached);
            Assert.True(!full.Activated && full.NoActivationsLeft, "the limit, by the server's own count");

            Assert.True(!SupervertalerLicence.ParseLemonSqueezyResponse(Replies.Refused("disabled", 1, 2)).NoActivationsLeft,
                "a refusal with an activation to spare is something else");
            Assert.True(!SupervertalerLicence.ParseLemonSqueezyResponse(Replies.Refused("active", 7, null)).NoActivationsLeft,
                "a key with no limit never runs out");
            Assert.True(!SupervertalerLicence.ParseLemonSqueezyResponse(Replies.NotFound).NoActivationsLeft,
                "no licence block, no conclusion");
        }

        public static void AReleaseIsConfirmedOnlyByTheServerSayingSo()
        {
            Assert.True(SupervertalerLicence.ParseLemonSqueezyResponse(Replies.Deactivated(true)).Deactivated, "deactivated");
            Assert.True(!SupervertalerLicence.ParseLemonSqueezyResponse(Replies.Deactivated(false)).Deactivated, "refused");
            Assert.True(!SupervertalerLicence.ParseLemonSqueezyResponse("<html>gateway</html>").Deactivated, "not an answer");
        }

        // ─── Activating, against a stand-in licence server ──────────

        /// <summary>
        /// The way back in after a rename or a reinstall when the key has no
        /// activations left: the one recorded here for the old name is
        /// released, and this account activated in its place.
        /// </summary>
        public static void AKeyWithNoActivationsLeft_ReleasesTheOneRecordedHereForAnotherAccount()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                int activations = 0;
                using (var server = new FakeLicenceServer((endpoint, form) =>
                {
                    if (endpoint == "deactivate") return Replies.Deactivated(true);
                    return ++activations == 1 ? Replies.LimitReached : Replies.Activated("instance-new");
                }))
                {
                    var licence = s.Open(server.Url);
                    var (ok, message) = licence.ActivateAsync(Key).GetAwaiter().GetResult();

                    Assert.True(ok, "activated: " + message);
                    Assert.True(message.Contains("released"), "and says what made room: " + message);
                    Assert.Equal("activate |deactivate instance-old|activate ", string.Join("|", server.Requests), "requests");
                    Assert.Equal(LicenceState.Licensed, licence.State, "state");
                    Assert.True(!licence.ForeignActivationFound, "nothing left to tell");

                    var mine = Read(s.PersonalPath);
                    Assert.Equal("instance-new", mine.InstanceId, "its own activation, in its own file");
                    Assert.Equal(Scene.Fingerprint, mine.MachineFingerprint, "under its own fingerprint");

                    var old = Read(s.SharedPath);
                    Assert.True(!old.IsActivated, "the released activation is cleared where it was recorded");
                    Assert.Equal(Someone, old.MachineFingerprint, "and the file is still theirs");
                    Assert.Equal(LicenceState.Licensed, s.Open(server.Url).State, "the next start agrees");
                }
            }
        }

        /// <summary>No activations left, and none of them recorded here: nothing of anyone's is released.</summary>
        public static void AKeyWithNoActivationsLeft_AndNoneRecordedHere_ReleasesNothing()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone, OtherKey));
                var theirs = File.ReadAllBytes(s.SharedPath);
                using (var server = new FakeLicenceServer((endpoint, form) => Replies.LimitReached))
                {
                    var (ok, message) = s.Open(server.Url).ActivateAsync(Key).GetAwaiter().GetResult();

                    Assert.True(!ok, "refused");
                    Assert.True(message.Contains("activation limit"), "with the server's reason: " + message);
                    Assert.Equal("activate ", string.Join("|", server.Requests), "one request, and no release");
                    Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs untouched");
                }
            }
        }

        /// <summary>Only running out of activations is cured by releasing one; any other refusal releases nothing.</summary>
        public static void ARefusalForAnyOtherReason_ReleasesNothing()
        {
            foreach (var refusal in new[] { Replies.Refused("disabled", 1, 2), Replies.NotFound, Replies.LimitReachedInAnotherStore })
            {
                using (var s = new Scene())
                {
                    Put(s.SharedPath, ActivatedFor(Someone));
                    var theirs = File.ReadAllBytes(s.SharedPath);
                    using (var server = new FakeLicenceServer((endpoint, form) => refusal))
                    {
                        var (ok, _) = s.Open(server.Url).ActivateAsync(Key).GetAwaiter().GetResult();

                        Assert.True(!ok, "refused");
                        Assert.Equal("activate ", string.Join("|", server.Requests), "no release for: " + refusal);
                        Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs untouched");
                    }
                }
            }
        }

        /// <summary>A release the server does not confirm changes nothing here and is not followed by a second try.</summary>
        public static void AReleaseTheServerDoesNotConfirm_ChangesNothing()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                var theirs = File.ReadAllBytes(s.SharedPath);
                using (var server = new FakeLicenceServer((endpoint, form) =>
                    endpoint == "deactivate" ? Replies.Deactivated(false) : Replies.LimitReached))
                {
                    var licence = s.Open(server.Url);
                    var (ok, _) = licence.ActivateAsync(Key).GetAwaiter().GetResult();

                    Assert.True(!ok, "refused");
                    Assert.Equal("activate |deactivate instance-old", string.Join("|", server.Requests), "tried once, then stopped");
                    Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "the activation it could not release is still recorded");
                    Assert.True(licence.State != LicenceState.Licensed, "and nothing licensed");
                }
            }
        }

        /// <summary>Another account's activation is never even validated from here, let alone renewed.</summary>
        public static void AnotherAccountsActivation_IsNeverValidatedFromHere()
        {
            using (var s = new Scene())
            {
                Put(s.SharedPath, ActivatedFor(Someone));
                var theirs = File.ReadAllBytes(s.SharedPath);
                using (var server = new FakeLicenceServer((endpoint, form) => Replies.Validated("active")))
                {
                    s.Open(server.Url);
                    var licence = s.Open(server.Url);
                    var (ok, message) = licence.ValidateOnlineAsync().GetAwaiter().GetResult();

                    Assert.True(!ok && message.Contains("No active licence"), message);
                    Assert.Equal(0, server.Requests.Length, "nothing sent");
                    Assert.True(File.ReadAllBytes(s.SharedPath).SequenceEqual(theirs), "theirs untouched");
                }
            }
        }

        /// <summary>Licence-server replies in the live API's shape.</summary>
        private static class Replies
        {
            private const string Meta =
                "{\"store_id\":307062,\"order_id\":1,\"product_id\":917153,\"variant_name\":\"Annual\"," +
                "\"customer_name\":\"Acme\",\"customer_email\":\"a@example.com\"}";

            private static string LicenceKey(string status, int? usage, int? limit) =>
                "{\"id\":1,\"status\":\"" + status + "\",\"key\":\"K\"," +
                "\"activation_limit\":" + (limit.HasValue ? limit.ToString() : "null") + "," +
                "\"activation_usage\":" + (usage.HasValue ? usage.ToString() : "null") + "," +
                "\"created_at\":\"2026-09-23T20:10:00.000000Z\",\"expires_at\":null}";

            public static string Activated(string instance) =>
                "{\"activated\":true,\"error\":null,\"license_key\":" + LicenceKey("active", 2, 2) + "," +
                "\"instance\":{\"id\":\"" + instance + "\",\"name\":\"x\",\"created_at\":\"2026-10-07T12:00:00.000000Z\"}," +
                "\"meta\":" + Meta + "}";

            public static readonly string LimitReached =
                "{\"activated\":false,\"error\":\"This license key has reached the activation limit.\"," +
                "\"license_key\":" + LicenceKey("active", 2, 2) + ",\"meta\":" + Meta + "}";

            public static readonly string LimitReachedInAnotherStore =
                "{\"activated\":false,\"error\":\"This license key has reached the activation limit.\"," +
                "\"license_key\":" + LicenceKey("active", 2, 2) + ",\"meta\":{\"store_id\":12345}}";

            public static string Refused(string status, int? usage, int? limit) =>
                "{\"activated\":false,\"error\":\"This license key is " + status + ".\"," +
                "\"license_key\":" + LicenceKey(status, usage, limit) + ",\"meta\":" + Meta + "}";

            public static readonly string NotFound =
                "{\"activated\":false,\"error\":\"license_key not found.\"}";

            public static string Deactivated(bool ok) =>
                "{\"deactivated\":" + (ok ? "true" : "false") + ",\"error\":" + (ok ? "null" : "\"refused\"") + "," +
                "\"license_key\":" + LicenceKey("active", 1, 2) + ",\"meta\":" + Meta + "}";

            public static string Validated(string status) =>
                "{\"valid\":true,\"error\":null,\"license_key\":" + LicenceKey(status, 1, 2) + "," +
                "\"instance\":{\"id\":\"instance-mine\",\"name\":\"x\",\"created_at\":\"2026-10-07T12:00:00.000000Z\"}," +
                "\"meta\":" + Meta + "}";
        }
    }
}
