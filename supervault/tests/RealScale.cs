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
    /// Scale test on the real Workbench TM: <c>SuperVault.Tests.exe real [db] [index folder]</c>.
    /// Builds an index from the database (opened read-only) into a scratch folder, unless one is given that already
    /// holds a build, then times lookups of 200 sampled units against it (exact, and fuzzy with one word changed).
    /// Never writes to the database or to the real SuperVault folder.
    /// </summary>
    static class RealScale
    {
        public static int Run(string[] args)
        {
            var db = args.Length > 0 ? args[0] : @"D:\Supervertaler\resources\supervertaler.db";
            var root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "sv-supervault-scale");
            if (Scratch.IsReal(root))
            { Console.WriteLine("Refusing to use the real EverTM folder for a test."); return 2; }
            var lib = VaultLibrary.For(root);
            Console.WriteLine($"database {db}\nindex    {root}");
            if (lib.Current == null)
            {
                // The optional Workbench import into SuperVault's own store, then the index is built from the store.
                var sw = Stopwatch.StartNew();
                var r = lib.Import((store, p, c) => VaultStore.ImportWorkbench(store, db, p, c), p => Console.Write("\r  " + p + "        "), CancellationToken.None);
                Console.WriteLine($"\nimport + index: {sw.Elapsed.TotalSeconds:F0} s ({r}); peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
            }
            var idx = lib.Current;
            var info = VaultLibrary.ReadInfo(idx.Folder);
            Console.WriteLine($"index: {idx.Count:N0} units, index build {info.GetValueOrDefault("seconds")} s, index {Directory.GetFiles(idx.Folder).Sum(f => new FileInfo(f).Length) / 1048576} MB, " +
                              $"store {new FileInfo(lib.StorePath).Length / 1048576} MB");

            var sample = Sample(db, 200);
            Console.WriteLine($"sample: {sample.Count} nl>en units of 40-300 characters");
            var doc = new Func<string, List<VaultPiece>>(t => new List<VaultPiece> { new VaultPiece { Text = t } });
            VaultLookup.Search(idx, doc("warm-up zin"), "nl", "en", 70, 5);

            var exactTimes = new List<double>(); int exactFound = 0;
            var fuzzyTimes = new List<double>(); int fuzzyFound = 0;
            var rnd = new Random(7);
            foreach (var (src, trg) in sample)
            {
                var sw = Stopwatch.StartNew();
                var m = VaultLookup.Search(idx, doc(VaultMarkup.Plain(src)), "nl", "en", 70, 5);
                exactTimes.Add(sw.Elapsed.TotalMilliseconds);
                if (m.Count > 0 && m[0].Score == 100) exactFound++;

                var words = VaultMarkup.Plain(src).Split(' ');
                words[rnd.Next(words.Length)] = "xyzzy";
                sw.Restart();
                m = VaultLookup.Search(idx, doc(string.Join(" ", words)), "nl", "en", 70, 5);
                fuzzyTimes.Add(sw.Elapsed.TotalMilliseconds);
                if (m.Count > 0 && m[0].Score >= 70 && VaultMarkup.Plain(m[0].Candidate.SourceRaw) == VaultMarkup.Plain(src)) fuzzyFound++;
            }
            Console.WriteLine($"exact: found {exactFound}/{sample.Count}, median {Median(exactTimes):F1} ms, p90 {P90(exactTimes):F1} ms, max {exactTimes.Max():F0} ms");
            Console.WriteLine($"fuzzy (one word changed): original found first {fuzzyFound}/{sample.Count}, median {Median(fuzzyTimes):F1} ms, p90 {P90(fuzzyTimes):F1} ms, max {fuzzyTimes.Max():F0} ms");
            // Where the time goes: candidate fetch alone, and markup parsing alone.
            var swc = Stopwatch.StartNew(); int cands = 0;
            foreach (var (src, _) in sample) cands += idx.Candidates(VaultMarkup.Plain(src), "nl", "en", 10, 70).Count;
            Console.WriteLine($"breakdown: candidates only {swc.Elapsed.TotalMilliseconds / sample.Count:F1} ms/lookup ({cands / sample.Count} per lookup)");
            var allC = sample.SelectMany(x => idx.Candidates(VaultMarkup.Plain(x.Item1), "nl", "en", 10, 70)).ToList();
            swc.Restart();
            foreach (var c in allC) { VaultDiff.Tokenize(VaultMarkup.Parse(c.SourceRaw)); VaultMarkup.Parse(c.TargetRaw); }
            Console.WriteLine($"breakdown: parse+tokenize {swc.Elapsed.TotalMilliseconds / sample.Count:F1} ms/lookup");
            Console.WriteLine($"process: {(Environment.Is64BitProcess ? "64" : "32")}-bit, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");
            return 0;
        }

        static string GetValueOrDefault(this Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "?";
        static double Median(List<double> x) { var s = x.OrderBy(v => v).ToList(); return s[s.Count / 2]; }
        static double P90(List<double> x) { var s = x.OrderBy(v => v).ToList(); return s[(int)(s.Count * 0.9)]; }

        static List<(string, string)> Sample(string db, int count)
        {
            var list = new List<(string, string)>();
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT source_text, target_text FROM translation_units WHERE source_lang LIKE 'nl%' AND target_lang LIKE 'en%' " +
                                      "AND length(source_text) BETWEEN 40 AND 300 AND id % 7919 = 3 LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", count);
                    using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
                }
            }
            return list;
        }
    }
}
