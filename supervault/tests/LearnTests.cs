using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Lucene.Net.Index;
using FSDirectory = Lucene.Net.Store.FSDirectory;
using Microsoft.Data.Sqlite;
using static Supervertaler.Core.SuperVault.Tests.LibraryTests;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>Learning as you work: confirmed segments into the store, and the index updated a piece at a time.</summary>
    static class LearnTests
    {
        static VaultLearnUnit U(string sl, string s, string tl, string t) => new VaultLearnUnit { SourceCulture = sl, Source = s, TargetCulture = tl, Target = t, Origin = "studio" };

        /// <summary>Stores everything waiting, as the background thread would, and fails the test if that doesn't finish.</summary>
        internal static void Settle(VaultLibrary lib, int seconds = 30)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (lib.LearnOnce() && lib.LearnWaiting() == 0) { lib.Refresh(); return; }
                Thread.Sleep(50);
            }
            throw new AssertFailed("learning did not finish: " + lib.LearnError);
        }

        static string Gen(VaultLibrary lib) => File.ReadAllText(Path.Combine(lib.Root, "current.txt")).Trim();

        /// <summary>The best match's target and score for a nl (or other) source, from the index now in use.</summary>
        static (string target, int score, int count) Best(VaultLibrary lib, string source, string sl = "nl", string tl = "en")
        {
            lib.Refresh();
            var m = VaultLookup.Search(lib.Current, Doc(source), sl, tl, 50, 10);
            return m.Count == 0 ? (null, 0, 0) : (VaultLookup.PlainOf(m[0].Target), m[0].Score, m.Count(x => x.Score == 100));
        }

        static long InboxCount(VaultLibrary lib)
        {
            var path = Path.Combine(lib.Root, "inbox.db");
            if (!File.Exists(path)) return 0;
            using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False")) { c.Open(); using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT count(*) FROM pending"; return (long)cmd.ExecuteScalar(); } }
        }

        // ---- the happy path ----

        public static void ConfirmedSegmentIsFoundWithoutARebuild()
        {
            var lib = Built(out _);
            var gen = Gen(lib);
            var sw = Stopwatch.StartNew();
            lib.Learn(new[] { U("nl-BE", "De filter moet maandelijks worden vervangen.", "en-US", "The filter must be replaced monthly.") });
            A.True(sw.ElapsedMilliseconds < 50, "Learn returns at once (" + sw.ElapsedMilliseconds + " ms)");
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "stored");
            A.Eq(7, lib.Status.Units, "indexed");
            A.Eq(gen, Gen(lib), "same generation: updated in place, not rebuilt");
            var b = Best(lib, "De filter moet maandelijks worden vervangen.");
            A.Eq(100, b.score, "found at 100%");
            A.Eq("The filter must be replaced monthly.", b.target, "its translation");
            A.Eq("De filter moet maandelijks worden vervangen.", Best(lib, "The filter must be replaced monthly.", "en", "nl").target, "and the other way round");
            A.True(!lib.IndexOutOfDate(), "index up to date");
            A.Eq(0L, InboxCount(lib), "inbox emptied");
        }

        public static void BackgroundThreadStoresWithinSeconds()
        {
            var lib = Built(out _);
            lib.Learn(new[] { U("nl", "Het display toont de restcapaciteit.", "en", "The display shows the remaining capacity.") });
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 15 && Best(lib, "Het display toont de restcapaciteit.").score != 100) Thread.Sleep(100);
            A.Eq(100, Best(lib, "Het display toont de restcapaciteit.").score, "found by a lookup without anyone calling LearnOnce");
            Console.WriteLine($"      (found {sw.ElapsedMilliseconds} ms after Learn)");
        }

        public static void FirstConfirmationCreatesSuperVault()
        {
            var lib = VaultLibrary.For(Scratch.Folder("fresh"));
            lib.Learn(new[] { U("de-DE", "Die Pumpe läuft.", "en-GB", "The pump is running.") });
            Settle(lib);
            A.Eq(VaultState.Ready, lib.Status.State, "an index was built for the first unit");
            A.Eq(100, Best(lib, "Die Pumpe läuft.", "de", "en").score, "found");
        }

        // ---- an empty start is a normal state, not an error ----

        /// <summary>No SuperVault folder at all: nothing found, no error, and the first confirmation creates everything.</summary>
        public static void EmptyStartNoFolder()
        {
            var root = Path.Combine(Scratch.Folder("nofolder"), "supervault");
            var lib = VaultLibrary.For(root);
            AssertEmptyButUsable(lib, "no folder");
            A.True(!Directory.Exists(root), "looking creates nothing");
            LearnIntoEmpty(lib, "fr-FR", "La pompe démarre.", "de-DE", "Die Pumpe startet.");
            A.True(File.Exists(lib.StorePath), "the store was created");
        }

        /// <summary>A store with no units and no index (e.g. what is left after the first write failed half-way).</summary>
        public static void EmptyStartStoreWithoutIndex()
        {
            var lib = VaultLibrary.For(Scratch.Folder("emptystore"));
            VaultStore.Create(lib.StorePath);
            AssertEmptyButUsable(lib, "empty store, no index");
            A.True(lib.Status.Message.Contains("no index yet"), "says the index comes later: " + lib.Status.Message);
            LearnIntoEmpty(lib, "nl-BE", "De klep sluit.", "en-US", "The valve closes.");
        }

        /// <summary>An index that holds no units (an empty import): ready, nothing found, learning updates it in place.</summary>
        public static void EmptyStartIndexWithNoUnits()
        {
            var lib = VaultLibrary.For(Scratch.Folder("emptyindex"));
            var r = lib.Import((store, p, c) => { using (var w = new VaultStore.Writer(store)) { w.Commit(); return w.Result; } }, null, CancellationToken.None);
            A.Eq(0, r.Added, "nothing imported");
            lib.Refresh();
            A.Eq(VaultState.Ready, lib.Status.State, "an empty index is ready");
            A.Eq(0, lib.Status.Units, "with no units");
            A.Eq(0, VaultLookup.Search(lib.Current, Doc("De klep sluit."), "nl", "en", 50, 5).Count, "lookup: nothing, no exception");
            A.Eq(0, VaultLookup.Concordance(lib.Current, "klep", "nl", "en", 50, 5).Count(), "concordance: nothing, no exception");
            var gen = Gen(lib);
            LearnIntoEmpty(lib, "fr-FR", "La vanne se ferme.", "de-DE", "Das Ventil schließt.");
            A.Eq(gen, Gen(lib), "the empty index was updated in place");
            lib.Build(null, CancellationToken.None);
            A.Eq(1, lib.Status.Units, "Rebuild index works too");
        }

        /// <summary>Rebuild index on a store with no units: an empty index, not an error.</summary>
        public static void EmptyStoreRebuildsToAnEmptyIndex()
        {
            var lib = VaultLibrary.For(Scratch.Folder("emptyrebuild"));
            VaultStore.Create(lib.StorePath);
            A.Eq(0, lib.Build(null, CancellationToken.None), "built with no units");
            lib.Refresh();
            A.Eq(VaultState.Ready, lib.Status.State, "ready");
        }

        static void AssertEmptyButUsable(VaultLibrary lib, string what)
        {
            var s = lib.Status;
            A.Eq(VaultState.Missing, s.State, what + ": state");
            A.True(s.Message.Contains("confirm"), what + ": the message mentions learning: " + s.Message);
            A.True(s.LearnError == null && s.LastBuildError == null, what + ": no error");
            A.Eq(0L, lib.StoreCount(), what + ": nothing stored");
            A.True(!lib.IndexOutOfDate(), what + ": not 'out of date'");
            A.Eq(0, VaultLookup.Search(lib.Current, Doc("De klep sluit."), "nl", "en", 50, 5).Count, what + ": lookup gives nothing");
        }

        static void LearnIntoEmpty(VaultLibrary lib, string sl, string src, string tl, string trg)
        {
            lib.Learn(new[] { U(sl, src, tl, trg) });
            Settle(lib);
            A.Eq(VaultState.Ready, lib.Status.State, "ready after the first confirmation");
            A.Eq(1, lib.Status.Units, "one unit indexed");
            var b = Best(lib, src, VaultMarkup.BaseLanguage(sl), VaultMarkup.BaseLanguage(tl));
            A.Eq(100, b.score, "the confirmed unit is found");
            A.Eq(trg, b.target, "with its translation");
        }

        // ---- read your own writes: Studio looks a segment up again right after confirming it ----

        /// <summary>The best match through VaultLibrary.Lookup, which CAT tools use: the index plus units just handed over.</summary>
        static (string target, int score, int count) Look(VaultLibrary lib, string source, string sl = "nl", string tl = "en")
        {
            var m = lib.Lookup(Doc(source), sl, tl, 50, 10);
            return m.Count == 0 ? (null, 0, 0) : (VaultLookup.PlainOf(m[0].Target), m[0].Score, m.Count(x => x.Score == 100));
        }

        /// <summary>
        /// Michael's Studio 2026 test, 8 Nov: the first unit confirmed into a SuperVault with no index wasn't shown in
        /// Translation Results until he reopened SuperVault's settings. Studio looks the segment up right after the
        /// confirm and caches the result; SuperVault stored the unit a moment later. Now the lookup sees it at once,
        /// even while it can't be stored at all.
        /// </summary>
        public static void AJustConfirmedUnitIsFoundAtOnceEvenWithNoIndex()
        {
            var lib = VaultLibrary.For(Path.Combine(Scratch.Folder("fresh"), "supervault"));
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "store-commit") throw new IOException("not stored yet (simulated)"); };
                lib.Learn(new[] { U("nl-BE", "In Fig. 3 is de pomp te zien.", "en-US", "Fig. 3 shows the pump.") });
                A.True(lib.Current == null, "no index yet");
                var b = Look(lib, "In Fig. 3 is de pomp te zien.");
                A.Eq((100, "Fig. 3 shows the pump."), (b.score, b.target), "found at once, before it is stored");
                A.Eq("In Fig. 3 is de pomp te zien.", Look(lib, "Fig. 3 shows the pump.", "en", "nl").target, "the other way round too");
                A.Eq(0, Look(lib, "In Fig. 3 is de pomp te zien.", "nl", "de").score, "not for another language pair");
                A.Eq(0, Look(lib, "In Fig. 4 is de pomp te zien.").score, "a fuzzy match waits for the index (a second or two)");
            }
            finally { VaultLibrary.FailPoint = null; }
            Settle(lib);
            var after = Look(lib, "In Fig. 3 is de pomp te zien.");
            A.Eq((100, 1), (after.score, after.count), "once stored: still found, once (the index and the recent unit are one match)");
            int fuzzy = Look(lib, "In Fig. 4 is de pomp te zien.").score;
            A.True(fuzzy >= 70 && fuzzy < 100, "and now as a fuzzy match too: " + fuzzy);
        }

        public static void AJustConfirmedUnitIsFoundAtOnceWithAnIndex()
        {
            var lib = Built(out _);
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "store-commit") throw new IOException("not stored yet (simulated)"); };
                lib.Learn(new[] { U("nl", "De motor stopt.", "en", "The motor stops.") });
                A.Eq(100, Look(lib, "De motor stopt.").score, "found at once");
                A.Eq(100, Look(lib, "De pomp wordt gestart.").score, "the index's units as before");
                lib.Learn(new[] { U("nl", "De motor stopt.", "en", "The engine stops.") });   // corrected before it was stored
                var b = Look(lib, "De motor stopt.");
                A.Eq(("The engine stops.", 1), (b.target, b.count), "the correction replaces it");
            }
            finally { VaultLibrary.FailPoint = null; }
        }

        /// <summary>
        /// The confirm call stays fast: single confirmations are found at once; a batch (Update Main TMs, 500 units a
        /// call) skips that bookkeeping and is handed over in a few milliseconds, as before.
        /// </summary>
        public static void HandingOverStaysFast()
        {
            var lib = VaultLibrary.For(Scratch.Folder("fast"));
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "store-commit") throw new IOException("not stored (simulated)"); };
                var units = Enumerable.Range(0, 1510).Select(i => U("nl", "Zin nummer " + i + " met <b>opmaak</b> erin.", "en", "Sentence number " + i + " with formatting in it.")).ToList();
                lib.Learn(units.Take(10));   // warm up
                var sw = Stopwatch.StartNew();
                for (int i = 10; i < 1010; i++) lib.Learn(new[] { units[i] });
                double perConfirm = sw.Elapsed.TotalMilliseconds / 1000;
                sw.Restart();
                lib.Learn(units.Skip(1010));
                long batch = sw.ElapsedMilliseconds;
                Console.WriteLine($"      (Learn: {perConfirm:F3} ms per confirmation, a batch of 500 in {batch} ms)");
                A.True(perConfirm < 1 && batch < 10, $"fast: {perConfirm:F3} ms per confirmation, 500 in {batch} ms");
                A.Eq(100, Look(lib, "Zin nummer 500 met opmaak erin.").score, "a single confirmation: found at once");
                A.Eq(0, Look(lib, "Zin nummer 1200 met opmaak erin.").score, "a batch unit: found once stored");
            }
            finally { lib.DropWaitingForTests(); VaultLibrary.FailPoint = null; }
        }

        public static void RecentUnitsExpireAfterAMinute()
        {
            var lib = Built(out _);
            long keep = VaultLibrary.RecentKeepTicks;
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "store-commit") throw new IOException("not stored (simulated)"); };
                VaultLibrary.RecentKeepTicks = Stopwatch.Frequency / 5;   // 200 ms instead of a minute
                lib.Learn(new[] { U("nl", "Een zin die niet lukt.", "en", "A sentence that fails.") });
                A.Eq(100, Look(lib, "Een zin die niet lukt.").score, "found at once");
                Thread.Sleep(400);
                A.Eq(0, Look(lib, "Een zin die niet lukt.").score, "no longer, once expired and never stored (it waits in the inbox)");
            }
            finally { VaultLibrary.FailPoint = null; VaultLibrary.RecentKeepTicks = keep; }
            Settle(lib);
            A.Eq(100, Look(lib, "Een zin die niet lukt.").score, "found again once stored");
        }

        public static void UnitWordingIsSingularForOne()
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            A.Eq("1 unit|0 units|2 units|1,234 units", string.Join("|", new long[] { 1, 0, 2, 1234 }.Select(n => VaultLibrary.Units(n, c))), "wording");
        }

        // ---- counting: one confirmation counts once, and says what it did ----

        /// <summary>
        /// Michael's acceptance test: segment 1 was a 100% SuperVault match, he confirmed only it, and the window said
        /// "2 stored". Studio sends a repetition it auto-propagated and confirmed in a second call, a moment later
        /// (TranslatableDocument.UpdateAutopropagatedSegmentsToTranslationMemory). The same unit twice counts once,
        /// and a unit SuperVault already holds is "already in SuperVault", not "stored".
        /// </summary>
        public static void ConfirmingAnExistingUnitTwiceCountsOnceAsAlreadyThere()
        {
            var lib = Built(out _);
            var u = U("nl-BE", "De pomp wordt gestart.", "en-US", "The pump is started.");   // imported, so already there
            lib.Learn(new[] { u });                                   // the confirmation
            lib.Learn(new[] { U(u.SourceCulture, u.Source, u.TargetCulture, u.Target) });   // the auto-propagated repetition
            Settle(lib);
            A.Eq(6L, lib.StoreCount(), "nothing added");
            A.Eq("1 confirmed: 0 added, 1 already in EverTM", lib.LearnedThisSession.ToString(), "counted once, as already there");
            A.Eq(1L, lib.Status.Learned.Already, "the status says the same");
        }

        public static void ANewUnitSentTwiceIsAddedOnce()
        {
            var lib = Built(out _);
            var u = U("nl-BE", "Een herhaalde zin.", "en-US", "A repeated sentence.");
            lib.Learn(new[] { u, U(u.SourceCulture, u.Source, u.TargetCulture, u.Target) });
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "one new unit");
            A.Eq("1 confirmed: 1 added, 0 already in EverTM", lib.LearnedThisSession.ToString(), "counted once, as added");
            lib.Learn(new[] { U(u.SourceCulture, u.Source, u.TargetCulture, u.Target) });   // confirmed again later, unchanged
            Settle(lib);
            A.Eq("2 confirmed: 1 added, 1 already in EverTM", lib.LearnedThisSession.ToString(), "later: already there");
        }

        public static void CountsSayAddedReplacedAlreadyAndSkipped()
        {
            var lib = Built(out _);
            const string src = "Draai de knop naar rechts.";
            lib.Learn(new[] { U("nl", src, "en", "Turn the knob to the rigth.") });
            Settle(lib);
            lib.Learn(new[] { U("nl", src, "en", "Turn the knob to the right.") });   // corrected
            Settle(lib);
            lib.Learn(new[] { U("nl", src, "en", "Turn the knob to the right.") });   // confirmed again, unchanged
            lib.Learn(new[] { U("nl", "Tekst", "nl", "Tekst") });                     // same language twice
            Settle(lib);
            var c = lib.LearnedThisSession;
            A.Eq((1L, 1L, 1L, 1L), (c.Added, c.Replaced, c.Already, c.Skipped), "added, replaced, already, skipped");
            A.Eq("4 confirmed: 1 added, 1 replaced, 1 already in EverTM, 1 skipped (empty or not text)", c.ToString(), "wording");
            A.Eq(1, Best(lib, src).count, "one unit for the source");
        }

        public static void ACorrectionInTheSameBatchStillWins()
        {
            var lib = Built(out _);
            const string src = "Sluit het deksel.";
            lib.Learn(new[] { U("nl", src, "en", "Close the lid"), U("nl", src, "en", "Close the lid."), U("nl", src, "en", "Close the lid.") });
            Settle(lib);
            A.Eq("Close the lid.", Best(lib, src).target, "the last translation is kept");
            A.Eq(1, Best(lib, src).count, "once");
            A.Eq("2 confirmed: 1 added, 1 replaced, 0 already in EverTM", lib.LearnedThisSession.ToString(), "the identical third one counts once with the second");
        }

        public static void OncePerLanguagePairWhateverDirectionAndVariant()
        {
            var lib = Built(out _);
            lib.Learn(new[]
            {
                U("en-GB", "The pump is started.", "nl-NL", "De pomp wordt gestart."),   // an imported unit, reversed, other variants
                U("nl-NL", "De motor draait.", "en-GB", "The motor is running."),
                U("en-US", "The motor is running.", "nl-BE", "De motor draait."),        // the same, reversed
                U("nl-BE", "De motor draait.", "en-US", "The motor is running."),        // the same, other variants
            });
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "one new unit");
            A.Eq(1, Best(lib, "De motor draait.").count, "found once");
        }

        public static void ChangedTranslationUpdatesTheLearnedUnit()
        {
            var lib = Built(out _);
            const string src = "Sluit de klep voordat u de pomp start.";
            lib.Learn(new[] { U("nl-BE", src, "en-US", "Close the valve befor starting the pump.") });
            Settle(lib);
            lib.Learn(new[] { U("nl-BE", src, "en-US", "Close the valve before starting the pump.") });
            lib.Learn(new[] { U("nl-NL", src, "en-GB", "Close the valve before you start the pump.") });   // other variants: same source
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "still one unit for this source");
            var b = Best(lib, src);
            A.Eq("Close the valve before you start the pump.", b.target, "the latest translation");
            A.Eq(1, b.count, "the earlier versions are gone from the index too");
            // The reverse direction is another source: confirming en>nl doesn't rewrite the nl>en unit.
            lib.Learn(new[] { U("en-GB", "Close the valve before you start the pump.", "nl-NL", "Sluit de klep vóór het starten van de pomp.") });
            Settle(lib);
            A.Eq(8L, lib.StoreCount(), "a second unit");
            A.Eq(2, Best(lib, "Close the valve before you start the pump.", "en", "nl").count, "en>nl finds both translations");
            A.Eq("Close the valve before you start the pump.", Best(lib, src).target, "nl>en unchanged");
        }

        public static void ConfirmationNeverChangesAnImportedUnit()
        {
            var lib = Built(out _);
            const string src = "De pomp wordt gestart.";   // imported as "The pump is started."
            lib.Learn(new[] { U("nl-BE", src, "en-US", "The pump starts.") });
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "a second translation, the imported one kept");
            A.Eq(2, Best(lib, src).count, "both found");
            lib.Learn(new[] { U("nl-BE", src, "en-US", "The pump starts up.") });
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "the learned one updated, not a third");
            lib.Learn(new[] { U("nl-BE", src, "en-US", "The pump is started.") });   // back to the imported translation
            Settle(lib);
            A.Eq(6L, lib.StoreCount(), "the learned unit is superseded by the imported one and removed");
            var b = Best(lib, src);
            A.Eq(1, b.count, "one 100% left");
            A.Eq("The pump is started.", b.target, "the imported one");
        }

        public static void TagsAreKeptAsImportsKeepThem()
        {
            var lib = Built(out _);
            lib.Learn(new[] { U("nl", "Druk op <g id=\"7\">Start</g id=\"7\"> om<x id=\"8\"/> te beginnen.", "en", "Press <g id=\"7\">Start</g id=\"7\"> to<x id=\"8\"/> begin.") });
            Settle(lib);
            var doc = new List<VaultPiece>
            {
                new VaultPiece { Text = "Druk op " }, new VaultPiece { Kind = VaultTagKind.Open, Name = "doc", Key = "d1" }, new VaultPiece { Text = "Start" },
                new VaultPiece { Kind = VaultTagKind.Close, Name = "doc", Key = "d1" }, new VaultPiece { Text = " om" }, new VaultPiece { Kind = VaultTagKind.Standalone, Name = "doc", Key = "d2" },
                new VaultPiece { Text = " te beginnen." },
            };
            var m = VaultLookup.Search(lib.Current, doc, "nl", "en", 70, 5);
            A.Eq(100, m[0].Score, "found with its tags");
            A.Eq(3, m[0].Target.Count(p => p.IsTag), "all three tags placed in the translation");
            A.Eq(VaultTagOutcome.Mapped, m[0].Tags, "tags mapped onto the document's");
        }

        public static void SmallImportUpdatesTheIndexInPlaceLargeOneRebuilds()
        {
            var lib = Built(out _);
            var gen = Gen(lib);
            lib.Import(Tmx(WriteTmx(Tu(("en-US", "One extra sentence."), ("nl-NL", "Eén extra zin.")))), null, CancellationToken.None);
            A.Eq(gen, Gen(lib), "1 unit into 6: same generation");
            A.Eq(100, Best(lib, "Eén extra zin.").score, "found");
            var body = string.Concat(Enumerable.Range(0, 10).Select(i => Tu(("en-US", "Sentence number " + i + " of ten."), ("nl-NL", "Zin nummer " + i + " van tien."))));
            lib.Import(Tmx(WriteTmx(body)), null, CancellationToken.None);
            A.True(gen != Gen(lib), "10 units into 7: rebuilt");
            A.Eq(17, lib.Status.Units, "all indexed");
            A.True(!lib.IndexOutOfDate(), "up to date");
        }

        public static void StageOneStoreAndIndexAreUpgradedInPlace()
        {
            var lib = Built(out _);
            // Make it look like stage 1 built it: no change log, no learned table, no position in the index.
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Pooling=False"))
            {
                c.Open();
                foreach (var sql in new[] { "DROP TRIGGER units_ai", "DROP TRIGGER units_au", "DROP TRIGGER units_ad", "DROP TABLE changes", "DROP TABLE learned", "UPDATE meta SET value = '1' WHERE name = 'schema'" })
                    Scratch.Exec(c, sql);
            }
            // An index as stage 1 wrote it: the same documents, no commit data.
            var old = Path.Combine(lib.Root, Gen(lib));
            var folder = Path.Combine(lib.Root, "gen-20261107000000-stage1");
            using (var dir = FSDirectory.Open(folder))
            using (var w = new IndexWriter(dir, new IndexWriterConfig(VaultIndex.V, new VaultIndex.NoAnalyzer()) { OpenMode = OpenMode.CREATE }))
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, lang_a, text_a, lang_b, text_b, variant FROM units";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            w.AddDocument(VaultIndex.MakeDocument(r.GetInt64(0).ToString(), r.GetString(1), r.GetString(2), VaultMarkup.Plain(r.GetString(2)), r.GetString(3), r.GetString(4), VaultMarkup.Plain(r.GetString(4)), r.GetString(5)));
                }
                w.Commit();
            }
            File.Copy(Path.Combine(old, "info.txt"), Path.Combine(folder, "info.txt"));
            File.WriteAllText(Path.Combine(lib.Root, "current.txt"), Path.GetFileName(folder));
            lib.Refresh();
            A.True(!lib.Current.HasStoreSeq, "looks like a stage 1 index");

            lib.Learn(new[] { U("nl", "Een nieuwe zin na de upgrade.", "en", "A new sentence after the upgrade.") });
            Settle(lib);
            A.Eq(7, lib.Status.Units, "learned into the old index, without a rebuild");
            A.Eq(100, Best(lib, "Een nieuwe zin na de upgrade.").score, "found");
            A.True(lib.Current.HasStoreSeq && !lib.IndexOutOfDate(), "now records its position");
        }

        // ---- failure modes ----

        public static void FailedStoreWriteLeavesNothingHalfIn()
        {
            var lib = Built(out _);
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "store-commit") throw new IOException("disk full (simulated)"); };
                lib.Learn(new[] { U("nl", "Een zin die eerst niet lukt.", "en", "A sentence that fails at first.") });
                A.True(!lib.LearnOnce(), "reported as not done");
                A.Eq(6L, lib.StoreCount(), "store unchanged: the transaction was rolled back");
                A.Eq(6, lib.Status.Units, "index unchanged and readable");
                A.Eq(100, Best(lib, "De pomp wordt gestart.").score, "lookups still work");
                A.Eq(1L, InboxCount(lib), "the unit waits in the inbox");
                A.True(lib.LearnError != null && lib.LearnError.Contains("disk full"), "error kept for the dialog: " + lib.LearnError);
                A.Eq(0L, lib.LearnedThisSession.Total, "a failed batch is not counted");
            }
            finally { VaultLibrary.FailPoint = null; }
            Settle(lib);
            A.Eq(100, Best(lib, "Een zin die eerst niet lukt.").score, "stored on the next try");
            A.Eq("1 confirmed: 1 added, 0 already in EverTM", lib.LearnedThisSession.ToString(), "counted once, when stored");
            A.Eq(null, lib.LearnError, "error cleared");
        }

        public static void FailedIndexWriteRollsBackAndCatchesUp()
        {
            var lib = Built(out _);
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "index-commit") throw new IOException("index write failed (simulated)"); };
                lib.Learn(new[] { U("nl", "Een zin voor de index.", "en", "A sentence for the index.") });
                A.True(!lib.LearnOnce(), "reported as not done");
                A.Eq(7L, lib.StoreCount(), "in the store (the memory itself)");
                A.Eq(6, lib.Status.Units, "the index is at its last commit: readable, without the unit");
                A.Eq(0, Best(lib, "Een zin voor de index.").count, "not half in the index");
                A.True(lib.IndexOutOfDate(), "out of date");
            }
            finally { VaultLibrary.FailPoint = null; }
            Settle(lib);
            A.Eq(100, Best(lib, "Een zin voor de index.").score, "the index caught up from the change log");
            A.True(!lib.IndexOutOfDate(), "up to date");
        }

        public static void CrashBetweenStoreAndInboxDoesNotDuplicate()
        {
            var lib = Built(out _);
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "inbox-delete") throw new IOException("Studio died here (simulated)"); };
                lib.Learn(new[] { U("nl", "Een zin die twee keer aankomt.", "en", "A sentence that arrives twice.") });
                A.True(!lib.LearnOnce(), "not done");
                A.Eq(7L, lib.StoreCount(), "stored");
                A.Eq(1L, InboxCount(lib), "and still in the inbox");
            }
            finally { VaultLibrary.FailPoint = null; }
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "learned again harmlessly: no duplicate");
            A.Eq(1, Best(lib, "Een zin die twee keer aankomt.").count, "found once");
        }

        public static void LockedStoreIsRetried()
        {
            var lib = Built(out _);
            int saved = VaultLibrary.StoreBusySeconds;
            VaultLibrary.StoreBusySeconds = 1;
            try
            {
                using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Pooling=False"))
                {
                    c.Open();
                    Scratch.Exec(c, "BEGIN IMMEDIATE");   // another program holds the write lock
                    lib.Learn(new[] { U("nl", "Een zin tijdens een vergrendeling.", "en", "A sentence during a lock.") });
                    var sw = Stopwatch.StartNew();
                    A.True(!lib.LearnOnce(), "not done while locked");
                    A.True(sw.Elapsed.TotalSeconds < 10, "gave up after the busy timeout, didn't hang");
                    A.Eq(1L, InboxCount(lib), "waits in the inbox");
                    A.Eq(100, Best(lib, "De pomp wordt gestart.").score, "lookups unaffected");
                    Scratch.Exec(c, "ROLLBACK");
                }
            }
            finally { VaultLibrary.StoreBusySeconds = saved; }
            Settle(lib);
            A.Eq(100, Best(lib, "Een zin tijdens een vergrendeling.").score, "stored once the lock was gone");
        }

        public static void LockedInboxKeepsUnitsInMemory()
        {
            var lib = Built(out _);
            lib.Learn(new[] { U("nl", "Eerste zin.", "en", "First sentence.") });
            Settle(lib);   // the inbox exists now
            int saved = VaultLibrary.InboxBusySeconds;
            VaultLibrary.InboxBusySeconds = 1;
            try
            {
                using (var c = new SqliteConnection("Data Source=" + Path.Combine(lib.Root, "inbox.db") + ";Pooling=False"))
                {
                    c.Open();
                    Scratch.Exec(c, "BEGIN IMMEDIATE");
                    lib.Learn(new[] { U("nl", "Een zin bij een vergrendelde inbox.", "en", "A sentence with a locked inbox.") });
                    A.True(!lib.LearnOnce(), "not done");
                    A.True(lib.LearnError != null, "error kept: " + lib.LearnError);
                    Scratch.Exec(c, "ROLLBACK");
                }
            }
            finally { VaultLibrary.InboxBusySeconds = saved; }
            Settle(lib);
            A.Eq(100, Best(lib, "Een zin bij een vergrendelde inbox.").score, "kept in memory, then stored");
        }

        public static void BuildLockHeldElsewhereIsWaitedOutNotWaitedOn()
        {
            var lib = Built(out _);
            using (new FileStream(Path.Combine(lib.Root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                lib.Learn(new[] { U("nl", "Een zin terwijl een andere Studio importeert.", "en", "A sentence while another Studio imports.") });
                var sw = Stopwatch.StartNew();
                A.True(!lib.LearnOnce(), "not done");
                A.True(sw.ElapsedMilliseconds < 2000, "returned without waiting for the lock (" + sw.ElapsedMilliseconds + " ms)");
                A.Eq(1L, InboxCount(lib), "safe in the inbox");
            }
            Settle(lib);
            A.Eq(100, Best(lib, "Een zin terwijl een andere Studio importeert.").score, "stored after the lock was released");
        }

        public static void UnitsLeftByAClosedStudioAreStoredByTheNext()
        {
            var lib = Built(out _);
            using (new FileStream(Path.Combine(lib.Root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                lib.Learn(new[] { U("nl", "Een zin van een Studio die werd gesloten.", "en", "A sentence from a Studio that was closed.") });
                lib.LearnOnce();
            }
            A.Eq(1L, InboxCount(lib), "left in the inbox");
            // Another "Studio": a second library object on the same folder, as another process would have.
            var other = VaultLibrary.ForTests(lib.Root);
            Settle(other);
            A.Eq(0L, InboxCount(lib), "the other one moved it");
            A.Eq(100, Best(other, "Een zin van een Studio die werd gesloten.").score, "found");
        }

        public static void DamagedIndexLeavesUnitsInTheStoreUntilRebuilt()
        {
            var root = CopyOfBuilt(gen => { foreach (var f in Directory.GetFiles(gen).Where(f => !f.EndsWith("info.txt"))) File.WriteAllBytes(f, new byte[] { 1, 2, 3 }); });
            var lib = VaultLibrary.For(root);
            A.Eq(VaultState.Broken, lib.Status.State, "broken, as in stage 1");
            lib.Learn(new[] { U("nl", "Een zin bij een beschadigde index.", "en", "A sentence with a damaged index.") });
            Settle(lib);
            A.Eq(7L, lib.StoreCount(), "the unit is in the store");
            A.Eq(VaultState.Broken, lib.Status.State, "still says Rebuild; not rebuilt behind your back");
            lib.Build(null, CancellationToken.None);
            A.Eq(100, Best(lib, "Een zin bij een beschadigde index.").score, "found after Rebuild index");
        }

        public static void MissingIndexIsBuiltOnLearning()
        {
            var lib = Built(out _);
            var gen = Path.Combine(lib.Root, Gen(lib));
            lib.Refresh();
            Thread.Sleep(10);
            File.Delete(Path.Combine(lib.Root, "current.txt"));
            lib.Refresh();
            A.Eq(VaultState.Missing, lib.Status.State, "no index");
            lib.Learn(new[] { U("nl", "Een zin zonder index.", "en", "A sentence without an index.") });
            Settle(lib);
            A.Eq(7, lib.Status.Units, "a new index from the store, with the new unit");
        }

        public static void LearningNeverThrows()
        {
            var lib = Built(out _);
            lib.Learn(null);
            lib.Learn(new VaultLearnUnit[] { null, U(null, null, null, null), U("nl", "", "en", ""), U("xx-INVALID", "x", "", "y") });
            Settle(lib);
            A.Eq(6L, lib.StoreCount(), "nothing stored, nothing thrown");
            // A SuperVault folder that can't exist (its parent is a file).
            var file = Path.Combine(Scratch.Folder("file"), "a-file");
            File.WriteAllText(file, "x");
            var bad = VaultLibrary.For(Path.Combine(file, "supervault"));
            bad.Learn(new[] { U("nl", "Een zin.", "en", "A sentence.") });
            A.True(!bad.LearnOnce(), "not done, no exception");
            A.True(bad.LearnError != null, "error kept: " + bad.LearnError);
        }

        // ---- several Studios at once ----

        /// <summary>
        /// Three processes learn 300 units each into one SuperVault, in batches, while a fourth imports 2,000 units
        /// (holding the build lock for a while) and this one keeps looking things up. Afterwards every unit must be in
        /// the store and in the index exactly once, with nothing left waiting.
        /// </summary>
        public static void SeveralProcessesAtOnceLoseNothing()
        {
            var lib = Built(out _);
            const int perChild = 300;
            var exe = typeof(LearnTests).Assembly.Location;
            var tmxBody = string.Concat(Enumerable.Range(0, 2000).Select(i => Tu(("en-US", "Imported sentence " + i + " about the motor."), ("nl-NL", "Geïmporteerde zin " + i + " over de motor."))));
            var tmx = WriteTmx(tmxBody);
            var procs = new List<Process>();
            for (int k = 0; k < 3; k++) procs.Add(Start(exe, "child", lib.Root, "learn", k.ToString(), perChild.ToString()));
            procs.Add(Start(exe, "child", lib.Root, "import", tmx));
            int lookups = 0;
            while (procs.Any(p => !p.HasExited))
            {
                var m = VaultLookup.Search(lib.Current, Doc("De pomp wordt gestart."), "nl", "en", 70, 5);
                A.Eq(100, m.Count > 0 ? m[0].Score : 0, "lookup while others write");
                lookups++;
                if (procs.Any(p => !p.HasExited && (DateTime.Now - p.StartTime).TotalSeconds > 180)) { procs.ForEach(p => { try { p.Kill(); } catch { } }); throw new AssertFailed("a child process hung (write-lock deadlock?)"); }
            }
            foreach (var p in procs) A.Eq(0, p.ExitCode, "child exit code (" + p.StartInfo.Arguments + "): " + p.StandardOutput.ReadToEnd());
            Settle(lib);
            long expected = 6 + 3 * perChild + 2000;
            A.Eq(expected, lib.StoreCount(), "every unit stored, none twice");
            A.Eq((int)expected, lib.Status.Units, "every unit indexed, none twice");
            A.Eq(0L, InboxCount(lib), "nothing left waiting");
            for (int k = 0; k < 3; k++)
                foreach (int i in new[] { 0, perChild / 2, perChild - 1 })
                    A.Eq(100, Best(lib, ChildSource(k, i)).score, "child " + k + " unit " + i + " found");
            A.Eq(100, Best(lib, "Geïmporteerde zin 1999 over de motor.").score, "imported unit found");
            Console.WriteLine($"      ({lookups:N0} lookups while 4 processes wrote)");
        }

        static string ChildSource(int k, int i) => $"Proces {k} bevestigt zin {i} over de klep.";

        static Process Start(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(a => "\"" + a + "\""))) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            var p = Process.Start(psi);
            p.OutputDataReceived += (s, e) => { };
            return p;
        }

        /// <summary>The other end of <see cref="SeveralProcessesAtOnceLoseNothing"/>: <c>SuperVault.Tests.exe child root learn k n</c> or <c>child root import tmx</c>.</summary>
        public static int Child(string[] args)
        {
            try
            {
                VaultLibrary.LearnDelayMs = 20;
                VaultLibrary.RetryDelayMs = 100;
                var lib = VaultLibrary.For(args[0]);
                if (args[1] == "import")
                {
                    for (int tries = 0; ; tries++)
                    {
                        try { lib.Import(Tmx(args[2]), null, CancellationToken.None); return 0; }
                        catch (IOException) when (tries < 600) { Thread.Sleep(100); }   // the build lock: someone is storing confirmations
                    }
                }
                int k = int.Parse(args[2]), n = int.Parse(args[3]);
                var rnd = new Random(k);
                for (int i = 0; i < n; i += 10)
                {
                    lib.Learn(Enumerable.Range(i, Math.Min(10, n - i)).Select(j => U("nl-BE", ChildSource(k, j), "en-US", $"Process {k} confirms sentence {j} about the valve.")));
                    Thread.Sleep(rnd.Next(0, 30));
                }
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 150)
                {
                    if (lib.LearnOnce() && lib.LearnWaiting() == 0) return 0;
                    Thread.Sleep(50);
                }
                Console.WriteLine("timed out: " + lib.LearnError);
                return 3;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 4; }
        }
    }
}
