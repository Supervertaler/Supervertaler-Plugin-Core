using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The shared key files must never be left empty or half-written, and two
    /// products saving keys at once must not lose either's key. Until 203 a save
    /// truncated the file and wrote it again in place, so a crash or a power cut
    /// between the two left it empty - every provider's key gone at once. Each
    /// save now writes the plain file and then the locked one, and both are held
    /// to the same standard.
    /// </summary>
    [Tests]
    internal static class ApiKeyStoreTests
    {
        private sealed class Root : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "sv-keys-" + Guid.NewGuid().ToString("N"));
            private readonly string _previousLocked = ApiKeyStore.LockedFilePath;

            public Root()
            {
                Directory.CreateDirectory(Dir);
                SupervertalerPaths.Set(Dir);
                ApiKeyStore.RedirectLockedFile(Program.LockedPathFor(Dir));
            }

            public string Settings => Path.Combine(Dir, "settings");
            public string LockedPath => Program.LockedPathFor(Dir);

            public void Dispose()
            {
                ApiKeyStore.RedirectLockedFile(_previousLocked);
                SupervertalerPaths.Reset();
                try { Directory.Delete(Dir, true); } catch { }
            }
        }

        public static void ASet_IsWritten_AndLeavesNoTemporaryFile()
        {
            using (var r = new Root())
            {
                Assert.True(ApiKeyStore.Set("openai", "sk-one"), "written");
                Assert.True(ApiKeyStore.Set("claude", "sk-ant-two"), "second key written");
                Assert.Equal("sk-one", ApiKeyStore.Get("openai"), "first key read back");
                Assert.Equal("sk-ant-two", ApiKeyStore.Get("claude"), "second key read back");
                Assert.Equal("sk-one", Locked.Unlock(r.LockedPath, "openai"), "first key locked too");
                Assert.Equal("sk-ant-two", Locked.Unlock(r.LockedPath, "claude"), "second key locked too");
                Assert.Equal(0, Directory.GetFiles(r.Settings, "*.tmp").Length, "no temporary file left");
                Assert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(r.LockedPath), "*.tmp").Length, "no temporary locked file left");
            }
        }

        /// <summary>
        /// A save that cannot get the file - someone holds it without sharing -
        /// reports failure and leaves the file exactly as it was. Nor does it go on
        /// to the locked file: a locked key newer than the plain one would be
        /// overwritten by the next sync.
        /// </summary>
        public static void ASet_ThatCannotGetTheFile_FailsAndLeavesItUnchanged()
        {
            using (var r = new Root())
            {
                ApiKeyStore.Set("openai", "sk-old");
                var before = File.ReadAllBytes(ApiKeyStore.FilePath);
                var lockedBefore = File.ReadAllBytes(r.LockedPath);
                using (new FileStream(ApiKeyStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.True(!ApiKeyStore.Set("openai", "sk-new"), "reported as not written");
                Assert.True(File.ReadAllBytes(ApiKeyStore.FilePath).SequenceEqual(before), "file unchanged");
                Assert.True(File.ReadAllBytes(r.LockedPath).SequenceEqual(lockedBefore), "locked file unchanged");
                Assert.Equal(0, Directory.GetFiles(r.Settings, "*.tmp").Length, "no temporary file left");
            }
        }

        /// <summary>
        /// Kills a process in the middle of saving keys, over and over - in the
        /// plain write, the locked write, or between the two. Whenever it dies, both
        /// files must still hold every key, each a whole old or new value, and the
        /// locked file must open; the next sync brings the locked key level with
        /// the plain one.
        /// </summary>
        public static void KillingAWriterMidSave_NeverLosesTheKeys()
        {
            using (var r = new Root())
            {
                ApiKeyStore.Set("claude", "sk-ant-stays");
                var exe = Assembly.GetExecutingAssembly().Location;
                var rnd = new Random(7);
                const int kills = 25;
                for (int i = 0; i < kills; i++)
                {
                    var p = Process.Start(new ProcessStartInfo(exe, $"--keys-forever \"{r.Dir}\"")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    });
                    // Wait for it to be writing, then kill it at a random moment.
                    var started = p.StandardOutput.ReadLine();
                    Assert.Equal("writing", started, "child " + i + " started writing");
                    Thread.Sleep(rnd.Next(5, 60));
                    try { p.Kill(); } catch { }
                    p.WaitForExit(10000);

                    var map = ApiKeyStore.Load();
                    Assert.Equal("sk-ant-stays", map.TryGetValue("claude", out var c) ? c : null,
                        "after kill " + (i + 1) + ", the untouched key is still there");
                    Assert.True(map.TryGetValue("openai", out var o) && o.StartsWith("sk-"),
                        "after kill " + (i + 1) + ", the key being saved is a whole old or new value");

                    var locked = Locked.Entries(r.LockedPath);   // throws if the file does not parse
                    Assert.Equal("sk-ant-stays", Locked.Unlock(r.LockedPath, "claude"),
                        "after kill " + (i + 1) + ", the untouched locked key is still there");
                    if (locked.ContainsKey("openai"))
                        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(Locked.Unlock(r.LockedPath, "openai") ?? "", "^sk-k+$"),
                            "after kill " + (i + 1) + ", the locked key being saved is a whole old or new value");
                }

                Assert.True(!ApiKeyStore.SyncLocked().Failed, "the sync after the kills");
                Assert.Equal(ApiKeyStore.Load()["openai"], Locked.Unlock(r.LockedPath, "openai"),
                    "after the sync, the locked key is the plain one");
            }
        }

        /// <summary>
        /// Two products saving different keys at the same time: each file is read,
        /// changed and written under one lock, so neither loses the other's keys -
        /// in the plain file or the locked one.
        /// </summary>
        public static void TwoWritersAtOnce_LoseNoKey()
        {
            using (var r = new Root())
            {
                const int each = 40;
                var go = Path.Combine(r.Dir, "go");
                var results = Program.RunChildren(2, i => $"--keys-loop \"{r.Dir}\" \"{go}\" w{i} {each}", go);
                Assert.True(results.All(x => x == "failed=0"), "every save written: " + string.Join(" / ", results));

                var map = ApiKeyStore.Load();
                var locked = Locked.Entries(r.LockedPath);
                for (int w = 0; w < 2; w++)
                    for (int n = 0; n < each; n++)
                    {
                        Assert.True(map.ContainsKey("w" + w + "_" + n), "key w" + w + "_" + n + " kept");
                        Assert.True(locked.ContainsKey("w" + w + "_" + n), "locked key w" + w + "_" + n + " kept");
                        Assert.Equal("v" + n, Locked.Unlock(r.LockedPath, "w" + w + "_" + n), "locked key w" + w + "_" + n + " opens");
                    }
            }
        }

        /// <summary>
        /// A product saving keys while another starts up and syncs: the sync holds
        /// the plain file while it writes the locked one, and the saver waits for
        /// it. No save fails, nothing deadlocks, and after a last sync every key is
        /// in both files.
        /// </summary>
        public static void ASaveAndASyncAtOnce_LoseNoKey()
        {
            using (var r = new Root())
            {
                const int each = 40;
                var go = Path.Combine(r.Dir, "go");
                var results = Program.RunChildren(2, i => i == 0
                    ? $"--keys-loop \"{r.Dir}\" \"{go}\" w0 {each}"
                    : $"--sync-loop \"{r.Dir}\" \"{go}\" 15", go);
                Assert.True(results.All(x => x == "failed=0"), "every save and sync went through: " + string.Join(" / ", results));

                Assert.True(!ApiKeyStore.SyncLocked().Failed, "a last sync");
                var map = ApiKeyStore.Load();
                for (int n = 0; n < each; n++)
                {
                    Assert.Equal("v" + n, map.TryGetValue("w0_" + n, out var v) ? v : null, "plain key w0_" + n);
                    Assert.Equal("v" + n, Locked.Unlock(r.LockedPath, "w0_" + n), "locked key w0_" + n);
                }
                Assert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(r.LockedPath), "*.damaged-*").Length, "nothing taken for damaged");
            }
        }
    }
}
