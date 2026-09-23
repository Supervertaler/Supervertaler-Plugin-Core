using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// What a child process does when a two-process test starts one. Each mode
    /// waits for the parent's "go" file, does its part, and prints one line.
    /// </summary>
    internal static class Child
    {
        public static int Run(string[] a)
        {
            try
            {
                switch (a[0])
                {
                    case "--race-create": return RaceCreate(a[1], a[2], a[3]);
                    case "--hammer": return Hammer(a[1], a[2], a[3], int.Parse(a[4]));
                    case "--anchor-hammer": return AnchorHammer(a);
                    case "--open": return Open(a[1], a[2], a[3], a[4], a[5]);
                    default:
                        Console.WriteLine("unknown mode " + a[0]);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
                return 3;
            }
        }

        private static void WaitFor(string goFile)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(goFile))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("no go signal");
                Thread.Sleep(1);
            }
        }

        /// <summary>One only-if-absent write of this child's own content.</summary>
        private static int RaceCreate(string path, string goFile, string id)
        {
            var bytes = LicenceFile.Serialise(new LicenceRecord { VariantName = "writer-" + id });
            WaitFor(goFile);
            Console.WriteLine(LicenceFile.Write(path, bytes, replaceExisting: false));
            return 0;
        }

        /// <summary>
        /// Writes and reads the same file as fast as it can for a while. Records
        /// vary in length, so a torn read cannot parse by luck.
        /// </summary>
        private static int Hammer(string path, string goFile, string id, int ms)
        {
            var rnd = new Random(id.GetHashCode());
            int written = 0, failed = 0, ok = 0, damaged = 0, unreadable = 0, missing = 0;

            WaitFor(goFile);
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                var record = new LicenceRecord
                {
                    VariantName = id + ":" + new string('x', rnd.Next(0, 4000)),
                    LicenseKey = "KEY-" + id,
                };
                if (LicenceFile.Write(path, LicenceFile.Serialise(record), replaceExisting: true)
                        == LicenceFile.WriteStatus.Written)
                    written++;
                else
                    failed++;

                switch (LicenceFile.TryRead(path, out var read, out _))
                {
                    case LicenceFile.ReadStatus.Ok:
                        // A whole record from one writer: its key and its name agree.
                        if (read.LicenseKey == "KEY-" + read.VariantName.Split(':')[0]) ok++;
                        else damaged++;
                        break;
                    case LicenceFile.ReadStatus.Damaged: damaged++; break;
                    case LicenceFile.ReadStatus.Unreadable: unreadable++; break;
                    default: missing++; break;
                }
            }

            Console.WriteLine($"written={written} failed={failed} ok={ok} damaged={damaged} unreadable={unreadable} missing={missing}");
            return 0;
        }

        /// <summary>
        /// Reconciles the anchor over and over with values that would move it
        /// the wrong way, checking after each write that neither value has moved
        /// past where it started in its forbidden direction.
        /// </summary>
        private static int AnchorHammer(string[] a)
        {
            var sharedKey = a[1];
            var legacyKey = a[2];
            var goFile = a[3];
            var iterations = int.Parse(a[4]);
            var start0 = new DateTime(long.Parse(a[5], CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var seen0 = new DateTime(long.Parse(a[6], CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var rnd = new Random(int.Parse(a[7]));
            var fp = MachineId.GetFingerprint();

            int violations = 0, unreadable = 0;
            WaitFor(goFile);
            for (int i = 0; i < iterations; i++)
            {
                // Half the calls legitimately move a value (start earlier, seen
                // later); half try to move each the forbidden way.
                var candidate = i % 2 == 0
                    ? start0.AddDays(rnd.Next(1, 30))
                    : start0.AddMinutes(-rnd.Next(1, 600));
                var now = i % 2 == 0
                    ? seen0.AddDays(-rnd.Next(1, 30))
                    : seen0.AddMinutes(rnd.Next(1, 600));

                TrialAnchor.Reconcile(sharedKey, legacyKey, fp, candidate, now);

                var stored = TrialAnchor.Read(sharedKey, fp);
                if (!stored.HasValue) { unreadable++; continue; }
                if (stored.Value.Start > start0 || stored.Value.Seen < seen0) violations++;
            }

            Console.WriteLine($"violations={violations} unreadable={unreadable}");
            return 0;
        }

        /// <summary>Starts the licence exactly as a product does, and reports what it found.</summary>
        private static int Open(string shared, string legacy, string anchorKey, string legacyAnchorKey, string goFile)
        {
            WaitFor(goFile);
            var licence = new SupervertalerLicence(shared, legacy, anchorKey, legacyAnchorKey);
            Console.WriteLine(licence.State + " " + licence.TrialEndsUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + " " + licence.HasKey);
            return 0;
        }
    }
}
