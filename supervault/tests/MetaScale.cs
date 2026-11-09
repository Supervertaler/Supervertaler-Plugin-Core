using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>
    /// Metadata at real scale: <c>SuperVault.Tests.exe metascale &lt;folder holding a COPY of evertm.db&gt; [workbench db]</c>.
    /// Upgrades the copy to schema 3, rebuilds its index in the new format, times lookups, then re-imports Workbench
    /// (opened read-only) into the copy so every unit gains its TM name, and times that and the lookups again.
    /// Prints counts and times only: TM names can be client names. Never touches the real EverTM folder.
    /// </summary>
    static class MetaScale
    {
        public static int Run(string[] args)
        {
            var root = args[0];
            var db = args.Length > 1 ? args[1] : @"D:\Supervertaler\resources\supervertaler.db";
            if (Scratch.IsReal(root)) { Console.WriteLine("Refusing to use the real EverTM folder."); return 2; }
            VaultLibrary.NoAutoUpgradeForTests = true;
            var lib = VaultLibrary.For(root);
            Console.WriteLine($"{(Environment.Is64BitProcess ? "64" : "32")}-bit; store copy {new FileInfo(lib.StorePath).Length / 1048576} MB, {lib.StoreCount():N0} units");

            var sw = Stopwatch.StartNew();
            VaultStore.Create(lib.StorePath);
            Console.WriteLine($"schema upgrade: {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            int n = lib.Build(null, CancellationToken.None);
            Console.WriteLine($"full rebuild, format {VaultIndex.Format}: {n:N0} units in {sw.Elapsed.TotalSeconds:F0} s; index {Size(lib)} MB; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
            Lookups(lib, "before the Workbench re-import");

            sw.Restart();
            double indexFrom = 0;
            var r = lib.Import((store, p, c) => VaultStore.ImportWorkbench(store, db, p, c),
                               p => { if (indexFrom == 0 && (p.StartsWith("indexing") || p.StartsWith("updating"))) indexFrom = sw.Elapsed.TotalSeconds; }, CancellationToken.None);
            Console.WriteLine($"index rebuilt: {(VaultLibrary.ReadInfo(lib.Current.Folder).TryGetValue("built", out var built) ? built : "no")}");
            Console.WriteLine($"Workbench re-import: {sw.Elapsed.TotalSeconds:F0} s in all, of which the store {indexFrom:F0} s and the index {sw.Elapsed.TotalSeconds - indexFrom:F0} s: {r.Added:N0} added, {r.Duplicates:N0} already there, {r.Enriched:N0} gained metadata, {r.NamesSplit} TM names split");
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                long Count(string sql) { using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); } }
                Console.WriteLine($"store now {new FileInfo(lib.StorePath).Length / 1048576} MB; {Count("SELECT count(*) FROM vals"):N0} distinct values, {Count("SELECT count(*) FROM unit_vals"):N0} unit-value links; " +
                                  $"units with a Client: {Count("SELECT count(DISTINCT uv.unit_id) FROM unit_vals uv JOIN vals v ON v.id = uv.val_id WHERE v.field = 'Client'"):N0}");
            }
            Console.WriteLine($"index {Size(lib)} MB");
            Lookups(lib, "after");
            SqliteConnection.ClearAllPools();
            return 0;
        }

        static long Size(VaultLibrary lib) { lib.Refresh(); var idx = lib.Current; return Directory.GetFiles(idx.Folder).Sum(f => new FileInfo(f).Length) / 1048576; }

        static void Lookups(VaultLibrary lib, string when)
        {
            lib.Refresh();
            var idx = lib.Current;
            var sample = new List<string>();
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT text_b FROM units WHERE lang_a = 'en' AND lang_b = 'nl' AND length(text_b) BETWEEN 30 AND 300 AND abs(random()) % 2000 = 0 LIMIT 300";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) sample.Add(VaultMarkup.Plain(r.GetString(0)));
                }
            }
            var doc = new Func<string, List<VaultPiece>>(t => new List<VaultPiece> { new VaultPiece { Text = t } });
            VaultLookup.Search(idx, doc("warm-up zin"), "nl", "en", 70, 5);
            var exact = new List<double>(); var fuzzy = new List<double>();
            int found = 0, withOlder = 0, withFields = 0;
            var rnd = new Random(7);
            foreach (var s in sample)
            {
                var sw = Stopwatch.StartNew();
                var m = VaultLookup.Search(idx, doc(s), "nl", "en", 70, 5, null, "en-GB");
                exact.Add(sw.Elapsed.TotalMilliseconds);
                if (m.Count > 0 && m[0].Score == 100) { found++; if (m[0].Candidate.Older > 0) withOlder++; if (m[0].Candidate.Fields.Count > 0) withFields++; }
                var words = s.Split(' ');
                words[rnd.Next(words.Length)] = "xyzzy";
                sw.Restart();
                VaultLookup.Search(idx, doc(string.Join(" ", words)), "nl", "en", 70, 5, null, "en-GB");
                fuzzy.Add(sw.Elapsed.TotalMilliseconds);
            }
            double P(List<double> l, double q) { var o = l.OrderBy(x => x).ToList(); return o.Count == 0 ? 0 : o[Math.Min(o.Count - 1, (int)(q * o.Count))]; }
            Console.WriteLine($"lookups {when}: {sample.Count} nl>en sentences, {found} found at 100% ({withOlder} with older translations hidden, {withFields} with fields); " +
                              $"exact median {P(exact, 0.5):F1} ms, p95 {P(exact, 0.95):F1} ms; fuzzy median {P(fuzzy, 0.5):F1} ms, p95 {P(fuzzy, 0.95):F1} ms");
        }
    }
}
