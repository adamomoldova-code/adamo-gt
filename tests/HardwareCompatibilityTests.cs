using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using PcPulse;

namespace PcPulseTests
{
    public static class HardwareCompatibilityTests
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Run();
                if (!args.Contains("--core-only"))
                {
                    if (Application.Current == null) { new Application(); }
                    DashboardIdentity();
                }
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void Run()
        {
            MemoryClockRequiresOrdinaryDdrAndCompleteConfiguredRate();
            UnknownMemoryArrayUseDoesNotInventFreeSockets();
            CompleteBatteryInventoryReplacesPartialSensorHealth();
            IncompleteBatteryInventoryClearsPreviousCapacityAndHealth();
            GenericGpuSensorCannotCreatePhantomDxgiAdapter();
            Console.WriteLine("PASS: hardware compatibility (RAM clock/slots, complete battery inventory, unmatched GPU sensors)");
        }

        public static void DashboardIdentity()
        {
            ShutdownMode previous = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                UnchangedDevicesKeepResults();
                ReplacementDiskInvalidatesOnlyItsResult();
                foreach (Action<Snapshot> change in new Action<Snapshot>[]
                {
                    delegate(Snapshot snapshot) { snapshot.Gpus[0].AdapterLuidLow++; },
                    delegate(Snapshot snapshot) { snapshot.Gpus[0].AdapterLuidHigh++; },
                    delegate(Snapshot snapshot) { snapshot.Gpus[0].Name = "Replacement GPU"; }
                })
                {
                    ReplacementGpuInvalidatesOnlyItsResult(change);
                }
                DisappearedDevicesDoNotKeepExportedResults();
                FallbackGpuReplacementClearsOldResult();
                InventoryRefreshBlocksManualTestsWithoutStartingWork();
                Console.WriteLine("PASS: dashboard compatibility (stable identity, replaced/disconnected/fallback devices, refresh admission)");
            }
            finally { Application.Current.ShutdownMode = previous; }
        }

        private static void MemoryClockRequiresOrdinaryDdrAndCompleteConfiguredRate()
        {
            Near(2400, HardwareMonitor.MemoryClockMhz(new List<Dictionary<string, object>> { Memory(34, 4800), Memory(34, 4800) }),
                "DDR5-4800 has calculated 2400 MHz transfer clock.");
            Near(1600, HardwareMonitor.MemoryClockMhz(new List<Dictionary<string, object>> { Memory(26, 3200), Memory(34, 3200) }),
                "The same confirmed ordinary-DDR transfer rate can share a calculated clock.");
            foreach (List<Dictionary<string, object>> modules in new[]
            {
                new List<Dictionary<string, object>> { Memory(35, 6400) },
                new List<Dictionary<string, object>> { Memory(34, 4800), Memory(35, 4800) },
                new List<Dictionary<string, object>> { Memory(34, 4800), Memory(34, 5600) },
                new List<Dictionary<string, object>> { Memory(null, 4800) },
                new List<Dictionary<string, object>> { Memory(34, null) },
                new List<Dictionary<string, object>> { Memory(34, 0) },
                new List<Dictionary<string, object>>()
            })
            {
                Require(!HardwareMonitor.MemoryClockMhz(modules).HasValue,
                    "LPDDR, unknown or inconsistent configured rates must not produce a guessed clock.");
            }
            Require(!HardwareMonitor.MemoryClockMhz(null).HasValue, "Absent RAM inventory has no calculated clock.");
            Dictionary<string, object> ratedOnly = Memory(34, null);
            ratedOnly["Speed"] = 5600;
            Require(!HardwareMonitor.MemoryClockMhz(new List<Dictionary<string, object>> { ratedOnly }).HasValue,
                "Rated maximum Speed must not masquerade as ConfiguredClockSpeed.");
        }

        private static void UnknownMemoryArrayUseDoesNotInventFreeSockets()
        {
            var modules = new List<Dictionary<string, object>> { Memory(34, 4800), Memory(34, 4800) };
            foreach (Dictionary<string, object> uncertain in new[]
            {
                new Dictionary<string, object> { { "MemoryDevices", 8 } },
                MemoryArray(null, 8), MemoryArray(1, 8), MemoryArray(2, 8)
            })
            {
                var snapshot = new Snapshot { RamSlotCount = 4, RamFreeSlots = 2 };
                HardwareMonitor.ApplyMemorySlots(snapshot, modules,
                    new List<Dictionary<string, object>> { MemoryArray(3, 2), uncertain });
                Require(snapshot.RamModuleCount == 2 && snapshot.RamHasModularFormFactor,
                    "Known installed SO-DIMMs remain visible when array classification is incomplete.");
                Require(!snapshot.RamSlotCount.HasValue && !snapshot.RamFreeSlots.HasValue,
                    "Missing, Other and Unknown array Use must clear total/free socket counts.");
            }
            var complete = new Snapshot();
            HardwareMonitor.ApplyMemorySlots(complete, modules,
                new List<Dictionary<string, object>> { MemoryArray(3, 4), MemoryArray(4, 8) });
            Require(complete.RamSlotCount == 4 && complete.RamFreeSlots == 2,
                "A positively identified video-memory array must not inflate system RAM sockets.");
        }

        private static void CompleteBatteryInventoryReplacesPartialSensorHealth()
        {
            var snapshot = new Snapshot { BatteryDesignWh = 50, BatteryFullChargeWh = 40, BatteryHealthPercent = 80 };
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot,
                new List<Dictionary<string, object>> { BatteryDesign("A", 50000), BatteryDesign("B", 40000) },
                new List<Dictionary<string, object>> { BatteryFull("B", 32000), BatteryFull("A", 30000) }, 2);
            Near(90, snapshot.BatteryDesignWh, "Complete two-battery design capacity replaces the earlier single-device value.");
            Near(62, snapshot.BatteryFullChargeWh, "Full capacities are paired by identity, not enumeration order.");
            Near(100d * 62 / 90, snapshot.BatteryHealthPercent, "Health is recomputed from total energy, replacing stale 80 percent.");
        }

        private static void IncompleteBatteryInventoryClearsPreviousCapacityAndHealth()
        {
            var snapshot = new Snapshot { BatteryDesignWh = 90, BatteryFullChargeWh = 62, BatteryHealthPercent = 80 };
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot,
                new List<Dictionary<string, object>> { BatteryDesign("A", 50000) },
                new List<Dictionary<string, object>> { BatteryFull("A", 40000) }, 2);
            Require(!snapshot.BatteryDesignWh.HasValue && !snapshot.BatteryFullChargeWh.HasValue && !snapshot.BatteryHealthPercent.HasValue,
                "One observed battery out of two expected cannot retain or invent an aggregate capacity/health.");
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot,
                new List<Dictionary<string, object>> { BatteryDesign("A", 50000) },
                new List<Dictionary<string, object>> { BatteryFull("A", 40000) });
            Near(80, snapshot.BatteryHealthPercent, "Existing callers without expectedCount retain complete single-battery behavior.");
        }

        private static void GenericGpuSensorCannotCreatePhantomDxgiAdapter()
        {
            var gpu = new GpuSnapshot { Name = "NVIDIA GeForce RTX 4080", AdapterIndex = 0, AdapterLuidLow = 500 };
            var snapshot = new Snapshot { Gpus = new List<GpuSnapshot> { gpu } };
            var matched = new HashSet<GpuSnapshot>();
            Require(HardwareMonitor.ResolveGpuSensorTarget(snapshot, "NVIDIA", matched) == null,
                "A generic driver name is not enough to identify an existing DXGI adapter.");
            Require(snapshot.Gpus.Count == 1 && Object.ReferenceEquals(snapshot.Gpus[0], gpu) && matched.Count == 0,
                "An unmatched sensor must not add a phantom GPU or consume the actual target.");
            Require(snapshot.Issues.Any(issue => issue.Contains("NVIDIA") && issue.Contains("DXGI")),
                "Unmatched GPU sensors remain explicit in diagnostics.");
            Require(Object.ReferenceEquals(HardwareMonitor.ResolveGpuSensorTarget(snapshot, "NVIDIA GeForce RTX 4080", matched), gpu),
                "A later exact name still maps to the actual GPU.");
        }

        private static void UnchangedDevicesKeepResults()
        {
            Window window = Program.LoadWindow();
            try
            {
                var dashboard = new Dashboard(window);
                PublishResults(dashboard, Fixture());
                Snapshot refreshed = Fixture();
                refreshed.Gpus[0].Load = 12;
                refreshed.Disks[0].Temperature = 34;
                dashboard.Render(refreshed);
                Require(Text(window, "DiskRead").Contains("6789") && Text(window, "GpuBenchmarkResult").Contains("432,1"),
                    "New telemetry objects for the same devices keep completed results.");
                DiagnosticReport report = Report(dashboard);
                Require(report.DiskTests.Count == 1 && report.GpuTests.Count == 1, "Stable results remain in exported reports.");
            }
            finally { window.Close(); }
        }

        private static void ReplacementDiskInvalidatesOnlyItsResult()
        {
            Window window = Program.LoadWindow();
            try
            {
                var dashboard = new Dashboard(window);
                Snapshot snapshot = Fixture();
                PublishResults(dashboard, snapshot);
                snapshot.Disks[0].StorageIdentity = "PHYSICAL-SSD-REPLACEMENT";
                dashboard.Render(snapshot);
                Require(!Text(window, "DiskRead").Contains("6789") && Text(window, "GpuBenchmarkResult").Contains("432,1"),
                    "A same-index/same-model replacement disk cannot inherit the old benchmark, including an in-place snapshot mutation.");
                DiagnosticReport report = Report(dashboard);
                Require(report.DiskTests.Count == 0 && report.GpuTests.Count == 1, "Only the replaced disk's export result is removed.");
            }
            finally { window.Close(); }
        }

        private static void ReplacementGpuInvalidatesOnlyItsResult(Action<Snapshot> change)
        {
            Window window = Program.LoadWindow();
            try
            {
                var dashboard = new Dashboard(window);
                Snapshot snapshot = Fixture();
                PublishResults(dashboard, snapshot);
                change(snapshot);
                dashboard.Render(snapshot);
                Require(!Text(window, "GpuBenchmarkResult").Contains("432,1") && Text(window, "DiskRead").Contains("6789"),
                    "Changing either LUID half or GPU name invalidates the prior result without clearing the unchanged disk.");
                DiagnosticReport report = Report(dashboard);
                Require(report.GpuTests.Count == 0 && report.DiskTests.Count == 1, "The replaced GPU's score is also removed from export.");
            }
            finally { window.Close(); }
        }

        private static void DisappearedDevicesDoNotKeepExportedResults()
        {
            Window window = Program.LoadWindow();
            try
            {
                var dashboard = new Dashboard(window);
                PublishResults(dashboard, Fixture());
                Snapshot snapshot = Fixture();
                snapshot.Disks.Clear();
                snapshot.Gpus.Clear();
                dashboard.Render(snapshot);
                DiagnosticReport report = Report(dashboard);
                Require(report.DiskTests.Count == 0 && report.GpuTests.Count == 0, "Disconnected devices have no stale results in the current report.");
            }
            finally { window.Close(); }
        }

        private static void FallbackGpuReplacementClearsOldResult()
        {
            Window window = Program.LoadWindow();
            try
            {
                var dashboard = new Dashboard(window);
                Snapshot snapshot = Fixture();
                snapshot.Gpus = new List<GpuSnapshot> { new GpuSnapshot { Name = "Fallback GPU A" } };
                dashboard.Render(snapshot);
                dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress
                {
                    AdapterName = "Fallback GPU A", Stage = "Fallback GPU A unavailable", Error = "Fixture unsupported"
                });
                Require(Text(window, "GpuBenchmarkResult").Contains("Fallback GPU A") && Report(dashboard).GpuTests.Count == 1,
                    "The initial fallback GPU status must be visible and exported before testing replacement.");
                snapshot.Gpus[0].Name = "Fallback GPU B";
                dashboard.Render(snapshot);
                Require(!Text(window, "GpuBenchmarkResult").Contains("Fallback GPU A") && Report(dashboard).GpuTests.Count == 0,
                    "A replacement GPU without a DXGI index cannot inherit the previous fallback status in UI or export.");
            }
            finally { window.Close(); }
        }

        private static void InventoryRefreshBlocksManualTestsWithoutStartingWork()
        {
            Window window = Program.LoadWindow();
            try
            {
                var dashboard = new Dashboard(window);
                dashboard.Render(Fixture());
                var diskButton = (Button)window.FindName("RunTestButton");
                var gpuButton = (Button)window.FindName("RunGpuTestButton");
                var stopButton = (Button)window.FindName("StopTestButton");
                Require(diskButton.IsEnabled && gpuButton.IsEnabled && !stopButton.IsEnabled,
                    "An idle inventory with testable devices enables manual tests and has nothing to stop.");
                dashboard.SetInventoryRefreshing(true);
                Require(!diskButton.IsEnabled && !gpuButton.IsEnabled && !stopButton.IsEnabled,
                    "Inventory refresh reserves test admission without pretending a benchmark is running.");
                dashboard.Render(Fixture());
                Require(!diskButton.IsEnabled && !gpuButton.IsEnabled && !stopButton.IsEnabled,
                    "Telemetry rendering cannot reopen test admission during an inventory refresh.");
                dashboard.SetInventoryRefreshing(false);
                Require(diskButton.IsEnabled && gpuButton.IsEnabled && !stopButton.IsEnabled,
                    "Finishing inventory refresh restores manual controls without enabling Stop.");
            }
            finally { window.Close(); }
        }

        private static void PublishResults(Dashboard dashboard, Snapshot snapshot)
        {
            dashboard.Render(snapshot);
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, ReadBytesPerSecond = 6789000000, WriteBytesPerSecond = 1234000000, Percent = 100, Stage = "Завершено" });
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, AdapterName = snapshot.Gpus[0].Name, Completed = true, FramesPerSecond = 432.1, FrameTimeMs = 2.3, DurationSeconds = 15, Percent = 100, Stage = "Завершено" });
        }

        private static Snapshot Fixture()
        {
            return new Snapshot
            {
                CpuName = "Fixture CPU", BatteryPresent = false,
                Disks = new List<DiskSnapshot> { new DiskSnapshot { Index = 0, Name = "Fixture SSD", StorageIdentity = "PHYSICAL-SSD-A", SizeBytes = 512000000000 } },
                Gpus = new List<GpuSnapshot> { new GpuSnapshot { Name = "Fixture GPU", AdapterIndex = 0, AdapterLuidLow = 500, AdapterLuidHigh = -2 } }
            };
        }

        private static DiagnosticReport Report(Dashboard dashboard)
        {
            return new JavaScriptSerializer().Deserialize<DiagnosticReport>(dashboard.CreateReport());
        }

        private static string Text(Window window, string name) { return ((TextBlock)window.FindName(name)).Text; }
        private static Dictionary<string, object> Memory(object type, object rate)
        {
            return new Dictionary<string, object> { { "SMBIOSMemoryType", type }, { "ConfiguredClockSpeed", rate }, { "Capacity", 16UL << 30 }, { "FormFactor", 12 } };
        }
        private static Dictionary<string, object> MemoryArray(object use, int slots)
        {
            return new Dictionary<string, object> { { "Use", use }, { "MemoryDevices", slots } };
        }
        private static Dictionary<string, object> BatteryDesign(string name, double capacity)
        {
            return new Dictionary<string, object> { { "InstanceName", name }, { "DesignedCapacity", capacity }, { "Capabilities", 0x80000000U } };
        }
        private static Dictionary<string, object> BatteryFull(string name, double capacity)
        {
            return new Dictionary<string, object> { { "InstanceName", name }, { "FullChargedCapacity", capacity } };
        }
        private static void Near(double expected, double? actual, string message)
        {
            Require(actual.HasValue && Math.Abs(expected - actual.Value) < 0.00001, message);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
