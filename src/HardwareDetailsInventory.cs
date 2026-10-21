using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace PcPulse
{
    internal static class HardwareDetailsInventory
    {
        internal static void ReadCpuCaches(Snapshot snapshot)
        {
            // The legacy API describes only the calling processor group.
            if (GetActiveProcessorGroupCount() != 1) { snapshot.CpuCacheSource = "Суммарный кэш для нескольких групп процессоров недоступен"; return; }
            uint length = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref length);
            if (length == 0 || length > 1048576 || Marshal.GetLastWin32Error() != 122) { return; }
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformation(buffer, ref length)) { return; }
                byte[] bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                ApplyCpuCacheRecords(snapshot, bytes, IntPtr.Size);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static void ApplyCpuCacheRecords(Snapshot snapshot, byte[] bytes, int pointerSize)
        {
            snapshot.CpuL1CacheBytes = snapshot.CpuL2CacheBytes = snapshot.CpuL3CacheBytes = null;
            snapshot.CpuCacheSource = "";
            int stride = pointerSize == 8 ? 32 : pointerSize == 4 ? 24 : 0;
            int unionOffset = pointerSize == 8 ? 16 : 8;
            if (stride == 0 || bytes == null || bytes.Length == 0 || bytes.Length % stride != 0) { return; }
            ulong[] totals = new ulong[4];
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int offset = 0; offset < bytes.Length; offset += stride)
            {
                if (BitConverter.ToUInt32(bytes, offset + pointerSize) != 2) { continue; }
                int level = bytes[offset + unionOffset];
                uint size = BitConverter.ToUInt32(bytes, offset + unionOffset + 4);
                uint type = BitConverter.ToUInt32(bytes, offset + unionOffset + 8);
                ulong mask = pointerSize == 8 ? BitConverter.ToUInt64(bytes, offset) : BitConverter.ToUInt32(bytes, offset);
                if (level < 1 || level > 3 || size == 0 || mask == 0 || type > 3) { continue; }
                string key = mask.ToString(CultureInfo.InvariantCulture) + ":" + level + ":" + type + ":" + size;
                // A shared cache is one relationship record, not one cache per set bit.
                if (seen.Add(key)) { totals[level] += size; }
            }
            snapshot.CpuL1CacheBytes = totals[1] == 0 ? (ulong?)null : totals[1];
            snapshot.CpuL2CacheBytes = totals[2] == 0 ? (ulong?)null : totals[2];
            snapshot.CpuL3CacheBytes = totals[3] == 0 ? (ulong?)null : totals[3];
            if (totals.Any(value => value > 0)) { snapshot.CpuCacheSource = "Windows: сумма физических кэшей; общие кэши учтены один раз"; }
        }

        internal static void ApplyInstalledMemory(Snapshot snapshot, IList<Dictionary<string, object>> modules)
        {
            snapshot.RamInstalledBytes = null;
            if (modules == null || modules.Count == 0) { return; }
            ulong total = 0;
            foreach (Dictionary<string, object> module in modules)
            {
                ulong? capacity = Unsigned(module, "Capacity");
                if (!capacity.HasValue || capacity.Value > ulong.MaxValue - total) { return; }
                total += capacity.Value;
            }
            if (total > 0) { snapshot.RamInstalledBytes = total; }
        }

        internal static void ApplyMemoryMaximum(Snapshot snapshot, IList<Dictionary<string, object>> arrays)
        {
            snapshot.RamMaximumBytes = null;
            snapshot.RamMaximumSource = "BIOS не сообщает достоверный предел памяти";
            if (arrays == null || arrays.Count == 0 || arrays.Any(row => !Unsigned(row, "Use").HasValue || Unsigned(row, "Use") <= 2)) { return; }
            List<Dictionary<string, object>> system = arrays.Where(row => Unsigned(row, "Use") == 3).ToList();
            if (system.Count == 0) { return; }
            ulong totalKb = 0;
            foreach (Dictionary<string, object> array in system)
            {
                ulong? capacity = Unsigned(array, "MaxCapacityEx");
                if (!capacity.HasValue || capacity == 0)
                {
                    capacity = Unsigned(array, "MaxCapacity");
                    if (capacity == 0x80000000UL || capacity == uint.MaxValue) { return; }
                }
                if (!capacity.HasValue || capacity == 0 || capacity > ulong.MaxValue / 1024 || totalKb > ulong.MaxValue / 1024 - capacity.Value) { return; }
                totalKb += capacity.Value;
            }
            ulong total = totalKb * 1024;
            if (snapshot.RamInstalledBytes.HasValue && total < snapshot.RamInstalledBytes.Value) { return; }
            snapshot.RamMaximumBytes = total;
            snapshot.RamMaximumSource = "Предел системных массивов памяти по BIOS/SMBIOS; совместимость модулей уточняется у производителя";
        }

        internal static ulong? Unsigned(Dictionary<string, object> row, string key)
        {
            object value;
            if (row == null || !row.TryGetValue(key, out value) || value == null) { return null; }
            ulong number;
            return ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? (ulong?)number : null;
        }

        internal static bool? ReadUnifiedGpuArchitecture(IntPtr adapter)
        {
            IntPtr device = IntPtr.Zero;
            IntPtr context = IntPtr.Zero;
            try
            {
                uint featureLevel;
                if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out device, out featureLevel, out context) < 0 || device == IntPtr.Zero) { return null; }
                CheckFeatureSupport check = (CheckFeatureSupport)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(device), 33 * IntPtr.Size), typeof(CheckFeatureSupport));
                int[] options = new int[8];
                if (check(device, 14, options, 32) < 0) { return null; }
                return options[7] != 0;
            }
            catch { return null; }
            finally
            {
                if (context != IntPtr.Zero) { Marshal.Release(context); }
                if (device != IntPtr.Zero) { Marshal.Release(device); }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnedLength);
        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CheckFeatureSupport(IntPtr device, uint feature, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 8)] int[] data, uint size);
    }
}
