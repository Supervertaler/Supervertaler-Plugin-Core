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
    /// Memory and time of SuperVault's big jobs, for finding the safe size in 32-bit Studio 2024. Run on COPIES only
    /// (a folder holding a copy of supervault.db); refuses the real SuperVault folder.
    ///
    ///     SuperVault.Tests.exe limits build  &lt;folder&gt;               full index build
    ///     SuperVault.Tests.exe limits lookup &lt;folder&gt;               200 exact + 200 fuzzy lookups on the built index
    ///     SuperVault.Tests.exe limits sync   &lt;folder&gt; &lt;units&gt;       import that many new units, index updated a piece at a time
    ///     SuperVault.Tests.exe limits export &lt;folder&gt; &lt;file.tmx&gt;    export everything to TMX
    ///
    /// Prints the time and the peaks: private bytes (memory the process really owns), virtual size (address space
    /// in use, what runs out first in a 32-bit process) and the .NET heap.
    /// </summary>
    static class Limits
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: limits build|lookup|sync|export <folder> [arg]"); return 2; }
            var root = Path.GetFullPath(args[1]);
            if (string.Equals(root.TrimEnd('\\'), Path.GetFullPath(VaultLibrary.DefaultRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || root.StartsWith(@"D:\Supervertaler", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("Refusing to use the real SuperVault folder."); return 2; }
            VaultLibrary.Max32BitUnits = long.MaxValue;   // measuring what the guard is based on: never refused here
            var lib = VaultLibrary.For(root);
            Console.WriteLine($"{(Environment.Is64BitProcess ? "64" : "32")}-bit; {args[0]} on {root}; store {VaultStore.Count(lib.StorePath):N0} units");
            using (var probe = Lucene.Net.Store.FSDirectory.Open(Path.Combine(Path.GetTempPath(), "sv-probe-" + Guid.NewGuid().ToString("N"))))
                Console.WriteLine("Lucene opens indexes with " + probe.GetType().Name);
            using (var s = new Sampler())
            {
                var sw = Stopwatch.StartNew();
                string last = null;
                Action<string> progress = p => { if (p != last) { last = p; Console.Write("\r  " + p + "  [" + s.Now() + "]        "); } };
                switch (args[0])
                {
                    case "build":
                        Console.WriteLine($"\nbuilt {lib.Build(progress, CancellationToken.None):N0} units");
                        break;
                    case "lookup":
                        Lookups(lib, s);
                        break;
                    case "sync":
                    {
                        int n = int.Parse(args[2]);
                        var r = lib.Import((store, p, c) => AddPadded(store, n), progress, CancellationToken.None);
                        Console.WriteLine($"\nimported {r}");
                        break;
                    }
                    case "export":
                    {
                        var hsw = Stopwatch.StartNew();
                        var h = VaultExport.Holdings(lib.StorePath, CancellationToken.None);
                        Console.WriteLine($"the choice list (pairs and origins): {h.Count} lines in {hsw.Elapsed.TotalSeconds:N1} s; {s.Now()}");
                        var r = VaultExport.Export(lib.StorePath, args[2], null, null, progress, CancellationToken.None);
                        Console.WriteLine($"\n{r}; {r.Bytes / 1048576.0:N0} MB");
                        break;
                    }
                    default: Console.WriteLine("unknown: " + args[0]); return 2;
                }
                Console.WriteLine($"time {sw.Elapsed.TotalSeconds:N0} s; peaks: {s.Peaks()}");
                var idx = lib.Current;
                if (idx != null) Console.WriteLine($"index {idx.Count:N0} units, {Directory.GetFiles(idx.Folder).Sum(f => new FileInfo(f).Length) / 1048576:N0} MB on disk");
            }
            VaultLibrary.CloseAllForTests();
            SqliteConnection.ClearAllPools();
            return 0;
        }

        /// <summary>Adds n new units (real units with a new marker appended), in one import transaction.</summary>
        static VaultImportResult AddPadded(string store, int n)
        {
            var mark = " {" + Guid.NewGuid().ToString("N").Substring(0, 6) + "}";
            // Streamed: a reader on one connection, the import transaction on another (WAL lets both run).
            using (var w = new VaultStore.Writer(store))
            using (var c = new SqliteConnection("Data Source=" + store + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT lang_a, text_a, lang_b, text_b FROM units ORDER BY id LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", n);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) w.Add(r.GetString(0), r.GetString(1) + mark, r.GetString(2), r.GetString(3) + mark, "tmx:limits.tmx");
                }
                w.Commit();
                return w.Result;
            }
        }

        static void Lookups(VaultLibrary lib, Sampler s)
        {
            var idx = lib.Current ?? throw new InvalidOperationException("no index: run limits build first");
            var sample = new List<string>();
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT text_b FROM units WHERE lang_a = 'en' AND lang_b = 'nl' AND length(text_b) BETWEEN 40 AND 300 ORDER BY random() LIMIT 200";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) sample.Add(VaultMarkup.Plain(r.GetString(0)));
                }
            }
            Console.WriteLine($"after opening the index: {s.Now()}");
            var doc = new Func<string, List<VaultPiece>>(t => new List<VaultPiece> { new VaultPiece { Text = t } });
            var times = new List<double>();
            var rnd = new Random(7);
            int found = 0;
            foreach (var text in sample)
            {
                var sw = Stopwatch.StartNew();
                var m = lib.Lookup(doc(text), "nl", "en", 70, 5);
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (m.Count > 0 && m[0].Score == 100) found++;
                var words = text.Split(' ');
                words[rnd.Next(words.Length)] = "xyzzy";
                sw.Restart();
                lib.Lookup(doc(string.Join(" ", words)), "nl", "en", 70, 5);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"{times.Count} lookups: {found}/{sample.Count} exact found; median {times[times.Count / 2]:F0} ms, p90 {times[(int)(times.Count * 0.9)]:F0} ms, max {times.Last():F0} ms; now {s.Now()}");
        }

        /// <summary>Samples the process's memory every 100 ms and keeps the peaks.</summary>
        sealed class Sampler : IDisposable
        {
            long _private, _virtual, _heap;
            readonly Thread _t;
            volatile bool _stop;
            public Sampler()
            {
                _t = new Thread(() => { while (!_stop) { Take(); Thread.Sleep(100); } }) { IsBackground = true };
                _t.Start();
            }
            void Take()
            {
                using (var p = Process.GetCurrentProcess())
                {
                    _private = Math.Max(_private, p.PrivateMemorySize64);
                    _virtual = Math.Max(_virtual, p.VirtualMemorySize64);
                }
                _heap = Math.Max(_heap, GC.GetTotalMemory(false));
            }
            public string Now()
            {
                using (var p = Process.GetCurrentProcess())
                    return $"private {p.PrivateMemorySize64 / 1048576} MB, virtual {p.VirtualMemorySize64 / 1048576} MB, heap {GC.GetTotalMemory(false) / 1048576} MB";
            }
            public string Peaks()
            {
                Take();
                return $"private {_private / 1048576} MB, virtual {_virtual / 1048576} MB, .NET heap {_heap / 1048576} MB";
            }
            public void Dispose() { _stop = true; _t.Join(); }
        }
    }
}
