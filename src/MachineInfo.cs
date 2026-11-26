using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Supervertaler.Core
{
    /// <summary>
    /// Two facts about the computer for the anonymous usage ping, shared by both
    /// plugins: which virtual machine Windows runs in, if any, and the real
    /// processor type. Together they show who runs Supervertaler on a Mac.
    ///
    /// <para>"parallels" is a Mac. "arm64" is almost always one too: Windows on
    /// an Apple Silicon Mac is Windows on ARM, whichever app runs it (Parallels,
    /// VMware Fusion, UTM); the exceptions are the few Snapdragon PCs. The VM
    /// name alone cannot say this for VMware or QEMU, which also run on PCs and
    /// servers.</para>
    ///
    /// <para>The processor type is the computer's, not the plugin's: Trados and
    /// memoQ are x86 or x64 programs, which Windows on ARM runs by emulation,
    /// and an emulated program is told the emulated processor. Until
    /// 18/19.20.210 the Trados ping sent that, so it never showed ARM.</para>
    ///
    /// <para>Both read local values only and never throw.</para>
    /// </summary>
    internal static class MachineInfo
    {
        /// <summary>
        /// "parallels", "vmware", "virtualbox", "hyper-v", "qemu", or "none".
        /// </summary>
        public static string VirtualizationHost()
        {
            try
            {
                // The hypervisor's virtual BIOS fills these in.
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    var host = Hypervisor(key?.GetValue("SystemManufacturer") as string,
                                          key?.GetValue("SystemProductName") as string);
                    if (host != "none") return host;
                }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Parallels\Parallels Tools"))
                    if (key != null) return "parallels";
            }
            catch { }
            return "none";
        }

        /// <summary>The hypervisor named by the BIOS manufacturer and product, or "none".</summary>
        internal static string Hypervisor(string manufacturer, string product)
        {
            var combined = ((manufacturer ?? "") + " " + (product ?? "")).ToLowerInvariant();
            if (combined.Contains("parallels")) return "parallels";
            if (combined.Contains("vmware")) return "vmware";
            if (combined.Contains("virtualbox")) return "virtualbox";
            if (combined.Contains("microsoft") && combined.Contains("virtual")) return "hyper-v";
            if (combined.Contains("qemu")) return "qemu";
            return "none";
        }

        /// <summary>
        /// The computer's own processor type: "arm64", "amd64", "x86", or
        /// "unknown". IsWow64Process2 (Windows 10 1709 and later) reports it even
        /// to an emulated program; the environment is the fallback for older
        /// Windows, where there is no ARM to miss.
        /// </summary>
        public static string NativeArchitecture()
        {
            try
            {
                if (IsWow64Process2(GetCurrentProcess(), out _, out var native))
                {
                    var name = MachineName(native);
                    if (name != "unknown") return name;
                }
            }
            catch { /* EntryPointNotFoundException before Windows 10 1709 */ }

            try
            {
                return FromEnvironment(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"),
                                       Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"));
            }
            catch { return "unknown"; }
        }

        /// <summary>An IMAGE_FILE_MACHINE value as a name.</summary>
        internal static string MachineName(ushort machine)
        {
            switch (machine)
            {
                case 0xAA64: return "arm64";
                case 0x8664: return "amd64";
                case 0x014C: return "x86";
                default: return "unknown";
            }
        }

        /// <summary>
        /// The processor type from the environment: a 32-bit program on 64-bit
        /// Windows finds the computer's in PROCESSOR_ARCHITEW6432.
        /// </summary>
        internal static string FromEnvironment(string wow64, string arch)
        {
            var value = !string.IsNullOrEmpty(wow64) ? wow64 : arch;
            return string.IsNullOrEmpty(value) ? "unknown" : value.ToLowerInvariant();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    }
}
