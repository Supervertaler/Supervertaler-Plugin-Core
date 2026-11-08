using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    static class LibraryTests
    {
        static List<(string, string, string, string)> Units() => new List<(string, string, string, string)>
        {
            ("De pomp wordt gestart.", "The pump is started.", "nl-BE", "en-US"),
            ("Klik op <b>Opslaan</b> om de wijzigingen te bewaren.", "Click <b>Save</b> to keep the changes.", "nl", "en"),
            ("Het apparaat moet worden schoongemaakt na elk gebruik.", "The device must be cleaned after each use.", "nl-NL", "en-GB"),
            ("The valve is closed automatically.", "De klep wordt automatisch gesloten.", "en-GB", "nl-NL"),
            ("Other", "AndereSearch in:\" \"All Categories", "en", "nl"),          // junk
            ("Gleiche Sprache", "Gleiche Sprache", "de-DE", "de-AT"),                 // same base language: skipped
            ("Die Pumpe wird gestartet.", "De pomp wordt gestart.", "de-DE", "nl-NL"), // another pair
            ("<ph x=\"1\"/>Afbeelding <ph x=\"2\"/> toont de pomp.", "<ph x=\"2\"/> shows the pump in figure <ph x=\"1\"/>.", "nl", "en"),
        };

        internal static List<VaultPiece> Doc(string text) => new List<VaultPiece> { new VaultPiece { Text = text } };

        internal static Func<string, Action<string>, CancellationToken, VaultImportResult> Workbench(string db, int timeout = 30) =>
            (store, p, c) => VaultStore.ImportWorkbench(store, db, p, c, timeout);

        internal static Func<string, Action<string>, CancellationToken, VaultImportResult> Tmx(string path) =>
            (store, p, c) => VaultTmx.Import(store, path, p, c);

        /// <summary>A library filled from a Workbench-style database (the optional import), index built.</summary>
        internal static VaultLibrary Built(out string db)
        {
            db = Scratch.Database(Units());
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            var r = lib.Import(Workbench(db), null, CancellationToken.None);
            A.Eq(6L, r.Added, "units stored (junk and same-language rows skipped): " + r);
            A.Eq(6, lib.Status.Units, "units indexed");
            return lib;
        }

        internal static string WriteTmx(string body, string srclang = "en-US")
        {
            var path = Path.Combine(Scratch.Folder("tmx"), "test.tmx");
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"" + srclang + "\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"x\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" + body + "</body></tmx>", new UTF8Encoding(false));
            return path;
        }

        internal static string Tu(params (string lang, string seg)[] tuvs) =>
            "<tu>" + string.Concat(tuvs.Select(t => "<tuv xml:lang=\"" + t.lang + "\"><seg>" + t.seg + "</seg></tuv>")) + "</tu>";

        public static void EmptyIsMissingNotAnError()
        {
            var lib = VaultLibrary.For(Scratch.Folder("empty"));
            A.True(lib.Current == null, "no index");
            A.Eq(VaultState.Missing, lib.Status.State, "state");
            A.True(lib.Status.Message.Contains("import"), "message says what to do: " + lib.Status.Message);
            A.Eq(0L, lib.StoreCount(), "no store");
            A.Throws<FileNotFoundException>(() => lib.Build(null, CancellationToken.None), "building an empty SuperVault");
            A.Eq(0, VaultLookup.Search(null, Doc("x"), "nl", "en", 50, 5).Count, "lookup on no index");
        }

        public static void ExactMatchBothDirectionsAndAnyVariant()
        {
            var lib = Built(out _);
            A.Eq(VaultState.Ready, lib.Status.State, "state");
            var idx = lib.Current;
            var m = VaultLookup.Search(idx, Doc("De pomp wordt  gestart."), "nl", "en", 70, 5);
            A.Eq(100, m[0].Score, "nl>en exact, extra space ignored");
            A.Eq("The pump is started.", VaultLookup.PlainOf(m[0].Target), "target");
            m = VaultLookup.Search(idx, Doc("The pump is started."), "en", "nl", 70, 5);
            A.Eq(100, m[0].Score, "en>nl from a unit stored nl-BE>en-US");
            A.Eq("De pomp wordt gestart.", VaultLookup.PlainOf(m[0].Target), "reverse target");
            m = VaultLookup.Search(idx, Doc("De pomp wordt gestart."), "nl", "de", 70, 5);
            A.Eq("Die Pumpe wird gestartet.", VaultLookup.PlainOf(m[0].Target), "nl>de from the de-nl unit, not the en one");
            A.True(idx.Pairs().SetEquals(new[] { "en|nl", "de|nl" }), "pairs: " + string.Join(",", idx.Pairs()));
        }

        public static void FuzzyMatchScoresBelow100()
        {
            var lib = Built(out _);
            var m = VaultLookup.Search(lib.Current, Doc("Het apparaat moet worden schoongemaakt na ieder gebruik."), "nl", "en", 50, 5);
            A.True(m.Count == 1 && m[0].Score >= 80 && m[0].Score < 100, "one fuzzy match, score " + (m.Count > 0 ? m[0].Score : -1));
            A.True(m[0].Diff.Edits.Any(e => e.Op == VaultEditOp.Change), "the changed word is marked");
        }

        public static void TagsMapOntoTheDocumentsTags()
        {
            var lib = Built(out _);
            var doc = new List<VaultPiece>
            {
                new VaultPiece { Text = "Klik op " }, new VaultPiece { Kind = VaultTagKind.Open, Key = "d1", Raw = "<1>" },
                new VaultPiece { Text = "Opslaan" }, new VaultPiece { Kind = VaultTagKind.Close, Key = "d1/c", Raw = "</1>" },
                new VaultPiece { Text = " om de wijzigingen te bewaren." },
            };
            var m = VaultLookup.Search(lib.Current, doc, "nl", "en", 70, 5);
            A.Eq(100, m[0].Score, "score");
            A.Eq(VaultTagOutcome.Mapped, m[0].Tags, "tags");
            A.Eq("-1,1,-1,3,-1", string.Join(",", m[0].TargetDocTag), "target tags are document tags 1 and 3");
        }

        public static void UnmappableTagsStillMatchAsText()
        {
            var lib = Built(out _);
            var m = VaultLookup.Search(lib.Current, Doc("Afbeelding toont de pomp."), "nl", "en", 70, 5);
            A.Eq(100, m[0].Score, "same words");
            A.Eq(VaultTagOutcome.Unmapped, m[0].Tags, "tags");
            A.True(m[0].Target.All(p => !p.IsTag), "offered without tags");
            A.Eq("shows the pump in figure .", VaultLookup.PlainOf(m[0].Target), "text kept");
        }

        public static void ConcordanceFindsWords()
        {
            var lib = Built(out _);
            var m = VaultLookup.Concordance(lib.Current, "pump", "en", "nl", 30, 10);
            A.True(m.Count >= 2, "units containing 'pump': " + m.Count);
        }

        // ---- TMX ----

        public static void TmxImportKeepsTagsAndServesBothWays()
        {
            var tmx = WriteTmx(
                Tu(("en-US", "Click <bpt i=\"1\" x=\"7\">&lt;b&gt;</bpt>Save<ept i=\"1\">&lt;/b&gt;</ept> now.<ph x=\"8\">&lt;br/&gt;</ph>"),
                   ("nl-BE", "Klik nu op <bpt i=\"1\" x=\"7\">{b}</bpt>Opslaan<ept i=\"1\">{/b}</ept>.<ph x=\"8\"/>")) +
                Tu(("en-GB", "The pump <hi type=\"x\">starts</hi>."), ("nl-NL", "De pomp start."), ("de-DE", "Die Pumpe startet.")));
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            var r = lib.Import(Tmx(tmx), null, CancellationToken.None);
            A.Eq(4L, r.Added, "1 + 3 language pairs from the trilingual TU: " + r);
            A.Eq(4, lib.Status.Units, "indexed");

            var doc = new List<VaultPiece>
            {
                new VaultPiece { Text = "Klik nu op " }, new VaultPiece { Kind = VaultTagKind.Open, Key = "d1", Raw = "<1>" },
                new VaultPiece { Text = "Opslaan" }, new VaultPiece { Kind = VaultTagKind.Close, Key = "d3", Raw = "</1>" },
                new VaultPiece { Text = "." }, new VaultPiece { Kind = VaultTagKind.Standalone, Key = "d5", Raw = "<br>" },
            };
            var m = VaultLookup.Search(lib.Current, doc, "nl", "en", 70, 5);
            A.Eq(100, m[0].Score, "nl>en from an en-US>nl-BE TMX unit");
            A.Eq(VaultTagOutcome.Mapped, m[0].Tags, "TMX tags kept and mapped: " + m[0].TagReason);
            A.Eq("Click Save now.", VaultLookup.PlainOf(m[0].Target), "native code inside bpt/ept/ph dropped");
            A.Eq(3, m[0].Target.Count(p => p.IsTag), "three tags in the target");
            m = VaultLookup.Search(lib.Current, Doc("Die Pumpe startet."), "de", "nl", 70, 5);
            A.Eq("De pomp start.", VaultLookup.PlainOf(m[0].Target), "de>nl from the trilingual TU");
            m = VaultLookup.Search(lib.Current, Doc("The pump starts."), "en", "nl", 70, 5);
            A.Eq(100, m[0].Score, "<hi> keeps its text");
        }

        public static void DuplicatesAreStoredOnce()
        {
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            var a = WriteTmx(Tu(("en-US", "Press <bpt i=\"1\"/>Start<ept i=\"1\"/>."), ("nl-BE", "Druk op <bpt i=\"1\"/>Start<ept i=\"1\"/>.")));
            // The same unit the other way round, other variants, other tag numbers, extra spaces.
            var b = WriteTmx(Tu(("nl-NL", "Druk  op <bpt i=\"4\" x=\"9\"/>Start<ept i=\"4\"/>."), ("en-GB", "Press <bpt i=\"4\" x=\"9\"/>Start<ept i=\"4\"/>.")), "nl-NL");
            var c = WriteTmx(Tu(("en-US", "Press Start."), ("nl-BE", "Druk op Start.")));   // same words, no formatting: a different unit
            A.Eq(1L, lib.Import(Tmx(a), null, CancellationToken.None).Added, "first");
            var r = lib.Import(Tmx(a), null, CancellationToken.None);
            A.True(r.Added == 0 && r.Duplicates == 1, "same file again: " + r);
            r = lib.Import(Tmx(b), null, CancellationToken.None);
            A.True(r.Added == 0 && r.Duplicates == 1, "reversed, other variants and tag ids: " + r);
            A.Eq(1L, lib.Import(Tmx(c), null, CancellationToken.None).Added, "without formatting is another unit");
            A.Eq(2L, lib.StoreCount(), "store");
        }

        public static void MalformedTmxChangesNothing()
        {
            var lib = Built(out _);
            var path = Path.Combine(Scratch.Folder("tmx"), "broken.tmx");
            File.WriteAllText(path, "<tmx><header srclang=\"en\"/><body>" + Tu(("en", "One new unit."), ("nl", "Een nieuwe eenheid.")) + "<tu><tuv xml:lang=\"en\"><seg>broken", new UTF8Encoding(false));
            A.Throws<System.Xml.XmlException>(() => lib.Import(Tmx(path), null, CancellationToken.None), "import of a cut-off TMX");
            A.Eq(6L, lib.StoreCount(), "the unit read before the error was rolled back");
            A.Eq(6, lib.Status.Units, "index as it was");
        }

        public static void IndexKnowsWhenItIsOutOfDate()
        {
            var lib = Built(out _);
            A.True(!lib.IndexOutOfDate(), "just built");
            VaultTmx.Import(lib.StorePath, WriteTmx(Tu(("en", "A brand new sentence."), ("nl", "Een gloednieuwe zin."))), null, CancellationToken.None);
            A.True(lib.IndexOutOfDate(), "store changed behind the index's back");
            lib.Build(null, CancellationToken.None);
            A.True(!lib.IndexOutOfDate(), "rebuilt");
        }

        // ---- failure modes ----

        public static void MissingWorkbenchDatabaseChangesNothing()
        {
            var lib = Built(out _);
            A.Throws<FileNotFoundException>(() => lib.Import(Workbench(@"C:\nowhere\supervertaler.db"), null, CancellationToken.None), "import");
            A.Eq(6, lib.Status.Units, "index still in use");
            A.Eq(6L, lib.StoreCount(), "store unchanged");
            A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built folder left");
        }

        public static void LockedWorkbenchDatabaseChangesNothing()
        {
            var lib = Built(out var db);
            using (var c = new SqliteConnection("Data Source=" + db + ";Pooling=False"))
            {
                c.Open();
                Scratch.Exec(c, "PRAGMA journal_mode=DELETE");   // in WAL mode readers are never blocked; an exclusive lock in rollback mode does
                Scratch.Exec(c, "BEGIN EXCLUSIVE");
                Scratch.Exec(c, "UPDATE translation_units SET usage_count = 1");
                A.Throws<SqliteException>(() => lib.Import(Workbench(db, 1), null, CancellationToken.None), "import from a locked database");
                Scratch.Exec(c, "ROLLBACK");
            }
            A.Eq(VaultState.Ready, lib.Status.State, "index still in use");
            A.Eq(6L, lib.StoreCount(), "store unchanged");
            A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built folder left");
        }

        public static void SecondJobAtOnceIsRefused()
        {
            var lib = Built(out var db);
            using (new FileStream(Path.Combine(lib.Root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                A.Throws<IOException>(() => lib.Build(null, CancellationToken.None), "build while another holds the lock");
                A.Throws<IOException>(() => lib.Import(Workbench(db), null, CancellationToken.None), "import while another holds the lock");
            }
        }

        public static void CancelledImportLeavesNothingBehind()
        {
            var lib = Built(out _);
            var big = Scratch.Database(Enumerable.Range(0, 5000).Select(i => ($"Zin nummer {i} over de pomp.", $"Sentence number {i} about the pump.", "nl", "en")));
            var cts = new CancellationTokenSource();
            cts.Cancel();
            A.Throws<OperationCanceledException>(() => lib.Import(Workbench(big), null, cts.Token), "cancelled import");
            A.Eq(6L, lib.StoreCount(), "store unchanged");
            A.Eq(6, lib.Status.Units, "index unchanged");
            A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built folder left");
        }

        public static void ImportWhileLookingUpSwitchesOverWithoutErrors()
        {
            var lib = Built(out _);
            var big = Scratch.Database(Enumerable.Range(0, 20000).Select(i => ($"Zin nummer {i} over de pomp.", $"Sentence number {i} about the pump.", "nl", "en")));
            var oldGen = Directory.GetDirectories(lib.Root, "gen-*").Single();
            string error = "not finished";
            var done = new ManualResetEventSlim();
            A.True(lib.StartImport(Workbench(big), e => { error = e; done.Set(); }), "import started");
            A.True(!lib.StartBuild(), "a second job in this process is refused");
            int lookups = 0;
            while (!done.IsSet)
            {
                var m = VaultLookup.Search(lib.Current, Doc("De pomp wordt gestart."), "nl", "en", 70, 5);
                A.Eq(100, m[0].Score, "lookup during the import");
                lookups++;
            }
            A.Eq(null, error, "import result");
            A.True(lib.LastJobResult.StartsWith("20,000 units added"), "summary: " + lib.LastJobResult);
            Thread.Sleep(2100);   // the pointer is re-read every 2 s
            A.Eq(20006, lib.Status.Units, "new index in use after " + lookups + " lookups during the import");
            A.Eq(100, VaultLookup.Search(lib.Current, Doc("Zin nummer 12345 over de pomp."), "nl", "en", 70, 5)[0].Score, "new unit found");
            for (int i = 0; i < 50 && Directory.Exists(oldGen); i++) Thread.Sleep(100);   // cleanup runs 200 ms after the switch (tests)
            A.True(!Directory.Exists(oldGen), "old generation deleted once unused");
        }

        /// <summary>A built SuperVault copied to a new folder this process has never opened, so it is opened fresh.</summary>
        internal static string CopyOfBuilt(Action<string> damage)
        {
            var built = Built(out _);
            var gen = Directory.GetDirectories(built.Root, "gen-*").Single();
            var root = Scratch.Folder("vault-copy");
            CopyDir(gen, Path.Combine(root, Path.GetFileName(gen)));
            File.Copy(built.StorePath, Path.Combine(root, "supervault.db"));
            File.WriteAllText(Path.Combine(root, "current.txt"), Path.GetFileName(gen));
            damage(Path.Combine(root, Path.GetFileName(gen)));
            return root;
        }

        public static void CorruptIndexIsReportedAndRebuildRepairsIt()
        {
            var root = CopyOfBuilt(gen => { foreach (var f in Directory.GetFiles(gen).Where(f => !f.EndsWith("info.txt"))) File.WriteAllBytes(f, new byte[] { 1, 2, 3 }); });
            var lib = VaultLibrary.For(root);
            A.True(lib.Current == null, "damaged index is not used");
            A.Eq(VaultState.Broken, lib.Status.State, "state");
            A.True(lib.Status.Message.Contains("Rebuild"), "message says what to do: " + lib.Status.Message);
            A.Eq(0, VaultLookup.Search(lib.Current, Doc("De pomp wordt gestart."), "nl", "en", 70, 5).Count, "no results, no exception");
            lib.Build(null, CancellationToken.None);
            A.Eq(VaultState.Ready, lib.Status.State, "rebuilt from the store");
            A.Eq(6, lib.Status.Units, "everything back");
        }

        public static void IndexOfAnotherFormatIsRebuiltNotRead()
        {
            var root = CopyOfBuilt(gen =>
            {
                var info = Path.Combine(gen, "info.txt");
                File.WriteAllText(info, File.ReadAllText(info).Replace("format=" + VaultIndex.Format, "format=" + (VaultIndex.Format - 1)));
            });
            var lib = VaultLibrary.For(root);
            A.True(lib.Current == null, "an index in an older format is not used");
            A.True(lib.Status.State == VaultState.Broken && lib.Status.Message.Contains("another version"), "status: " + lib.Status.Message);
            lib.Build(null, CancellationToken.None);
            A.Eq(VaultState.Ready, lib.Status.State, "rebuilt");
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
    }
}
