using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// A stable fingerprint for this computer and Windows account, used to bind
    /// a licence activation and to key the trial anchor.
    ///
    /// It contains nothing product-specific, which is what makes one licence
    /// work for every Supervertaler product: an activation is a computer, not a
    /// product install. Moved here unchanged from the Trados plugin. Any change
    /// to what goes into it re-keys every existing activation and trial anchor,
    /// so it must not change.
    /// </summary>
    internal static class MachineId
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer, int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

        /// <summary>
        /// Returns a stable SHA256 fingerprint string for this machine + user combination.
        /// </summary>
        public static string GetFingerprint()
        {
            try
            {
                var sb = new StringBuilder();

                sb.Append(Environment.MachineName ?? "");
                sb.Append("|");

                try
                {
                    var identity = WindowsIdentity.GetCurrent();
                    sb.Append(identity?.User?.Value ?? "");
                }
                catch
                {
                    sb.Append("no-sid");
                }
                sb.Append("|");

                try
                {
                    var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var root = System.IO.Path.GetPathRoot(systemDrive) ?? @"C:\";

                    if (GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0))
                    {
                        sb.Append(serial.ToString("X8"));
                    }
                    else
                    {
                        sb.Append("no-vol");
                    }
                }
                catch
                {
                    sb.Append("no-vol");
                }

                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    var hex = new StringBuilder(64);
                    foreach (var b in bytes)
                        hex.Append(b.ToString("x2"));
                    return hex.ToString();
                }
            }
            catch
            {
                // Absolute fallback – still deterministic per machine name
                return "fallback-" + (Environment.MachineName ?? "unknown").GetHashCode().ToString("x8");
            }
        }
    }
}
