using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// Removing a wrong translation (docs/design/evertm-pane.md, section 8): every formatting and variant, both
    /// directions; hidden at once in this process, applied through the inbox; re-imports and the batch task don't bring
    /// it back, a confirmation in the editor does; undo and restore bring it back exactly; failures keep it queued.
    /// Test data uses Acme / PROJ-001 only.
    /// </summary>
    static class RemoveTests
    {
        static VaultLibrary Lib() => VaultLibrary.For(Scratch.Folder("remove"));

        static string TmxFile(string body, string name = "acme.tmx")
        {
            var path = Path.Combine(Scratch.Folder("tmx"), name);
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"x\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" + body + "</body></tmx>", new UTF8Encoding(false));
            return path;
        }

        static string T(string nl, string en, string attrs = "", string inner = "", string nlLang = "nl-NL", string enLang = "en-GB") =>
            "<tu" + attrs + ">" + inner + "<tuv xml:lang=\"" + nlLang + "\"><seg>" + nl + "</seg></tuv><tuv xml:lang=\"" + enLang + "\"><seg>" + en + "</seg></tuv></tu>";

        static VaultImportResult Import(VaultLibrary lib, string tmx) => lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);

        static List<VaultMatch> Find(VaultLibrary lib, string text, string sl = "nl", string tl = "en", string culture = null)
        {
            lib.Refresh();
            return lib.Lookup(LibraryTests.Doc(text), sl, tl, 50, 10, culture);
        }

        static string Target(VaultMatch m) => VaultLookup.PlainOf(m.Target);

        static List<string> Targets(VaultLibrary lib, string text, string sl = "nl", string tl = "en") => Find(lib, text, sl, tl).Select(Target).ToList();

        /// <summary>Stores and applies everything waiting, as the learning thread would.</summary>
        static void Settle(VaultLibrary lib, int seconds = 30)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (lib.LearnOnce() && lib.LearnWaiting() == 0 && lib.RemovalsWaiting() == 0) { lib.Refresh(); return; }
                Thread.Sleep(50);
            }
            throw new AssertFailed("removing did not finish: " + lib.LearnError);
        }

        static long N(VaultLibrary lib, string sql)
        {
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            }
        }

        const string Pump = "De pomp wordt gestart.";
        const string Typo = "The pmup is started.";

        /// <summary>One sentence with a newer typo (in two formattings and two variants) and an older good translation.</summary>
        static VaultLibrary WithTypo()
        {
            var lib = Lib();
            Import(lib, TmxFile(
                T(Pump, "The pump is started.", " creationdate=\"20200101T000000Z\"", "<prop type=\"x-Project\">PROJ-001</prop>") +
                T(Pump, Typo, " creationdate=\"20250101T000000Z\" creationid=\"jdoe\"", "<prop type=\"x-Client\">Acme</prop>") +
                T("De <bpt i=\"1\">&lt;b&gt;</bpt>pomp<ept i=\"1\">&lt;/b&gt;</ept> wordt gestart.", "The <bpt i=\"1\">&lt;b&gt;</bpt>pmup<ept i=\"1\">&lt;/b&gt;</ept> is started.", " creationdate=\"20240101T000000Z\"") +
                T(Pump + "<ph x=\"1\">&lt;br/&gt;</ph>", Typo + "<ph x=\"1\">&lt;br/&gt;</ph>", " creationdate=\"20230101T000000Z\"", "", "nl-BE", "en-US") +
                T("De klep sluit.", "The valve closes.")));
            return lib;
        }

        static VaultCandidate TypoOf(VaultLibrary lib)
        {
            var m = Find(lib, Pump);
            A.True(m.Count > 0 && Target(m[0]) == Typo, "the typo is the newest, so it is the match: " + (m.Count > 0 ? Target(m[0]) : "none"));
            return m[0].Candidate;
        }

        // ---- the happy path ----

        public static void RemovingTakesEveryFormattingAndVariantInBothDirections()
        {
            var lib = WithTypo();
            A.Eq(5L, lib.StoreCount(), "five units");
            lib.Remove(new VaultCandidate { SourceRaw = "Opwarmen.", TargetRaw = "Warm-up." }, "nl", "en", null);   // the first call in a process loads its code
            var typo = TypoOf(lib);
            var sw = Stopwatch.StartNew();
            var key = lib.Remove(typo, "nl", "en", "mbeijer");
            A.True(sw.ElapsedMilliseconds < 50, "Remove returns at once (" + sw.ElapsedMilliseconds + " ms)");
            A.True(key != null, "a key for Undo");
            A.True(!Targets(lib, Pump).Contains(Typo), "hidden in this process at once");
            bool told = false;
            lib.RemovalsApplied += () => told = true;
            Settle(lib);
            A.Eq(2L, lib.StoreCount(), "the three units of the typo are gone (plain, bold, nl-BE>en-US)");
            A.True(told, "the library says when a removal has been applied, so a pane can look again");
            A.Eq("The pump is started.", Targets(lib, Pump).Single(), "only the good translation is left: now it is the match");
            A.Eq(0, Find(lib, Typo, "en", "nl").Count(m => m.Score == 100), "and the reverse direction no longer has it");
            A.Eq(2L, N(lib, "SELECT count(*) FROM removed"), "a mark for it (and one for the warm-up)");
            A.Eq(3L, N(lib, "SELECT count(*) FROM removed_units"), "with a copy of each unit");
            A.Eq(1, lib.Status.Units - 1, "the index holds the 2 left: " + lib.Status.Units);
        }

        public static void ASecondProcessStopsOfferingItWithinTwoSeconds()
        {
            var lib = WithTypo();
            var other = VaultLibrary.ForTests(lib.Root);
            A.Eq(Typo, Targets(other, Pump).First(), "the other process offers the typo");
            lib.Remove(TypoOf(lib), "nl", "en", null);
            Settle(lib);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500 && Targets(other, Pump).FirstOrDefault() == Typo) Thread.Sleep(100);
            A.Eq("The pump is started.", Targets(other, Pump).FirstOrDefault(), "the other process stopped offering it (" + sw.ElapsedMilliseconds + " ms)");
        }

        // ---- what doesn't bring it back, and what does ----

        public static void ReImportingSkipsIt()
        {
            var lib = WithTypo();
            lib.Remove(TypoOf(lib), "nl", "en", null);
            Settle(lib);
            var r = Import(lib, TmxFile(T(Pump, Typo, " creationdate=\"20260101T000000Z\"") + T("De klep sluit niet.", "The valve does not close."), "again.tmx"));
            A.Eq(1L, r.Removed, "the removed translation is skipped and counted");
            A.True(r.ToString().Contains("you removed it from EverTM"), "and the summary says why: " + r);
            A.Eq(1L, r.Added, "the rest is imported");
            A.True(!Targets(lib, Pump).Contains(Typo), "still not offered");
        }

        static VaultLearnUnit Confirm(string nl, string en, bool fromBatch) =>
            new VaultLearnUnit { SourceCulture = "nl-NL", Source = nl, TargetCulture = "en-GB", Target = en, Origin = "studio", User = "mbeijer", Project = "PROJ-001", FromBatch = fromBatch };

        public static void TheBatchTaskDoesntBringItBackAConfirmationInTheEditorDoes()
        {
            var lib = WithTypo();
            lib.Remove(TypoOf(lib), "nl", "en", null);
            Settle(lib);
            lib.Learn(new[] { Confirm(Pump, Typo, fromBatch: true) });
            Settle(lib);
            A.True(!Targets(lib, Pump).Contains(Typo), "Update Main Translation Memories: not stored");
            A.Eq(1L, lib.LearnedThisSession.Removed, "and counted as not stored because removed");

            lib.Learn(new[] { Confirm(Pump, Typo, fromBatch: false) });
            A.Eq(Typo, Targets(lib, Pump).FirstOrDefault(), "confirmed in the editor: offered again at once in this process");
            Settle(lib);
            var m = Find(lib, Pump);
            A.Eq(Typo, Target(m[0]), "and stored: it is the newest again");
            A.Eq(0L, N(lib, "SELECT count(*) FROM removed"), "the mark is gone");
            A.Eq(5L, lib.StoreCount(), "its three units came back (as removed) and the confirmation merged into one of them");
            A.True(m[0].Candidate.Fields.Any(f => f.Field == "Client" && f.Value == "Acme"), "with the metadata it had before: Client Acme");
        }

        // ---- undo and restore ----

        public static void UndoBringsItBackExactly()
        {
            var lib = WithTypo();
            var before = Find(lib, Pump)[0].Candidate;
            var key = lib.Remove(before, "nl", "en", "mbeijer");
            Settle(lib);
            var list = lib.Removed(0, out int total);
            A.Eq(1, total, "one removed translation listed");
            A.Eq(3, list[0].Units, "of three units");
            A.Eq("mbeijer", list[0].RemovedBy, "by whom");
            A.True(DateTime.UtcNow - list[0].RemovedAt < TimeSpan.FromMinutes(1), "and when");
            A.Eq(Typo, VaultMarkup.Plain(list[0].TextA == null ? "" : (list[0].Key.LangA == "en" ? list[0].TextA : list[0].TextB)), "with its texts");

            lib.Restore(key, "mbeijer");
            Settle(lib);
            A.Eq(5L, lib.StoreCount(), "all three units back");
            var after = Find(lib, Pump)[0].Candidate;
            A.Eq(Typo, VaultMarkup.Plain(after.TargetRaw), "the typo is the newest again");
            A.Eq(before.Created, after.Created, "its dates as they were");
            A.Eq(before.CreatedBy, after.CreatedBy, "its user");
            A.Eq(string.Join("|", before.Fields), string.Join("|", after.Fields), "its fields");
            A.Eq(1L, N(lib, "SELECT count(*) FROM units WHERE variant = 'nl-BE>en-US'"), "its variant as it was");
            A.Eq(0, lib.Removed(0, out total).Count, "the list is empty again");
            A.Eq(0L, N(lib, "SELECT count(*) FROM removed_units"), "no copies left");
        }

        public static void RemoveUndoRemoveAgainWorksInOneGo()
        {
            var lib = WithTypo();
            var c = TypoOf(lib);
            var key = lib.Remove(c, "nl", "en", null);
            lib.Restore(key);
            lib.Remove(c, "nl", "en", null);
            Settle(lib);
            A.Eq(2L, lib.StoreCount(), "applied in order: removed, back, removed again");
            A.True(!Targets(lib, Pump).Contains(Typo), "not offered");
        }

        // ---- failure paths ----

        public static void AFailedRemovalStaysHiddenAndIsAppliedLater()
        {
            var lib = WithTypo();
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "removal-commit") throw new IOException("disk full (simulated)"); };
                lib.Remove(TypoOf(lib), "nl", "en", null);
                for (int i = 0; i < 3; i++) lib.LearnOnce();
                A.Eq(5L, lib.StoreCount(), "nothing removed: the store's transaction rolled back");
                A.Eq(1L, lib.RemovalsWaiting(), "the removal waits in the inbox");
                A.True(lib.LearnError != null, "and says why: " + lib.LearnError);
                A.True(!Targets(lib, Pump).Contains(Typo), "meanwhile this process still hides it");
            }
            finally { VaultLibrary.FailPoint = null; }
            Settle(lib);
            A.Eq(2L, lib.StoreCount(), "applied once the store can be written");
            A.Eq(0L, lib.RemovalsWaiting(), "nothing waiting");
        }

        public static void ARemovalLeftByAClosedStudioIsAppliedByTheNext()
        {
            var lib = WithTypo();
            var c = TypoOf(lib);
            var other = VaultLibrary.ForTests(lib.Root);
            try
            {
                VaultLibrary.FailPoint = p => { if (p == "removal-commit") throw new IOException("Studio closed (simulated)"); };
                lib.Remove(c, "nl", "en", null);
                lib.LearnOnce();
            }
            finally { VaultLibrary.FailPoint = null; }
            lib.DropRemovalsHiddenForTests();   // as if that Studio had closed: only inbox.db remembers
            A.Eq(1L, other.RemovalsWaiting(), "the other process sees it waiting in inbox.db");
            Settle(other);
            A.Eq(2L, lib.StoreCount(), "and applies it");
        }

        public static void ALearnedUnitRemovedThenItsSourceCorrected()
        {
            var lib = WithTypo();
            lib.Learn(new[] { Confirm("De klep opent.", "The valve opnes.", false) });
            Settle(lib);
            var m = Find(lib, "De klep opent.")[0];
            lib.Remove(m.Candidate, "nl", "en", null);
            Settle(lib);
            A.Eq(0L, N(lib, "SELECT count(*) FROM learned"), "its learned row went with it");
            lib.Learn(new[] { Confirm("De klep opent.", "The valve opens.", false) });
            Settle(lib);
            A.Eq("The valve opens.", Target(Find(lib, "De klep opent.")[0]), "the correction is learned afresh");
        }

        public static void ARemovalDuringARebuildIsAppliedAfterIt()
        {
            var lib = WithTypo();
            lib.Remove(TypoOf(lib), "nl", "en", null);
            lib.Build(null, CancellationToken.None);   // holds the build lock; the removal is applied after it, under the same lock
            A.True(!Targets(lib, Pump).Contains(Typo), "hidden throughout, and gone in the new generation");
            Settle(lib);
            A.Eq(2L, lib.StoreCount(), "removed from the store");
        }

        public static void NothingToRemoveIsHarmless()
        {
            var lib = Lib();
            A.Eq(null, lib.Remove(null, "nl", "en", null), "no translation: nothing queued");
            A.Eq(null, lib.Remove(new VaultCandidate { SourceRaw = "Iets.", TargetRaw = "" }, "nl", "en", null), "no target: nothing queued");
            lib.Restore(VaultRemovalKey.For("nl", "Nooit verwijderd.", "en", "Never removed."));
            Settle(lib);
            A.Eq(0, lib.Removed(0, out int total).Count + total, "restoring what was never removed changes nothing");
        }

        // ---- scale ----

        /// <summary>Marks cost one indexed lookup per imported unit, and nothing while there are none.</summary>
        public static void ImportSpeedWithMarks()
        {
            const int n = 20000;
            double Run(VaultLibrary lib, string tag)
            {
                var sw = Stopwatch.StartNew();
                lib.Import((store, p, c) =>
                {
                    using (var w = new VaultStore.Writer(store))
                    {
                        for (int i = 0; i < n; i++) w.Add("nl-NL", "Zin nummer " + i + " " + tag + ".", "en-GB", "Sentence number " + i + " " + tag + ".", "x");
                        w.Commit();
                        return w.Result;
                    }
                }, null, CancellationToken.None);
                return sw.Elapsed.TotalMilliseconds;
            }
            var plain = Lib();
            double without = Run(plain, "a");
            var marked = VaultLibrary.For(Scratch.Folder("remove-marks"));
            Import(marked, TmxFile(T(Pump, Typo)));
            for (int i = 0; i < 200; i++) { marked.Remove(new VaultCandidate { SourceRaw = "Weg " + i + ".", TargetRaw = "Gone " + i + "." }, "nl", "en", null); }
            marked.Remove(TypoOf(marked), "nl", "en", null);
            Settle(marked);
            A.Eq(201L, N(marked, "SELECT count(*) FROM removed"), "201 marks");
            double with = Run(marked, "b");
            Console.WriteLine($"      (importing {n:N0} units: {without:N0} ms without marks, {with:N0} ms with 201)");
            A.True(with < without * 1.6 + 500, "marks cost little per unit: " + with + " vs " + without);
        }
    }
}
