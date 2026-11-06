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
            var filter = args.Length > 0 ? args[0] : null;
            if (filter == "real") return RealScale.Run(args.Skip(1).ToArray());
            VaultLibrary.CleanupDelayMs = 200;
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
            try { Directory.Delete(Base, true); } catch { Console.WriteLine("(scratch left at " + Base + ")"); }
        }
    }
}
