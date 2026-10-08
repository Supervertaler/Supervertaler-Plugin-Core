using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// A settings file must not be lost to someone else reading it at the moment
    /// it is saved. On 7-8 October 2026 one save in four of the Trados plugin's
    /// project settings failed with "Unable to remove the file to be replaced"
    /// (File.Replace while the file was open elsewhere), silently. These are the
    /// situations it met, and what a write must do in each.
    /// </summary>
    [Tests]
    internal static class AtomicFileTests
    {
        private sealed class Folder : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "sv-atomic-" + Guid.NewGuid().ToString("N"));
            public Folder() { Directory.CreateDirectory(Dir); }
            public string File(string name) => Path.Combine(Dir, name);
            public string[] Temps() => Directory.GetFiles(Dir, "*.tmp");
            public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        /// <summary>The failure itself, kept as a fact: File.Replace cannot replace a file another reader has open.</summary>
        public static void FileReplace_FailsWhileTheFileIsOpenForReading()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                File.WriteAllText(path, "old");
                File.WriteAllText(path + ".new", "new");

                bool threw = false;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { File.Replace(path + ".new", path, null); }
                    catch (IOException) { threw = true; }
                }
                Assert.True(threw, "File.Replace refuses while a reader holds the file - the October failure");
            }
        }

        public static void AWrite_ReplacesTheFile_WhileAReaderWithDeleteSharingHasItOpen()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                File.WriteAllText(path, "old");

                using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.Equal(AtomicFile.Outcome.Written, AtomicFile.Write(path, Utf8("new"), true, null), "written at once");
                    var old = new byte[3];
                    reader.Read(old, 0, 3);
                    Assert.Equal("old", Encoding.UTF8.GetString(old), "the reader keeps the file it opened");
                }
                Assert.Equal("new", File.ReadAllText(path), "the new file is in place");
                Assert.Equal(0, f.Temps().Length, "temporary files left");
            }
        }

        /// <summary>What a virus scanner, a sync tool or File.ReadAllText does: hold the file briefly, without delete sharing.</summary>
        public static void AWrite_WaitsOutABriefReaderThatBlocksIt()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                File.WriteAllText(path, "old");

                var opened = new ManualResetEventSlim();
                var holder = Task.Run(() =>
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        opened.Set();
                        Thread.Sleep(100);
                    }
                });
                opened.Wait();

                Assert.Equal(AtomicFile.Outcome.Written, AtomicFile.Write(path, Utf8("new"), true, null), "written once the reader let go");
                holder.Wait();
                Assert.Equal("new", File.ReadAllText(path), "the new file is in place");
                Assert.Equal(0, f.Temps().Length, "temporary files left");
            }
        }

        public static void AWrite_ThatCannotComplete_LeavesTheOldFileAndNoTemporaryFile()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                File.WriteAllText(path, "old");
                string logged = null;

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.Equal(AtomicFile.Outcome.Failed, AtomicFile.Write(path, Utf8("new"), true, m => logged = m), "gave up");
                }

                Assert.Equal("old", File.ReadAllText(path), "the old file is exactly as it was");
                Assert.Equal(0, f.Temps().Length, "temporary files left");
                Assert.True(logged != null && logged.Contains("settings.json not written"), "and said why: " + logged);
            }
        }

        public static void AFirstWrite_DoesNotOverwriteAFileAlreadyThere()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                Assert.Equal(AtomicFile.Outcome.Written, AtomicFile.Write(path, Utf8("first"), false, null), "created");
                Assert.Equal(AtomicFile.Outcome.AlreadyExists, AtomicFile.Write(path, Utf8("second"), false, null), "not overwritten");
                Assert.Equal("first", File.ReadAllText(path), "the first stands");
                Assert.Equal(0, f.Temps().Length, "temporary files left");
            }
        }

        /// <summary>A reader is as patient as a writer: a brief exclusive hold is waited out; a missing file is not.</summary>
        public static void ReadAllText_WaitsOutABriefExclusiveHold_ButNotAMissingFile()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                File.WriteAllText(path, "text");

                var opened = new ManualResetEventSlim();
                var holder = Task.Run(() =>
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        opened.Set();
                        Thread.Sleep(100);
                    }
                });
                opened.Wait();
                Assert.Equal("text", AtomicFile.ReadAllText(path), "read once the holder let go");
                holder.Wait();

                var clock = System.Diagnostics.Stopwatch.StartNew();
                bool missing = false;
                try { AtomicFile.ReadAllText(f.File("absent.json")); }
                catch (FileNotFoundException) { missing = true; }
                Assert.True(missing && clock.ElapsedMilliseconds < 50, "a missing file throws at once (" + clock.ElapsedMilliseconds + " ms)");
            }
        }

        /// <summary>Reading must never be what stops someone else's write.</summary>
        public static void ReadAllText_LetsAWriteThrough_AndHonoursAByteOrderMark()
        {
            using (var f = new Folder())
            {
                var path = f.File("settings.json");
                File.WriteAllText(path, "{\"a\":1}", Encoding.UTF8);   // with a BOM, as the plugin writes them
                Assert.Equal("{\"a\":1}", AtomicFile.ReadAllText(path), "the BOM is not part of the text");

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    Assert.Equal(AtomicFile.Outcome.Written, AtomicFile.Write(path, Utf8("{}"), true, null), "a write while it is open the way ReadAllText opens it");
                Assert.Equal("{}", AtomicFile.ReadAllText(path), "and the new text reads back");
            }
        }
    }
}
