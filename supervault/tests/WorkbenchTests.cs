using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using static Supervertaler.Core.SuperVault.Tests.LibraryTests;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>Import from Workbench with a choice of TMs: the list (names, pairs, counts) and importing only the ticked ones.</summary>
    static class WorkbenchTests
    {
        /// <summary>
        /// A Workbench database with TMs "a" (named Alpha: 3 nl>en, 1 en>nl), "b" (Beta: 2 de>nl), "c" (units, no name),
        /// "d" (Delta: named, no units) and "e" (an empty name, units). Without <paramref name="names"/>, no
        /// translation_memories table at all.
        /// </summary>
        static string Db(bool names = true, bool oldNamesTable = false)
        {
            var path = Path.Combine(Scratch.Folder("wb"), "supervertaler.db");
            using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                c.Open();
                Scratch.Exec(c, @"CREATE TABLE translation_units (id INTEGER PRIMARY KEY AUTOINCREMENT, source_text TEXT NOT NULL, target_text TEXT NOT NULL,
                                 source_lang TEXT NOT NULL, target_lang TEXT NOT NULL, tm_id TEXT NOT NULL, source_hash TEXT NOT NULL)");
                Scratch.Exec(c, "CREATE INDEX idx_tu_tm_id ON translation_units(tm_id)");
                int n = 0;
                void Unit(string tm, string sl, string s, string tl, string t)
                {
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO translation_units (source_text, target_text, source_lang, target_lang, tm_id, source_hash) VALUES ($s, $t, $sl, $tl, $tm, $h)";
                        cmd.Parameters.AddWithValue("$s", s); cmd.Parameters.AddWithValue("$t", t); cmd.Parameters.AddWithValue("$sl", sl);
                        cmd.Parameters.AddWithValue("$tl", tl); cmd.Parameters.AddWithValue("$tm", tm); cmd.Parameters.AddWithValue("$h", (n++).ToString());
                        cmd.ExecuteNonQuery();
                    }
                }
                Unit("a", "nl", "De pomp wordt gestart.", "en", "The pump is started.");
                Unit("a", "nl", "De klep is dicht.", "en", "The valve is closed.");
                Unit("a", "nl", "Druk op Start.", "en", "Press Start.");
                Unit("a", "en", "The lid is open.", "nl", "Het deksel is open.");
                Unit("b", "de", "Die Pumpe läuft.", "nl", "De pomp draait.");
                Unit("b", "de", "Das Ventil ist zu.", "nl", "De klep is dicht.");
                Unit("c", "nl", "Een zin zonder naam.", "es", "Una frase sin nombre.");
                Unit("e", "nl", "Een zin met een lege naam.", "en", "A sentence with an empty name.");
                if (names && !oldNamesTable)
                {
                    Scratch.Exec(c, "CREATE TABLE translation_memories (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, tm_id TEXT NOT NULL UNIQUE)");
                    Scratch.Exec(c, "INSERT INTO translation_memories (name, tm_id) VALUES ('Alpha', 'a'), ('Beta', 'b'), ('Delta', 'd'), ('', 'e')");
                }
                if (names && oldNamesTable) Scratch.Exec(c, "CREATE TABLE translation_memories (id INTEGER PRIMARY KEY, title TEXT)");
            }
            SqliteConnection.ClearAllPools();
            return path;
        }

        static string Hash(string path) { using (var sha = SHA256.Create()) using (var f = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(f)); }

        public static void ListShowsNamesPairsAndCounts()
        {
            var db = Db();
            var tms = VaultStore.ListWorkbenchTms(db, CancellationToken.None);
            A.Eq("Alpha|Beta|c|Delta|e", string.Join("|", tms.Select(t => t.Name)), "sorted by name; no name or an empty one: the tm_id");
            var a = tms.Single(t => t.TmId == "a");
            A.Eq(4L, a.Units, "Alpha's units");
            A.Eq("nl > en, en > nl", a.PairsText, "both directions, largest first");
            A.Eq(2L, tms.Single(t => t.TmId == "b").Units, "Beta's units");
            var d = tms.Single(t => t.TmId == "d");
            A.True(d.Units == 0 && d.PairsText == "", "a named TM with no units is listed with 0");
        }

        public static void MissingNamesTableListsTmIds()
        {
            var tms = VaultStore.ListWorkbenchTms(Db(names: false), CancellationToken.None);
            A.Eq("a|b|c|e", string.Join("|", tms.Select(t => t.Name)), "names are the tm_ids; no TM without units");
            tms = VaultStore.ListWorkbenchTms(Db(oldNamesTable: true), CancellationToken.None);
            A.Eq("a|b|c|e", string.Join("|", tms.Select(t => t.Name)), "a names table without the expected columns: tm_ids, no error");
        }

        public static void ImportsOnlyTheTickedTms()
        {
            var db = Db();
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            var r = lib.Import((store, p, c) => VaultStore.ImportWorkbench(store, db, p, c, tmIds: new[] { "b", "c" }), null, CancellationToken.None);
            A.Eq(3L, r.Added, "Beta's 2 and c's 1");
            lib.Refresh();
            A.Eq(100, VaultLookup.Search(lib.Current, Doc("Die Pumpe läuft."), "de", "nl", 50, 5).FirstOrDefault()?.Score ?? 0, "a ticked TM's unit is found");
            A.Eq(0, VaultLookup.Search(lib.Current, Doc("De pomp wordt gestart."), "nl", "en", 99, 5).Count, "an unticked TM's unit is not");
        }

        public static void EmptyChoiceImportsNothing()
        {
            var db = Db();
            var root = Scratch.Folder("vault");
            var r = VaultStore.ImportWorkbench(Path.Combine(root, "supervault.db"), db, null, CancellationToken.None, tmIds: new string[0]);
            A.Eq(0L, r.Added + r.Read, "nothing read or added");
            A.True(!File.Exists(Path.Combine(root, "supervault.db")), "no store created");
        }

        public static void UnknownTmIdsMatchNothing()
        {
            var db = Db();
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            var r = lib.Import((store, p, c) => VaultStore.ImportWorkbench(store, db, p, c, tmIds: new[] { "no-such-tm", "d" }), null, CancellationToken.None);
            A.Eq(0L, r.Added, "an unknown tm_id and a TM without units: nothing");
            r = lib.Import((store, p, c) => VaultStore.ImportWorkbench(store, db, p, c, tmIds: new[] { "no-such-tm", "a", "a", "it's \"quoted\"; --" }), null, CancellationToken.None);
            A.Eq(4L, r.Added, "the known one is imported; unknown, repeated and odd tm_ids do no harm");
        }

        public static void NoChoiceImportsEverythingAsBefore()
        {
            var db = Db();
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            var r = lib.Import((store, p, c) => VaultStore.ImportWorkbench(store, db, p, c), null, CancellationToken.None);
            A.Eq(8L, r.Added, "every unit");
        }

        public static void WorkbenchDatabaseIsOnlyRead()
        {
            var db = Db();
            var before = Hash(db);
            VaultStore.ListWorkbenchTms(db, CancellationToken.None);
            VaultStore.ImportWorkbench(Path.Combine(Scratch.Folder("vault"), "supervault.db"), db, null, CancellationToken.None, tmIds: new[] { "a", "b" });
            SqliteConnection.ClearAllPools();
            A.Eq(before, Hash(db), "byte-identical after listing and importing");
            A.True(!File.Exists(db + "-wal") && !File.Exists(db + "-journal"), "no journal left beside it");
        }

        public static void MissingWorkbenchDatabaseIsAClearError()
        {
            A.Throws<FileNotFoundException>(() => VaultStore.ListWorkbenchTms(Path.Combine(Scratch.Folder("none"), "supervertaler.db"), CancellationToken.None), "listing");
        }

        public static void TheChoiceIsRemembered()
        {
            var lib = VaultLibrary.For(Scratch.Folder("vault"));
            A.Eq(0, lib.WorkbenchUnticked().Count, "nothing remembered yet: everything ticked");
            lib.RememberWorkbenchUnticked(new[] { "b", "line\nbreak", "c" });
            A.Eq("b|c", string.Join("|", lib.WorkbenchUnticked().OrderBy(x => x)), "remembered (a tm_id with a line break can't be, and is left out)");
            lib.RememberWorkbenchUnticked(new string[0]);
            A.Eq(0, lib.WorkbenchUnticked().Count, "everything ticked again");
        }

        /// <summary>
        /// Many TMs, many units: the list is one pass, so its time grows with the units, not with units times TMs.
        /// 300,000 units in 300 TMs.
        /// </summary>
        public static void ListIsOnePassOverManyTms()
        {
            var path = Path.Combine(Scratch.Folder("wb-big"), "supervertaler.db");
            using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                c.Open();
                Scratch.Exec(c, "CREATE TABLE translation_units (id INTEGER PRIMARY KEY AUTOINCREMENT, source_text TEXT NOT NULL, target_text TEXT NOT NULL, source_lang TEXT NOT NULL, target_lang TEXT NOT NULL, tm_id TEXT NOT NULL)");
                Scratch.Exec(c, "CREATE INDEX idx_tu_tm_id ON translation_units(tm_id)");
                Scratch.Exec(c, @"WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 299999)
                                 INSERT INTO translation_units (source_text, target_text, source_lang, target_lang, tm_id)
                                 SELECT 'Zin ' || i, 'Sentence ' || i, 'nl', 'en', 'tm' || (i % 300) FROM n");
            }
            SqliteConnection.ClearAllPools();
            var sw = Stopwatch.StartNew();
            var tms = VaultStore.ListWorkbenchTms(path, CancellationToken.None);
            A.True(tms.Count == 300 && tms.All(t => t.Units == 1000), "300 TMs of 1,000 units");
            Console.WriteLine($"      (listed 300 TMs, 300,000 units in {sw.ElapsedMilliseconds} ms)");
        }
    }
}
