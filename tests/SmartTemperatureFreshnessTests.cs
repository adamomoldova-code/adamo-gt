using System;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class SmartTemperatureFreshnessTests
    {
        public static void Run()
        {
            CachedSampleKeepsAcquisitionTime();
            ScopedPollingIsPerDiskAndReferenceCounted();
            ExtendedTemperatureKeepsItsOwnTime();
            StressRejectsStaleTemperature();
            RunnerDisposesPollingOnFailure();
        }

        private static DiskSnapshot Disk(uint index)
        { return new DiskSnapshot { Index = index, Name = "SMART temperature unit fixture", StorageIdentity = "temperature-fixture-" + index }; }

        private static void CachedSampleKeepsAcquisitionTime()
        {
            int reads = 0;
            var reader = new SmartReader(delegate(uint index) { reads++; return new SmartReading { Temperature = 42 + reads }; });
            DiskSnapshot disk = Disk(81); DateTime start = DateTime.UtcNow;
            reader.Apply(disk, start); reader.Apply(disk, start.AddSeconds(20));
            Require(reads == 1 && disk.Temperature == 43 && disk.TemperatureSampledAtUtc == start,
                "Normal 30-second cache must retain the original sample timestamp.");
            reader.Apply(disk, start.AddSeconds(30));
            Require(reads == 2 && disk.TemperatureSampledAtUtc == start.AddSeconds(30), "A real SMART refresh must advance its sample timestamp.");
        }

        private static void ScopedPollingIsPerDiskAndReferenceCounted()
        {
            int selectedReads = 0, otherReads = 0;
            DiskSnapshot disk = Disk(82), other = Disk(83); DateTime start = DateTime.UtcNow;
            var reader = new SmartReader(delegate(uint index)
            { if (index == disk.Index) { selectedReads++; } else { otherReads++; } return new SmartReading { Temperature = 45 }; });
            reader.Apply(disk, start); reader.Apply(other, start);
            IDisposable first = SmartReader.BeginStressTemperaturePolling(disk);
            IDisposable second = SmartReader.BeginStressTemperaturePolling(disk);
            try
            {
                reader.Apply(disk, start.AddSeconds(1));
                Require(selectedReads == 1, "Active polling still caches readings for two seconds.");
                reader.Apply(disk, start.AddSeconds(2)); reader.Apply(other, start.AddSeconds(2));
                Require(selectedReads == 2 && otherReads == 1, "Only the selected disk receives a shorter cache interval.");
                first.Dispose(); first.Dispose();
                reader.Apply(disk, start.AddSeconds(4));
                Require(selectedReads == 3, "An idempotently disposed lease must not cancel another active lease.");
                second.Dispose();
                reader.Apply(disk, start.AddSeconds(8));
                Require(selectedReads == 3 && disk.TemperatureSampledAtUtc == start.AddSeconds(4), "Normal caching resumes after the final lease is disposed.");
            }
            finally { first.Dispose(); second.Dispose(); }
        }

        private static void ExtendedTemperatureKeepsItsOwnTime()
        {
            int extendedReads = 0; DateTime start = DateTime.UtcNow;
            DiskSnapshot disk = Disk(84);
            var reader = new SmartReader(delegate(uint index) { return new SmartReading(); },
                delegate(DiskSnapshot selected) { extendedReads++; return new SmartReading { Temperature = 51 }; });
            reader.Apply(disk, start, false);
            Require(!disk.TemperatureSampledAtUtc.HasValue, "Deferred extended SMART must not invent a temperature timestamp.");
            reader.Apply(disk, start.AddSeconds(5), true); reader.Apply(disk, start.AddSeconds(20), true);
            Require(extendedReads == 1 && disk.TemperatureSampledAtUtc == start.AddSeconds(5), "Extended SMART uses its own acquisition time, not the NVMe attempt time.");
            using (SmartReader.BeginStressTemperaturePolling(disk)) { reader.Apply(disk, start.AddSeconds(21), true); }
            Require(extendedReads == 2 && disk.TemperatureSampledAtUtc == start.AddSeconds(21), "Active SSD stress also refreshes the extended SMART path.");
        }

        private static void StressRejectsStaleTemperature()
        {
            DiskSnapshot disk = Disk(85); var sample = new Snapshot { Timestamp = DateTime.Now, CpuTemperature = 62, CpuTemperatureTrustedForStress = true };
            sample.Disks.Add(disk); disk.Temperature = 80;
            var options = new StressOptions { Components = StressComponent.Ssd, Disk = disk, DurationSeconds = 1 };
            Require(!StressTestRunner.Temperature(sample, options, StressComponent.Ssd).HasValue, "Unknown acquisition time cannot enable SSD thermal control.");
            disk.TemperatureSampledAtUtc = DateTime.UtcNow.AddSeconds(-20);
            Require(!StressTestRunner.Temperature(sample, options, StressComponent.Ssd).HasValue, "Fresh snapshot timestamp cannot revive stale SMART temperature.");
            Require(StressTestRunner.Temperature(sample, options, StressComponent.Cpu) == 62, "SMART freshness must not suppress CPU telemetry.");
            disk.TemperatureSampledAtUtc = DateTime.UtcNow;
            Require(StressTestRunner.Temperature(sample, options, StressComponent.Ssd) == 80, "Fresh temperature remains available for the SSD thermal cutoff.");
            bool ran = false;
            StressRunResult result = StressTestRunner.RunAsync(options, null, delegate { return sample; }, CancellationToken.None,
                delegate(StressComponent component, StressOptions selected, Action<StressProgress> report, CancellationToken token) { ran = true; }).GetAwaiter().GetResult();
            Require(!ran && result.Cancelled && result.SafetyStopReason.Contains("70"), "A fresh over-temperature SSD must stop before any workload.");
        }

        private static void RunnerDisposesPollingOnFailure()
        {
            DiskSnapshot disk = Disk(86); DateTime start = DateTime.UtcNow; int reads = 0;
            var reader = new SmartReader(delegate(uint index) { reads++; return new SmartReading { Temperature = 40 }; });
            reader.Apply(disk, start);
            StressRunResult result = StressTestRunner.RunAsync(new StressOptions { Components = StressComponent.Ssd, Disk = disk, DurationSeconds = 1 }, null, null,
                CancellationToken.None, delegate(StressComponent component, StressOptions selected, Action<StressProgress> report, CancellationToken token)
                { reader.Apply(disk, start.AddSeconds(2)); throw new InvalidOperationException("fixture SSD failure"); }).GetAwaiter().GetResult();
            reader.Apply(disk, start.AddSeconds(5));
            Require(reads == 2 && result.Results[0].Error.Contains("fixture SSD failure"), "Runner failure must release the fast-polling lease and retain the workload failure.");
        }

        private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    }
}
