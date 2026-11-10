using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    sealed class AssertFailed : Exception { public AssertFailed(string m) : base(m) { } }

    static class Program
    {
        static int Main(string[] args)
        {
            // Before anything else: remember where the user's real data folder is (for Scratch.IsReal), then point
            // Supervertaler's data folder at the scratch folder for the whole run, so nothing a test does can reach
            // D:\Supervertaler (logs, settings, EverTM). In memory only: config.json is not written.
            Scratch.Isolate();
            var filter = args.Length > 0 ? args[0] : null;
            if (filter == "real") return RealScale.Run(args.Skip(1).ToArray());
            if (filter == "child") return LearnTests.Child(args.Skip(1).ToArray());
            if (filter == "learnscale") return LearnScale.Run(args.Skip(1).ToArray());
            if (filter == "limits") return Limits.Run(args.Skip(1).ToArray());
            if (filter == "metascale") return MetaScale.Run(args.Skip(1).ToArray());
            if (filter == "workbenchlist")
            {
                // Times the list of Workbench TMs on a real database (read-only). Prints counts only: TM names can be client names.
                var db = args.Length > 1 ? args[1] : Path.Combine(Scratch.RealDataRoot, "resources", "supervertaler.db");   // read-only
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var tms = VaultStore.ListWorkbenchTms(db, System.Threading.CancellationToken.None);
                    Console.WriteLine($"{(Environment.Is64BitProcess ? "64" : "32")}-bit run {i + 1}: {tms.Count} TMs, {tms.Sum(t => t.Units):N0} units, {tms.Count(t => t.Units == 0)} without units, {tms.Count(t => t.Name == t.TmId)} without a name, {tms.Sum(t => t.Pairs.Count)} TM/pair rows, in {sw.ElapsedMilliseconds} ms");
                }
                return 0;
            }
            VaultLibrary.CleanupDelayMs = 200;
            VaultLibrary.LearnDelayMs = 300;   // the background thread waits a little, so the tests' own LearnOnce usually goes first
            var tests = typeof(Program).Assembly.GetTypes()
                .Where(t => t.Name.EndsWith("Tests"))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void)))
                .Where(m => filter == null || (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            int failed = 0;
            foreach (var t in tests)
            {
                var name = t.DeclaringType.Name + "." + t.Name;
                var sw = Stopwatch.StartNew();
                try { t.Invoke(null, null); Console.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)"); }
                catch (TargetInvocationException e)
                {
                    failed++;
                    Console.WriteLine($"FAIL  {name}\n      {e.InnerException?.Message}");
                    if (!(e.InnerException is AssertFailed)) Console.WriteLine(e.InnerException?.StackTrace);
                }
            }
            Scratch.CleanUp();
            Console.WriteLine(failed == 0 ? $"ALL {tests.Count} PASSED" : $"{failed} of {tests.Count} FAILED");
            return failed == 0 ? 0 : 1;
        }
    }

    static class A
    {
        public static void True(bool c, string what) { if (!c) throw new AssertFailed(what); }
        public static void Eq<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new AssertFailed($"{what}: expected [{expected}], got [{actual}]");
        }
        public static void Throws<TEx>(Action a, string what) where TEx : Exception
        {
            try { a(); }
            catch (TEx) { return; }
            catch (Exception e) { throw new AssertFailed($"{what}: expected {typeof(TEx).Name}, got {e.GetType().Name}: {e.Message}"); }
            throw new AssertFailed($"{what}: expected {typeof(TEx).Name}, nothing thrown");
        }
    }

    /// <summary>Temporary folders and Workbench-style databases. Everything lives under one folder, deleted at the end.</summary>
    static class Scratch
    {
        static readonly string Base = Path.Combine(Path.GetTempPath(), "sv-supervault-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        /// <summary>The user's real Supervertaler data folder, read before <see cref="Isolate"/> replaced it for this run.</summary>
        public static string RealDataRoot { get; private set; }

        /// <summary>This run's own data folder: Supervertaler's data folder while the tests run.</summary>
        public static string DataRoot => Path.Combine(Base, "data");

        /// <summary>Points Supervertaler's data folder at <see cref="DataRoot"/> (in memory; config.json untouched).</summary>
        public static void Isolate()
        {
            if (RealDataRoot != null) return;
            RealDataRoot = SupervertalerPaths.Root;
            Directory.CreateDirectory(DataRoot);
            SupervertalerPaths.Set(DataRoot);
        }

        /// <summary>True for the real EverTM folder, under either name, or anything inside it: a test must never use those.</summary>
        public static bool IsReal(string root)
        {
            var real = RealDataRoot ?? SupervertalerPaths.Root;
            var full = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return new[] { Path.Combine(real, VaultLibrary.FolderName), Path.Combine(real, VaultLibrary.OldFolderName) }
                .Any(r => full.StartsWith(Path.GetFullPath(r).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }

        public static string Folder(string name) { var p = Path.Combine(Base, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(p); return p; }

        public static string Database(IEnumerable<(string src, string trg, string sl, string tl)> units)
        {
            var path = Path.Combine(Folder("db"), "supervertaler.db");
            using (var c = new SqliteConnection("Data Source=" + path))
            {
                c.Open();
                Exec(c, @"CREATE TABLE translation_units (id INTEGER PRIMARY KEY AUTOINCREMENT, source_text TEXT NOT NULL, target_text TEXT NOT NULL,
                         source_lang TEXT NOT NULL, target_lang TEXT NOT NULL, tm_id TEXT NOT NULL, project_id TEXT, context_before TEXT, context_after TEXT,
                         source_hash TEXT NOT NULL, created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP, modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                         usage_count INTEGER DEFAULT 0, created_by TEXT, notes TEXT, target_hash TEXT, UNIQUE(source_hash, target_text, tm_id))");
                Exec(c, "PRAGMA journal_mode=WAL");
                using (var tx = c.BeginTransaction())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO translation_units (source_text, target_text, source_lang, target_lang, tm_id, source_hash) VALUES ($s, $t, $sl, $tl, 'test', $h)";
                    var ps = cmd.Parameters.Add("$s", SqliteType.Text); var pt = cmd.Parameters.Add("$t", SqliteType.Text);
                    var psl = cmd.Parameters.Add("$sl", SqliteType.Text); var ptl = cmd.Parameters.Add("$tl", SqliteType.Text); var ph = cmd.Parameters.Add("$h", SqliteType.Text);
                    int n = 0;
                    foreach (var u in units) { ps.Value = u.src; pt.Value = u.trg; psl.Value = u.sl; ptl.Value = u.tl; ph.Value = (n++).ToString(); cmd.ExecuteNonQuery(); }
                    tx.Commit();
                }
            }
            SqliteConnection.ClearAllPools();
            return path;
        }

        public static void Exec(SqliteConnection c, string sql) { using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); } }

        public static void CleanUp()
        {
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(Base)) return;
            VaultLibrary.CloseAllForTests();   // open indexes keep their files mapped, and Windows won't delete those
            for (int i = 0; ; i++)
            {
                try { Directory.Delete(Base, true); return; }
                catch when (i < 10) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(300); }
                catch { Console.WriteLine("(scratch left at " + Base + ")"); return; }
            }
        }
    }
}
