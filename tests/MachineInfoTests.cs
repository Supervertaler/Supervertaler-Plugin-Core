namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// What the usage ping says about the computer: the VM host from the BIOS
    /// names, and the real processor type, which must be the computer's even
    /// when Windows on ARM emulates the plugin's processor.
    /// </summary>
    [Tests]
    internal static class MachineInfoTests
    {
        public static void Hypervisor_NamesEachKnownHost()
        {
            Assert.Equal("parallels", MachineInfo.Hypervisor("Parallels International GmbH.", "Parallels ARM Virtual Machine"), "Parallels on Apple Silicon");
            Assert.Equal("parallels", MachineInfo.Hypervisor("Parallels Software International Inc.", "Parallels Virtual Platform"), "Parallels on an Intel Mac");
            Assert.Equal("vmware", MachineInfo.Hypervisor("VMware, Inc.", "VMware20,1"), "VMware");
            Assert.Equal("virtualbox", MachineInfo.Hypervisor("innotek GmbH", "VirtualBox"), "VirtualBox");
            Assert.Equal("hyper-v", MachineInfo.Hypervisor("Microsoft Corporation", "Virtual Machine"), "Hyper-V");
            Assert.Equal("qemu", MachineInfo.Hypervisor("QEMU", "QEMU Virtual Machine"), "QEMU, as UTM reports it");
        }

        public static void Hypervisor_IsNoneForARealComputerOrNothingAtAll()
        {
            Assert.Equal("none", MachineInfo.Hypervisor("Dell Inc.", "XPS 15 9530"), "a real PC");
            Assert.Equal("none", MachineInfo.Hypervisor("Microsoft Corporation", "Surface Laptop 7"), "a Surface is Microsoft but not virtual");
            Assert.Equal("none", MachineInfo.Hypervisor(null, null), "no BIOS key");
        }

        public static void MachineName_MapsTheImageFileMachineValues()
        {
            Assert.Equal("arm64", MachineInfo.MachineName(0xAA64), "ARM64");
            Assert.Equal("amd64", MachineInfo.MachineName(0x8664), "x64");
            Assert.Equal("x86", MachineInfo.MachineName(0x014C), "x86");
            Assert.Equal("unknown", MachineInfo.MachineName(0), "IMAGE_FILE_MACHINE_UNKNOWN");
        }

        public static void FromEnvironment_PrefersTheComputersOwnValue()
        {
            Assert.Equal("amd64", MachineInfo.FromEnvironment("AMD64", "x86"), "32-bit program on 64-bit Windows");
            Assert.Equal("arm64", MachineInfo.FromEnvironment(null, "ARM64"), "native program");
            Assert.Equal("unknown", MachineInfo.FromEnvironment("", null), "nothing set");
        }

        /// <summary>On this computer the real call agrees with Windows' own answer.</summary>
        public static void NativeArchitecture_IsAKnownValueHere()
        {
            var arch = MachineInfo.NativeArchitecture();
            Assert.True(arch == "amd64" || arch == "arm64" || arch == "x86", "got " + arch);
            var expected = System.Environment.Is64BitOperatingSystem ? new[] { "amd64", "arm64" } : new[] { "x86" };
            Assert.True(System.Array.IndexOf(expected, arch) >= 0, "matches the OS bitness: " + arch);
        }

        public static void VirtualizationHost_NeverThrows()
        {
            var host = MachineInfo.VirtualizationHost();
            Assert.True(!string.IsNullOrEmpty(host), "a value: " + host);
        }
    }
}
