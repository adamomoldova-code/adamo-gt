using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PcPulse
{
    internal sealed class SmartReading
    {
        internal double? HealthPercent;
        internal ulong? PowerOnHours;
        internal double? Temperature;
        internal byte? CriticalWarning;
        internal bool? SmartPassed;
        internal string Status = "SMART недоступен";
        internal string Source = "";
    }

    internal sealed class SmartReader
    {
        private sealed class CachedReading
        {
            internal DateTime ExpiresUtc;
            internal DateTime SampledAtUtc;
            internal SmartReading Reading;
        }

        private static readonly object stressPollingSync = new object();
        private static readonly Dictionary<string, int> stressPolling = new Dictionary<string, int>();

        internal static IDisposable BeginStressTemperaturePolling(DiskSnapshot disk)
        {
            if (disk == null) { throw new ArgumentNullException("disk"); }
            string identity = CacheIdentity(disk);
            var lease = new StressPollingLease(identity);
            lock (stressPollingSync)
            {
                int count; stressPolling.TryGetValue(identity, out count);
                stressPolling[identity] = checked(count + 1);
            }
            return lease;
        }

        private sealed class StressPollingLease : IDisposable
        {
            private readonly string identity;
            private int disposed;
            internal StressPollingLease(string value) { identity = value; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
                lock (stressPollingSync)
                {
                    int count = stressPolling[identity];
                    if (count == 1) { stressPolling.Remove(identity); }
                    else { stressPolling[identity] = count - 1; }
                }
            }
        }

        private static string CacheIdentity(DiskSnapshot disk)
        { return disk.Index.ToString(CultureInfo.InvariantCulture) + "|" + disk.StorageIdentity + "|" + disk.Name + "|" + disk.SizeBytes; }

        private static bool ShouldRefresh(string identity, CachedReading cached, DateTime utcNow)
        {
            if (cached == null || utcNow >= cached.ExpiresUtc) { return true; }
            lock (stressPollingSync)
            { return stressPolling.ContainsKey(identity) && utcNow >= cached.SampledAtUtc.AddSeconds(2); }
        }

        private readonly Dictionary<string, CachedReading> cache = new Dictionary<string, CachedReading>();
        private readonly Dictionary<string, CachedReading> extendedCache = new Dictionary<string, CachedReading>();
        private readonly Func<uint, SmartReading> read;
        private readonly Func<DiskSnapshot, SmartReading> readExtended;

        internal SmartReader() : this(ReadNvme, new SmartctlSmartReader().Read) { }
        internal SmartReader(Func<uint, SmartReading> reader) : this(reader, null) { }
        internal SmartReader(Func<uint, SmartReading> reader, Func<DiskSnapshot, SmartReading> extendedReader)
        {
            read = reader;
            readExtended = extendedReader;
        }

        internal void Apply(DiskSnapshot disk, DateTime utcNow)
        {
            Apply(disk, utcNow, false);
        }

        internal void Apply(DiskSnapshot disk, DateTime utcNow, bool includeExtendedSmart)
        {
            string identity = CacheIdentity(disk);
            CachedReading cached;
            if (!cache.TryGetValue(identity, out cached) || ShouldRefresh(identity, cached, utcNow))
            {
                SmartReading reading;
                try { reading = read(disk.Index) ?? new SmartReading(); }
                catch { reading = new SmartReading { Status = "SMART недоступен: ошибка чтения Windows" }; }
                cached = new CachedReading { Reading = reading, SampledAtUtc = utcNow, ExpiresUtc = utcNow.AddSeconds(30) };
                cache[identity] = cached;
            }
            SmartReading result = cached.Reading;
            DateTime? temperatureSampledAt = cached.SampledAtUtc;
            if (!HasData(result) && readExtended != null)
            {
                if (includeExtendedSmart)
                {
                    CachedReading extended;
                    if (!extendedCache.TryGetValue(identity, out extended) || ShouldRefresh(identity, extended, utcNow))
                    {
                        SmartReading reading;
                        try { reading = readExtended(disk) ?? new SmartReading(); }
                        catch { reading = new SmartReading { Status = "SMART: расширенное чтение недоступно" }; }
                        extended = new CachedReading { Reading = reading, SampledAtUtc = utcNow, ExpiresUtc = utcNow.AddSeconds(30) };
                        extendedCache[identity] = extended;
                    }
                    result = extended.Reading;
                    temperatureSampledAt = extended.SampledAtUtc;
                }
                else
                {
                    result = new SmartReading { Status = "Расширенное чтение SMART начнётся после открытия окна" };
                    temperatureSampledAt = null;
                }
            }
            disk.HealthPercent = result.HealthPercent;
            disk.PowerOnHours = result.PowerOnHours;
            disk.Temperature = result.Temperature;
            // Cached readings retain their original acquisition time. A fresh
            // Snapshot.Timestamp must not make cached SMART temperature look live.
            disk.TemperatureSampledAtUtc = result.Temperature.HasValue ? temperatureSampledAt : null;
            disk.CriticalWarning = result.CriticalWarning;
            disk.SmartPassed = result.SmartPassed;
            disk.SmartStatus = result.Status;
            disk.SmartSource = result.Source;
        }

        private static bool HasData(SmartReading reading)
        {
            return reading.HealthPercent.HasValue || reading.PowerOnHours.HasValue || reading.Temperature.HasValue || reading.CriticalWarning.HasValue;
        }

        internal static byte[] CreateNvmeQuery()
        {
            // STORAGE_PROPERTY_QUERY header (8) + protocol request (40) + health log (512).
            byte[] buffer = new byte[560];
            WriteUInt32(buffer, 0, 50); // StorageDeviceProtocolSpecificProperty, read-only query.
            WriteUInt32(buffer, 8, 3); // ProtocolTypeNvme.
            WriteUInt32(buffer, 12, 2); // NVMeDataTypeLogPage.
            WriteUInt32(buffer, 16, 2); // NVME_LOG_PAGE_HEALTH_INFO.
            WriteUInt32(buffer, 24, 40);
            WriteUInt32(buffer, 28, 512);
            return buffer;
        }

        private static SmartReading ReadNvme(uint index)
        {
            using (SafeFileHandle handle = CreateFile("\\\\.\\PhysicalDrive" + index.ToString(CultureInfo.InvariantCulture), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) { return Unavailable(Marshal.GetLastWin32Error()); }
                byte[] buffer = CreateNvmeQuery();
                uint returned;
                if (!DeviceIoControl(handle, 0x002D1400, buffer, (uint)buffer.Length, buffer, (uint)buffer.Length, out returned, IntPtr.Zero))
                {
                    return Unavailable(Marshal.GetLastWin32Error());
                }
                SmartReading reading;
                return TryParseNvmeResponse(buffer, returned, out reading) ? reading : new SmartReading { Status = "SMART: Windows вернула неполные или некорректные данные" };
            }
        }

        private static SmartReading Unavailable(int error)
        {
            return new SmartReading { Status = error == 5 ? "SMART: Windows не разрешает чтение" : "SMART NVMe недоступен через Windows (код " + error.ToString(CultureInfo.InvariantCulture) + ")" };
        }

        internal static bool TryParseNvmeResponse(byte[] buffer, uint returned, out SmartReading reading)
        {
            reading = null;
            if (buffer == null || returned > buffer.Length || returned < 48) { return false; }
            if (BitConverter.ToUInt32(buffer, 0) != 48 || BitConverter.ToUInt32(buffer, 4) != 48 || BitConverter.ToUInt32(buffer, 8) != 3 || BitConverter.ToUInt32(buffer, 12) != 2) { return false; }
            uint offset = BitConverter.ToUInt32(buffer, 24);
            uint length = BitConverter.ToUInt32(buffer, 28);
            if (offset < 40 || length < 512 || (ulong)8 + offset + length > returned) { return false; }
            int start = (int)(8 + offset);
            bool anyData = false;
            for (int index = 0; index < 512; index++) { if (buffer[start + index] != 0) { anyData = true; break; } }
            if (!anyData || buffer[start + 3] > 100 || buffer[start + 4] > 100) { return false; }
            byte warning = buffer[start];
            int kelvin = BitConverter.ToUInt16(buffer, start + 1);
            byte used = buffer[start + 5];
            ulong? hours = BitConverter.ToUInt64(buffer, start + 128);
            for (int index = 136; index < 144; index++) { if (buffer[start + index] != 0) { hours = null; break; } }
            reading = new SmartReading
            {
                HealthPercent = Math.Max(0, 100d - used),
                PowerOnHours = hours,
                Temperature = kelvin > 0 && kelvin != ushort.MaxValue ? (double?)(kelvin - 273.15d) : null,
                CriticalWarning = warning,
                SmartPassed = warning == 0,
                Status = warning == 0 ? "Критических предупреждений SMART нет" : "Предупреждение SMART: " + DescribeWarning(warning),
                Source = "NVMe SMART: остаток ресурса = 100 − Percentage Used; оценка производителя, не гарантия исправности"
            };
            return true;
        }

        internal static string DescribeWarning(byte warning)
        {
            List<string> labels = new List<string>();
            if ((warning & 1) != 0) { labels.Add("мало резервных блоков"); }
            if ((warning & 2) != 0) { labels.Add("температура за порогом"); }
            if ((warning & 4) != 0) { labels.Add("снижена надёжность"); }
            if ((warning & 8) != 0) { labels.Add("режим только чтения"); }
            if ((warning & 16) != 0) { labels.Add("сбой резервирования памяти"); }
            if ((warning & 224) != 0) { labels.Add("дополнительный флаг 0x" + (warning & 224).ToString("X2", CultureInfo.InvariantCulture)); }
            return string.Join("; ", labels.ToArray());
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    }
}
