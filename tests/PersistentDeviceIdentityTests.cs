using System;
using PcPulse;

namespace PcPulseTests
{
    internal static class PersistentDeviceIdentityTests
    {
        internal static void Run()
        {
            string first = PersistentDeviceIdentity.Disk(" AB123  ", "Example SSD", 512000000000UL);
            Require(first.Length == 64 && !first.Contains("AB123"), "Disk serial is hashed locally");
            Require(first == PersistentDeviceIdentity.Disk("ab123", "EXAMPLE SSD", 512000000000UL), "Driver padding and casing do not replace the disk");
            Require(first != PersistentDeviceIdentity.Disk("AB124", "Example SSD", 512000000000UL), "An identical replacement model has a different identity");
            foreach (string unknown in new[] { "", "000000", "FFFFFFFF", "Unknown", "N/A", "Default String" })
            { Require(PersistentDeviceIdentity.Disk(unknown, "Example SSD", 512000000000UL) == "", "Placeholder serial does not establish identity"); }
            Require(PersistentDeviceIdentity.Disk("AB123", "Example SSD", null) == "", "Incomplete disk identity remains unknown");
            Require(PersistentDeviceIdentity.Gpu(0x8086, 0x46A6, 0x8965103C, 0x0C) == "PCI:VEN_8086:DEV_46A6:SUBSYS_8965103C:REV_0C", "GPU model identity is independent of its transient LUID and enumeration index");
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
