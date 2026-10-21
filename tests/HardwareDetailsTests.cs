using System;
using System.Collections.Generic;
using System.Linq;
using PcPulse;

namespace PcPulseTests
{
    public static class HardwareDetailsTests
    {
        public static void Run()
        {
            CacheTotalsCountPhysicalCachesOnce();
            RamTotalsRequireCompleteNonOverflowingInventory();
            NvmeRequestIsReadOnlyHealthQuery();
            NvmeParserValidatesDescriptorAndCounters();
            SmartCacheExpiresAndDoesNotRetainStaleSuccess();
            ScreenIdentityAndConnectionStaySpecific();
            GpuSensorsRequireUnambiguousIdentity();
            Console.WriteLine("PASS: hardware details (cache totals, RAM limits, NVMe validation/cache, display/GPU identity)");
        }

        private static void CacheTotalsCountPhysicalCachesOnce()
        {
            List<byte[]> records = new List<byte[]>();
            for (int core = 0; core < 6; core++)
            {
                ulong mask = 3UL << (core * 2);
                records.Add(Cache(mask, 1, 48 * 1024, 2));
                records.Add(Cache(mask, 1, 32 * 1024, 1));
                records.Add(Cache(mask, 2, 1280 * 1024, 0));
            }
            for (int core = 0; core < 8; core++)
            {
                ulong mask = 1UL << (core + 12);
                records.Add(Cache(mask, 1, 32 * 1024, 2));
                records.Add(Cache(mask, 1, 64 * 1024, 1));
            }
            records.Add(Cache(0xF000, 2, 2 * 1024 * 1024, 0));
            records.Add(Cache(0xF0000, 2, 2 * 1024 * 1024, 0));
            records.Add(Cache(0xFFFFF, 3, 24 * 1024 * 1024, 0));
            records.Add(Cache(0xFFFFF, 3, 24 * 1024 * 1024, 0));
            Snapshot snapshot = new Snapshot();
            HardwareDetailsInventory.ApplyCpuCacheRecords(snapshot, records.SelectMany(record => record).ToArray(), 8);
            Require(snapshot.CpuL1CacheBytes == 1277952, "L1 includes instruction and data caches once per physical core");
            Require(snapshot.CpuL2CacheBytes == 12058624, "shared E-core L2 is not multiplied by logical CPUs");
            Require(snapshot.CpuL3CacheBytes == 25165824, "shared duplicate L3 is counted once");
            HardwareDetailsInventory.ApplyCpuCacheRecords(snapshot, new byte[33], 8);
            Require(!snapshot.CpuL1CacheBytes.HasValue && !snapshot.CpuL3CacheBytes.HasValue, "truncated topology must not retain old totals");
            HardwareDetailsInventory.ApplyCpuCacheRecords(snapshot, Cache(0, 1, 1024, 0), 8);
            Require(!snapshot.CpuL1CacheBytes.HasValue, "zero processor mask is not a cache record");
        }

        private static void RamTotalsRequireCompleteNonOverflowingInventory()
        {
            Snapshot snapshot = new Snapshot { RamTotalBytes = 31UL * 1024 * 1024 * 1024 };
            HardwareDetailsInventory.ApplyInstalledMemory(snapshot, new[] { Row("Capacity", 16UL << 30), Row("Capacity", 16UL << 30) });
            Require(snapshot.RamInstalledBytes == 32UL << 30 && snapshot.RamTotalBytes == 31UL << 30, "installed RAM stays separate from usable RAM");
            HardwareDetailsInventory.ApplyMemoryMaximum(snapshot, new[] { Array(3, 64UL << 20, 0), Array(4, 1, 0) });
            Require(snapshot.RamMaximumBytes == 64UL << 30, "max capacity uses KB and ignores video memory arrays");
            HardwareDetailsInventory.ApplyMemoryMaximum(snapshot, new[] { Array(3, 32UL << 20, 0), Array(3, 32UL << 20, 0) });
            Require(snapshot.RamMaximumBytes == 64UL << 30, "multiple system arrays are combined");
            HardwareDetailsInventory.ApplyMemoryMaximum(snapshot, new[] { Array(3, 0, 64UL << 20) });
            Require(snapshot.RamMaximumBytes == 64UL << 30, "legacy max capacity fallback");
            foreach (IList<Dictionary<string, object>> invalid in new IList<Dictionary<string, object>>[]
            {
                new[] { Array(3, 16UL << 20, 0) },
                new[] { Array(3, 64UL << 20, 0), Array(3, 0, 0) },
                new[] { Array(3, 0, 0x80000000UL) },
                new[] { Array(3, ulong.MaxValue, 0) },
                new[] { Array(3, ulong.MaxValue / 1024, 0), Array(3, 1, 0) },
                new[] { Array(3, 64UL << 20, 0), Row("MaxCapacityEx", 64UL << 20) }
            })
            {
                HardwareDetailsInventory.ApplyMemoryMaximum(snapshot, invalid);
                Require(!snapshot.RamMaximumBytes.HasValue, "invalid, incomplete or contradictory RAM maximum must be unknown");
            }
            HardwareDetailsInventory.ApplyInstalledMemory(snapshot, new[] { Row("Capacity", 16UL << 30), Row("Missing", 0) });
            Require(!snapshot.RamInstalledBytes.HasValue, "partial module capacities cannot become installed total");
            HardwareDetailsInventory.ApplyInstalledMemory(snapshot, new[] { Row("Capacity", ulong.MaxValue), Row("Capacity", 1) });
            Require(!snapshot.RamInstalledBytes.HasValue, "installed RAM overflow rejected");
        }

