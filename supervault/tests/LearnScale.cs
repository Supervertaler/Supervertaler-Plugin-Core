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
    /// Learning at scale: <c>SuperVault.Tests.exe learnscale &lt;scratch root&gt; [store to copy]</c>.
    /// The scratch root must hold (or receive a copy of) a filled supervault.db; the index is built there if missing.
    /// Then: confirmations one at a time as a translator makes them, a batch "Update Main TMs" of 20,000, and a 50,000-unit
    /// import through the piece-at-a-time path, each timed, with lookups timed before and during, and the peak memory.
    /// Never touches the real SuperVault folder: it refuses it, and only ever reads the store it copies.
    /// </summary>
    static class LearnScale
    {
        static readonly string Tag = "r" + (Environment.TickCount & 0xFFFF) + "-";   // each run learns sentences of its own

        public static int Run(string[] args)
        {
            var root = Path.GetFullPath(args[0]);
            if (string.Equals(root.TrimEnd('\\'), Path.GetFullPath(VaultLibrary.DefaultRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("Refusing to use the real SuperVault folder for a test."); return 2; }
            Directory.CreateDirectory(root);
            var store = Path.Combine(root, "supervault.db");
            if (!File.Exists(store))
            {
                if (args.Length < 2) { Console.WriteLine("No store in " + root + " and none given to copy."); return 2; }
                File.Copy(args[1], store);   // reads the original only
            }
            var lib = VaultLibrary.For(root);
            Console.WriteLine($"{(Environment.Is64BitProcess ? "64" : "32")}-bit process; root {root}");
            var sw = Stopwatch.StartNew();
            long target = args.Length > 2 ? long.Parse(args[2]) : 0;
            long have = lib.StoreCount();
            if (have < target)
            {
                // Pad the scratch copy to the target size with variants of its own units (same lengths and languages).
                using (var w = new VaultStore.Writer(store))
                using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "SELECT lang_a, text_a, lang_b, text_b FROM units ORDER BY id LIMIT $n";
                        cmd.Parameters.AddWithValue("$n", target - have);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) w.Add(r.GetString(0), r.GetString(1) + " (variant)", r.GetString(2), r.GetString(3) + " (variant)", "scale");
                    }
                    w.Commit();
                    Console.WriteLine($"padded {have:N0} -> {have + w.Result.Added:N0} units in {sw.Elapsed.TotalSeconds:F0} s");
                }
                sw.Restart();
            }
            if (lib.Current == null)
            {
                lib.Build(p => Console.Write("\r  " + p + "        "), CancellationToken.None);
                Console.WriteLine($"\nbuild: {sw.Elapsed.TotalSeconds:F0} s");
            }
            var idx = lib.Current;
            Console.WriteLine($"store {lib.StoreCount():N0} units, {new FileInfo(store).Length / 1048576} MB; index {idx.Count:N0} units, {Directory.GetFiles(idx.Folder).Sum(f => new FileInfo(f).Length) / 1048576} MB, {Directory.GetFiles(idx.Folder, "*.si").Length} segments");
            Mem("after opening");

            var sample = Sample(store, 3000);
            var doc = new Func<string, List<VaultPiece>>(t => new List<VaultPiece> { new VaultPiece { Text = t } });
            Console.WriteLine($"lookups before learning: {LookupTimes(lib, sample.Take(200), doc)}");

            // 1. A translator confirming segments one at a time, every 200 ms, through the background thread.
            var learnCall = new List<double>(); var visible = new List<double>();
            VaultLibrary.LearnDelayMs = 500;
            for (int i = 0; i < 100; i++)
            {
                var (a, ta, b, tb) = sample[i];
                var u = new VaultLearnUnit { SourceCulture = a, Source = ta + " [" + Tag + "L" + i + "]", TargetCulture = b, Target = tb + " [" + Tag + "L" + i + "]", Origin = "studio" };
                var t = Stopwatch.StartNew();
                lib.Learn(new[] { u });
                learnCall.Add(t.Elapsed.TotalMilliseconds);
                if (i % 10 == 9)
                {
                    // how long until this one is found by a lookup?
                    while (t.Elapsed.TotalSeconds < 60)
                    {
                        var m = VaultLookup.Search(lib.Current, doc(VaultMarkup.Plain(u.Source)), VaultMarkup.BaseLanguage(a), VaultMarkup.BaseLanguage(b), 99, 1);
                        if (m.Count > 0 && m[0].Score == 100) break;
                        Thread.Sleep(50);
                    }
                    visible.Add(t.Elapsed.TotalMilliseconds);
                }
                else Thread.Sleep(200);
            }
            Console.WriteLine($"one at a time: Learn() call median {Median(learnCall):F3} ms, max {learnCall.Max():F2} ms; confirmed -> found by a lookup: median {Median(visible) / 1000:F1} s, max {visible.Max() / 1000:F1} s (includes the 2 s lookup refresh)");
            Mem("after 100 confirmations");

            // 2. A flush on its own, timed: 1 unit, then 200.
            foreach (int n in new[] { 1, 200 })
            {
                lib.Learn(sample.Skip(100).Take(n).Select((x, i) => new VaultLearnUnit { SourceCulture = x.Item1, Source = x.Item2 + " [" + Tag + "F" + n + "/" + i + "]", TargetCulture = x.Item3, Target = x.Item4 + " [F]", Origin = "studio" }));
                var t = Stopwatch.StartNew();
                while (!(lib.LearnOnce() && lib.LearnWaiting() == 0)) Thread.Sleep(10);
                Console.WriteLine($"flush of {n} unit(s) into store and index: {t.ElapsedMilliseconds} ms");
            }

            // 3. Batch "Update Main TMs": 20,000 units handed over in batches of 500.
            {
                var t = Stopwatch.StartNew();
                double maxCall = 0;
                for (int k = 0; k < 40; k++)
                {
                    var c = Stopwatch.StartNew();
                    lib.Learn(Enumerable.Range(0, 500).Select(i => { var x = sample[(k * 500 + i) % sample.Count]; return new VaultLearnUnit { SourceCulture = x.Item1, Source = x.Item2 + " [" + Tag + "B" + k + "/" + i + "]", TargetCulture = x.Item3, Target = x.Item4 + " [B]", Origin = "studio" }; }));
                    maxCall = Math.Max(maxCall, c.Elapsed.TotalMilliseconds);
                }
                var lookupsDuring = LookupTimes(lib, sample.Skip(200).Take(100), doc);
                while (!(lib.LearnOnce() && lib.LearnWaiting() == 0)) Thread.Sleep(50);
                Console.WriteLine($"batch of 20,000: handed over with the slowest call {maxCall:F1} ms; stored and indexed in {t.Elapsed.TotalSeconds:F1} s; lookups meanwhile: {lookupsDuring}");
            }
            Mem("after the batch");

            // 4. A 50,000-unit import (more than 'small' only when the store is under 200,000 units).
            {
                var t = Stopwatch.StartNew();
                var gen = lib.Current.Folder;
                var r = lib.Import((s, p, c) =>
                {
                    using (var w = new VaultStore.Writer(s))
                    {
                        for (int i = 0; i < 50000; i++) { var x = sample[i % sample.Count]; w.Add(x.Item1, x.Item2 + " [" + Tag + "I" + i + "]", x.Item3, x.Item4 + " [I]", "scale"); }
                        w.Commit();
                        return w.Result;
                    }
                }, null, CancellationToken.None);
                lib.Refresh();
                Console.WriteLine($"import of 50,000: {t.Elapsed.TotalSeconds:F1} s ({(lib.Current.Folder == gen ? "piece at a time" : "rebuilt")}), {r.Added:N0} added");
            }
            Mem("after the import");
            lib.Refresh();
            idx = lib.Current;
            Console.WriteLine($"index now {idx.Count:N0} units, {Directory.GetFiles(idx.Folder).Sum(f => new FileInfo(f).Length) / 1048576} MB, {Directory.GetFiles(idx.Folder, "*.si").Length} segments; out of date: {lib.IndexOutOfDate()}");
            Console.WriteLine($"lookups after learning: {LookupTimes(lib, sample.Take(200), doc)}");
            return 0;
        }

        static void Mem(string when)
        {
            var p = Process.GetCurrentProcess();
            Console.WriteLine($"  memory {when}: working set {p.WorkingSet64 / 1048576} MB, peak {p.PeakWorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB");
        }

        static string LookupTimes(VaultLibrary lib, IEnumerable<(string, string, string, string)> units, Func<string, List<VaultPiece>> doc)
        {
            var times = new List<double>();
            foreach (var (a, ta, b, _) in units)
            {
                var sw = Stopwatch.StartNew();
                VaultLookup.Search(lib.Current, doc(VaultMarkup.Plain(ta)), VaultMarkup.BaseLanguage(a), VaultMarkup.BaseLanguage(b), 70, 5);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            var s = times.OrderBy(v => v).ToList();
            return $"median {s[s.Count / 2]:F1} ms, p90 {s[(int)(s.Count * 0.9)]:F1} ms";
        }

        static double Median(List<double> x) { var s = x.OrderBy(v => v).ToList(); return s[s.Count / 2]; }

        /// <summary>Units of 40-300 characters from the store, oriented lang_a > lang_b.</summary>
        static List<(string, string, string, string)> Sample(string store, int count)
        {
            var list = new List<(string, string, string, string)>();
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT lang_b, text_b, lang_a, text_a FROM units WHERE lang_a = 'en' AND lang_b = 'nl' AND length(text_b) BETWEEN 40 AND 300 AND id % 97 = 5 LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", count);
                    using (var r = cmd.ExecuteReader()) while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                }
            }
            return list;
        }
    }
}
