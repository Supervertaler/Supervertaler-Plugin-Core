using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The memory-bank write tools (MCP: read_supermemory_file,
    /// write_supermemory_file, update_supermemory_section,
    /// append_terminology_row) and the plugin's own Quick Add and bank-file
    /// editor all write through BankFileStore. The spec's tests first, then what
    /// the banks on disk actually look like: mixed line endings, several tables
    /// per terminology file, files somebody else is holding open.
    /// </summary>
    [Tests]
    internal static class BankFileStoreTests
    {
        private sealed class Banks : IDisposable
        {
            public readonly string Dir;
            public string Root => Path.Combine(Dir, "memory-banks");
            public string Backups => Path.Combine(Dir, "backups");
            public readonly BankFileStore Store;

            public Banks(string writeRefusal = null, string parent = null)
            {
                Dir = Path.Combine(parent ?? Path.GetTempPath(), "sv-banks-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Store = new BankFileStore(Root, Backups, writeRefusal);
            }

            public string Bank(string name, params (string File, string Text)[] files)
            {
                var dir = Path.Combine(Root, name);
                Directory.CreateDirectory(dir);
                foreach (var f in files) File.WriteAllBytes(Path.Combine(dir, f.File), Encoding.UTF8.GetBytes(f.Text));
                return dir;
            }

            public string FileIn(string bank, string file) => Path.Combine(Root, bank, file);
            public string Text(string bank, string file) => Encoding.UTF8.GetString(File.ReadAllBytes(FileIn(bank, file)));
            public byte[] Bytes(string bank, string file) => File.ReadAllBytes(FileIn(bank, file));
            public string[] Temps(string bank) => Directory.GetFiles(Path.Combine(Root, bank), "*.tmp");
            public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        }

        private const string Terms =
            "# Terminology - acme\r\n\r\n" +
            "| Source | Target | Scope | Note |\r\n" +
            "|---|---|---|---|\r\n" +
            "| hellingmeter | inclinometer | client | not tilt sensor |\r\n" +
            "\r\n### Provenance\r\n\r\nFrom the 2025 jobs.\r\n";

        // ── The spec's tests ─────────────────────────────────────────────────

        public static void Create_Read_Update_RoundTrip()
        {
            using (var b = new Banks())
            {
                b.Bank("acme");
                var created = b.Store.Write("acme", "figures.md", "# Figures\n\nFig. 1 is the housing.", "new", false);
                Assert.True(created.Ok && created.Created, "created: " + created.Error);
                Assert.Equal("acme", created.Bank, "the bank is in the result");
                Assert.Equal("figures.md", created.Path, "the path is in the result");

                var read = b.Store.Read("acme", "figures.md");
                Assert.True(read.Ok, "read back: " + read.Error);
                Assert.Equal(created.Version, read.Version, "the version written is the version read");
                Assert.Equal("# Figures\r\n\r\nFig. 1 is the housing.\r\n", read.Content, "a new file gets CRLF and a final newline");

                var updated = b.Store.Write("acme", "figures.md", "# Figures\n\nFig. 1 is the casing.", read.Version, false);
                Assert.True(updated.Ok && !updated.Created, "updated: " + updated.Error);
                Assert.True(updated.Version != read.Version, "a new version");
                Assert.Equal(updated.Version, b.Store.Read("acme", "figures.md").Version, "and the file has it");
            }
        }

        public static void AppendRow_FillsTheSkeletonPlaceholder_ThenAddsBelow()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", MemoryBanks.SkeletonBody("terminology.md", "acme")));

                var first = b.Store.AppendTerminologyRow("acme", "hellingmeter", "inclinometer", "Client", "not tilt sensor", false);
                Assert.True(first.Ok, "first row: " + first.Error);
                Assert.Equal("| hellingmeter | inclinometer | client | not tilt sensor |", first.Row, "the row written, scope lower-cased");
                Assert.True(!b.Text("acme", "terminology.md").Contains("|  |  |  |  |"), "the empty placeholder row was filled, not left behind");

                var second = b.Store.AppendTerminologyRow("acme", "aandrijving", "drive", "domain", null, false);
                Assert.True(second.Ok, "second row: " + second.Error);
                var text = b.Text("acme", "terminology.md");
                Assert.True(text.IndexOf("hellingmeter", StringComparison.Ordinal) < text.IndexOf("aandrijving", StringComparison.Ordinal),
                    "rows accumulate in order");
                Assert.True(text.Contains("| aandrijving | drive | domain |  |\r\n"), "an empty note is an empty cell, in the file's CRLF");
            }
        }

        public static void ReplaceSection_ChangesOnlyThatSection_AndAddsAMissingOne()
        {
            using (var b = new Banks())
            {
                const string style =
                    "# Style\r\n\r\n## Register\r\n\r\nFormal.\r\n\r\n## Numbers\r\n\r\nUse a comma.\r\n\r\n### Provenance\r\n\r\nOld.\r\n";
                b.Bank("acme", ("style.md", style));
                var v = b.Store.Read("acme", "style.md").Version;

                var r = b.Store.ReplaceSection("acme", "style.md", "## Register", "Formal, but not stiff.\nNo contractions.", v, false);
                Assert.True(r.Ok, "replaced: " + r.Error);
                Assert.Equal(
                    "# Style\r\n\r\n## Register\r\n\r\nFormal, but not stiff.\r\nNo contractions.\r\n\r\n## Numbers\r\n\r\nUse a comma.\r\n\r\n### Provenance\r\n\r\nOld.\r\n",
                    b.Text("acme", "style.md"), "only the body under ## Register changed");

                var added = b.Store.ReplaceSection("acme", "style.md", "Dates", "Write 8 October 2026.", r.Version, false);
                Assert.True(added.Ok && added.Note != null, "added: " + added.Error);
                Assert.True(b.Text("acme", "style.md").EndsWith("Old.\r\n\r\n## Dates\r\n\r\nWrite 8 October 2026.\r\n"),
                    "a missing section goes at the end");

                // The last section runs to the end of the file, ### subsections included.
                var last = b.Store.ReplaceSection("acme", "style.md", "numbers", "Use a point.", added.Version, false);
                Assert.True(last.Ok, "case-insensitive heading: " + last.Error);
                Assert.True(b.Text("acme", "style.md").Contains("## Numbers\r\n\r\nUse a point.\r\n\r\n## Dates"),
                    "the ### Provenance under Numbers belonged to it and went with it");
            }
        }

        public static void VersionConflict_ReturnsTheCurrentContent_AndWritesNothing()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms));
                var seen = b.Store.Read("acme", "terminology.md").Version;

                // Obsidian, or a colleague in the team folder, saves in between.
                var changed = Terms.Replace("not tilt sensor", "never tilt sensor");
                File.WriteAllBytes(b.FileIn("acme", "terminology.md"), Encoding.UTF8.GetBytes(changed));

                foreach (var r in new[]
                {
                    b.Store.Write("acme", "terminology.md", "# Wiped", seen, false),
                    b.Store.ReplaceSection("acme", "terminology.md", "Provenance", "x", seen, false),
                })
                {
                    Assert.True(!r.Ok && r.Conflict, "refused as a conflict");
                    Assert.Equal(changed, r.Content, "with the file as it is now, to merge into");
                    Assert.Equal(BankFileStore.VersionOf(Encoding.UTF8.GetBytes(changed)), r.Version, "and its version, to retry with");
                }
                Assert.Equal(changed, b.Text("acme", "terminology.md"), "the other writer's change survives");
                Assert.True(!Directory.Exists(b.Backups), "no backup either: nothing was replaced");

                var create = b.Store.Write("acme", "terminology.md", "# New", "new", false);
                Assert.True(!create.Ok && create.Conflict && create.Content == changed, "\"new\" on an existing file is a conflict too");
            }
        }

        public static void PathTraversal_AndOtherPaths_AreRejected()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms));
                b.Bank("other", ("brief.md", "# Other\r\n"));
                File.WriteAllText(Path.Combine(b.Root, "outside.md"), "x");

                foreach (var path in new[]
                {
                    "../other/brief.md", "..\\other\\brief.md", "../outside.md", "reference/../../other/brief.md",
                    "./terminology.md", "C:\\Windows\\win.ini.md", "/terminology.md", "\\\\server\\share\\x.md",
                    "~/x.md", "notes.txt", "terminology", ".hidden.md", "sub/notes.md", "a//b.md", "", null,
                })
                {
                    var w = b.Store.Write("acme", path, "# x", "new", false);
                    Assert.True(!w.Ok && !w.Created, "write refused: '" + path + "'");
                    var r = b.Store.Read("acme", path);
                    Assert.True(!r.Ok, "read refused: '" + path + "'");
                }
                Assert.Equal("# Other\r\n", b.Text("other", "brief.md"), "the other bank is untouched");
                Assert.Equal(1, Directory.GetFiles(Path.Combine(b.Root, "acme")).Length, "nothing was created in the bank");
            }
        }

        public static void Reference_And_Shared_AreProtected()
        {
            using (var b = new Banks())
            {
                var acme = b.Bank("acme", ("terminology.md", Terms));
                Directory.CreateDirectory(Path.Combine(acme, "reference"));
                File.WriteAllText(Path.Combine(acme, "reference", "harvest.md"), "# Harvest\r\n");
                b.Bank("_shared", ("style.md", "# House style\r\n"));

                var read = b.Store.Read("acme", "reference/harvest.md");
                Assert.True(read.Ok, "reference/ can be read: " + read.Error);
                var write = b.Store.Write("acme", "reference/harvest.md", "# Changed", read.Version, false);
                Assert.True(!write.Ok && write.Error.Contains("reference/"), "but never written");
                Assert.True(!b.Store.Write("acme", "Reference/new.md", "# x", "new", false).Ok, "in any letter case");

                var v = b.Store.Read("_shared", "style.md").Version;
                var refused = b.Store.Write("_shared", "style.md", "# Changed", v, false);
                Assert.True(!refused.Ok && refused.Error.Contains("allowShared"), "_shared needs allowShared");
                Assert.True(!b.Store.AppendTerminologyRow("_shared", "a", "b", "domain", null, false).Ok, "for a row too");
                Assert.True(!b.Store.ReplaceSection("_shared", "style.md", "X", "y", v, false).Ok, "and a section");
                Assert.True(b.Store.Write("_shared", "style.md", "# Changed", v, true).Ok, "and gets it with allowShared");
            }
        }

        public static void DuplicateTerminologyRow_IsRefused_WithTheExistingRow()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms +
                    "\r\n## Rejected\r\n\r\n| Source | Rejected | Why |\r\n|---|---|---|\r\n| **Aandrijving** | engine | wrong part |\r\n"));
                var before = b.Bytes("acme", "terminology.md");

                var r = b.Store.AppendTerminologyRow("acme", "HELLINGMETER", "tilt meter", "client", null, false);
                Assert.True(!r.Ok, "a source term already there, in any case, is refused");
                Assert.Equal("| hellingmeter | inclinometer | client | not tilt sensor |", r.Row, "and the existing row comes back");
                Assert.Equal("Terminology - acme", r.Section, "with the heading it sits under");

                var other = b.Store.AppendTerminologyRow("acme", "aandrijving", "drive", "client", null, false);
                Assert.True(!other.Ok && other.Section == "Rejected", "a term in any table counts, emphasis aside");
                Assert.True(before.SequenceEqual(b.Bytes("acme", "terminology.md")), "the file is untouched");
            }
        }

        public static void Search_And_LoadContext_SeeAWriteAtOnce()
        {
            using (var b = new Banks())
            {
                var dir = b.Bank("acme", ("terminology.md", Terms), ("brief.md", "# Acme\r\n\r\nA client.\r\n"));

                // A reader that has already built its index and loaded the bank,
                // as the plugin's cached reader has: the 30-second index must not
                // hide the write.
                var cached = new MemoryBankReader(dir);
                Assert.Equal(0, cached.Search("zwenkwiel", 10).Count, "not there yet");
                cached.LoadContext("p", null, "nl", "en", tokenBudget: 0);

                Assert.True(b.Store.AppendTerminologyRow("acme", "zwenkwiel", "castor", "domain", null, false).Ok, "row added");
                var v = b.Store.Read("acme", "brief.md").Version;
                Assert.True(b.Store.ReplaceSection("acme", "brief.md", "Contacts", "Ask for Fenna.", v, false).Ok, "section added");

                Assert.True(new MemoryBankReader(dir).Search("zwenkwiel", 10).Count > 0, "search (a fresh reader per call, as the bridge does) finds the row");
                Assert.True(cached.Search("zwenkwiel", 10).Count > 0, "and so does the cached reader: bodies are re-read when the file's time changes");
                var block = MemoryBankReader.FormatForPrompt(cached.LoadContext("p", null, "nl", "en", tokenBudget: 0));
                Assert.True(block.Contains("castor") && block.Contains("Ask for Fenna."), "and the context the next prompt gets has both");
            }
        }

        // ── Line endings, encoding, final newline ────────────────────────────

        public static void AnLfFile_StaysLf_ThroughEveryKindOfWrite()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms.Replace("\r\n", "\n")), ("style.md", "# Style\n\n## Register\n\nFormal.\n"));

                Assert.True(b.Store.AppendTerminologyRow("acme", "zwenkwiel", "castor", "domain", "x", false).Ok, "row");
                var v = b.Store.Read("acme", "style.md").Version;
                var s = b.Store.ReplaceSection("acme", "style.md", "Register", "Formal.\r\nNo contractions.", v, false);
                Assert.True(s.Ok, "section");
                Assert.True(b.Store.Write("acme", "style.md", "# Style\r\n\r\nRewritten.\r\n", s.Version, false).Ok, "whole file");

                Assert.True(!b.Text("acme", "terminology.md").Contains("\r"), "terminology.md is still LF only");
                Assert.Equal("# Style\n\nRewritten.\n", b.Text("acme", "style.md"), "style.md too, even from CRLF content");
            }
        }

        public static void AMixedFile_KeepsEveryUntouchedLineByteForByte()
        {
            using (var b = new Banks())
            {
                // Two tools have edited this one: mostly CRLF, two LF lines.
                const string mixed = "# Style\r\n\r\n## A\n\r\nalpha\r\n\r\n## B\r\n\r\nbeta\n\r\n## C\r\n\r\ngamma\r\n";
                b.Bank("acme", ("style.md", mixed));
                var v = b.Store.Read("acme", "style.md").Version;

                Assert.True(b.Store.ReplaceSection("acme", "style.md", "B", "BETA", v, false).Ok, "replaced");
                Assert.Equal("# Style\r\n\r\n## A\n\r\nalpha\r\n\r\n## B\r\n\r\nBETA\r\n\r\n## C\r\n\r\ngamma\r\n",
                    b.Text("acme", "style.md"), "the LF line in section A is still LF; the new lines take the majority CRLF");
            }
        }

        public static void ABom_IsKept_AndNeverAdded()
        {
            using (var b = new Banks())
            {
                b.Bank("acme");
                File.WriteAllBytes(b.FileIn("acme", "brief.md"),
                    new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("# Acme\r\n")).ToArray());

                var read = b.Store.Read("acme", "brief.md");
                Assert.Equal("# Acme\r\n", read.Content, "the BOM is not part of the text");
                Assert.True(b.Store.Write("acme", "brief.md", "# Acme Ltd", read.Version, false).Ok, "written");
                var bytes = b.Bytes("acme", "brief.md");
                Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "the BOM is still there");

                Assert.True(b.Store.Write("acme", "new.md", "# New", "new", false).Ok, "created");
                Assert.True(b.Bytes("acme", "new.md")[0] == (byte)'#', "and a new file has none");
            }
        }

        public static void AFileWithoutAFinalNewline_StaysWithout()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("style.md", "# Style\n\n## A\n\nalpha"));
                var v = b.Store.Read("acme", "style.md").Version;
                var r = b.Store.ReplaceSection("acme", "style.md", "B", "beta", v, false);
                Assert.True(r.Ok, "added");
                Assert.Equal("# Style\n\n## A\n\nalpha\n\n## B\n\nbeta", b.Text("acme", "style.md"), "no final newline added");

                Assert.True(b.Store.Write("acme", "style.md", "# Style\n", r.Version, false).Ok, "rewritten");
                Assert.Equal("# Style", b.Text("acme", "style.md"), "nor by a whole-file write");
            }
        }

        public static void ANonUtf8File_IsLeftAlone()
        {
            using (var b = new Banks())
            {
                b.Bank("acme");
                var cp1252 = new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\r', (byte)'\n' };
                File.WriteAllBytes(b.FileIn("acme", "brief.md"), cp1252);

                var read = b.Store.Read("acme", "brief.md");
                Assert.True(!read.Ok && read.Error.Contains("UTF-8"), "not read as if it were UTF-8");
                var write = b.Store.ReplaceSection("acme", "brief.md", "X", "y", BankFileStore.VersionOf(cp1252), false);
                Assert.True(!write.Ok && write.Error.Contains("UTF-8"), "nor rewritten");
                Assert.True(cp1252.SequenceEqual(b.Bytes("acme", "brief.md")), "the bytes are as they were");
            }
        }

        // ── Backups and failure paths ────────────────────────────────────────

        public static void TheBackup_IsThePreviousVersion_InTheDataFolder_OneGeneration()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms));
                var original = b.Bytes("acme", "terminology.md");
                var backup = Path.Combine(b.Backups, "acme", "terminology.md.bak");

                var first = b.Store.AppendTerminologyRow("acme", "a", "b", "client", null, false);
                Assert.Equal(backup, first.Backup, "the result says where");
                Assert.True(original.SequenceEqual(File.ReadAllBytes(backup)), "the previous version, byte for byte");

                var afterFirst = b.Bytes("acme", "terminology.md");
                Assert.True(b.Store.AppendTerminologyRow("acme", "c", "d", "client", null, false).Ok, "second write");
                Assert.True(afterFirst.SequenceEqual(File.ReadAllBytes(backup)), "one generation: the next write replaces it");
                Assert.Equal(1, Directory.GetFiles(Path.Combine(b.Backups, "acme")).Length, "one backup file");
                Assert.Equal(1, Directory.GetFiles(Path.Combine(b.Root, "acme")).Length, "and nothing beside the bank file");

                var created = b.Store.Write("acme", "figures.md", "# F", "new", false);
                Assert.True(created.Backup == null, "a new file has nothing to back up");
            }
        }

        public static void AnUnchangedWrite_WritesNothing()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("brief.md", "# Acme\n"));
                var v = b.Store.Read("acme", "brief.md").Version;
                var r = b.Store.Write("acme", "brief.md", "# Acme\r\n", v, false);
                Assert.True(r.Ok && r.Unchanged && r.Version == v, "the same text in the file's own endings is no change");
                Assert.True(!Directory.Exists(b.Backups), "so no backup");
            }
        }

        public static void WhenTheBackupCannotBeMade_NothingIsWritten()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms));
                // A file where the backups folder should be: no folder can be made there.
                File.WriteAllText(b.Backups, "in the way");
                var before = b.Bytes("acme", "terminology.md");

                var r = b.Store.AppendTerminologyRow("acme", "a", "b", "client", null, false);
                Assert.True(!r.Ok && r.Error.Contains("previous version could not be saved"), "refused: " + r.Error);
                Assert.True(before.SequenceEqual(b.Bytes("acme", "terminology.md")), "the bank file is untouched");
                Assert.Equal(0, b.Temps("acme").Length, "and no temporary file is left");
            }
        }

        public static void AFileHeldOpenElsewhere_IsRefusedCleanly()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms));
                var before = b.Bytes("acme", "terminology.md");

                // A sync tool or scanner holding it without delete sharing, for
                // longer than AtomicFile waits.
                BankFileResult r;
                using (new FileStream(b.FileIn("acme", "terminology.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    r = b.Store.AppendTerminologyRow("acme", "a", "b", "client", null, false);

                Assert.True(!r.Ok && !r.Conflict, "refused, and not mistaken for a conflict: " + r.Error);
                Assert.True(before.SequenceEqual(b.Bytes("acme", "terminology.md")), "the file is as it was");
                Assert.Equal(0, b.Temps("acme").Length, "no temporary file is left in the bank");
                Assert.True(b.Store.AppendTerminologyRow("acme", "a", "b", "client", null, false).Ok, "and once it is let go, the same call works");
            }
        }

        public static void TwoCreatesAtOnce_OneWins_TheOtherGetsAConflict()
        {
            using (var b = new Banks())
            {
                b.Bank("acme");
                var go = new ManualResetEventSlim();
                var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
                {
                    go.Wait();
                    return b.Store.Write("acme", "figures.md", "# Writer " + i, "new", false);
                })).ToArray();
                go.Set();
                var results = tasks.Select(t => t.Result).ToList();

                Assert.Equal(1, results.Count(x => x.Ok && x.Created), "exactly one created it");
                var loser = results.Single(x => !x.Ok);
                Assert.True(loser.Conflict && loser.Content == b.Text("acme", "figures.md"), "the other got the winner's file back");
            }
        }

        public static void Banks_AreNamedExactly_AndNeverCreated()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("terminology.md", Terms));
                Assert.True(b.Store.Read("ACME", "terminology.md").Ok, "letter case does not matter");
                Assert.Equal("acme", b.Store.Read("ACME", "terminology.md").Bank, "and the result names the folder as it is");

                foreach (var name in new[] { "acme.", "_acme", "nobody", "", null, "../acme", ".trash" })
                {
                    var r = b.Store.AppendTerminologyRow(name, "a", "b", "client", null, false);
                    Assert.True(!r.Ok, "refused: '" + name + "'");
                }
                Assert.Equal(1, Directory.GetDirectories(b.Root).Length, "no bank was created");
                Assert.True(b.Store.AppendTerminologyRow("nobody", "a", "b", "client", null, false).Error.Contains("acme"),
                    "the refusal lists the banks there are");
            }
        }

        public static void WithTheTeamFolderMissing_AgentWritesAreRefused_ReadsAreNot()
        {
            using (var b = new Banks("The team folder is not in use."))
            {
                b.Bank("acme", ("terminology.md", Terms));
                var read = b.Store.Read("acme", "terminology.md");
                Assert.True(read.Ok, "reading is fine");
                Assert.True(!b.Store.Write("acme", "terminology.md", "# x", read.Version, false).Ok, "write refused");
                Assert.True(!b.Store.AppendTerminologyRow("acme", "a", "b", "client", null, false).Ok, "row refused");
                Assert.True(!b.Store.ReplaceSection("acme", "terminology.md", "X", "y", read.Version, false).Ok, "section refused");
                Assert.Equal(Terms, b.Text("acme", "terminology.md"), "unchanged");
            }
        }

        public static void MissingArguments_AreRefused()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("brief.md", "# Acme\r\n"));
                var v = b.Store.Read("acme", "brief.md").Version;
                Assert.True(!b.Store.Write("acme", "brief.md", "# x", null, false).Ok, "no ifVersion, no overwrite");
                Assert.True(!b.Store.ReplaceSection("acme", "brief.md", "X", "y", "", false).Ok, "for a section too");
                Assert.True(!b.Store.Write("acme", "brief.md", "  \n ", v, false).Ok, "a bank file is never emptied");
                Assert.True(!b.Store.Write("acme", "gone.md", "# x", v, false).Ok, "a version for a file that is not there");
                Assert.True(!b.Store.AppendTerminologyRow("acme", "a", "", "client", null, false).Ok, "a row needs a target");
                Assert.True(!b.Store.AppendTerminologyRow("acme", "a", "b", "everywhere", null, false).Ok, "and a real scope");
                Assert.True(!b.Store.ReplaceSection("acme", "brief.md", "### Sub", "y", v, false).Ok, "only ## sections");
                Assert.True(!b.Store.ReplaceSection("acme", "brief.md", "X", "text\n## Smuggled\nmore", v, false).Ok,
                    "content may not open a section of its own");
                Assert.Equal("# Acme\r\n", b.Text("acme", "brief.md"), "nothing changed");
            }
        }

        // ── Which table, which heading ───────────────────────────────────────

        public static void ARow_GoesIntoTheFirstTableWithAScopeColumn()
        {
            using (var b = new Banks())
            {
                // As in a real bank: a TM comparison first, then the decisions,
                // then the rejected variants.
                b.Bank("acme", ("terminology.md",
                    "# Terms\n\n## TM check\n\n| Dutch | TM A | TM B |\n|---|---|---|\n| ventiel | valve | valve |\n\n" +
                    "## Decisions\n\n| Dutch | English | Scope | Why it is here |\n|:--|:--|:--|:--|\n| klep | flap | client | not valve |\n\n" +
                    "## Rejected\n\n| English | Dutch | Note |\n|---|---|---|\n| lid | deksel | too informal |\n"));

                var r = b.Store.AppendTerminologyRow("acme", "zuiger", "piston", "domain", "per the drawings", false);
                Assert.True(r.Ok, "added: " + r.Error);
                Assert.Equal("Decisions", r.Section, "into the decisions table");
                Assert.True(b.Text("acme", "terminology.md").Contains(
                    "| klep | flap | client | not valve |\n| zuiger | piston | domain | per the drawings |\n\n## Rejected"),
                    "right below its last row, the note in the last column");
            }
        }

        public static void NoTableWithAScopeColumn_IsRefused_NotGuessed()
        {
            using (var b = new Banks())
            {
                const string prose = "# Terms\r\n\r\n## Pumps\r\n\r\nWe say pomp.\r\n\r\n| English | Dutch |\r\n|---|---|\r\n| pump | pomp |\r\n";
                b.Bank("acme", ("terminology.md", prose));
                var r = b.Store.AppendTerminologyRow("acme", "zuiger", "piston", "domain", null, false);
                Assert.True(!r.Ok && r.Error.Contains("Scope") && r.Error.Contains("Pumps"), "refused, naming what is there: " + r.Error);
                Assert.Equal(prose, b.Text("acme", "terminology.md"), "unchanged");
            }
        }

        public static void ABankWithoutTerminologyMd_GetsTheSkeleton()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("brief.md", "# Acme\r\n"));
                var r = b.Store.AppendTerminologyRow("acme", "zuiger", "piston", "domain", null, false);
                Assert.True(r.Ok && r.Created, "created: " + r.Error);
                var text = b.Text("acme", "terminology.md");
                Assert.True(text.StartsWith("# Terminology - acme") && text.Contains("| zuiger | piston | domain |  |"), "the skeleton, filled");
            }
        }

        public static void Headings_InCodeFences_AndFrontmatter_DoNotCount()
        {
            using (var b = new Banks())
            {
                b.Bank("acme", ("method.md",
                    "---\naudience: assistant\n# not a heading\n---\n# Method\n\n## C#\n\nUse C# 7.\n\n```\n## Steps\n```\n\n## Steps\n\nOld steps.\n"));
                var v = b.Store.Read("acme", "method.md").Version;

                var r = b.Store.ReplaceSection("acme", "method.md", "Steps", "New steps.", v, false);
                Assert.True(r.Ok && r.Note == null, "the real ## Steps was found: " + r.Error);
                var text = b.Text("acme", "method.md");
                Assert.True(text.Contains("```\n## Steps\n```") && text.EndsWith("## Steps\n\nNew steps.\n"), "the fenced one was left alone");

                r = b.Store.ReplaceSection("acme", "method.md", "C#", "Use C# 12.", r.Version, false);
                Assert.True(r.Ok && r.Note == null && b.Text("acme", "method.md").Contains("## C#\n\nUse C# 12.\n"), "## C# keeps its #");

                b.Bank("twice", ("style.md", "## A\n\none\n\n## A\n\ntwo\n"));
                var t = b.Store.ReplaceSection("twice", "style.md", "A", "x", b.Store.Read("twice", "style.md").Version, false);
                Assert.True(!t.Ok && t.Error.Contains("2 sections"), "two sections of the same name: not guessed");
            }
        }

        // ── The plugin's editor ──────────────────────────────────────────────

        public static void TheEditor_SavesAnyBankFile_WithTheSameCare()
        {
            using (var b = new Banks())
            {
                var acme = b.Bank("acme");
                Directory.CreateDirectory(Path.Combine(acme, "reference"));
                var path = Path.Combine(acme, "reference", "glossary.txt");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a\nb\n"));

                var opened = b.Store.Load(path);
                Assert.True(opened.Ok && opened.Path == "reference/glossary.txt", "opened: " + opened.Error);
                var saved = b.Store.Save(path, "a\r\nb\r\nc\r\n", opened.Version);
                Assert.True(saved.Ok, "saved: " + saved.Error);
                Assert.Equal("a\nb\nc\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)), "in the file's own LF");
                Assert.True(File.Exists(Path.Combine(b.Backups, "acme", "reference", "glossary.txt.bak")), "backed up like the rest");

                File.WriteAllText(path, "changed elsewhere\n");
                var stale = b.Store.Save(path, "mine\n", saved.Version);
                Assert.True(!stale.Ok && stale.Conflict, "a change made meanwhile is noticed");
                Assert.True(b.Store.Save(path, "mine\n", stale.Version).Ok, "and overruled only by saving again on purpose");

                Assert.True(!b.Store.Load(Path.Combine(b.Dir, "elsewhere.md")).Ok, "nothing outside the banks");
            }
        }

        // ── A team folder on a file server ───────────────────────────────────

        /// <summary>
        /// A team folder is a share on a file server, where a rename may not
        /// replace a file in one step the way it does on a local disk, and
        /// AtomicFile falls back to MoveFileEx. The same rules must hold there.
        /// Run against this computer's own admin share (\\localhost\C$), so it is
        /// skipped, saying so, where that share cannot be reached.
        /// </summary>
        public static void OverANetworkShare_TheSameRulesHold()
        {
            var temp = Path.GetTempPath();
            var unc = @"\\localhost\" + temp.Substring(0, 1) + "$" + temp.Substring(2);
            if (!Directory.Exists(unc))
            {
                Console.WriteLine("      skipped: " + unc + " cannot be reached");
                return;
            }

            using (var b = new Banks(parent: unc))
            {
                b.Bank("team", ("terminology.md", Terms.Replace("\r\n", "\n")));
                Assert.True(b.Root.StartsWith(@"\\", StringComparison.Ordinal), "working through the share");

                var row = b.Store.AppendTerminologyRow("team", "zwenkwiel", "castor", "domain", null, false);
                Assert.True(row.Ok && row.Backup != null, "a row replaces the file over the share: " + row.Error);
                Assert.True(!b.Text("team", "terminology.md").Contains("\r"), "still LF");

                var stale = row.Version;
                Assert.True(b.Store.AppendTerminologyRow("team", "zuiger", "piston", "domain", null, false).Ok, "a colleague's row");
                var lost = b.Store.ReplaceSection("team", "terminology.md", "Provenance", "x", stale, false);
                Assert.True(!lost.Ok && lost.Conflict && lost.Content.Contains("zuiger"), "a stale version is still caught over the share");

                var created = b.Store.Write("team", "figures.md", "# F", "new", false);
                Assert.True(created.Ok && b.Store.Write("team", "figures.md", "# G", "new", false).Conflict, "create-only holds over the share");

                // Someone on another computer has the file open without delete sharing.
                BankFileResult held;
                using (new FileStream(b.FileIn("team", "figures.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    held = b.Store.Write("team", "figures.md", "# H", created.Version, false);
                Assert.True(!held.Ok && !held.Conflict, "refused cleanly while held: " + held.Error);
                Assert.Equal("# F\r\n", b.Text("team", "figures.md"), "and the file is as it was");
                Assert.Equal(0, b.Temps("team").Length, "no temporary file is left on the share");
            }
        }

        // ── Scale ────────────────────────────────────────────────────────────

        /// <summary>
        /// The largest terminology.md in real use is 29 KB, a few hundred rows.
        /// This is 10,000 rows (about 850 KB, thirty times that): every
        /// operation reads, checks and rewrites the whole file, which must stay
        /// well under a second. Too big to hand an AI client whole, so the read
        /// is refused - but with the version, so one section can still change.
        /// </summary>
        public static void TenThousandRows_StayFast()
        {
            using (var b = new Banks())
            {
                var sb = new StringBuilder("# Terminology - big\r\n\r\n| Source | Target | Scope | Note |\r\n|---|---|---|---|\r\n");
                for (int i = 0; i < 10000; i++)
                    sb.Append("| bronterm ").Append(i).Append(" | target term ").Append(i).Append(" | client | a note of some length, as the real ones have |\r\n");
                sb.Append("\r\n## Notes\r\n\r\nOld.\r\n");
                b.Bank("big", ("terminology.md", sb.ToString()));

                var clock = Stopwatch.StartNew();
                var row = b.Store.AppendTerminologyRow("big", "zwenkwiel", "castor", "domain", null, false);
                var dup = b.Store.AppendTerminologyRow("big", "BRONTERM 9999", "x", "domain", null, false);
                var read = b.Store.Read("big", "terminology.md");
                var sec = b.Store.ReplaceSection("big", "terminology.md", "Notes", "New.", read.Version, false);
                clock.Stop();

                Assert.True(row.Ok, "row: " + row.Error);
                Assert.True(!dup.Ok && dup.Row != null, "the duplicate in row 9,999 is found");
                Assert.True(!read.Ok && read.Content == null && read.Version == row.Version, "the read is refused for size, with the version: " + read.Error);
                Assert.True(sec.Ok, "section: " + sec.Error);
                Assert.True(clock.ElapsedMilliseconds < 2000, "append, duplicate check, read and section replace took " + clock.ElapsedMilliseconds + " ms");
                Console.WriteLine("      10,000 rows (" + (b.Bytes("big", "terminology.md").Length / 1024) + " KB): 4 operations in "
                    + clock.ElapsedMilliseconds + " ms");
            }
        }
    }
}