        private static void NvmeRequestIsReadOnlyHealthQuery()
        {
            byte[] query = SmartReader.CreateNvmeQuery();
            Require(query.Length == 560, "query fits header, protocol and full 512-byte log");
            Require(BitConverter.ToUInt32(query, 0) == 50 && BitConverter.ToUInt32(query, 4) == 0, "storage device protocol property standard query");
            Require(BitConverter.ToUInt32(query, 8) == 3 && BitConverter.ToUInt32(query, 12) == 2 && BitConverter.ToUInt32(query, 16) == 2, "NVMe health log selected");
            Require(BitConverter.ToUInt32(query, 24) == 40 && BitConverter.ToUInt32(query, 28) == 512, "protocol-relative offset and log length");
            Require(query.Skip(48).All(value => value == 0), "output space initialized, no payload to write to disk");
        }

        private static void NvmeParserValidatesDescriptorAndCounters()
        {
            SmartReading reading;
            byte[] response = NvmeResponse();
            Require(SmartReader.TryParseNvmeResponse(response, 560, out reading), "valid log parses");
            Require(reading.HealthPercent == 95 && reading.PowerOnHours == 5737 && Math.Abs(reading.Temperature.Value - 33.85) < 0.001, "correct endurance, hours, Kelvin conversion");
            response[48] = 4;
            Require(SmartReader.TryParseNvmeResponse(response, 560, out reading) && reading.CriticalWarning == 4 && reading.HealthPercent == 95, "critical warning is separate from remaining endurance");
            response[53] = 255;
            Require(SmartReader.TryParseNvmeResponse(response, 560, out reading) && reading.HealthPercent == 0, "endurance beyond 100 percent used clamps at zero");
            response[48 + 136] = 1;
            Require(SmartReader.TryParseNvmeResponse(response, 560, out reading) && !reading.PowerOnHours.HasValue, "128-bit counter overflow does not silently truncate");
            response = NvmeResponse();
            Put(response, 24, uint.MaxValue);
            Require(!SmartReader.TryParseNvmeResponse(response, 560, out reading), "overflowing descriptor offset rejected");
            response = NvmeResponse();
            Put(response, 28, 513);
            Require(!SmartReader.TryParseNvmeResponse(response, 560, out reading), "length beyond returned bytes rejected");
            response = NvmeResponse();
            Put(response, 0, 0);
            Require(!SmartReader.TryParseNvmeResponse(response, 560, out reading), "invalid descriptor version rejected");
            response = NvmeResponse();
            Require(!SmartReader.TryParseNvmeResponse(response, 559, out reading), "partial health log rejected");
            Require(!SmartReader.TryParseNvmeResponse(response, 561, out reading), "returned length beyond buffer rejected");
            System.Array.Clear(response, 48, 512);
            Require(!SmartReader.TryParseNvmeResponse(response, 560, out reading), "empty log must not report 100 percent healthy");
            Require(!SmartReader.TryParseNvmeResponse(null, 0, out reading), "missing response rejected");
        }

        private static void SmartCacheExpiresAndDoesNotRetainStaleSuccess()
        {
            int calls = 0;
            SmartReader reader = new SmartReader(delegate(uint index)
            {
                calls++;
                if (calls == 3) { throw new InvalidOperationException("device disappeared"); }
                return new SmartReading { HealthPercent = 95, PowerOnHours = 5737, CriticalWarning = 0 };
            });
            DateTime start = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
            DiskSnapshot disk = new DiskSnapshot { Index = 0 };
            reader.Apply(disk, start);
            reader.Apply(disk, start.AddSeconds(29));
            Require(calls == 1, "SMART not polled on every live UI tick");
            reader.Apply(new DiskSnapshot { Index = 1 }, start);
            Require(calls == 2, "cache kept per physical disk");
            reader.Apply(disk, start.AddSeconds(30));
            Require(calls == 3 && !disk.HealthPercent.HasValue && !disk.PowerOnHours.HasValue && !disk.CriticalWarning.HasValue, "failed refresh clears stale successful data");
            reader.Apply(disk, start.AddSeconds(31));
            Require(calls == 3, "read errors are cached too");
        }

