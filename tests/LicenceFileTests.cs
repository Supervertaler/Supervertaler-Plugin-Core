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
    /// The shared licence file is written by two processes. These pin the
    /// failure paths: a write that cannot complete leaves the old file and no
    /// temporary file, a reader never blocks a writer, a reader never sees half
    /// a file, and two first writers do not overwrite each other.
    /// </summary>
    [Tests]
    internal static class LicenceFileTests
    {
        private static byte[] Bytes(string name) =>
            LicenceFile.Serialise(new LicenceRecord { VariantName = name });

        private static string NameIn(string path)
        {
            Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(path, out var r, out _), "read back");
            return r.VariantName;
        }

        public static void CreateOnly_DoesNotOverwrite_AndReportsIt()
        {
            using (var s = new Scene())
            {
                Assert.Equal(LicenceFile.WriteStatus.Written,
                    LicenceFile.Write(s.SharedPath, Bytes("first"), replaceExisting: false), "first write");
                Assert.Equal(LicenceFile.WriteStatus.AlreadyExists,
                    LicenceFile.Write(s.SharedPath, Bytes("second"), replaceExisting: false), "second write");
                Assert.Equal("first", NameIn(s.SharedPath), "content");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        public static void Replace_ReplacesTheFile()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, Bytes("old"), replaceExisting: false);
                Assert.Equal(LicenceFile.WriteStatus.Written,
                    LicenceFile.Write(s.SharedPath, Bytes("new"), replaceExisting: true), "replace");
                Assert.Equal("new", NameIn(s.SharedPath), "content");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        /// <summary>
        /// Also the only test that proves the rename by handle is the path in
        /// use. The reader is held for the whole call, longer than the retry
        /// budget, and the MoveFileEx fallback is refused under it for all of
        /// it - so a broken FILE_RENAME_INFO, which falls back silently, fails
        /// here. Checked on 2026-09-23 by moving the name offset by 4: this test
        /// failed at both 32 and 64 bit.
        /// </summary>
        public static void AReaderNeverBlocksAWriter()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, Bytes("old"), replaceExisting: false);

                // Held open exactly as TryRead opens it.
                using (var reader = new FileStream(s.SharedPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var sw = Stopwatch.StartNew();
                    Assert.Equal(LicenceFile.WriteStatus.Written,
                        LicenceFile.Write(s.SharedPath, Bytes("new"), replaceExisting: true), "write under a reader");
                    Assert.True(sw.ElapsedMilliseconds < 100, "the write did not wait for the reader");

                    // The open reader still sees the whole old file, not a mix.
                    var old = new byte[reader.Length];
                    reader.Read(old, 0, old.Length);
                    Assert.True(LicenceFile.TryParse(old, out var r) && r.VariantName == "old",
                        "the reader's view is the complete old file");
                }
                Assert.Equal("new", NameIn(s.SharedPath), "new readers see the new file");
            }
        }

        public static void AWriteThatCannotComplete_GivesUpCleanly()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, Bytes("old"), replaceExisting: false);

                // A foreign program holding the file without delete sharing, for
                // longer than the writer is prepared to wait.
                LicenceFile.WriteStatus status;
                var sw = Stopwatch.StartNew();
                using (new FileStream(s.SharedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    status = LicenceFile.Write(s.SharedPath, Bytes("new"), replaceExisting: true);
                }

                Assert.Equal(LicenceFile.WriteStatus.Failed, status, "write status");
                Assert.True(sw.ElapsedMilliseconds < 2000, "gave up promptly, took " + sw.ElapsedMilliseconds + " ms");
                Assert.Equal("old", NameIn(s.SharedPath), "the old file is untouched");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left on the failure path");
            }
        }

        public static void AWriteRetriesPastABriefHold()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, Bytes("old"), replaceExisting: false);

                var held = new FileStream(s.SharedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = Task.Run(() => { Thread.Sleep(50); held.Dispose(); });

                Assert.Equal(LicenceFile.WriteStatus.Written,
                    LicenceFile.Write(s.SharedPath, Bytes("new"), replaceExisting: true), "write after the hold ends");
                release.Wait();
                Assert.Equal("new", NameIn(s.SharedPath), "content");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        public static void Read_TellsMissingDamagedAndUnreadableApart()
        {
            using (var s = new Scene())
            {
                Assert.Equal(LicenceFile.ReadStatus.Missing,
                    LicenceFile.TryRead(s.SharedPath, out _, out _), "no folder");

                Directory.CreateDirectory(Path.GetDirectoryName(s.SharedPath));
                Assert.Equal(LicenceFile.ReadStatus.Missing,
                    LicenceFile.TryRead(s.SharedPath, out _, out _), "no file");

                File.WriteAllBytes(s.SharedPath, new byte[0]);
                Assert.Equal(LicenceFile.ReadStatus.Damaged,
                    LicenceFile.TryRead(s.SharedPath, out _, out _), "empty file");

                File.WriteAllText(s.SharedPath, "{\"licenseKey\": \"abc\", \"trialStar");
                Assert.Equal(LicenceFile.ReadStatus.Damaged,
                    LicenceFile.TryRead(s.SharedPath, out _, out _), "truncated file");

                using (new FileStream(s.SharedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Equal(LicenceFile.ReadStatus.Unreadable,
                        LicenceFile.TryRead(s.SharedPath, out _, out _), "file held exclusively");
                }
            }
        }

        public static void Read_AcceptsTheTradosFileWithItsByteOrderMark()
        {
            using (var s = new Scene())
            {
                Scene.WriteTradosStyle(s.LegacyPath, new LicenceRecord { LicenseKey = "K", VariantName = "V" });
                var raw = File.ReadAllBytes(s.LegacyPath);
                Assert.True(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "the fixture has a BOM");

                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.LegacyPath, out var r, out _), "status");
                Assert.Equal("K", r.LicenseKey, "key");
            }
        }

        public static void Sweep_RemovesOnlyAbandonedTemporaryFiles()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, Bytes("x"), replaceExisting: false);
                var abandoned = s.SharedPath + ".aaaa.tmp";
                var inFlight = s.SharedPath + ".bbbb.tmp";
                File.WriteAllText(abandoned, "");
                File.WriteAllText(inFlight, "");
                File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-1));

                LicenceFile.SweepAbandonedTemporaryFiles(s.SharedPath, TimeSpan.FromMinutes(10));

                Assert.True(!File.Exists(abandoned), "an hour-old temporary file is removed");
                Assert.True(File.Exists(inFlight), "a fresh one may belong to a live write and is kept");
                Assert.True(File.Exists(s.SharedPath), "the licence file itself is kept");
            }
        }

        /// <summary>Four processes all make the first write at once. Exactly one wins, and nobody overwrites it.</summary>
        public static void FirstWriteRace_AcrossProcesses_HasOneWinner()
        {
            using (var s = new Scene())
            {
                var results = Program.RunChildren(4,
                    i => $"--race-create \"{s.SharedPath}\" \"{s.GoFile}\" {i}", s.GoFile);

                Assert.Equal(1, results.Count(r => r == "Written"), "winners among [" + string.Join(", ", results) + "]");
                Assert.Equal(3, results.Count(r => r == "AlreadyExists"), "losers told the file exists");

                var winner = Array.IndexOf(results, "Written");
                Assert.Equal("writer-" + winner, NameIn(s.SharedPath), "the file is the winner's");
                Assert.Equal(0, s.TempFilesLeft().Length, "temporary files left");
            }
        }

        /// <summary>
        /// Three processes write and read the same file for two seconds. No
        /// read may ever see a partial or mixed file.
        /// </summary>
        public static void TwoProcessHammer_NoReadEverSeesAPartialFile()
        {
            using (var s = new Scene())
            {
                LicenceFile.Write(s.SharedPath, Bytes("seed"), replaceExisting: false);

                var results = Program.RunChildren(3,
                    i => $"--hammer \"{s.SharedPath}\" \"{s.GoFile}\" p{i} 2000", s.GoFile);

                int Sum(string key) => results.Sum(r =>
                    int.Parse(r.Split(' ').First(p => p.StartsWith(key + "=")).Substring(key.Length + 1)));

                Console.WriteLine("      " + string.Join(" | ", results));
                Assert.True(results.All(r => r.StartsWith("written=")), "every child ran: " + string.Join(" | ", results));
                Assert.True(Sum("written") > 100, "enough writes to mean something");
                Assert.Equal(0, Sum("damaged"), "reads that saw a partial or mixed file");
                Assert.Equal(0, Sum("missing"), "reads that found no file mid-replace");
                Assert.Equal(LicenceFile.ReadStatus.Ok, LicenceFile.TryRead(s.SharedPath, out _, out _), "final file");
                var left = s.TempFilesLeft();
                Assert.True(left.Length == 0, "temporary files left: " +
                    string.Join(", ", left.Select(f => Path.GetFileName(f) + " (" + new FileInfo(f).Length + " bytes)")));
            }
        }
    }
}
