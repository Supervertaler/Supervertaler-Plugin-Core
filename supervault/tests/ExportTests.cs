using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Core.SuperVault.Tests
{
    /// <summary>TMX export (VaultExport), and the size guard for 32-bit Studio (VaultLibrary.TooLargeFor32Bit).</summary>
    static class ExportTests
    {
        // Every kind of stored text SuperVault meets: its own neutral codes (TMX and Studio TM imports), Workbench's raw
        // HTML-ish and memoQ codes, a tag closed in another segment, text that only looks like a tag, XML specials,
        // a line break, and both directions and several variants.
        static readonly (string sl, string src, string tl, string trg, string origin)[] Mixed =
        {
            ("nl-BE", "De pomp wordt gestart.", "en-US", "The pump is started.", "workbench"),
            ("en-GB", "The valve is closed automatically.", "nl-NL", "De klep wordt automatisch gesloten.", "workbench"),
            ("nl", "Klik op <b>Opslaan</b> om de wijzigingen te bewaren.", "en", "Click <b>Save</b> to keep the changes.", "workbench"),
            ("nl-NL", "Druk op <g id=\"5\">Start</g id=\"5\"> en <x id=\"6\"/> wacht.", "en-GB", "Press <g id=\"5\">Start</g id=\"5\"> and <x id=\"6\"/> wait.", "tmx:Acme PROJ-001.tmx"),
            ("en-US", "Use [1}bold{1] and {2} here.", "nl-BE", "Gebruik hier [1}vet{1] en {2}.", "studio"),
            ("nl", "Open <g id=\"1\">de klep", "en", "Open <g id=\"1\">the valve", "sdltm:Acme.sdltm"),        // closed in the next segment
            ("nl", "dicht</g id=\"1\"> en klaar.", "en", "closed</g id=\"1\"> and done.", "sdltm:Acme.sdltm"),  // opened in the previous one
            ("en", "Press <Enter> if a < b & c > d.", "nl", "Druk op <Enter> als a < b & c > d.", "studio"),
            ("en", "Line one\nline two \"quoted\" 'single'.", "nl", "Regel een\nregel twee \"aangehaald\" 'enkel'.", "studio"),
            ("nl", "<g id=\"1\"><g id=\"2\">Genest</g id=\"2\"> en <x id=\"3\"/></g id=\"1\"> klaar.", "en", "<g id=\"1\"><x id=\"3\"/> and <g id=\"2\">nested</g id=\"2\"></g id=\"1\"> done.", "workbench"),
            ("nl", "<pt x=\"1\">Twee<pt x=\"2\"> keer</pt x=\"2\"></pt x=\"1\"> dezelfde.", "en", "<pt x=\"1\">Twice<pt x=\"2\"> the</pt x=\"2\"></pt x=\"1\"> same.", "workbench"),
            ("de-DE", "Die Pumpe läuft. Ünïcödé 𝔘.", "nl-NL", "De pomp draait. Ünïcödé 𝔘.", null),
        };

        static VaultLibrary Filled(string name = "export", IEnumerable<(string, string, string, string, string)> units = null)
        {
            var lib = VaultLibrary.For(Scratch.Folder(name));
            lib.Import((store, p, c) =>
            {
                using (var w = new VaultStore.Writer(store))
                {
                    foreach (var (sl, src, tl, trg, origin) in units ?? Mixed) w.Add(sl, src, tl, trg, origin);
                    w.Commit();
                    return w.Result;
                }
            }, null, CancellationToken.None);
            return lib;
        }

        sealed class Row
        {
            public string LangA, TextA, LangB, TextB, Key, Variant, Origin, Added;
            public override string ToString() => string.Join(" | ", LangA, TextA, LangB, TextB, Variant, Origin, Added);
        }

        static List<Row> Rows(string store)
        {
            var rows = new List<Row>();
            using (var c = new SqliteConnection("Data Source=" + store + ";Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT lang_a, text_a, lang_b, text_b, key, variant, origin, added FROM units ORDER BY id";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            rows.Add(new Row
                            {
                                LangA = r.GetString(0), TextA = r.GetString(1), LangB = r.GetString(2), TextB = r.GetString(3), Key = r.GetString(4),
                                Variant = r.IsDBNull(5) ? null : r.GetString(5), Origin = r.IsDBNull(6) ? null : r.GetString(6),
                                Added = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                            });
                }
            }
            return rows;
        }

        static string TmxPath(string name = "out.tmx") => Path.Combine(Scratch.Folder("tmx-out"), name);

        public static void RoundTripGivesTheSameUnits()
        {
            var lib = Filled();
            var before = Rows(lib.StorePath);
            A.Eq(Mixed.Length, before.Count, "units stored");
            var tmx = TmxPath();
            var r = VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None);
            A.Eq((long)Mixed.Length, r.Units, "units exported");
            A.Eq(new FileInfo(tmx).Length, r.Bytes, "size reported");

            var again = VaultLibrary.For(Scratch.Folder("reimport"));
            var imp = again.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);
            A.Eq((long)Mixed.Length, imp.Added, "units imported again: " + imp);
            var after = Rows(again.StorePath);
            for (int i = 0; i < before.Count; i++)
                A.Eq(before[i].ToString() + " | " + before[i].Key, after[i].ToString() + " | " + after[i].Key, "unit " + i + " identical after export and import");

            // And into a SuperVault that already holds them: all duplicates.
            var dup = lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);
            A.Eq(0L, dup.Added, "nothing new when imported into the EverTM it came from");
            A.Eq((long)Mixed.Length, dup.Duplicates, "every unit recognised as already there");
        }

        public static void TmxIsValidAndKeepsDirectionVariantsDatesAndOrigin()
        {
            var lib = Filled();
            var tmx = TmxPath();
            VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None);
            var doc = new XmlDocument();
            doc.Load(tmx);   // well-formed
            var root = doc.DocumentElement;
            A.Eq("tmx", root.Name, "root");
            A.Eq("1.4", root.GetAttribute("version"), "TMX version");
            var header = (XmlElement)root.SelectSingleNode("header");
            foreach (var attr in new[] { "creationtool", "creationtoolversion", "segtype", "o-tmf", "adminlang", "srclang", "datatype" })
                A.True(header.GetAttribute(attr).Length > 0, "header has the required " + attr);
            A.Eq("*all*", header.GetAttribute("srclang"), "header srclang");
            var tus = root.SelectNodes("body/tu").Cast<XmlElement>().ToList();
            A.Eq(Mixed.Length, tus.Count, "one tu per unit");
            for (int i = 0; i < tus.Count; i++)
            {
                var tu = tus[i];
                var tuvs = tu.SelectNodes("tuv").Cast<XmlElement>().ToList();
                A.Eq(2, tuvs.Count, "two tuvs");
                A.Eq(Mixed[i].sl, tu.GetAttribute("srclang"), "tu " + i + " srclang is the variant it came with");
                A.Eq(Mixed[i].sl, tuvs[0].GetAttribute("xml:lang"), "tu " + i + " source tuv first, in its variant");
                A.Eq(Mixed[i].tl, tuvs[1].GetAttribute("xml:lang"), "tu " + i + " target tuv in its variant");
                A.True(System.Text.RegularExpressions.Regex.IsMatch(tu.GetAttribute("creationdate"), @"^\d{8}T\d{6}Z$"), "creationdate in TMX form: " + tu.GetAttribute("creationdate"));
                var prop = (XmlElement)tu.SelectSingleNode("prop[@type='x-EverTM-Origin']");
                A.Eq(Mixed[i].origin, prop?.InnerText, "origin prop");
                foreach (var seg in tu.SelectNodes("tuv/seg").Cast<XmlElement>())
                {
                    // Inline elements: bpt and ept pair by i within the segment; x is a whole number; it has pos.
                    var bpt = seg.SelectNodes("bpt").Cast<XmlElement>().Select(e => e.GetAttribute("i")).ToList();
                    var ept = seg.SelectNodes("ept").Cast<XmlElement>().Select(e => e.GetAttribute("i")).ToList();
                    A.Eq(string.Join(",", bpt.OrderBy(x => x)), string.Join(",", ept.OrderBy(x => x)), "tu " + i + ": every bpt has its ept");
                    foreach (var e in seg.ChildNodes.OfType<XmlElement>())
                    {
                        A.True(new[] { "bpt", "ept", "ph", "it" }.Contains(e.Name), "only TMX inline elements: " + e.Name);
                        if (e.HasAttribute("x")) A.True(int.TryParse(e.GetAttribute("x"), out _), "x is a number");
                        if (e.Name == "it") A.True(e.GetAttribute("pos") == "begin" || e.GetAttribute("pos") == "end", "it has pos");
                    }
                }
            }
            // A tag pair and the same tag in the target share x.
            var t3 = tus[3];
            var srcX = t3.SelectNodes("tuv[1]/seg/*[@x]").Cast<XmlElement>().Select(e => e.Name + e.GetAttribute("x")).ToList();
            var trgX = t3.SelectNodes("tuv[2]/seg/*[@x]").Cast<XmlElement>().Select(e => e.Name + e.GetAttribute("x")).ToList();
            A.Eq(string.Join(",", srcX), string.Join(",", trgX), "same x for the same tag in source and target");
            // Workbench's <b> is a pair; its native code is the stored code.
            var b = (XmlElement)tus[2].SelectSingleNode("tuv[1]/seg/bpt");
            A.Eq("<b>", b?.InnerText, "native code is the code as stored");
            A.Eq(1, tus[5].SelectNodes("tuv[1]/seg/it[@pos='begin']").Count, "a tag closed in another segment is an <it pos=begin>");
            A.Eq(1, tus[6].SelectNodes("tuv[1]/seg/it[@pos='end']").Count, "a tag opened in another segment is an <it pos=end>");
            A.Eq(0, tus[7].SelectNodes("tuv[1]/seg/*").Count, "\"<Enter>\" and \"a < b\" stay text");
            A.Eq(Mixed[7].src, ((XmlElement)tus[7].SelectSingleNode("tuv[1]/seg")).InnerText, "text with XML specials unchanged");
        }

        public static void AnUndatedUnitIsNeverGivenTheExportDate()
        {
            var lib = Filled("undated", new[] { ("nl", "Zonder datum.", "en", "Without a date.", "workbench") });
            using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Pooling=False"))
            {
                c.Open();
                using (var cmd = c.CreateCommand()) { cmd.CommandText = "UPDATE units SET added = NULL"; cmd.ExecuteNonQuery(); }
            }
            var tmx = TmxPath();
            VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None);
            var doc = new XmlDocument(); doc.Load(tmx);
            var tu = (XmlElement)doc.SelectSingleNode("/tmx/body/tu");
            A.True(!tu.HasAttribute("creationdate") && !tu.HasAttribute("changedate"), "no date written for a unit without one");
        }

        public static void ATmxFromAnotherToolStillGetsNeutralCodes()
        {
            // The native code is trusted only in SuperVault's own files: another tool's RTF stays out of the store.
            var tmx = LibraryTests.WriteTmx(LibraryTests.Tu(("en-US", "Click <bpt i=\"1\">{\\b </bpt>Save<ept i=\"1\">}</ept>."), ("nl-NL", "Klik op <bpt i=\"1\">{\\b </bpt>Opslaan<ept i=\"1\">}</ept>.")));
            var lib = VaultLibrary.For(Scratch.Folder("other-tool"));
            lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);
            var row = Rows(lib.StorePath).Single();
            A.Eq("Click <g id=\"1\">Save</g id=\"1\">.", row.TextA, "neutral codes");
            A.Eq("tmx:test.tmx", row.Origin, "origin is the file");
        }

        public static void AHandEditedTagInOurOwnFileFallsBackToNeutralCodes()
        {
            var lib = Filled("hand", new[] { ("nl", "Klik op <b>Opslaan</b>.", "en", "Click <b>Save</b>.", "workbench") });
            var tmx = TmxPath();
            VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None);
            File.WriteAllText(tmx, File.ReadAllText(tmx).Replace("&lt;/b&gt;</ept>", "nonsense</ept>"));
            var again = VaultLibrary.For(Scratch.Folder("hand-again"));
            again.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);
            var row = Rows(again.StorePath).Single();
            A.True(row.TextA.Contains("<g id=") || row.TextB.Contains("<g id="), "neutral codes, not the broken native code: " + row);
            A.True(!row.TextA.Contains("nonsense") && !row.TextB.Contains("nonsense"), "no stray native code in the text");
        }

        public static void ChooseALanguagePairAndOrigins()
        {
            var lib = Filled();
            var holdings = VaultExport.Holdings(lib.StorePath, CancellationToken.None);
            A.Eq((long)Mixed.Length, holdings.Sum(h => h.Units), "holdings cover every unit");
            A.Eq(2, holdings.Select(h => h.Pair).Distinct().Count(), "two language pairs");
            A.True(holdings.Any(h => h.Origin == ""), "a unit without an origin is listed with \"\"");

            long Count(string pair, string[] origins)
            {
                var path = TmxPath();
                var r = VaultExport.Export(lib.StorePath, path, pair, origins, null, CancellationToken.None);
                var tus = new XmlDocument(); tus.Load(path);
                A.Eq(r.Units, (long)tus.SelectNodes("/tmx/body/tu").Count, "tu count matches the result");
                return r.Units;
            }
            A.Eq(11L, Count("en|nl", null), "one language pair");
            A.Eq(1L, Count("de|nl", null), "the other pair");
            A.Eq(5L, Count(null, new[] { "workbench" }), "one origin");
            A.Eq(6L, Count(null, new[] { "studio", "sdltm:Acme.sdltm", "tmx:Acme PROJ-001.tmx" }), "several origins");
            A.Eq(3L, Count("en|nl", new[] { "studio" }), "pair and origin together");
            A.Eq(1L, Count(null, new[] { "" }), "units without an origin");
            A.Eq(0L, Count("fr|nl", null), "a pair EverTM doesn't hold: an empty, valid TMX");
            A.Eq(0L, Count(null, new string[0]), "no origins chosen: nothing");
        }

        public static void StoppedExportLeavesNoFile()
        {
            var lib = Filled("big", Enumerable.Range(0, 5000).Select(i => ("nl", "Zin nummer " + i + " met <b>tekst</b>.", "en", "Sentence number " + i + " with <b>text</b>.", "workbench")));
            var folder = Scratch.Folder("stopped");
            var tmx = Path.Combine(folder, "out.tmx");
            var cts = new CancellationTokenSource();
            A.Throws<OperationCanceledException>(() => VaultExport.Export(lib.StorePath, tmx, null, null, p => cts.Cancel(), cts.Token), "stopping throws");
            A.Eq(0, Directory.GetFiles(folder).Length, "no file and no temporary file left");

            // Stopping an export over an existing file leaves that file as it was.
            File.WriteAllText(tmx, "the previous export");
            var cts2 = new CancellationTokenSource();
            A.Throws<OperationCanceledException>(() => VaultExport.Export(lib.StorePath, tmx, null, null, p => cts2.Cancel(), cts2.Token), "stopping throws");
            A.Eq("the previous export", File.ReadAllText(tmx), "existing file untouched");
            A.Eq(1, Directory.GetFiles(folder).Length, "no temporary file left");

            // A complete export replaces it.
            var r = VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None);
            A.Eq(5000L, r.Units, "complete export");
            A.True(File.ReadAllText(tmx).StartsWith("<?xml"), "replaced by the new file");
            A.Eq(1, Directory.GetFiles(folder).Length, "just the file");
        }

        public static void FailedExportLeavesNoFile()
        {
            var lib = Filled();
            var folder = Scratch.Folder("failed");
            var tmx = Path.Combine(folder, "out.tmx");
            File.WriteAllText(tmx, "the previous export");
            VaultExport.FailPoint = p => { if (p == "rename") throw new IOException("simulated: disk full"); };
            try { A.Throws<IOException>(() => VaultExport.Export(lib.StorePath, tmx, null, null, null, CancellationToken.None), "failure reported"); }
            finally { VaultExport.FailPoint = null; }
            A.Eq("the previous export", File.ReadAllText(tmx), "existing file untouched");
            A.Eq(1, Directory.GetFiles(folder).Length, "no temporary file left");

            A.Throws<DirectoryNotFoundException>(() => VaultExport.Export(lib.StorePath, Path.Combine(folder, "missing", "out.tmx"), null, null, null, CancellationToken.None), "missing folder");
            A.Throws<FileNotFoundException>(() => VaultExport.Export(Path.Combine(folder, "none.db"), Path.Combine(folder, "x.tmx"), null, null, null, CancellationToken.None), "no store");
            A.Eq(1, Directory.GetFiles(folder).Length, "nothing created");
        }

        public static void ExportReadsASnapshotAndBlocksNoWriter()
        {
            var lib = Filled("snap", Enumerable.Range(0, 3000).Select(i => ("nl", "Zin " + i + ".", "en", "Sentence " + i + ".", "workbench")));
            var tmx = TmxPath();
            int calls = 0;
            var r = VaultExport.Export(lib.StorePath, tmx, null, null, p =>
            {
                if (calls++ > 0) return;
                // Half-way into the export: an import in "another Studio" adds 100 units, and a confirmed segment is learned.
                var other = VaultLibrary.ForTests(lib.Root);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                other.Import((store, pp, c) =>
                {
                    using (var w = new VaultStore.Writer(store, busyTimeoutSeconds: 2))
                    {
                        for (int i = 0; i < 100; i++) w.Add("nl", "Nieuw " + i + ".", "en", "New " + i + ".", "tmx:new.tmx");
                        w.Commit();
                        return w.Result;
                    }
                }, null, CancellationToken.None);
                other.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-BE", Source = "Bevestigd.", TargetCulture = "en-US", Target = "Confirmed.", Origin = "studio" } });
                A.True(other.LearnOnce(), "the confirmed segment is stored during the export");
                A.True(sw.ElapsedMilliseconds < 5000, "the writers didn't wait for the export (a wait would have hit the 2 s busy timeout): " + sw.ElapsedMilliseconds + " ms");
            }, CancellationToken.None);
            A.Eq(3000L, r.Units, "the export has the store as it was when it began");
            A.Eq(3101L, lib.StoreCount(), "and the store has the new units");
            var doc = new XmlDocument(); doc.Load(tmx);
            A.Eq(3000, doc.SelectNodes("/tmx/body/tu").Count, "file complete");
        }

        public static void ExportRunsAsABackgroundJob()
        {
            var lib = Filled();
            var tmx = TmxPath();
            var done = new ManualResetEventSlim();
            string error = "not called";
            A.True(lib.StartExport(tmx, null, null, e => { error = e; done.Set(); }), "started");
            A.True(done.Wait(20000), "finished");
            A.Eq(null, error, "no error");
            A.True(lib.LastJobResult.Contains(Mixed.Length + " units exported"), "result: " + lib.LastJobResult);
            // Lookups never touched the store; they carried on.
            A.True(lib.Lookup(LibraryTests.Doc("De pomp wordt gestart."), "nl", "en", 70, 5).Any(m => m.Score == 100), "lookups unaffected");
        }

        // ---- the size guard for 32-bit Studio ----

        static void As32Bit(long limit, Action a)
        {
            VaultLibrary.Is32BitForTests = true;
            var old = VaultLibrary.Max32BitUnits;
            VaultLibrary.Max32BitUnits = limit;
            try { a(); }
            finally { VaultLibrary.Is32BitForTests = null; VaultLibrary.Max32BitUnits = old; }
        }

        static Func<string, Action<string>, CancellationToken, VaultImportResult> Adding(int n, string tag) => (store, p, c) =>
        {
            using (var w = new VaultStore.Writer(store))
            {
                for (int i = 0; i < n; i++) w.Add("nl", tag + " zin " + i + ".", "en", tag + " sentence " + i + ".", "workbench");
                w.Commit();
                return w.Result;
            }
        };

        public static void GuardDecidesBy32BitAndSize()
        {
            As32Bit(10, () =>
            {
                A.Eq(null, VaultLibrary.TooLargeFor32Bit(10), "at the limit: fine");
                var m = VaultLibrary.TooLargeFor32Bit(11);
                A.True(m != null && m.Contains("Studio 2026") && m.Contains("Nothing was changed"), "past it: refused, naming the way out: " + m);
                A.True(VaultLibrary.TooLargeFor32Bit(11, importing: true).Contains("This import would take EverTM past 10 units"), "import wording");
            });
            VaultLibrary.Is32BitForTests = false;
            try { A.Eq(null, VaultLibrary.TooLargeFor32Bit(long.MaxValue), "64-bit: never refused"); }
            finally { VaultLibrary.Is32BitForTests = null; }
        }

        public static void ImportPastTheLimitIn32BitAddsNothing()
        {
            var lib = VaultLibrary.For(Scratch.Folder("guard-import"));
            As32Bit(10, () =>
            {
                var r = lib.Import(Adding(8, "a"), null, CancellationToken.None);
                A.Eq(8L, r.Added, "under the limit: imported");
                var ex = Assert<VaultTooLargeException>(() => lib.Import(Adding(5, "b"), null, CancellationToken.None));
                A.True(ex.Message.Contains("Studio 2026"), "message names the way out: " + ex.Message);
                A.Eq(8L, lib.StoreCount(), "the refused import added nothing (rolled back)");
                A.Eq(8, lib.Status.Units, "index unchanged");
                r = lib.Import(Adding(2, "c"), null, CancellationToken.None);
                A.Eq(2L, r.Added, "up to the limit exactly: fine");
                // Confirmed segments are always stored, even past the limit: the index is updated a piece at a time,
                // which needs little memory, and nothing you confirmed is ever thrown away.
                lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-BE", Source = "Bevestigd.", TargetCulture = "en-US", Target = "Confirmed.", Origin = "studio" } });
                A.True(lib.LearnOnce(), "learned");
                A.Eq(11L, lib.StoreCount(), "past the limit by learning");
                A.Eq(11, lib.Status.Units, "and indexed");
                // ...but a rebuild of that is refused, leaving the index in use as it was.
                var name = lib.Current.Folder;
                var bex = Assert<VaultTooLargeException>(() => lib.Build(null, CancellationToken.None));
                A.True(bex.Message.StartsWith("EverTM holds 11 units, more than it has been tested with in Studio 2024 (10 units)") && bex.Message.Contains("rebuild in Studio 2026"), "rebuild message: " + bex.Message);
                lib.Refresh();
                A.Eq(name, lib.Current.Folder, "the index in use is untouched");
                A.Eq(1, Directory.GetDirectories(lib.Root, "gen-*").Length, "no half-built generation left");
            });
            // The same in a 64-bit process: no limit.
            VaultLibrary.Is32BitForTests = false;
            try
            {
                var r = lib.Import(Adding(50, "d"), null, CancellationToken.None);
                A.Eq(50L, r.Added, "64-bit: imported");
                A.Eq(61, lib.Build(null, CancellationToken.None), "64-bit: rebuilt");
            }
            finally { VaultLibrary.Is32BitForTests = null; }
        }

        public static void TmxImportPastTheLimitIn32BitAddsNothing()
        {
            var lib = VaultLibrary.For(Scratch.Folder("guard-tmx"));
            var tmx = LibraryTests.WriteTmx(string.Concat(Enumerable.Range(0, 30).Select(i => LibraryTests.Tu(("en-US", "Sentence " + i + "."), ("nl-NL", "Zin " + i + ".")))));
            As32Bit(20, () =>
            {
                var ex = Assert<VaultTooLargeException>(() => lib.Import(LibraryTests.Tmx(tmx), null, CancellationToken.None));
                A.Eq(0L, lib.StoreCount(), "nothing added");
                var done = new ManualResetEventSlim(); string error = null;
                A.True(lib.StartImport(LibraryTests.Tmx(tmx), e => { error = e; done.Set(); }), "started");
                A.True(done.Wait(20000), "finished");
                A.True(error != null && error.Contains("Studio 2026"), "the window shows the message: " + error);
            });
        }

        static TEx Assert<TEx>(Action a) where TEx : Exception
        {
            try { a(); }
            catch (TEx e) { return e; }
            catch (Exception e) { throw new AssertFailed($"expected {typeof(TEx).Name}, got {e.GetType().Name}: {e.Message}"); }
            throw new AssertFailed($"expected {typeof(TEx).Name}, nothing thrown");
        }
    }
}
