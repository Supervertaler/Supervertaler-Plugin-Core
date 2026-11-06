using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        static List<VaultPiece> Doc(string text) => new List<VaultPiece> { new VaultPiece { Text = text } };

        static VaultLibrary Built(out string db)
        {
            db = Scratch.Database(Units());
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            int n = lib.Build(db, null, CancellationToken.None);
            A.Eq(6, n, "units indexed (junk and same-language rows skipped)");
            return lib;
        }

        public static void NoIndexIsMissingNotAnError()
        {
            var lib = VaultLibrary.For(Scratch.Folder("empty"));
            A.True(lib.Current == null, "no index");
            A.Eq(VaultState.Missing, lib.Status.State, "state");
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
            // The document has no tags; the unit has two placeholders.
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

        public static void MissingDatabaseFailsCleanlyAndKeepsTheIndex()
        {
            var lib = Built(out _);
            var before = lib.Status.Units;
            A.Throws<FileNotFoundException>(() => lib.Build(@"C:\nowhere\supervertaler.db", null, CancellationToken.None), "build");
            A.Eq(before, lib.Status.Units, "old index still in use");
            A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built folder left");
        }

        public static void LockedDatabaseFailsCleanlyAndKeepsTheIndex()
        {
            var lib = Built(out var db);
            using (var c = new SqliteConnection("Data Source=" + db))
            {
                c.Open();
                Scratch.Exec(c, "PRAGMA journal_mode=DELETE");   // in WAL mode readers are never blocked; an exclusive lock in rollback mode does
                Scratch.Exec(c, "BEGIN EXCLUSIVE");
                Scratch.Exec(c, "UPDATE translation_units SET usage_count = 1");
                A.Throws<SqliteException>(() => lib.Build(db, null, CancellationToken.None, busyTimeoutSeconds: 1), "build against a locked database");
                Scratch.Exec(c, "ROLLBACK");
            }
            A.Eq(VaultState.Ready, lib.Status.State, "old index still in use");
            A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built folder left");
        }

        public static void SecondBuildAtOnceIsRefused()
        {
            var lib = Built(out var db);
            using (new FileStream(Path.Combine(lib.Root, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                A.Throws<IOException>(() => lib.Build(db, null, CancellationToken.None), "build while another holds the lock");
        }

        public static void CancelledBuildLeavesNothingBehind()
        {
            var lib = Built(out _);
            var big = Scratch.Database(Enumerable.Range(0, 5000).Select(i => ($"Zin nummer {i} over de pomp.", $"Sentence number {i} about the pump.", "nl", "en")));
            var cts = new CancellationTokenSource();
            cts.Cancel();
            A.Throws<OperationCanceledException>(() => lib.Build(big, null, cts.Token), "cancelled build");
            A.Eq(6, lib.Status.Units, "old index still in use");
            A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built folder left");
        }

        public static void RebuildWhileLookingUpSwitchesOverWithoutErrors()
        {
            var lib = Built(out _);
            var big = Scratch.Database(Units().Concat(Enumerable.Range(0, 20000).Select(i => ($"Zin nummer {i} over de pomp.", $"Sentence number {i} about the pump.", "nl", "en"))));
            var oldGen = Directory.GetDirectories(lib.Root, "gen-*").Single();
            string buildError = "not finished";
            var done = new ManualResetEventSlim();
            A.True(lib.StartBuild(big, e => { buildError = e; done.Set(); }), "build started");
            A.True(!lib.StartBuild(big), "a second build in this process is refused");
            int lookups = 0;
            while (!done.IsSet)
            {
                var m = VaultLookup.Search(lib.Current, Doc("De pomp wordt gestart."), "nl", "en", 70, 5);
                A.Eq(100, m[0].Score, "lookup during the build");
                lookups++;
            }
            A.Eq(null, buildError, "build result");
            Thread.Sleep(2100);   // the pointer is re-read every 2 s
            A.Eq(20006, lib.Status.Units, "new index in use after " + lookups + " lookups during the build");
            A.Eq(100, VaultLookup.Search(lib.Current, Doc("Zin nummer 12345 over de pomp."), "nl", "en", 70, 5)[0].Score, "new unit found");
            for (int i = 0; i < 50 && Directory.Exists(oldGen); i++) Thread.Sleep(100);   // cleanup runs 200 ms after the switch (tests)
            A.True(!Directory.Exists(oldGen), "old generation deleted once unused");
        }

        public static void CorruptIndexOpenedFirstTimeIsBroken()
        {
            var db = Scratch.Database(Units());
            var root = Scratch.Folder("vault");
            // Build with a throwaway instance at another path, then move the generation in place: this process has never opened it.
            var buildRoot = Scratch.Folder("vault-build");
            VaultLibrary.For(buildRoot).Build(db, null, CancellationToken.None);
            var gen = Directory.GetDirectories(buildRoot, "gen-*").Single();
            foreach (var f in Directory.GetFiles(gen).Where(f => !f.EndsWith("info.txt"))) File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
            CopyDir(gen, Path.Combine(root, Path.GetFileName(gen)));
            File.WriteAllText(Path.Combine(root, "current.txt"), Path.GetFileName(gen));

            var lib = VaultLibrary.For(root);
            A.True(lib.Current == null, "damaged index is not used");
            var s = lib.Status;
            A.Eq(VaultState.Broken, s.State, "state");
            A.True(s.Message.Contains("Rebuild"), "message says what to do: " + s.Message);
            A.Eq(0, VaultLookup.Search(lib.Current, Doc("De pomp wordt gestart."), "nl", "en", 70, 5).Count, "no results, no exception");
            lib.Build(db, null, CancellationToken.None);
            A.Eq(VaultState.Ready, lib.Status.State, "rebuild repairs it");
        }

        public static void IndexOfAnotherFormatIsRebuiltNotRead()
        {
            var db = Scratch.Database(Units());
            var buildRoot = Scratch.Folder("vault-build");
            VaultLibrary.For(buildRoot).Build(db, null, CancellationToken.None);
            var gen = Directory.GetDirectories(buildRoot, "gen-*").Single();
            var root = Scratch.Folder("vault");
            CopyDir(gen, Path.Combine(root, Path.GetFileName(gen)));
            var info = Path.Combine(root, Path.GetFileName(gen), "info.txt");
            File.WriteAllText(info, File.ReadAllText(info).Replace("format=" + VaultIndex.Format, "format=" + (VaultIndex.Format - 1)));
            File.WriteAllText(Path.Combine(root, "current.txt"), Path.GetFileName(gen));
            var lib = VaultLibrary.For(root);
            A.True(lib.Current == null, "an index in an older format is not used");
            A.True(lib.Status.State == VaultState.Broken && lib.Status.Message.Contains("another version"), "status: " + lib.Status.Message);
            lib.Build(db, null, CancellationToken.None);
            A.Eq(VaultState.Ready, lib.Status.State, "rebuilt");
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
    }
}
