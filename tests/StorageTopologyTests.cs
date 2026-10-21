using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PcPulse;

namespace PcPulseTests
{
    public static class StorageTopologyTests
    {
        public static int Main()
        {
            try
            {
                Types();
                Volumes();
                RuntimeBinding();
                DiskSpdBenchmarkTests.Results();
                DiskSpdBenchmarkTests.CancellationAndTimeout();
                DiskSpdBenchmarkTests.PreparationAndBoundaries();
                Console.WriteLine("PASS: 6 storage compatibility and DiskSpd test groups; no benchmark workload executed.");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void Types()
        {
            var disks = new List<DiskSnapshot>
            {
                new DiskSnapshot { Index = 0 }, new DiskSnapshot { Index = 4 },
                new DiskSnapshot { Index = 7 }, new DiskSnapshot { Index = 8 },
                new DiskSnapshot { Index = 9 }, new DiskSnapshot { Index = 10 }
            };
            var windows = new[]
            {
                WindowsDisk(0, 17, "nvme-id"), WindowsDisk(4, 11, "sata-id"),
                WindowsDisk(7, 16, "virtual-pool"), WindowsDisk(8, 7, "usb-id"),
                WindowsDisk(9, 13, "mmc-id"), WindowsDisk(10, 8, "raid-id")
            };
            var physical = new[]
            {
                PhysicalDisk("4", "NVME-ID", 4), PhysicalDisk("0", "SATA-ID", 3),
                PhysicalDisk("7", "VIRTUAL-POOL", 4), PhysicalDisk("other-id", "USB-ID", 4),
                PhysicalDisk("9", "MMC-ID", 0), PhysicalDisk("10", "RAID-ID", 4)
            };
            StorageTopology.ApplyDiskTypes(disks, windows, physical);
            Equal("SSD · NVMe", disks[0].MediaType, "PhysicalDisk.DeviceId must not be mistaken for the Windows disk number.");
            Equal("HDD · SATA", disks[1].MediaType, "Media joins use unique identifiers, independent of enumeration order.");
            Equal("Тип не определён · Storage Spaces", disks[2].MediaType, "A virtual pool does not inherit an underlying member SSD type.");
            Equal("SSD · USB", disks[3].MediaType, "Fixed USB SSDs retain the reported transport and uniquely matched medium.");
            Equal("Тип не определён · MMC", disks[4].MediaType, "MMC is visible without pretending to have SSD endurance semantics.");
            Equal("Тип не определён · RAID", disks[5].MediaType, "A hardware RAID logical disk is not labelled as one physical member.");

            StorageTopology.ApplyDiskTypes(disks, windows, physical.Concat(new[] { PhysicalDisk("99", "nvme-id", 3) }));
            Equal("Тип не определён · NVMe", disks[0].MediaType, "Conflicting provider identities do not guess the media.");
            StorageTopology.ApplyDiskTypes(disks, windows.Concat(new[] { WindowsDisk(99, 11, "sata-id") }), physical);
            Equal("Тип не определён · SATA", disks[1].MediaType, "Duplicated OS unique IDs cannot identify a particular physical drive.");
            windows[0]["UniqueId"] = "00000000";
            physical[0]["UniqueId"] = "00000000";
            StorageTopology.ApplyDiskTypes(disks, windows, physical);
            Equal("Тип не определён · NVMe", disks[0].MediaType, "Placeholder unique IDs cannot supply a match.");
            Equal("SAS", StorageTopology.BusName(10), "SAS transport recognized.");
            Equal("VHD", StorageTopology.BusName(15), "File-backed virtualization remains explicit.");
        }

        public static void Volumes()
        {
            var disks = new List<DiskSnapshot>
            {
                new DiskSnapshot { Index = 0 }, new DiskSnapshot { Index = 1 }, new DiskSnapshot { Index = 2 }, new DiskSnapshot { Index = 3 }
            };
            var partitions = new[] { Partition("p0", 0), Partition("p1", 1), Partition("p2", 2), Partition("p3", 3) };
            var links = new[]
            {
                Link("p0", "C:"), Link("p0", "D:"), Link("p0", "E:"), Link("p1", "E:"),
                Link("p2", "F:"), Link("p3", "G:"), Link("p3", "H:"), Link("p3", "I:"), Link("p3", "J:")
            };
            var logical = new[]
            {
                Volume("C:", 3, "NTFS", 1024L * 1024 * 1024), Volume("D:", 3, "NTFS", 10L * 1024 * 1024 * 1024),
                Volume("E:", 3, "ReFS", 100L * 1024 * 1024 * 1024), Volume("F:", 3, "NTFS", 20L * 1024 * 1024 * 1024),
                Volume("G:", 2, "NTFS", 10L * 1024 * 1024 * 1024), Volume("H:", 3, "exFAT", 10L * 1024 * 1024 * 1024),
                Volume("I:", 3, "FAT32", 10L * 1024 * 1024 * 1024), Volume("J:", 4, "NTFS", 10L * 1024 * 1024 * 1024)
            };
            Apply(disks, logical, partitions, links);
            Equal(@"D:\", disks[0].TestVolumeRoot, "A full system volume must not hide a testable second partition on the same disk.");
            Equal("C:, D:, E:", disks[0].Volumes, "All known volumes remain visible, including one not suitable for a per-drive test.");
            Equal("", disks[1].TestVolumeRoot, "A multi-disk volume cannot produce a speed attributed to a single member.");
            Equal(@"F:\", disks[2].TestVolumeRoot, "An existing fixed USB SSD uses the same safe volume rules.");
            Equal("", disks[3].TestVolumeRoot, "Removable, exFAT, FAT32 and network volumes are not silently enrolled in automatic writes.");

            logical[0]["FreeSpace"] = 3L * 1024 * 1024 * 1024;
            Apply(disks, logical, partitions, links);
            Equal(@"C:\", disks[0].TestVolumeRoot, "A suitable user temp/system volume keeps priority to avoid unnecessary root-directory permissions.");
            logical[0].Remove("FreeSpace");
            logical[1]["FreeSpace"] = 2L * 1024 * 1024 * 1024 - 1;
            Apply(disks, logical, partitions, links);
            Equal("", disks[0].TestVolumeRoot, "Unknown or insufficient free space cannot be assumed adequate.");
            logical[1]["FreeSpace"] = 2L * 1024 * 1024 * 1024;
            Apply(disks, logical, partitions, links.Concat(new[] { Link("p0", "D:") }));
            Equal(@"D:\", disks[0].TestVolumeRoot, "Duplicate associations to the same disk are harmless and are deduplicated.");
            Apply(disks, logical, partitions, links.Concat(new[] { Link("missing-partition", "D:") }));
            Equal("", disks[0].TestVolumeRoot, "Unresolved members make a logical disk ambiguous.");
            Apply(disks, logical, partitions.Concat(new[] { Partition("p0", 1) }), links);
            Equal("", disks[0].TestVolumeRoot, "Conflicting partition owners cannot authorize a benchmark.");
            Equal("", disks[1].TestVolumeRoot, "Conflicting partition mappings are rejected for both disks.");
        }

        public static void RuntimeBinding()
        {
            byte[] response = Extents(4, 4);
            uint[] disks = StorageTopology.ParseExtents(response, (uint)response.Length);
            Require(disks.SequenceEqual(new uint[] { 4 }), "Multiple extents on one physical disk remain supported.");
            StorageTopology.ValidateDiskBinding(disks, 4);
            Throws<InvalidOperationException>(delegate { StorageTopology.ValidateDiskBinding(disks, 0); }, "A reassigned drive letter must not select a different disk.");
            byte[] multiple = Extents(4, 7);
            Throws<InvalidOperationException>(delegate { StorageTopology.ValidateDiskBinding(StorageTopology.ParseExtents(multiple, (uint)multiple.Length), 4); }, "Mixed extents reject per-disk measurements.");
            Throws<IOException>(delegate { StorageTopology.ParseExtents(response, (uint)response.Length - 1); }, "Truncated IOCTL output is rejected.");
            Throws<IOException>(delegate { StorageTopology.ParseExtents(response, (uint)response.Length + 1); }, "Impossible returned buffer size is rejected.");
            Throws<IOException>(delegate { StorageTopology.ParseExtents(new byte[8], 8); }, "Zero extents are not a disk identity.");
            byte[] invalid = Extents(0);
            Array.Copy(BitConverter.GetBytes(-1L), 0, invalid, 16, 8);
            Throws<IOException>(delegate { StorageTopology.ParseExtents(invalid, (uint)invalid.Length); }, "Negative offsets are rejected.");
            invalid = Extents(0);
            Array.Copy(BitConverter.GetBytes(0L), 0, invalid, 24, 8);
            Throws<IOException>(delegate { StorageTopology.ParseExtents(invalid, (uint)invalid.Length); }, "Empty extents are rejected.");
            invalid = Extents(0);
            Array.Copy(BitConverter.GetBytes(uint.MaxValue), 0, invalid, 0, 4);
            Throws<IOException>(delegate { StorageTopology.ParseExtents(invalid, (uint)invalid.Length); }, "Unbounded extent counts are rejected.");
            foreach (string path in new[] { null, "", "C:", @"C:\folder\", @"\\.\PhysicalDrive0", @"\\server\share\", "#0" })
            {
                Throws<InvalidOperationException>(delegate { StorageTopology.NormalizeRoot(path); }, "Only an explicit local root can reach the IOCTL reader.");
            }
            Equal(@"D:\", StorageTopology.NormalizeRoot("d:/"), "Volume root normalized without resolving relative paths.");
            string identity = StorageTopology.CreateIdentity(@"SCSI\DISK&MODEL_SAMPLE\ABC");
            Require(identity.Length == 64 && !identity.Contains("MODEL_SAMPLE"), "Disk identity stores a hash, not raw device identifiers.");
            Equal(identity, StorageTopology.CreateIdentity(@"scsi\disk&model_sample\abc"), "Windows device identifiers compare case insensitively.");
            Equal("", StorageTopology.CreateIdentity(" "), "Missing identity remains missing.");
            StorageTopology.ValidateIdentity(identity, identity);
            Throws<InvalidOperationException>(delegate { StorageTopology.ValidateIdentity(identity, ""); }, "Missing live identity stops a previously identified disk.");
            Throws<InvalidOperationException>(delegate { StorageTopology.ValidateIdentity(identity, StorageTopology.CreateIdentity("replacement")); }, "Replacement hardware cannot reuse the captured disk identity.");
        }

        private static byte[] Extents(params uint[] disks)
        {
            byte[] bytes = new byte[8 + 24 * disks.Length];
            Array.Copy(BitConverter.GetBytes((uint)disks.Length), bytes, 4);
            for (int index = 0; index < disks.Length; index++)
            {
                int offset = 8 + 24 * index;
                Array.Copy(BitConverter.GetBytes(disks[index]), 0, bytes, offset, 4);
                Array.Copy(BitConverter.GetBytes(1048576L + index * 1048576L), 0, bytes, offset + 8, 8);
                Array.Copy(BitConverter.GetBytes(1048576L), 0, bytes, offset + 16, 8);
            }
            return bytes;
        }

        private static void Apply(List<DiskSnapshot> disks, IEnumerable<Dictionary<string, object>> logical,
            IEnumerable<Dictionary<string, object>> partitions, IEnumerable<Dictionary<string, object>> links)
        {
            StorageTopology.ApplyVolumes(disks, logical, partitions, links, @"C:\", @"C:\", 1024L * 1024 * 1024);
        }

        private static Dictionary<string, object> WindowsDisk(uint number, ulong bus, string identity) { return Row("Number", number, "BusType", bus, "UniqueId", identity, "UniqueIdFormat", 3); }
        private static Dictionary<string, object> PhysicalDisk(string deviceId, string identity, ulong media) { return Row("DeviceId", deviceId, "UniqueId", identity, "UniqueIdFormat", 3, "MediaType", media); }
        private static Dictionary<string, object> Partition(string name, uint index) { return Row("DeviceID", name, "DiskIndex", index); }
        private static Dictionary<string, object> Link(string partition, string volume) { return Row("Antecedent", "Win32_DiskPartition.DeviceID=\"" + partition + "\"", "Dependent", "Win32_LogicalDisk.DeviceID=\"" + volume + "\""); }
        private static Dictionary<string, object> Volume(string letter, uint type, string format, long free) { return Row("DeviceID", letter, "DriveType", type, "FileSystem", format, "FreeSpace", free); }
        private static Dictionary<string, object> Row(params object[] pairs)
        {
            var row = new Dictionary<string, object>();
            for (int index = 0; index < pairs.Length; index += 2) { row[(string)pairs[index]] = pairs[index + 1]; }
            return row;
        }
        private static void Require(bool value, string message) { if (!value) { throw new Exception(message); } }
        private static void Equal<T>(T expected, T actual, string message) { Require(object.Equals(expected, actual), message + " Expected=" + expected + "; actual=" + actual); }
        private static void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception(message);
        }
    }
}
