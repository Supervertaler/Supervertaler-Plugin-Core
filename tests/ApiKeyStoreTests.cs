using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The shared key file must never be left empty or half-written, and two
    /// products saving keys at once must not lose either's key. Until 203 a save
    /// truncated the file and wrote it again in place, so a crash or a power cut
    /// between the two left it empty - every provider's key gone at once.
    /// </summary>
    [Tests]
    internal static class ApiKeyStoreTests
    {
        private sealed class Root : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "sv-keys-" + Guid.NewGuid().ToString("N"));
            public Root() { Directory.CreateDirectory(Dir); SupervertalerPaths.Set(Dir); }
            public string Settings => Path.Combine(Dir, "settings");
            public void Dispose()
            {
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
                Assert.Equal(0, Directory.GetFiles(r.Settings, "*.tmp").Length, "no temporary file left");
            }
        }

        /// <summary>
        /// A save that cannot get the file - someone holds it without sharing -
        /// reports failure and leaves the file exactly as it was.
        /// </summary>
        public static void ASet_ThatCannotGetTheFile_FailsAndLeavesItUnchanged()
        {
            using (var r = new Root())
            {
                ApiKeyStore.Set("openai", "sk-old");
                var before = File.ReadAllBytes(ApiKeyStore.FilePath);
                using (new FileStream(ApiKeyStore.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.True(!ApiKeyStore.Set("openai", "sk-new"), "reported as not written");
                Assert.True(File.ReadAllBytes(ApiKeyStore.FilePath).SequenceEqual(before), "file unchanged");
                Assert.Equal(0, Directory.GetFiles(r.Settings, "*.tmp").Length, "no temporary file left");
            }
        }

        /// <summary>
        /// Kills a process in the middle of saving keys, over and over. Whenever it
        /// dies, the file must still hold every key: the old set or the new one.
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
                }
            }
        }

        /// <summary>
        /// Two products saving different keys at the same time: the file is read,
        /// changed and written under one lock, so neither loses the other's keys.
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
                for (int w = 0; w < 2; w++)
                    for (int n = 0; n < each; n++)
                        Assert.True(map.ContainsKey("w" + w + "_" + n), "key w" + w + "_" + n + " kept");
            }
        }
    }
}