        private static void ScreenIdentityAndConnectionStaySpecific()
        {
            Require(ScreenInventory.NormalizeInstance("\\\\?\\DISPLAY#AUO7A99#4&123&0&UID100#{123-456}") == ScreenInventory.NormalizeInstance("DISPLAY\\AUO7A99\\4&123&0&UID100_0"), "interface path matches exact WMI monitor instance");
            Require(ScreenInventory.NormalizeInstance("DISPLAY\\AUO7A99\\OTHER_0") != ScreenInventory.NormalizeInstance("DISPLAY\\AUO7A99\\4&123&0&UID100_0"), "identical model panels remain distinct devices");
            Require(ScreenInventory.InternalConnection(11) == true && ScreenInventory.InternalConnection(6) == true, "embedded DisplayPort and LVDS are internal");
            Require(ScreenInventory.InternalConnection(10) == false && !ScreenInventory.InternalConnection(uint.MaxValue).HasValue, "external DisplayPort and unknown remain distinct");
        }

        private static void GpuSensorsRequireUnambiguousIdentity()
        {
            GpuSnapshot first = new GpuSnapshot { Name = "NVIDIA GeForce RTX 4080", AdapterIndex = 0, AdapterLuidLow = 100 };
            GpuSnapshot second = new GpuSnapshot { Name = "NVIDIA GeForce RTX 4080", AdapterIndex = 1, AdapterLuidLow = 200 };
            Snapshot snapshot = new Snapshot { Gpus = new List<GpuSnapshot> { first, second } };
            HashSet<GpuSnapshot> matched = new HashSet<GpuSnapshot>();
            Require(HardwareMonitor.ResolveGpuSensorTarget(snapshot, "NVIDIA GeForce RTX 4080", matched) == null, "same-name cards cannot be identified by enumeration order");
            Require(snapshot.Gpus.Count == 2 && matched.Count == 0, "ambiguous sensors must not create a third phantom card or consume a target");
            matched.Add(first);
            Require(HardwareMonitor.ResolveGpuSensorTarget(snapshot, "NVIDIA GeForce RTX 4080", matched) == null, "an earlier match must not remove ambiguity for the remaining same-name card");
            Require(!first.Temperature.HasValue && !second.Temperature.HasValue && !first.Load.HasValue && !second.Load.HasValue, "ambiguous adapter readings remain unknown");
            Require(snapshot.Issues.Count == 1, "ambiguity is reported without duplicate issues");

            GpuSnapshot intel = new GpuSnapshot { Name = "Intel(R) Iris(R) Xe Graphics", AdapterIndex = 0, AdapterLuidLow = 300 };
            snapshot = new Snapshot { Gpus = new List<GpuSnapshot> { intel, first } };
            matched.Clear();
            Require(Object.ReferenceEquals(HardwareMonitor.ResolveGpuSensorTarget(snapshot, "Intel Iris Xe Graphics", matched), intel), "a unique normalized exact name remains usable");
            Require(HardwareMonitor.ResolveGpuSensorTarget(snapshot, "Intel Iris Xe Graphics", matched) == null, "duplicate hardware sensor records cannot overwrite a matched adapter");
        }

        private static byte[] Cache(ulong mask, byte level, uint size, uint type)
        {
            byte[] record = new byte[32];
            Buffer.BlockCopy(BitConverter.GetBytes(mask), 0, record, 0, 8);
            Put(record, 8, 2);
            record[16] = level;
            Put(record, 20, size);
            Put(record, 24, type);
            return record;
        }

        private static byte[] NvmeResponse()
        {
            byte[] bytes = SmartReader.CreateNvmeQuery();
            Put(bytes, 0, 48);
            Put(bytes, 4, 48);
            bytes[49] = 51;
            bytes[50] = 1;
            bytes[51] = 100;
            bytes[52] = 10;
            bytes[53] = 5;
            Buffer.BlockCopy(BitConverter.GetBytes(5737UL), 0, bytes, 176, 8);
            return bytes;
        }

        private static Dictionary<string, object> Row(string key, object value) { return new Dictionary<string, object> { { key, value } }; }
        private static Dictionary<string, object> Array(ulong use, ulong extended, ulong legacy) { return new Dictionary<string, object> { { "Use", use }, { "MaxCapacityEx", extended }, { "MaxCapacity", legacy } }; }
        private static void Put(byte[] bytes, int offset, uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException("Hardware details: " + message); } }
    }
}
