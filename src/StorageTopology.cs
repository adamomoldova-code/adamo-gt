using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace PcPulse
{
    internal static class StorageTopology
    {
        internal const long FreeSpaceReserveBytes = 1024L * 1024 * 1024;
        private const int MaximumExtents = 4096;

        internal static void ApplyDiskTypes(List<DiskSnapshot> disks, IEnumerable<Dictionary<string, object>> windowsDisks,
            IEnumerable<Dictionary<string, object>> physicalDisks)
        {
            var windows = (windowsDisks ?? new Dictionary<string, object>[0]).ToList();
            var physical = (physicalDisks ?? new Dictionary<string, object>[0]).ToList();
            foreach (DiskSnapshot disk in disks)
            {
                var matches = windows.Where(row => Unsigned(row, "Number") == disk.Index).ToList();
                if (matches.Count != 1) { continue; }
                Dictionary<string, object> source = matches[0];
                ulong? bus = Unsigned(source, "BusType");
                string type = "Тип не определён";
                string identity = UniqueIdentity(source);
                if (identity.Length > 0 && !IsVirtualOrArray(bus) && windows.Count(row => UniqueIdentity(row) == identity) == 1)
                {
                    var physicalMatches = physical.Where(row => UniqueIdentity(row) == identity).ToList();
                    if (physicalMatches.Count == 1)
                    {
                        ulong? media = Unsigned(physicalMatches[0], "MediaType");
                        type = media == 4 ? "SSD" : media == 3 ? "HDD" : media == 5 ? "SCM" : type;
                    }
                }
                string connection = BusName(bus);
                if (connection.Length == 0 && disk.MediaType.EndsWith(" · USB", StringComparison.Ordinal)) { connection = "USB"; }
                disk.MediaType = type + (connection.Length == 0 ? "" : " · " + connection);
            }
        }

        private static string UniqueIdentity(Dictionary<string, object> row)
        {
            string identifier = Text(row, "UniqueId").Trim().ToUpperInvariant();
            ulong? format = Unsigned(row, "UniqueIdFormat");
            if (!format.HasValue || identifier.Length == 0 || identifier == "UNKNOWN" || identifier == "NONE"
                || identifier == "N/A" || identifier == "NOT AVAILABLE" || identifier.All(character => character == '0' || character == '-' || character == ' '))
            {
                return "";
            }
            return format.Value.ToString(CultureInfo.InvariantCulture) + ":" + identifier;
        }

        private static bool IsVirtualOrArray(ulong? bus)
        {
            return bus == 8 || bus == 9 || bus == 14 || bus == 15 || bus == 16;
        }

        internal static string BusName(ulong? bus)
        {
            switch (bus)
            {
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "IEEE 1394";
                case 6: return "Fibre Channel";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Виртуальный диск";
                case 15: return "VHD";
                case 16: return "Storage Spaces";
                case 17: return "NVMe";
                default: return "";
            }
        }

        internal static void ApplyVolumes(List<DiskSnapshot> disks, IEnumerable<Dictionary<string, object>> logicalDisks,
            IEnumerable<Dictionary<string, object>> partitions, IEnumerable<Dictionary<string, object>> links,
            string temporaryRoot, string systemRoot, long requiredFileBytes)
        {
            if (requiredFileBytes <= 0 || requiredFileBytes > DiskSpdBenchmark.FileSizeBytes) { throw new ArgumentOutOfRangeException("requiredFileBytes"); }
            ulong requiredFree = (ulong)(requiredFileBytes + FreeSpaceReserveBytes);
            var partitionOwners = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in partitions ?? new Dictionary<string, object>[0])
            {
                string name = Text(row, "DeviceID");
                ulong? number = Unsigned(row, "DiskIndex");
                if (name.Length == 0 || !number.HasValue || number > uint.MaxValue) { continue; }
                HashSet<uint> owners;
                if (!partitionOwners.TryGetValue(name, out owners)) { owners = new HashSet<uint>(); partitionOwners.Add(name, owners); }
                owners.Add((uint)number.Value);
            }
            var volumeOwners = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
            var unresolvedVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in links ?? new Dictionary<string, object>[0])
            {
                string volume = ReferenceDeviceId(Text(row, "Dependent")).ToUpperInvariant();
                if (!IsDriveLetter(volume)) { continue; }
                HashSet<uint> owners;
                if (!partitionOwners.TryGetValue(ReferenceDeviceId(Text(row, "Antecedent")), out owners)) { unresolvedVolumes.Add(volume); continue; }
                HashSet<uint> current;
                if (!volumeOwners.TryGetValue(volume, out current)) { current = new HashSet<uint>(); volumeOwners.Add(volume, current); }
                current.UnionWith(owners);
            }
            var logical = (logicalDisks ?? new Dictionary<string, object>[0]).ToList();
            foreach (DiskSnapshot disk in disks)
            {
                disk.TestVolumeRoot = "";
                var names = volumeOwners.Where(pair => pair.Value.Contains(disk.Index)).Select(pair => pair.Key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                disk.Volumes = string.Join(", ", names.ToArray());
                var eligible = new List<VolumeCandidate>();
                foreach (string name in names)
                {
                    if (volumeOwners[name].Count != 1 || unresolvedVolumes.Contains(name)) { continue; }
                    var matching = logical.Where(row => string.Equals(Text(row, "DeviceID"), name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matching.Count != 1) { continue; }
                    Dictionary<string, object> volumeRow = matching[0];
                    ulong? free = Unsigned(volumeRow, "FreeSpace");
                    if (Unsigned(volumeRow, "DriveType") != 3 || !IsSupportedFileSystem(Text(volumeRow, "FileSystem")) || !free.HasValue || free < requiredFree) { continue; }
                    eligible.Add(new VolumeCandidate { Name = name, Free = free.Value });
                }
                VolumeCandidate selected = eligible.OrderByDescending(candidate => SameRoot(candidate.Name, temporaryRoot))
                    .ThenByDescending(candidate => SameRoot(candidate.Name, systemRoot)).ThenByDescending(candidate => candidate.Free)
                    .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (selected != null) { disk.TestVolumeRoot = selected.Name + "\\"; }
            }
        }

        private sealed class VolumeCandidate
        {
            internal string Name;
            internal ulong Free;
        }

        private static bool SameRoot(string letter, string root)
        {
            return string.Equals(letter, (root ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string ReferenceDeviceId(string reference)
        {
            Match match = Regex.Match(reference, "DeviceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\") : "";
        }

        private static bool IsDriveLetter(string name)
        {
            return name != null && name.Length == 2 && name[1] == ':' && ((name[0] >= 'A' && name[0] <= 'Z') || (name[0] >= 'a' && name[0] <= 'z'));
        }

        internal static string NormalizeRoot(string path)
        {
            if (path == null || path.Length != 3 || (path[2] != '\\' && path[2] != '/') || !IsDriveLetter(path.Substring(0, 2)))
            {
                throw new InvalidOperationException("Нет однозначно определённого локального тома для теста накопителя.");
            }
            return path.Substring(0, 2).ToUpperInvariant() + "\\";
        }

        internal static bool IsSupportedFileSystem(string fileSystem)
        {
            return string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) || string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ValidateVolume(string path, long requiredFileBytes)
        {
            string root = NormalizeRoot(path);
            if (requiredFileBytes <= 0 || requiredFileBytes > DiskSpdBenchmark.FileSizeBytes) { throw new ArgumentOutOfRangeException("requiredFileBytes"); }
            DriveInfo drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) { throw new InvalidOperationException("Для автоматического теста нужен доступный локальный фиксированный диск."); }
            if (!IsSupportedFileSystem(drive.DriveFormat)) { throw new InvalidOperationException("Автоматический тест поддерживает тома NTFS и ReFS."); }
            if (drive.AvailableFreeSpace < requiredFileBytes + FreeSpaceReserveBytes) { throw new IOException("Для теста недостаточно места: нужен тестовый файл и ещё 1 GB свободного пространства."); }
            return root;
        }

        internal static BenchmarkTarget OpenBenchmarkTarget(DiskSnapshot disk, long requiredFileBytes)
        {
            if (disk == null) { throw new ArgumentNullException("disk"); }
            string root = ValidateVolume(disk.TestVolumeRoot, requiredFileBytes);
            if (!string.IsNullOrEmpty(disk.StorageIdentity)) { ValidateIdentity(disk.StorageIdentity, ReadDiskIdentity(disk.Index)); }
            SafeFileHandle handle = OpenVolume(root);
            try
            {
                ValidateDiskBinding(ReadExtents(handle), disk.Index);
                return new BenchmarkTarget(root, handle);
            }
            catch { handle.Dispose(); throw; }
        }

        internal static string CreateIdentity(string plugAndPlayDeviceId)
        {
            if (string.IsNullOrWhiteSpace(plugAndPlayDeviceId)) { return ""; }
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] normalized = Encoding.UTF8.GetBytes(plugAndPlayDeviceId.Trim().ToUpperInvariant());
                return BitConverter.ToString(algorithm.ComputeHash(normalized)).Replace("-", "");
            }
        }

        private static string ReadDiskIdentity(uint diskIndex)
        {
            var options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(3) };
            using (var searcher = new ManagementObjectSearcher(new ManagementScope("root\\cimv2"),
                new ObjectQuery("SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = " + diskIndex.ToString(CultureInfo.InvariantCulture)), options))
            using (ManagementObjectCollection rows = searcher.Get())
            {
                string identity = "";
                int count = 0;
                foreach (ManagementObject row in rows)
                {
                    using (row) { count++; identity = CreateIdentity(Convert.ToString(row["PNPDeviceID"], CultureInfo.InvariantCulture)); }
                }
                return count == 1 ? identity : "";
            }
        }

        internal static void ValidateIdentity(string expected, string actual)
        {
            if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Состав накопителей изменился или идентификатор диска недоступен. Нажмите «Обновить» перед тестированием.");
            }
        }

        internal sealed class BenchmarkTarget : IDisposable
        {
            internal string VolumeRoot { get; private set; }
            private readonly SafeFileHandle handle;
            internal BenchmarkTarget(string root, SafeFileHandle volume) { VolumeRoot = root; handle = volume; }
            public void Dispose() { handle.Dispose(); }
        }

        internal static uint[] ReadVolumeDiskNumbers(string root)
        {
            using (SafeFileHandle handle = OpenVolume(NormalizeRoot(root))) { return ReadExtents(handle); }
        }

        private static SafeFileHandle OpenVolume(string root)
        {
            SafeFileHandle handle = CreateFile("\\\\.\\" + root.TrimEnd('\\'), 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid) { return handle; }
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Windows не подтвердила физический диск для выбранного тома (код " + error.ToString(CultureInfo.InvariantCulture) + "). Тест не запущен.");
        }

        private static uint[] ReadExtents(SafeFileHandle volume)
        {
            byte[] buffer = new byte[32];
            uint returned;
            if (!DeviceIoControl(volume, 0x00560000, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                uint count = returned >= 4 ? BitConverter.ToUInt32(buffer, 0) : 0;
                if (error != 234 || count < 2 || count > MaximumExtents) { throw ExtentsUnavailable(); }
                buffer = new byte[8 + 24 * count];
                if (!DeviceIoControl(volume, 0x00560000, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out returned, IntPtr.Zero)) { throw ExtentsUnavailable(); }
            }
            return ParseExtents(buffer, returned);
        }

        internal static uint[] ParseExtents(byte[] buffer, uint returned)
        {
            if (buffer == null || returned > buffer.Length || returned < 8) { throw ExtentsUnavailable(); }
            uint count = BitConverter.ToUInt32(buffer, 0);
            if (count == 0 || count > MaximumExtents || 8UL + 24UL * count > returned) { throw ExtentsUnavailable(); }
            HashSet<uint> disks = new HashSet<uint>();
            for (uint index = 0; index < count; index++)
            {
                int offset = (int)(8 + 24 * index);
                long start = BitConverter.ToInt64(buffer, offset + 8);
                long length = BitConverter.ToInt64(buffer, offset + 16);
                if (start < 0 || length <= 0 || start > long.MaxValue - length) { throw ExtentsUnavailable(); }
                disks.Add(BitConverter.ToUInt32(buffer, offset));
            }
            return disks.OrderBy(value => value).ToArray();
        }

        internal static void ValidateDiskBinding(uint[] actualDisks, uint expectedDisk)
        {
            if (actualDisks == null || actualDisks.Length == 0) { throw ExtentsUnavailable(); }
            if (actualDisks.Any(index => index != expectedDisk))
            {
                throw new InvalidOperationException("Том относится к другому диску или объединяет несколько дисков. Тест выбранного накопителя не запущен.");
            }
        }

        private static IOException ExtentsUnavailable()
        {
            return new IOException("Не удалось однозначно определить физический диск тома. Тест накопителя не запущен.");
        }

        private static string Text(Dictionary<string, object> row, string key)
        {
            object value;
            return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        }

        private static ulong? Unsigned(Dictionary<string, object> row, string key)
        {
            ulong value;
            return ulong.TryParse(Text(row, key), NumberStyles.None, CultureInfo.InvariantCulture, out value) ? (ulong?)value : null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    }
}
