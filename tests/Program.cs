using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
                return Child.Run(args);

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
            }

            Console.WriteLine();
            Console.WriteLine($"{tests.Count - failed} passed, {failed} failed, {clock.Elapsed.TotalSeconds:F1} s");
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

        public static string Fingerprint => MachineId.GetFingerprint();

        public SupervertalerLicence Open() =>
            new SupervertalerLicence(SharedPath, LegacyPath, AnchorKey, LegacyAnchorKey);

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
}
