using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Runs every public static void method on a class marked [Tests], and
    /// doubles as the child process the two-process tests start.
    /// </summary>
    internal static class Program
    {
        /// <summary>Parent of every registry key the tests create; deleted at the end.</summary>
        internal static readonly string RegistryRoot =
            @"Software\SupervertalerCoreTests\" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Where a test with its own data root keeps its locked key file. Child
        /// processes find it from the root they are given.
        /// </summary>
        internal static string LockedPathFor(string root) => Path.Combine(root, "appdata", "api-keys.locked.json");

        private static int Main(string[] args)
        {
            // No test, and no child process a test starts, may touch the user's own
            // locked key file. It is in %APPDATA%, which redirecting the data root
            // does not move, so it is redirected here for the whole run, before
            // anything else; a test that uses it points it somewhere of its own.
            var runDir = Path.Combine(Path.GetTempPath(), "sv-core-tests-" + Guid.NewGuid().ToString("N"));
            var runLocked = Path.Combine(runDir, "api-keys.locked.json");
            ApiKeyStore.RedirectLockedFile(runLocked);

            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
                return Child.Run(args);

            var realKeys = RealKeyFiles.Look();

            var filter = args.Length > 0 ? args[0] : null;
            var logs = new List<string>();
            SupervertalerLicence.Log = m => { lock (logs) logs.Add(m); };

            var tests = typeof(Program).Assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<TestsAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
                .Where(m => filter == null || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            int failed = 0;
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (var test in tests)
                {
                    var name = test.DeclaringType.Name + "." + test.Name;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        test.Invoke(null, null);
                        Console.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)");
                    }
                    catch (TargetInvocationException tie)
                    {
                        failed++;
                        Console.WriteLine($"FAIL  {name}");
                        Console.WriteLine("      " + tie.InnerException?.Message);
                        if (!(tie.InnerException is AssertFailed))
                            Console.WriteLine(tie.InnerException?.StackTrace);
                    }

                    // A test that moved the locked file must put it back, or the
                    // next one could reach the real file.
                    if (ApiKeyStore.LockedFilePath != runLocked)
                    {
                        failed++;
                        Console.WriteLine($"FAIL  {name} left the locked key file pointing at {ApiKeyStore.LockedFilePath}");
                        ApiKeyStore.RedirectLockedFile(runLocked);
                    }
                }
            }
            finally
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(RegistryRoot, false);
                    using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\SupervertalerCoreTests"))
                    {
                        if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                        {
                            parent.Close();
                            Registry.CurrentUser.DeleteSubKey(@"Software\SupervertalerCoreTests", false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("WARNING: could not remove test registry keys: " + ex.Message);
                }
                try { Directory.Delete(runDir, true); } catch { }
            }

            // The run as a whole: the user's own key files exactly as they were.
            var changed = realKeys.ChangedSince();
            if (changed == null)
            {
                Console.WriteLine("PASS  Program.RealKeyFilesUntouched");
            }
            else
            {
                failed++;
                Console.WriteLine("FAIL  Program.RealKeyFilesUntouched");
                Console.WriteLine("      " + changed);
            }

            Console.WriteLine();
            Console.WriteLine($"{tests.Count + 1 - failed} passed, {failed} failed, {clock.Elapsed.TotalSeconds:F1} s");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>Starts copies of this program and returns what each printed.</summary>
        internal static string[] RunChildren(int count, Func<int, string> argsFor, string goFile)
        {
            var exe = Assembly.GetExecutingAssembly().Location;
            var procs = Enumerable.Range(0, count).Select(i => Process.Start(new ProcessStartInfo(exe, argsFor(i))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })).ToList();

            // Let every child reach its starting line before any of them runs,
            // so the race actually happens.
            Thread.Sleep(300);
            File.WriteAllText(goFile, "go");

            var outputs = procs.Select(p => p.StandardOutput.ReadToEndAsync()).ToList();
            foreach (var p in procs)
            {
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { }
                    throw new AssertFailed("a child process did not finish within 60 s");
                }
            }
            return outputs.Select(o => o.Result.Trim()).ToArray();
        }
    }

    /// <summary>
    /// The user's own API key files - the plain one in the real data folder, the
    /// locked one in the real %APPDATA% - and anything beside them named like them
    /// (a temporary file, a set-aside copy). Seen by name, size and timestamps
    /// only, never opened, so the run reads no real key. Any difference after the
    /// run means a test reached one; or a product saved a key meanwhile, in which
    /// case run again.
    /// </summary>
    internal sealed class RealKeyFiles
    {
        private readonly List<string> _folders;
        private readonly Dictionary<string, string> _seen;

        private RealKeyFiles(List<string> folders)
        {
            _folders = folders;
            _seen = Snapshot(folders);
        }

        public static RealKeyFiles Look()
        {
            var folders = new List<string>();
            SupervertalerPaths.Reset();
            try { folders.Add(Path.Combine(SupervertalerPaths.Root, "settings")); }
            catch { }
            SupervertalerPaths.Reset();
            var locked = ApiKeyStore.DefaultLockedFilePath;
            if (locked != null) folders.Add(Path.GetDirectoryName(locked));
            return new RealKeyFiles(folders);
        }

        /// <summary>Null when nothing changed; otherwise which files did, by name.</summary>
        public string ChangedSince()
        {
            var now = Snapshot(_folders);
            var changes = new List<string>();
            foreach (var f in _seen.Keys.Union(now.Keys))
            {
                _seen.TryGetValue(f, out var before);
                now.TryGetValue(f, out var after);
                if (before != after)
                    changes.Add(f + (before == null ? " appeared" : after == null ? " disappeared" : " changed"));
            }
            return changes.Count == 0 ? null : string.Join("; ", changes);
        }

        private static Dictionary<string, string> Snapshot(List<string> folders)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "api-keys*"))
                {
                    var info = new FileInfo(file);
                    seen[file] = info.Length + "|" + info.CreationTimeUtc.Ticks + "|" + info.LastWriteTimeUtc.Ticks;
                }
            }
            return seen;
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class TestsAttribute : Attribute { }

    internal sealed class AssertFailed : Exception
    {
        public AssertFailed(string message) : base(message) { }
    }

    internal static class Assert
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new AssertFailed(what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertFailed($"{what}: expected {Show(expected)}, got {Show(actual)}");
        }

        private static string Show<T>(T value) =>
            value is DateTime d ? d.ToString("O") : value?.ToString() ?? "null";
    }

    /// <summary>
    /// A temporary data folder and registry key standing in for the real ones,
    /// laid out the same way: the Trados file under trados/settings, the
    /// Trados anchor one key below the shared one.
    /// </summary>
    internal sealed class Scene : IDisposable
    {
        private static int _counter;

        public readonly string Dir;
        public readonly string AnchorKey;

        public Scene()
        {
            var n = Interlocked.Increment(ref _counter);
            Dir = Path.Combine(Path.GetTempPath(), "sv-core-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            AnchorKey = Program.RegistryRoot + @"\scene" + n;
        }

        public string SharedPath => Path.Combine(Dir, "licence", "licence.json");
        public string LegacyPath => Path.Combine(Dir, "trados", "settings", "license.json");
        public string LegacyAnchorKey => AnchorKey + @"\Trados";
        public string GoFile => Path.Combine(Dir, "go");

        /// <summary>This computer and account's own file, when licence.json is someone else's.</summary>
        public string PersonalPath => LicenceFile.PersonalPath(SharedPath, Fingerprint);

        public static string Fingerprint => MachineId.GetFingerprint();

        // Nothing listens on the discard port, so a test that reaches for the
        // network fails at once rather than talking to the real licence server.
        private const string NoServer = "http://127.0.0.1:9/v1/licenses";

        public SupervertalerLicence Open(string licenceServer = NoServer) =>
            new SupervertalerLicence(SharedPath, LegacyPath, AnchorKey, LegacyAnchorKey, licenceServer);

        /// <summary>Writes a licence file the way the Trados plugin did: in place, with a BOM.</summary>
        public static void WriteTradosStyle(string path, LicenceRecord record)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Encoding.UTF8.GetString(LicenceFile.Serialise(record)), Encoding.UTF8);
        }

        public string[] TempFilesLeft()
        {
            var dir = Path.GetDirectoryName(SharedPath);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.tmp") : new string[0];
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(AnchorKey, false); } catch { }
        }
    }

    /// <summary>
    /// Until disposed, this process takes the computer's time zone to be a
    /// fixed <c>hours</c> from UTC, with no daylight saving. .NET Framework
    /// keeps the local zone in two places, TimeZone.CurrentTimeZone and
    /// TimeZoneInfo.Local, and both are replaced; disposing clears both, so the
    /// real zone is read again. Child processes are not affected.
    /// </summary>
    internal sealed class LocalTimeZone : IDisposable
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public LocalTimeZone(double hours)
        {
            var offset = TimeSpan.FromHours(hours);

            var legacy = TimeZone.CurrentTimeZone;
            legacy.GetType().GetField("m_ticksOffset", Hidden).SetValue(legacy, offset.Ticks);
            ((Hashtable)legacy.GetType().GetField("m_CachedDaylightChanges", Hidden).GetValue(legacy)).Clear();

            var cache = typeof(TimeZoneInfo).GetField("s_cachedData", Hidden).GetValue(null);
            var name = "UTC" + Show(hours);
            cache.GetType().GetField("m_localTimeZone", Hidden)
                .SetValue(cache, TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name));

            // Should a framework update move either, fail here rather than
            // test the real zone and pass.
            Assert.Equal(offset, TimeZone.CurrentTimeZone.GetUtcOffset(DateTime.MinValue), "TimeZone.CurrentTimeZone replaced");
            Assert.Equal(offset, TimeZoneInfo.Local.GetUtcOffset(DateTime.MinValue), "TimeZoneInfo.Local replaced");
        }

        /// <summary>"+3", "-5", "+5.5", "±0": for messages.</summary>
        public static string Show(double hours) => hours.ToString("+0.#;-0.#;±0", CultureInfo.InvariantCulture);

        public void Dispose() => CultureInfo.CurrentCulture.ClearCachedData();
    }
}
