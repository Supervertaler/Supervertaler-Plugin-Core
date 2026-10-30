using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The locked key file, step 1 of docs/design/api-key-protection.md: read the
    /// plain file first, else the locked one; save the plain file, then the locked
    /// one; sync plain into locked at start-up, never removing. Mostly the ways it
    /// can go wrong - a key locked under another account, a damaged or half-written
    /// file, a file someone else holds, products at different steps - because the
    /// rule is that none of them ends as an empty box or a blank or older key sent
    /// to a provider. Every test works in a temporary data root and a temporary
    /// locked file; the run as a whole checks the user's own are untouched.
    /// </summary>
    [Tests]
    internal static class LockedKeyTests
    {
        private const string K1 = "sk-ant-test-K1-PROJ-001";
        private const string K2 = "sk-ant-test-K2-PROJ-001";
        private const string Place = "AI Settings";

        // Design note §7, M1 and M2, for the Anthropic key; the en dashes spelt
        // out, so a source file read in the wrong encoding cannot pass.
        private const string M1 = "Your Anthropic key can't be read on this computer \u2013 it was saved under another Windows sign-in, "
                                + "or your Windows password was reset. Paste the key in again; nothing else is affected.";
        private const string M2 = "Can't use your Anthropic key \u2013 it can't be read on this computer (saved under another Windows sign-in, "
                                + "or your Windows password was reset). Paste it in again in AI Settings.";

        private static string M6(string setAside) =>
            "The file holding your locked API keys was damaged and has been set aside as " + setAside + ". Please paste your keys in again.";

        public static void RoundTrip_TheKeyAppearsNowhereInTheLockedFile()
        {
            using (var s = new KeyScene())
            {
                const string key = "sk-ant-test-RoundTrip-PROJ-001";
                Assert.True(ApiKeyStore.Set("claude", key), "saved");
                Assert.Equal(key, s.Unlocked("claude"), "the locked file holds it, locked");

                var raw = s.LockedBytes();
                Assert.True(IndexOf(raw, new UTF8Encoding(false).GetBytes(key)) < 0, "the key is not in the locked file as UTF-8");
                Assert.True(IndexOf(raw, Encoding.Unicode.GetBytes(key)) < 0, "nor as UTF-16");
                Assert.True(raw[0] == (byte)'{', "no byte-order mark");

                var blob = s.LockedEntries()["claude"];
                Assert.True(Regex.IsMatch(blob, "^[A-Za-z0-9+/]+={0,2}$"), "standard base64, no line breaks");
                Assert.True(blob.StartsWith("AQAAANCMnd8BFdERjHoAwE/Cl+sB", StringComparison.Ordinal), "a Windows DPAPI blob");
                Assert.True(s.LockedEntries().ContainsKey("_comment"), "the file says what it is");

                // With the plain file gone - as after step 2's import - it still reads.
                File.Delete(s.PlainPath);
                Assert.Equal(key, ApiKeyStore.Get("claude"), "read from the locked file");
                Assert.Equal(ApiKeyStatus.Present, ApiKeyStore.Status("claude"), "present");
                Assert.Equal(null, ApiKeyStore.MissingKeyMessage("claude", Place), "nothing to say");
                Assert.Equal(null, ApiKeyStore.UnreadableKeyNote("claude"), "no note");
                Assert.Equal(0, Directory.GetFiles(s.LockedDir, "*.tmp").Length, "no temporary file left");
            }
        }

        /// <summary>
        /// A key locked under another account, made for real: locked with an entropy
        /// value, so the contract's call without one is refused. Reported by name,
        /// kept through other saves and through an empty one, replaced by a new key.
        /// </summary>
        public static void UnreadableEntry_IsReportedKeptAndOnlyReplacedByANewKey()
        {
            using (var s = new KeyScene())
            {
                const string other = "sk-ant-test-Elsewhere-PROJ-001";
                var elsewhere = Locked.LockedElsewhere(other);
                s.WriteLocked(Locked.Json("claude", elsewhere, "openai", Locked.Lock("sk-test-O1")));
                var before = s.LockedBytes();

                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("claude"), "unreadable, not absent");
                Assert.Equal(null, ApiKeyStore.Get("claude"), "no key handed out");
                Assert.Equal(M2, ApiKeyStore.MissingKeyMessage("claude", Place), "M2 when an action needs it");
                Assert.Equal(M1, ApiKeyStore.UnreadableKeyNote("claude"), "M1 beside the key box");
                Assert.True(s.LoggedAny("claude key cannot be unlocked") && s.LoggedAny("Windows error 0x"), "the Windows error code is logged: " + s.Log());
                Assert.True(!s.LoggedAny(other), "no key in the log");
                Console.WriteLine("      logged: " + s.Log());
                Assert.True(KeyScene.Same(before, s.LockedBytes()), "reading changes nothing");

                Assert.True(ApiKeyStore.Set("openai", "sk-test-O2"), "another provider's key saved");
                Assert.Equal(elsewhere, s.LockedEntries()["claude"], "the unreadable entry kept through another save");

                Assert.True(ApiKeyStore.Set("claude", ""), "an empty save - a dialog's OK on the empty box - succeeds");
                Assert.Equal(elsewhere, s.LockedEntries()["claude"], "and keeps the unreadable entry");
                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("claude"), "still reported");

                Assert.True(ApiKeyStore.Set("claude", K2), "a new key saved");
                Assert.Equal(K2, s.Unlocked("claude"), "the new key replaced it");
                File.Delete(s.PlainPath);
                Assert.Equal(K2, ApiKeyStore.Get("claude"), "and reads from the locked file");
            }
        }

        /// <summary>A flipped byte, or a value that is not base64 at all: unreadable, never absent. A blank value is no key.</summary>
        public static void TamperedOrGarbledEntries_AreUnreadable()
        {
            using (var s = new KeyScene())
            {
                s.WriteLocked(Locked.Json("claude", Locked.Tampered(K1), "openai", "not base64 at all!", "gemini", ""));
                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("claude"), "a flipped byte");
                Assert.Equal(M2, ApiKeyStore.MissingKeyMessage("claude", Place), "M2 for it");
                Assert.True(s.LoggedAny("Windows error 0x"), "Windows' refusal logged: " + s.Log());
                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("openai"), "not base64");
                Assert.True(s.LoggedAny("openai key cannot be unlocked by this Windows account (not base64)"), "said so: " + s.Log());
                Assert.Equal(ApiKeyStatus.Absent, ApiKeyStore.Status("gemini"), "a blank value is no key");
                Console.WriteLine("      logged: " + s.Log());
                Assert.Equal("No API key set for Gemini. Add one in AI Settings.", ApiKeyStore.MissingKeyMessage("gemini", Place), "today's wording when there is none");
            }
        }

        /// <summary>
        /// Design note §8, test 3, for step 1: every combination of the plain file
        /// (absent, K1, K2, damaged, empty) and the locked file (absent, K1, K2,
        /// unreadable entry, damaged). What a read yields, then what the start-up
        /// sync leaves. It is the mixed-version test and the crash test too: plain K2
        /// with locked K1 is a step-1 save that died between its two writes; plain
        /// absent with locked K2 is after a step-2 import; a damaged locked file is a
        /// half-written one. It asserts the key each state yields.
        /// </summary>
        public static void StateTable_Step1_EveryPlainAndLockedFile()
        {
            var plains = new[] { "absent", "K1", "K2", "damaged", "empty" };
            var lockeds = new[] { "absent", "K1", "K2", "unreadable", "damaged" };
            var deepl = Locked.Lock("deepl-test-D1");   // Sidekick's, which core must keep

            foreach (var p in plains)
                foreach (var l in lockeds)
                    using (var s = new KeyScene())
                    {
                        var where = "plain " + p + ", locked " + l;
                        switch (p)
                        {
                            case "K1": s.WritePlain(Locked.Json("claude", K1)); break;
                            case "K2": s.WritePlain(Locked.Json("claude", K2)); break;
                            case "damaged": s.WritePlain("{\n  \"claude\": \"" + K2.Substring(0, 12)); break;
                            case "empty": s.WritePlain(""); break;
                        }
                        switch (l)
                        {
                            case "K1": s.WriteLocked(Locked.Json("claude", Locked.Lock(K1), "deepl", deepl)); break;
                            case "K2": s.WriteLocked(Locked.Json("claude", Locked.Lock(K2), "deepl", deepl)); break;
                            case "unreadable": s.WriteLocked(Locked.Json("claude", Locked.LockedElsewhere(K1), "deepl", deepl)); break;
                            case "damaged":
                                var whole = new UTF8Encoding(false).GetBytes(Locked.Json("claude", Locked.Lock(K1), "deepl", deepl));
                                s.WriteLockedBytes(whole.Take(whole.Length / 2).ToArray());
                                break;
                        }

                        var plainKey = p == "K1" ? K1 : p == "K2" ? K2 : null;
                        var lockedKey = l == "K1" ? K1 : l == "K2" ? K2 : null;
                        var expected = plainKey ?? lockedKey;
                        var status = expected != null ? ApiKeyStatus.Present
                                   : l == "unreadable" || l == "damaged" ? ApiKeyStatus.Unreadable
                                   : ApiKeyStatus.Absent;

                        Assert.Equal(expected, ApiKeyStore.Get("claude"), where + ": read");
                        Assert.Equal(status, ApiKeyStore.Status("claude"), where + ": status");
                        if (status == ApiKeyStatus.Unreadable)
                            Assert.True(ApiKeyStore.MissingKeyMessage("claude", Place).StartsWith("Can't use your Anthropic key \u2013", StringComparison.Ordinal),
                                where + ": says the key can't be used");

                        var plainBefore = s.PlainBytes();
                        var lockedBefore = s.LockedBytes();
                        var sync = ApiKeyStore.SyncLocked();
                        Assert.True(!sync.Failed, where + ": the sync did not fail");
                        Assert.True(KeyScene.Same(plainBefore, s.PlainBytes()), where + ": the sync does not touch the plain file");

                        if (plainKey == null)
                        {
                            Assert.Equal(0, sync.Locked, where + ": nothing to lock");
                            Assert.True(KeyScene.Same(lockedBefore, s.LockedBytes()), where + ": the locked file exactly as it was");
                            Assert.Equal(null, sync.Notice, where + ": nothing to report");
                        }
                        else
                        {
                            Assert.Equal(plainKey, s.Unlocked("claude"), where + ": the plain key locked");
                            Assert.Equal(lockedKey == plainKey ? 0 : 1, sync.Locked, where + ": keys locked");
                            if (l == "damaged")
                            {
                                var aside = s.SetAside();
                                Assert.Equal(1, aside.Length, where + ": the damaged file set aside");
                                Assert.True(KeyScene.Same(lockedBefore, File.ReadAllBytes(aside[0])), where + ": byte for byte");
                                Assert.Equal(M6(aside[0]), sync.Notice, where + ": M6 to show");
                            }
                            else
                            {
                                Assert.Equal(null, sync.Notice, where + ": nothing to report");
                                if (l != "absent")
                                    Assert.Equal(deepl, s.LockedEntries()["deepl"], where + ": another product's key kept");
                            }
                        }

                        Assert.Equal(expected, ApiKeyStore.Get("claude"), where + ": read after the sync");
                        Assert.Equal(status, ApiKeyStore.Status("claude"), where + ": status after the sync");
                    }
        }

        /// <summary>
        /// Design note §8, test 4: a damaged, empty or emptied plain file leaves the
        /// locked file byte for byte; a plain file with one key changes that key only.
        /// </summary>
        public static void TheSyncNeverRemoves()
        {
            using (var s = new KeyScene())
            {
                var w = Locked.Lock("sk-test-W1");
                s.WriteLocked(Locked.Json("claude", Locked.Lock(K1), "openai", Locked.Lock("sk-test-O1"),
                    "deepl", Locked.Lock("deepl-test-D1"), "workbench:custom_openai:Acme server", w));
                var before = s.LockedBytes();

                foreach (var plain in new[] { "{\"claude\": \"sk-ant-te", "garbage <<<", "", "{}", "{\n  \"_comment\": \"no keys\"\n}" })
                {
                    s.WritePlain(plain);
                    var sync = ApiKeyStore.SyncLocked();
                    Assert.True(!sync.Failed && sync.Locked == 0, "nothing locked from '" + plain + "'");
                    Assert.True(KeyScene.Same(before, s.LockedBytes()), "locked file untouched by '" + plain + "'");
                }

                s.WritePlain(Locked.Json("openai", "sk-test-O2"));
                Assert.Equal(1, ApiKeyStore.SyncLocked().Locked, "one key changed");
                Assert.Equal(K1, s.Unlocked("claude"), "the key the plain file lacks is kept");
                Assert.Equal("sk-test-O2", s.Unlocked("openai"), "the key it has is replaced");
                Assert.Equal("deepl-test-D1", s.Unlocked("deepl"), "Sidekick's key kept");
                Assert.Equal(w, s.LockedEntries()["workbench:custom_openai:Acme server"], "Workbench's key kept, as it was");
            }
        }

        /// <summary>
        /// Design note §8, test 6: a truncated locked file is damaged - every key in it
        /// unreadable, with a message naming the file - and at the next write it is
        /// set aside, byte for byte, never overwritten, and reported with M6.
        /// </summary>
        public static void AHalfWrittenLockedFile_IsSetAsideAtTheNextWrite()
        {
            using (var s = new KeyScene())
            {
                var whole = new UTF8Encoding(false).GetBytes(Locked.Json("claude", Locked.Lock(K1), "deepl", Locked.Lock("deepl-test-D1")));
                var truncated = whole.Take(whole.Length - 40).ToArray();
                s.WriteLockedBytes(truncated);

                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("claude"), "unreadable, not absent");
                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("openai"), "every key, even one never saved");
                Assert.Equal("Can't use your Anthropic key \u2013 the file holding your locked API keys is damaged: " + s.LockedPath
                    + ". Paste the key in again in AI Settings.", ApiKeyStore.MissingKeyMessage("claude", Place), "names the file");
                Assert.True(ApiKeyStore.UnreadableKeyNote("claude").Contains("damaged: " + s.LockedPath), "beside the box too");
                Assert.True(s.LoggedAny("is damaged"), "logged");
                Assert.Equal(0, s.SetAside().Length, "a read sets nothing aside");

                // An empty save has nothing to write, so nothing is set aside either.
                Assert.True(ApiKeyStore.Set("claude", ""), "an empty save");
                Assert.True(KeyScene.Same(truncated, s.LockedBytes()), "the damaged file left as it was");

                Assert.True(ApiKeyStore.Set("openai", "sk-test-O1"), "a key saved");
                var aside = s.SetAside();
                Assert.Equal(1, aside.Length, "set aside");
                Assert.True(Path.GetFileName(aside[0]).StartsWith("api-keys.locked.damaged-", StringComparison.Ordinal), "named as the design note says");
                Assert.True(KeyScene.Same(truncated, File.ReadAllBytes(aside[0])), "byte for byte");
                Assert.True(s.LoggedAny(M6(aside[0])), "M6 logged: " + s.Log());
                Assert.Equal("sk-test-O1", s.Unlocked("openai"), "the new file holds the new key");
                Assert.Equal(ApiKeyStatus.Absent, ApiKeyStore.Status("deepl"), "what only the damaged file held is no longer read");
            }
        }

        /// <summary>
        /// Design note §8, test 6, second half: a temporary file abandoned by a killed
        /// writer is swept at the next write once it is old enough to be nobody's; a
        /// young one is left; the real file is intact.
        /// </summary>
        public static void AnAbandonedTemporaryFile_IsSwept_AndTheFileIsIntact()
        {
            using (var s = new KeyScene())
            {
                ApiKeyStore.Set("claude", K1);
                var old = s.LockedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var young = s.LockedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(old, "{\"claude\": \"half");
                File.WriteAllText(young, "{\"claude\": \"half");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));

                Assert.Equal(K1, s.Unlocked("claude"), "the real file is intact beside them");
                Assert.True(ApiKeyStore.Set("openai", "sk-test-O1"), "next save");
                Assert.True(!File.Exists(old), "the abandoned one swept");
                Assert.True(File.Exists(young), "a young one left: it could be a write in progress");
                Assert.Equal(K1, s.Unlocked("claude"), "the key still there");
                Assert.Equal("sk-test-O1", s.Unlocked("openai"), "and the new one");
            }
        }

        /// <summary>
        /// Design note §8, test 7: entries core does not know - Sidekick's DeepL key,
        /// Workbench's profiles, written by another product's JSON writer with escapes
        /// - are still there after core rewrites the file, ids spelt as they were.
        /// </summary>
        public static void UnknownEntriesSurviveACoreRewrite()
        {
            using (var s = new KeyScene())
            {
                var d = Locked.Lock("deepl-test-D1");
                var w1 = Locked.Lock("sk-test-W1");
                var w2 = Locked.Lock("sk-test-W2");
                // As Python's json.dump writes it: non-ASCII escaped.
                s.WriteLocked("{\"_comment\": \"x\", \"_future\": \"y\", \"deepl\": \"" + d + "\", "
                    + "\"workbench:custom_openai:Acme server\": \"" + w1 + "\", "
                    + "\"Workbench:Custom_OpenAI:Acm\\u00e9 \\u00dcber\": \"" + w2 + "\"}");

                Assert.True(ApiKeyStore.Set("claude", K1), "core saves a key");
                var entries = s.LockedEntries();
                Assert.Equal(d, entries["deepl"], "Sidekick's key kept");
                Assert.Equal(w1, entries["workbench:custom_openai:Acme server"], "a Workbench profile's key kept");
                Assert.Equal(w2, entries["Workbench:Custom_OpenAI:Acm\u00e9 \u00dcber"], "one with escapes, kept and spelt as it was");
                Assert.True(!entries.ContainsKey("_future"), "ids starting with _ are not keys");
                Assert.Equal(K1, s.Unlocked("claude"), "and core's own");

                Assert.Equal("sk-test-W1", ApiKeyStore.Get("WORKBENCH:custom_openai:acme server"), "ids are case-insensitive");
            }
        }

        /// <summary>
        /// A product's own key - a named profile - goes in the locked file only, under
        /// an id with quotes and backslashes dropped from the name, and its messages
        /// name the profile.
        /// </summary>
        public static void PrivateIds_LiveInTheLockedFileOnly()
        {
            using (var s = new KeyScene())
            {
                var id = ApiKeyStore.PrivateId("trados", "custom_openai", "Acme \"EU\" server\\2");
                Assert.Equal("trados:custom_openai:Acme EU server2", id, "the id");

                Assert.True(ApiKeyStore.Set(id, "sk-test-acme"), "saved");
                Assert.True(!File.Exists(s.PlainPath), "not in the shared plain file");
                Assert.Equal("sk-test-acme", s.Unlocked(id), "in the locked file, spelt as given");
                Assert.Equal("sk-test-acme", ApiKeyStore.Get(id), "read back");

                s.WritePlain(Locked.Json("claude", K1));
                ApiKeyStore.SyncLocked();
                Assert.Equal("sk-test-acme", s.Unlocked(id), "the sync leaves it alone");

                Assert.True(ApiKeyStore.Set(id, ""), "removed");
                Assert.True(!s.LockedEntries().ContainsKey(id), "gone");

                s.WriteLocked(Locked.Json(id, Locked.LockedElsewhere("sk-test-acme")));
                Assert.True(ApiKeyStore.MissingKeyMessage(id, Place).StartsWith("Can't use your Acme EU server2 key \u2013", StringComparison.Ordinal),
                    "the message names the profile: " + ApiKeyStore.MissingKeyMessage(id, Place));
            }
        }

        /// <summary>
        /// Design note §8, test 9: with the shared key unreadable, ResolveApiKey hands
        /// out no key at all - not a blank one, and not the plugin's own older copy -
        /// and says why with M2. (Only paths that stop at the shared store are tested:
        /// a key absent everywhere falls through to the user's real Workbench settings.)
        /// </summary>
        public static void NoBlankOrOlderKeyReachesAProvider()
        {
            using (var s = new KeyScene())
            {
                s.WriteLocked(Locked.Json("claude", Locked.LockedElsewhere(K2)));
                var local = new AiApiKeys { Claude = K1 };

                Assert.Equal(null, LlmClient.ResolveApiKey("claude", local, Place, out var problem), "no key");
                Assert.Equal(M2, problem, "M2 instead");
                Assert.Equal(null, LlmClient.ResolveApiKey("claude", local), "the old overload: null too, never blank, never the plugin's copy");

                s.WritePlain(Locked.Json("claude", K2));
                Assert.Equal(K2, LlmClient.ResolveApiKey("claude", local, Place, out problem), "the plain file decides in step 1");
                Assert.Equal(null, problem, "nothing to say");
            }
        }

        /// <summary>
        /// MigrateFrom fills the plain file from a product's own copies, never past a
        /// key either file holds. Otherwise, after a step-2 product had imported and
        /// removed the plain file, a step-1 start would put its older copy back in
        /// the plain file - which step 1 reads first - and the sync would then lock
        /// it over the newer key.
        /// </summary>
        public static void MigrateFrom_NeverBringsBackAnOlderKey()
        {
            using (var s = new KeyScene())
            {
                s.WriteLocked(Locked.Json("claude", Locked.Lock(K2)));
                var local = new AiApiKeys { Claude = K1, OpenAi = "sk-test-O1" };

                Assert.Equal(1, ApiKeyStore.MigrateFrom(local), "only the key neither file has");
                Assert.True(!ApiKeyStore.Load().ContainsKey("claude"), "the older key is not put in the plain file");
                Assert.Equal(K2, ApiKeyStore.Get("claude"), "the newer key still reads");
                ApiKeyStore.SyncLocked();
                Assert.Equal(K2, s.Unlocked("claude"), "and stays locked");
                Assert.Equal("sk-test-O1", s.Unlocked("openai"), "the migrated one is locked by the sync");
            }

            using (var s = new KeyScene())
            {
                s.WriteLocked(Locked.Json("claude", Locked.LockedElsewhere(K2)));
                Assert.Equal(0, ApiKeyStore.MigrateFrom(new AiApiKeys { Claude = K1 }), "not past a key this account cannot read");
                Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("claude"), "which is reported instead");
            }

            using (var s = new KeyScene())
            {
                s.WriteLocked("{\"claude\": \"AQAA");
                Assert.Equal(0, ApiKeyStore.MigrateFrom(new AiApiKeys { Claude = K1, OpenAi = "sk-test-O1" }), "nothing past a damaged locked file");
                Assert.True(!File.Exists(s.PlainPath), "the plain file not created");
            }
        }

        /// <summary>
        /// The rows of the design note's §5 table not already in the state table,
        /// with products at different steps acting on the same files.
        /// </summary>
        public static void MixedVersions_EachStepSeesTheOthersKeys()
        {
            // Step 1 saves, step 0 reads: the plain file has it, in the form a
            // step-0 product (and Sidekick's parser) reads.
            using (var s = new KeyScene())
            {
                ApiKeyStore.Set("claude", K1);
                Assert.Equal(K1, ApiKeyStore.Load()["claude"], "a whole-file read of the plain file sees it");
                Assert.True(File.ReadAllText(s.PlainPath).Contains("\"claude\": \"" + K1 + "\""), "written as before");
            }

            // Step 1 saves after a step-2 import removed the plain file.
            using (var s = new KeyScene())
            {
                s.WriteLocked(Locked.Json("claude", Locked.Lock(K1), "openai", Locked.Lock("sk-test-O1")));
                Assert.True(ApiKeyStore.Set("claude", K2), "saved");
                Assert.Equal(1, ApiKeyStore.Load().Count, "the plain file recreated with that one key");
                Assert.Equal(K2, ApiKeyStore.Get("claude"), "read from the plain file");
                Assert.Equal("sk-test-O1", ApiKeyStore.Get("openai"), "the rest from the locked file");
                Assert.Equal(K2, s.Unlocked("claude"), "and step 2, reading the locked file, sees the new key too");
            }

            // Step 2 saves while a step-1 product's plain file exists: it removes the
            // key from the plain file and locks the new one, and step 1 follows.
            using (var s = new KeyScene())
            {
                ApiKeyStore.Set("claude", K1);
                ApiKeyStore.Set("openai", "sk-test-O1");
                s.WritePlain(Locked.Json("openai", "sk-test-O1"));              // step 2: X out of the plain file...
                s.WriteLocked(Locked.Json("claude", Locked.Lock(K2), "openai", Locked.Lock("sk-test-O1")));   // ...then locked
                Assert.Equal(K2, ApiKeyStore.Get("claude"), "step 1 falls through to the locked file");
                ApiKeyStore.SyncLocked();
                Assert.Equal(K2, s.Unlocked("claude"), "and its sync does not undo the save");
            }

            // Known and accepted (§5): a key deleted by hand or by a step-0 product
            // during the step-1 months comes back from the locked file.
            using (var s = new KeyScene())
            {
                ApiKeyStore.Set("claude", K1);
                s.WritePlain("{}");
                Assert.Equal(K1, ApiKeyStore.Get("claude"), "comes back, by design");
            }
        }

        /// <summary>
        /// The locked file held by another process for longer than a writer waits: a
        /// read says it could not be opened (not "no key"), a save still counts
        /// because the plain file decides, a removal does not - the key would still
        /// be read - and the next sync catches up.
        /// </summary>
        public static void ALockedFileHeldByAnotherProcess()
        {
            using (var s = new KeyScene())
            {
                ApiKeyStore.Set("claude", K1);
                File.Delete(s.PlainPath);

                using (new FileStream(s.LockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.Equal(ApiKeyStatus.Unreadable, ApiKeyStore.Status("claude"), "unreadable while held, not absent");
                    Assert.Equal("Can't use your Anthropic key \u2013 the file holding your locked API keys could not be opened: "
                        + s.LockedPath + ". Try again in a moment.", ApiKeyStore.MissingKeyMessage("claude", Place), "says so");

                    Assert.True(ApiKeyStore.Set("openai", "sk-test-O1"), "a save still counts: the plain file decides");
                    Assert.True(s.LoggedAny("openai key is saved in api-keys.json but not yet locked"), "and the gap is logged: " + s.Log());
                    Assert.True(!ApiKeyStore.Set("claude", ""), "a removal does not: the locked key would still be read");
                }

                var sync = ApiKeyStore.SyncLocked();
                Assert.Equal(1, sync.Locked, "the sync catches up");
                Assert.Equal("sk-test-O1", s.Unlocked("openai"), "locked now");
                Assert.Equal(K1, ApiKeyStore.Get("claude"), "the key whose removal failed is still there, as reported");
            }
        }

        /// <summary>
        /// Every product must lock and unlock exactly as the design note's contract
        /// says, or a key locked by one will not open in another. Here the contract's
        /// raw Windows call - no description, no entropy, as Sidekick and Workbench
        /// make it - against core, both ways, with a key that is not plain ASCII.
        /// </summary>
        public static void ContractMatchesTheRawWindowsCall()
        {
            using (var s = new KeyScene())
            {
                const string theirs = "sk-ant-test-Contract-Acm\u00e9-PROJ-001";
                var utf8 = new UTF8Encoding(false);
                s.WriteLocked(Locked.Json("claude", Convert.ToBase64String(RawDpapi(true, utf8.GetBytes(theirs)))));
                Assert.Equal(theirs, ApiKeyStore.Get("claude"), "core opens a key the raw call locked");

                const string ours = "sk-test-Contract-\u00dcber";
                Assert.True(ApiKeyStore.Set("openai", ours), "core locks one");
                var opened = RawDpapi(false, Convert.FromBase64String(s.LockedEntries()["openai"]));
                Assert.True(opened.SequenceEqual(utf8.GetBytes(ours)),
                    "the raw call opens it to exactly the key's UTF-8 bytes - no byte-order mark, no terminating zero (" + opened.Length + " bytes)");
            }
        }

        /// <summary>
        /// The design note guessed about a millisecond to unlock one key. Measured
        /// here and printed: one unlock, one whole lookup from a locked file of
        /// fifteen keys, and a start-up sync of fifteen keys with nothing to change.
        /// </summary>
        public static void OneUnlockTakesAboutAMillisecond()
        {
            var blob = DpapiSecret.Protect("sk-ant-test-Timing-PROJ-001");
            DpapiSecret.TryUnprotect(blob, out _, out _);
            const int unlocks = 200;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < unlocks; i++)
                Assert.True(DpapiSecret.TryUnprotect(blob, out _, out _), "unlocked");
            var unlockMs = clock.Elapsed.TotalMilliseconds / unlocks;

            double lookupMs, syncMs;
            using (var s = new KeyScene())
            {
                var pairs = new List<string>();
                for (int i = 0; i < 15; i++) { pairs.Add(i == 0 ? "claude" : "provider" + i); pairs.Add("sk-test-" + i + "-" + new string('k', 100)); }
                s.WritePlain(Locked.Json(pairs.ToArray()));
                ApiKeyStore.SyncLocked();

                const int syncs = 10;
                clock.Restart();
                for (int i = 0; i < syncs; i++)
                    Assert.Equal(0, ApiKeyStore.SyncLocked().Locked, "nothing to change");
                syncMs = clock.Elapsed.TotalMilliseconds / syncs;

                File.Delete(s.PlainPath);
                const int lookups = 100;
                clock.Restart();
                for (int i = 0; i < lookups; i++)
                    Assert.True(ApiKeyStore.Get("claude") != null, "read from the locked file");
                lookupMs = clock.Elapsed.TotalMilliseconds / lookups;
            }

            Console.WriteLine($"      one unlock {unlockMs:F3} ms; one lookup from the locked file {lookupMs:F2} ms; a 15-key sync with nothing to change {syncMs:F1} ms");
            Assert.True(unlockMs < 20, "an unlock takes " + unlockMs + " ms");
        }

        /// <summary>
        /// The locked file is where the design note says, and no test reaches it: the
        /// run redirects it before anything else.
        /// </summary>
        public static void TheRealLockedFile_IsInAppData_AndNotTheOneTestsUse()
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Supervertaler", "api-keys.locked.json");
            Assert.Equal(expected, ApiKeyStore.DefaultLockedFilePath, "%APPDATA%\\Supervertaler\\api-keys.locked.json");
            Assert.True(!string.Equals(ApiKeyStore.LockedFilePath, expected, StringComparison.OrdinalIgnoreCase), "redirected for the run");
            Assert.Equal("Saved on this computer only, locked to your Windows sign-in.", ApiKeyStore.LockedKeyLine, "M3");
        }

        // ─── helpers ─────────────────────────────────────────────────────────────

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        /// <summary>The contract's call, made raw: no description, no entropy, never a prompt.</summary>
        private static byte[] RawDpapi(bool protect, byte[] input)
        {
            var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
            try
            {
                var dataIn = new DataBlob { Size = input.Length, Data = pinned.AddrOfPinnedObject() };
                var dataOut = new DataBlob();
                var ok = protect
                    ? CryptProtectData(ref dataIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut)
                    : CryptUnprotectData(ref dataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut);
                if (!ok) throw new AssertFailed("the raw call failed: Windows error " + Marshal.GetLastWin32Error());
                try
                {
                    var output = new byte[dataOut.Size];
                    Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
                    return output;
                }
                finally { LocalFree(dataOut.Data); }
            }
            finally { pinned.Free(); }
        }
    }

    /// <summary>Reads and writes a locked key file the way another product would: strict JSON, then Windows.</summary>
    internal static class Locked
    {
        /// <summary>Every entry, id exactly as written. Throws if the file is not valid JSON.</summary>
        public static Dictionary<string, string> Entries(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var entry in (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(text))
                map[entry.Key] = entry.Value as string;
            return map;
        }

        /// <summary>The key under <paramref name="id"/>, spelt exactly so, unlocked; null when absent or refused.</summary>
        public static string Unlock(string path, string id) =>
            File.Exists(path) && Entries(path).TryGetValue(id, out var blob) && DpapiSecret.TryUnprotect(blob, out var key, out _) ? key : null;

        public static string Lock(string key) => DpapiSecret.Protect(key);

        /// <summary>
        /// Locked with an entropy value, so the contract's call - which passes none -
        /// is refused: a key saved under another Windows account, made for real.
        /// </summary>
        public static string LockedElsewhere(string key) => Convert.ToBase64String(ProtectedData.Protect(
            new UTF8Encoding(false).GetBytes(key), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, DataProtectionScope.CurrentUser));

        /// <summary>Locked properly, then one byte near the end - the signature - flipped.</summary>
        public static string Tampered(string key)
        {
            var blob = Convert.FromBase64String(Lock(key));
            blob[blob.Length - 30] ^= 0x5A;
            return Convert.ToBase64String(blob);
        }

        /// <summary>A key file as another product writes one: id, value, id, value...</summary>
        public static string Json(params string[] idValue)
        {
            var json = new JavaScriptSerializer();
            var sb = new StringBuilder("{\n  \"_comment\": \"written by a test\"");
            for (int i = 0; i < idValue.Length; i += 2)
                sb.Append(",\n  ").Append(json.Serialize(idValue[i])).Append(": ").Append(json.Serialize(idValue[i + 1]));
            return sb.Append("\n}\n").ToString();
        }
    }

    /// <summary>
    /// A temporary data root and locked key file standing in for the user's, and
    /// what core logged meanwhile. Puts all of it back when disposed.
    /// </summary>
    internal sealed class KeyScene : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "sv-locked-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logged = new List<string>();
        private readonly string _previousLocked = ApiKeyStore.LockedFilePath;
        private readonly Action<string> _previousLog = ApiKeyStore.Log;

        public KeyScene()
        {
            Directory.CreateDirectory(Dir);
            SupervertalerPaths.Set(Dir);
            ApiKeyStore.RedirectLockedFile(Program.LockedPathFor(Dir));
            ApiKeyStore.Log = m => { lock (_logged) _logged.Add(m); };
        }

        public string PlainPath => ApiKeyStore.FilePath;
        public string LockedPath => ApiKeyStore.LockedFilePath;
        public string LockedDir => Path.GetDirectoryName(LockedPath);

        public void WritePlain(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlainPath));
            File.WriteAllText(PlainPath, text, new UTF8Encoding(false));
        }

        public void WriteLocked(string text) => WriteLockedBytes(new UTF8Encoding(false).GetBytes(text));

        public void WriteLockedBytes(byte[] bytes)
        {
            Directory.CreateDirectory(LockedDir);
            File.WriteAllBytes(LockedPath, bytes);
        }

        public byte[] PlainBytes() => File.Exists(PlainPath) ? File.ReadAllBytes(PlainPath) : null;
        public byte[] LockedBytes() => File.Exists(LockedPath) ? File.ReadAllBytes(LockedPath) : null;
        public Dictionary<string, string> LockedEntries() => Locked.Entries(LockedPath);
        public string Unlocked(string id) => Locked.Unlock(LockedPath, id);

        public string[] SetAside() => Directory.Exists(LockedDir)
            ? Directory.GetFiles(LockedDir, "api-keys.locked.damaged-*.json")
            : new string[0];

        public bool LoggedAny(string text) { lock (_logged) return _logged.Any(l => l.Contains(text)); }
        public string Log() { lock (_logged) return string.Join(" | ", _logged); }

        public static bool Same(byte[] a, byte[] b) => a == null ? b == null : b != null && a.SequenceEqual(b);

        public void Dispose()
        {
            ApiKeyStore.Log = _previousLog;
            ApiKeyStore.RedirectLockedFile(_previousLocked);
            SupervertalerPaths.Reset();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }
}
