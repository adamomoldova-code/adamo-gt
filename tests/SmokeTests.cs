using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcPulse;

namespace PcPulseTests
{
    public static class SmokeTests
    {
        private static int passed;
        private static int failed;
        private static string artifactDirectory;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                artifactDirectory = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(artifactDirectory);
            }

            if (args.Contains("--crm-only"))
            {
                var crmApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Run("CRM transport validates office access, saves exact retries and protects credentials", CrmIntakeClientTests.Run);
                Run("CRM product data excludes private identifiers and marks uncertain measurements", CrmProductDraftTests.Run);
                Run("Legacy CRM dialog remains compatible for existing tests", CrmProductDraftTests.RunUi);
                Run("Minimal CRM client authenticates, validates responses and safely resumes", CrmQuickIntakeClientTests.Run);
                Run("Minimal CRM dialog asks only password, quantity and condition", CrmQuickIntakeWindowTests.Run);
                Run("Automatic CRM payload includes product specs without private identifiers", CrmQuickPayloadTests.Run);
                Run("Minimal CRM flow sends the frozen first page through the actual v2 client", CrmQuickFlowTests.Run);
                Run("Dashboard CRM action uses inventory and does not activate other buttons", CrmDashboardTests.Run);
                Run("CRM visibility requires a fresh strict server decision and rejects stale responses", CrmOfficeAccessTests.Run);
                Run("CRM screenshot captures the real first page without other windows", CrmDashboardCaptureTests.Run);
                Run("Dashboard viewport remains usable with CRM action", DashboardViewportTests.Run);
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
                return failed == 0 ? 0 : 1;
            }

            if (args.Contains("--peripherals-only"))
            {
                Run("Manual device verdicts persist independently of automatic benchmark history", PeripheralHistoryTests.Run);
                Run("Audio capture and channel tests release buffers on failure and cancellation", PeripheralAudioTests.Run);
                Run("Camera preview cancels late startup and releases sessions without hardware capture", CameraPreviewTests.Run);
                var peripheralsApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Run("Keyboard test captures only explicit local input and preserves system shortcuts", KeyboardTestPanelTests.Run);
                Run("Peripheral tabs remain accessible and manual verdicts require evidence", PeripheralWindowTests.Run);
                Run("Device controls and report retain manual results", ConfigurationReportTests.Run);
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
                return failed == 0 ? 0 : 1;
            }

            if (args.Contains("--startup-only"))
            {
                var startupApp = new Application();
                Run("Hardware presentation precedes sequential checks and cancellation", StartupSequenceTests.Run);
                Run("Background completion preserves the WPF UI context", StartupSequenceTests.UiContext);
                Run("Results appear progressively and startup state survives live refresh", BenchmarkControls);
                Run("Completed results and device selections survive refresh", DeviceSelection);
                Run("Memory type and slots fit the dashboard", MemorySlotWindow);
                Run("Battery capacity and cycles fit the dashboard", BatteryCapacityWindow);
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
                return failed == 0 ? 0 : 1;
            }

            if (args.Contains("--ports-only"))
            {
                var portsApp = new Application();
                Run("Computer identity uses descriptive models and rejects BIOS placeholders", ComputerIdentity);
                Run("Online search limits disclosure and official source URLs", PortSourceSafety);
                Run("Official specification parser respects scope, variants and optional ports", PortLookupTests.Run);
                Run("Ports and computer model render separately from local telemetry", PortsWindow);
                Run("Device selection and completed benchmark survive the six-card layout", DeviceSelection);
                Run("Battery capacity remains visible", BatteryCapacityWindow);
                Run("Memory slot counts remain visible", MemorySlotWindow);
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
                return failed == 0 ? 0 : 1;
            }

            if (args.Contains("--battery-only"))
            {
                var batteryApp = new Application();
                Run("Battery runtime is an explicit capacity-based light-work estimate", BatteryRuntimeEstimatorTests.Run);
                Run("Only absolute battery energy is converted to watt-hours", BatteryCapacityUnits);
                Run("Battery capacities aggregate only complete compatible devices", BatteryCapacityAggregation);
                Run("Battery cycles preserve unavailable values and separate battery counters", BatteryCycles);
                Run("Battery capacity is displayed separately from charge and health", BatteryCapacityWindow);
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
                return failed == 0 ? 0 : 1;
            }

            if (args.Contains("--memory-only"))
            {
                var memoryApp = new Application();
                Run("Installed memory and free slots use consistent system arrays", MemorySlotCounts);
                Run("Unknown and soldered memory do not invent free sockets", MemorySlotUnknowns);
                Run("Memory slot counts render on the single page", MemorySlotWindow);
                Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
                return failed == 0 ? 0 : 1;
            }

            Run("Unavailable sensor readings do not become zero", MissingValues);
            Run("CRM transport validates office access, saved retries and credentials", CrmIntakeClientTests.Run);
            Run("CRM product projection keeps only reviewed hardware data", CrmProductDraftTests.Run);
            Run("Minimal CRM client authenticates, validates responses and safely resumes", CrmQuickIntakeClientTests.Run);
            Run("Automatic CRM payload includes product specs without private identifiers", CrmQuickPayloadTests.Run);
            Run("CRM office visibility rejects missing access, timeout and stale responses", CrmOfficeAccessTests.Run);
            Run("Stress CPU, RAM and GPU kernels validate results and release resources", StressWorkloadTests.Run);
            Run("Stress SSD protects owned files and detects corrupted blocks", StressSsdTests.Run);
            Run("Stress SSD temperatures retain sample age and scoped refresh", SmartTemperatureFreshnessTests.Run);
            Run("Stress sessions isolate choices, cancellation and thermal limits", StressWindowTests.RunSession);
            Run("Real zero readings remain visible", ZeroValues);
            Run("Memory above four GiB retains its full capacity", LargeMemory);
            Run("Memory, capacity and throughput use their stated units", Units);
            Run("DiskSpd validates engine, profile and measured XML results", DiskSpdBenchmarkTests.Results);
            Run("DiskSpd cancellation and timeout stop the worker", DiskSpdBenchmarkTests.CancellationAndTimeout);
            Run("DiskSpd file preparation and target boundaries", DiskSpdBenchmarkTests.PreparationAndBoundaries);
            Run("Gauge values are bounded and reject non-finite readings", GaugeBounds);
            Run("CPU cache, memory limits, display identity and NVMe health readers", HardwareDetailsTests.Run);
            Run("Extended SMART preserves identity, unknowns, warnings and read-only limits", ExtendedSmartTests.Run);
            Run("Signed sensor installer validation and truthful installation results", SensorSetupTests.Run);
            Run("Sensor access requests Windows elevation once and preserves probe arguments", SensorStartupTests.Run);
            Run("CPU temperatures select package or die readings without unrelated zones", TemperatureDiagnosticsTests.CpuSelection);
            Run("GPU temperatures distinguish unsupported and empty sensors", TemperatureDiagnosticsTests.GpuSelection);
            Run("Windows/OEM temperatures keep component identity, units and fresh readings without PawnIO", DriverlessTemperatureTests.Run);
            Run("Temperature diagnostics explain administrator, driver and adapter requirements", TemperatureDiagnosticsTests.Availability);
            Run("Temperature explanations and readable unavailable states", TemperaturePresentationTests.Run);
            Run("Cached benchmark dates stay separate from live readings and allow manual tests", HistoryPresentationTests.Run);
            Run("A repeat startup never starts SSD or GPU benchmarks", HistoryStartupTests.Run);
            Run("Persistent identities distinguish replacement SSDs from changing adapter indices", PersistentDeviceIdentityTests.Run);
            Run("Automatic benchmarks are claimed once across concurrent launches", BenchmarkHistoryTests.FirstAttempt);
            Run("Successful benchmarks persist and restore with their original dates", BenchmarkHistoryTests.SuccessfulRestore);
            Run("Cancelled or failed retests preserve previous valid results", BenchmarkHistoryTests.FailurePreservesResults);
            Run("Changed hardware cannot inherit saved results from another device", BenchmarkHistoryTests.HardwareChanges);
            Run("Invalid or unavailable history never triggers an automatic stress test", BenchmarkHistoryTests.InvalidHistory);
            Run("Concurrent result saves retain independent disk and GPU results", BenchmarkHistoryTests.ConcurrentResults);
            Run("Shared history grants ordinary users read-only file access", BenchmarkHistoryTests.SecurityPolicies);
            Run("Manual device verdicts persist independently of automatic benchmark history", PeripheralHistoryTests.Run);
            Run("Audio capture and channel tests release buffers on failure and cancellation", PeripheralAudioTests.Run);
            Run("Camera preview cancels late startup and releases sessions without hardware capture", CameraPreviewTests.Run);
            Run("Shared history mutex preserves permissions and opens with minimal rights", BenchmarkHistoryTests.SharedMutexAccess);
            Run("Shared history rejects unsafe directories and redirected ancestors", BenchmarkHistoryTests.SharedStorageRejectsUnsafePaths);
            Run("Shutdown observes successful, cancelled and failed workers", ShutdownCoordinatorTests.CompletedWorkers);
            Run("Shutdown waits for workers that finish within its deadline", ShutdownCoordinatorTests.PendingWorkersComplete);
            Run("A blocked hardware worker cannot hold shutdown indefinitely", ShutdownCoordinatorTests.DeadlineReturns);
            Run("Verified model and panel catalog rejects ambiguous or untrusted matches", HardwareCatalogTests.Run);
            Run("GPU timestamps, hardware identity failures, stop and shader compilation", GpuBenchmarkTests.Run);
            Run("GPU selection follows LUID across adapter reorder", GpuCompatibilityTests.Run);
            Run("Storage type uses OS disk number and verified identity", StorageTopologyTests.Types);
            Run("Storage target selection handles free space and multiple owners", StorageTopologyTests.Volumes);
            Run("Storage target binding rejects other and replaced devices", StorageTopologyTests.RuntimeBinding);
            Run("Battery runtime is an explicit capacity-based light-work estimate", BatteryRuntimeEstimatorTests.Run);
            Run("Hardware inventory handles incomplete and mixed devices", HardwareCompatibilityTests.Run);
            Run("Compound port descriptions keep separate quantities and optional connectors", AdditionalCompatibilityTests.CompoundPortClauses);
            Run("Legacy benchmark settings preserve case-insensitive explicit file sizes", AdditionalCompatibilityTests.LegacyConfigurationCasing);
            Run("Legacy external display connectors are not treated as unknown panels", AdditionalCompatibilityTests.ExternalDisplayConnector);
            // Dialog tests close unowned fixture windows while pumping the Dispatcher.
            // Keep the shared test Application alive until the runner exits.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Run("CRM dialog handles quantity, reviewed photo and pending operations", CrmProductDraftTests.RunUi);
            Run("Minimal CRM dialog asks only password, quantity and condition", CrmQuickIntakeWindowTests.Run);
            Run("Minimal CRM flow sends the frozen first page through the actual v2 client", CrmQuickFlowTests.Run);
            Run("Dashboard CRM action is isolated and waits for completed inventory", CrmDashboardTests.Run);
            Run("CRM screenshot captures the real first page without other windows", CrmDashboardCaptureTests.Run);
            Run("Stress dialog requires a manual choice and waits for stop cleanup", StressWindowTests.Run);
            Run("Keyboard test captures only explicit local input and preserves system shortcuts", KeyboardTestPanelTests.Run);
            Run("Peripheral tabs remain accessible and manual verdicts require evidence", PeripheralWindowTests.Run);
            Run("Dashboard scales to smaller work areas without clipping", DashboardViewportTests.Run);
            Run("Sensor help fits small monitor work areas and its content remains scrollable", DialogViewportTests.Run);
            Run("Replaced hardware cannot inherit old benchmark results", HardwareCompatibilityTests.DashboardIdentity);
            Run("Bounded benchmark profiles and truthful partial report export", ConfigurationReportTests.Run);
            Run("Compact dashboard stays readable and preserves GPU and screen selection", DashboardLayoutTests.Run);
            Run("Computer identity uses descriptive models and rejects BIOS placeholders", ComputerIdentity);
            Run("Online search limits disclosure and official source URLs", PortSourceSafety);
            Run("Official specification parser respects scope, variants and optional ports", PortLookupTests.Run);
            Run("Ports and computer model render separately from local telemetry", PortsWindow);
            Run("Only absolute battery energy is converted to watt-hours", BatteryCapacityUnits);
            Run("Battery capacities aggregate only complete compatible devices", BatteryCapacityAggregation);
            Run("Battery cycles preserve unavailable values and separate battery counters", BatteryCycles);
            Run("Battery capacity is displayed separately from charge and health", BatteryCapacityWindow);
            Run("Installed memory and free slots use consistent system arrays", MemorySlotCounts);
            Run("Unknown and soldered memory do not invent free sockets", MemorySlotUnknowns);
            Run("Memory slot counts render on the single page", MemorySlotWindow);
            Run("Desktop and unavailable temperature states are explicit", DesktopWindow);
            Run("Device selection and completed benchmark survive refresh", DeviceSelection);
            Run("Benchmark controls dispatch cancellation and retry", BenchmarkControls);
            Run("Hardware presentation precedes sequential checks and cancellation", StartupSequenceTests.Run);
            Run("GPU follows disks and respects stop and failure", GpuStartupTests.Run);
            Run("Background completion preserves the WPF UI context", StartupSequenceTests.UiContext);
            Run("Benchmark cancellation removes only its temporary file", BenchmarkCancellation);
            Run("Invalid and pre-cancelled benchmark never reports a speed", BenchmarkUnavailable);

            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void ComputerIdentity()
        {
            var snapshot = new Snapshot();
            HardwareMonitor.ApplyComputerIdentity(snapshot, "HP", "HP ZBook Firefly 14 inch G9 Mobile Workstation PC", "6T6Y0US#ABA", "", "", "");
            Equal("HP", snapshot.ComputerManufacturer, "Manufacturer");
            Equal("HP ZBook Firefly 14 inch G9 Mobile Workstation PC", snapshot.ComputerModel, "Human model");
            Equal("6T6Y0US#ABA", snapshot.ComputerSku, "SKU");
            HardwareMonitor.ApplyComputerIdentity(snapshot, "LENOVO", "21AHCTO1WW", "", "LENOVO", "21AHCTO1WW", "ThinkPad T14 Gen 3");
            Equal("ThinkPad T14 Gen 3", snapshot.ComputerModel, "Lenovo machine code resolved");
            Equal("21AHCTO1WW", snapshot.ComputerSku, "Machine type retained without guessing");
            foreach (string placeholder in new[] { "To be filled by O.E.M.", "System Product Name", "Default string", "N/A", "00000", "\0\t" })
            {
                Equal("", HardwareMonitor.SanitizeModelValue(placeholder), "BIOS placeholder");
            }
        }

        private static void PortSourceSafety()
        {
            True(PortLookup.IsAllowedSource("https://support.hp.com/us-en/document/ish_5881971-5882022-16", "HP"), "Official HP source accepted");
            foreach (string url in new[] { "http://support.hp.com/test", "https://support.hp.com.evil.test/test", "https://support.hp.com@evil.test/test", "https://127.0.0.1/test", "https://support.hp.com:444/test", "file:///C:/test" })
            {
                True(!PortLookup.IsAllowedSource(url, "HP"), "Untrusted source rejected: " + url);
            }
            string search = PortLookup.SearchUrl(new Snapshot { ComputerManufacturer = "HP", ComputerModel = "ZBook Firefly 14 G9", ComputerSku = "6T6Y0US", ComputerName = "private-machine-name", CpuName = "private-cpu-value" });
            True(search.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "HTTPS search");
            True(!search.Contains("private-machine-name") && !search.Contains("private-cpu-value"), "Only public model identity sent");
        }

        private static void PortsWindow()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            Snapshot snapshot = Fixture();
            snapshot.ComputerManufacturer = "HP";
            snapshot.ComputerModel = "HP ZBook Firefly 14 inch G9 Mobile Workstation PC";
            snapshot.ComputerSku = "6T6Y0US#ABA";
            dashboard.Render(snapshot);
            True(Text(window, "ComputerModel").Contains("Firefly"), "Model shown from local inventory");
            dashboard.RenderPorts(new PortSpecification { Model = snapshot.ComputerModel, IsLoading = true });
            var refresh = (Button)window.FindName("PortsRefreshButton");
            var source = (Button)window.FindName("PortsSourceButton");
            var search = (Button)window.FindName("PortsSearchButton");
            True(!refresh.IsEnabled && !source.IsEnabled, "Loading prevents repeated lookup and empty source link");
            var specification = new PortSpecification
            {
                Model = snapshot.ComputerModel,
                SourceUrl = "https://support.hp.com/us-en/document/ish_5881971-5882022-16",
                SourceTitle = "HP specification",
                IsCached = true,
                CheckedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
                Status = "Сохранённая спецификация",
                Note = "Опциональные порты зависят от комплектации.",
                Ports = new List<PortEntry>
                {
                    new PortEntry { Name = "USB-C / Thunderbolt 4", Count = 2 },
                    new PortEntry { Name = "USB-A", Count = 2 },
                    new PortEntry { Name = "HDMI", Count = 1 },
                    new PortEntry { Name = "Аудио 3,5 мм", Count = 1 },
                    new PortEntry { Name = "Nano SIM", Count = 1, Optional = true },
                    new PortEntry { Name = "Smart Card", Count = 1, Optional = true },
                    new PortEntry { Name = "Unknown", Count = 0 }
                }
            };
            dashboard.RenderPorts(specification);
            var ports = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("PortsList");
            var optional = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("OptionalPortsList");
            Equal(4, ports.Children.Count, "Only confirmed positive counts shown together");
            Equal(2, optional.Children.Count, "Optional sockets separated");
            True(Text(window, "PortsStatus").Contains("Кэш"), "Cached source explicitly labelled");
            True(refresh.IsEnabled && source.IsEnabled, "Lookup enables refresh and source");
            string openedUrl = "";
            bool requestedRefresh = false;
            bool requestedSearch = false;
            dashboard.PortsSourceRequested += delegate(string value) { openedUrl = value; };
            dashboard.PortsRefreshRequested += delegate { requestedRefresh = true; };
            dashboard.PortsSearchRequested += delegate { requestedSearch = true; };
            source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(specification.SourceUrl, openedUrl, "Source action opens exact evidence link");
            True(requestedRefresh && requestedSearch, "Refresh and browser search actions dispatched");
            dashboard.Render(snapshot);
            Equal(4, ports.Children.Count, "Live telemetry does not erase port lookup");
            if (artifactDirectory != null) { Program.Render(window, Path.Combine(artifactDirectory, "ports-960.png"), 960, 680); }
            dashboard.RenderPorts(new PortSpecification { Model = snapshot.ComputerModel, Status = "Нет подтверждённых данных", SourceUrl = "file:///C:/invalid" });
            Equal(0, ports.Children.Count, "Missing result clears old confirmed counts");
            Equal(0, optional.Children.Count, "Missing result clears old options");
            True(!source.IsEnabled, "Unsafe or missing source is disabled");
            True(((TextBlock)window.FindName("PortsEmpty")).Visibility == Visibility.Visible, "Missing data explained");
        }

        private static void MissingValues()
        {
            Equal("Нет данных", Display.Number(null, " °C", 0), "Missing temperature");
            Equal("Нет данных", Display.Number(double.NaN, " °C", 0), "NaN temperature");
            Equal("Нет данных", Display.Number(double.PositiveInfinity, " %", 0), "Positive infinity");
            Equal("Нет данных", Display.Number(double.NegativeInfinity, " %", 0), "Negative infinity");
            Equal("Нет данных", Display.Memory(null), "Missing memory");
            Equal("Нет данных", Display.DiskSize(null), "Missing disk capacity");
            Equal("Нет данных", Display.Speed(null), "Missing disk speed");
        }

        private static void BatteryCapacityUnits()
        {
            const uint absoluteSystemBattery = 0x80000000;
            Equal((double?)51.31, HardwareMonitor.BatteryCapacityWh(51310, absoluteSystemBattery), "mWh to Wh");
            Equal((double?)31.937, HardwareMonitor.BatteryCapacityWh(31937, absoluteSystemBattery), "Current full-charge energy");
            Equal((double?)null, HardwareMonitor.BatteryCapacityWh(51310, null), "Unknown unit capabilities");
            Equal((double?)null, HardwareMonitor.BatteryCapacityWh(51310, 0xC0000000), "Relative capacity is not Wh");
            foreach (double? raw in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, uint.MaxValue })
            {
                Equal((double?)null, HardwareMonitor.BatteryCapacityWh(raw, absoluteSystemBattery), "Invalid or sentinel capacity");
            }
        }

        private static Dictionary<string, object> BatteryDesign(string name, double capacity, uint flags)
        {
            return new Dictionary<string, object> { { "InstanceName", name }, { "DesignedCapacity", capacity }, { "Capabilities", flags } };
        }

        private static Dictionary<string, object> BatteryFull(string name, double capacity)
        {
            return new Dictionary<string, object> { { "InstanceName", name }, { "FullChargedCapacity", capacity } };
        }

        private static void BatteryCapacityAggregation()
        {
            var design = new List<Dictionary<string, object>> { BatteryDesign("A", 50000, 0x80000000), BatteryDesign("B", 40000, 0x80000000) };
            var full = new List<Dictionary<string, object>> { BatteryFull("B", 32000), BatteryFull("A", 30000) };
            var snapshot = new Snapshot();
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot, design, full);
            Equal((double?)90, snapshot.BatteryDesignWh, "All absolute factory capacities summed by identity");
            Equal((double?)62, snapshot.BatteryFullChargeWh, "Full-charge capacities summed by identity");
            snapshot = new Snapshot();
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot, design, new List<Dictionary<string, object>> { full[0] });
            Equal((double?)null, snapshot.BatteryDesignWh, "Partial inventory cannot be labelled total energy");
            design[1]["Capabilities"] = 0xC0000000U;
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot, design, full);
            Equal((double?)null, snapshot.BatteryFullChargeWh, "Relative and absolute units cannot be summed");
            Equal((double?)null, snapshot.BatteryHealthPercent, "Mixed units cannot form aggregate health");
            snapshot = new Snapshot();
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot, new List<Dictionary<string, object>> { design[1] }, new List<Dictionary<string, object>> { full[0] });
            Equal((double?)80, snapshot.BatteryHealthPercent, "Single relative battery still has a valid ratio");
            Equal((double?)null, snapshot.BatteryDesignWh, "Relative ratio must not become Wh");
            snapshot = new Snapshot();
            HardwareMonitor.ApplyBatteryCapacityInventory(snapshot, new List<Dictionary<string, object>> { design[0], design[0] }, full);
            Equal((double?)null, snapshot.BatteryDesignWh, "Duplicate identities cannot inflate energy");
        }

        private static void BatteryCycles()
        {
            Equal((uint?)87, HardwareMonitor.BatteryCycleCountValue(87), "Controller cycle count");
            foreach (double? count in new double?[] { null, 0, -1, 1.5, double.NaN, double.PositiveInfinity, uint.MaxValue })
            {
                Equal((uint?)null, HardwareMonitor.BatteryCycleCountValue(count), "Unsupported or invalid counter remains unknown");
            }
            var rows = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "InstanceName", "A" }, { "CycleCount", 87U } }
            };
            var snapshot = new Snapshot();
            HardwareMonitor.ApplyBatteryCycleInventory(snapshot, rows, 1);
            Equal((uint?)87, snapshot.BatteryCycleCount, "Single battery counter");
            True(!snapshot.BatteryCyclesPerDevice, "Single count is displayed directly");
            rows.Add(new Dictionary<string, object> { { "InstanceName", "B" }, { "CycleCount", 120U } });
            HardwareMonitor.ApplyBatteryCycleInventory(snapshot, rows, 2);
            Equal((uint?)null, snapshot.BatteryCycleCount, "Counters are never added across batteries");
            True(snapshot.BatteryCyclesPerDevice && snapshot.BatteryCycleDetail.Contains("87") && snapshot.BatteryCycleDetail.Contains("120"), "Individual battery counts retained");
            HardwareMonitor.ApplyBatteryCycleInventory(snapshot, new List<Dictionary<string, object>> { rows[0] }, 2);
            Equal((uint?)null, snapshot.BatteryCycleCount, "Partial multi-battery reading cannot look like one total");
            HardwareMonitor.ApplyBatteryCycleInventory(snapshot, new List<Dictionary<string, object>> { rows[0], rows[0] }, 1);
            Equal((uint?)null, snapshot.BatteryCycleCount, "Duplicate device identities rejected");
            HardwareMonitor.ApplyBatteryCycleInventory(snapshot, new List<Dictionary<string, object>>(), 1);
            Equal((uint?)null, snapshot.BatteryCycleCount, "Absent counter clears previous value");
        }

        private static void BatteryCapacityWindow()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            Snapshot snapshot = Fixture();
            snapshot.BatteryPresent = true;
            snapshot.BatteryPercent = 97;
            snapshot.BatteryHealthPercent = 62.24;
            snapshot.BatteryDesignWh = 51.31;
            snapshot.BatteryFullChargeWh = 31.937;
            snapshot.BatteryStatus = "Подключено питание";
            snapshot.BatteryCycleCount = 137;
            snapshot.BatteryCycleDetail = "Контроллер батареи сообщает 137 циклов";
            dashboard.Render(snapshot);
            Equal("51,31 Вт·ч", Text(window, "BatteryDesignCapacity"), "Factory capacity is energy, not percentage");
            Equal("31,94 Вт·ч", Text(window, "BatteryFullCapacity"), "Current full-charge capacity");
            Equal("≈ 2,1–4,6 ч", Text(window, "BatteryRuntime"), "Estimated full-charge light-work runtime");
            Equal("97 %", Text(window, "BatteryPercent"), "Charge stays separate");
            Equal("62 %", Text(window, "BatteryHealth"), "Health stays separate");
            Equal("137", Text(window, "BatteryCycles"), "Cycle count visible separately from charge and health");
            snapshot.BatteryCyclesPerDevice = true;
            snapshot.BatteryCycleCount = null;
            snapshot.BatteryCycleDetail = "Батарея 1: 137; батарея 2: 92";
            dashboard.Render(snapshot);
            Equal("По батареям", Text(window, "BatteryCycles"), "Multiple battery cycles cannot be summed");
            Equal(snapshot.BatteryCycleDetail, ((TextBlock)window.FindName("BatteryCycles")).ToolTip as string, "Per-battery values accessible");
            snapshot.BatteryCyclesPerDevice = false;
            snapshot.BatteryCycleDetail = "";
            snapshot.BatteryDesignWh = null;
            snapshot.BatteryFullChargeWh = null;
            dashboard.Render(snapshot);
            Equal("Нет данных", Text(window, "BatteryDesignCapacity"), "Unknown capacity must not become zero");
            Equal("Нет данных", Text(window, "BatteryFullCapacity"), "Unknown full-charge capacity");
            Equal("Нет данных", Text(window, "BatteryRuntime"), "Unknown capacity cannot give runtime");
            Equal("Нет данных", Text(window, "BatteryCycles"), "Unavailable count must not become zero");
            snapshot.BatteryPresent = false;
            dashboard.Render(snapshot);
            Equal("—", Text(window, "BatteryDesignCapacity"), "Desktop has no battery capacity");
            Equal("—", Text(window, "BatteryFullCapacity"), "Desktop has no full-charge capacity");
            Equal("—", Text(window, "BatteryRuntime"), "No battery means no runtime");
            Equal("—", Text(window, "BatteryCycles"), "No battery has no cycle count");
            snapshot.BatteryPresent = true;
            snapshot.BatteryDesignWh = 51.31;
            snapshot.BatteryFullChargeWh = 31.937;
            snapshot.BatteryCycleCount = 137;
            dashboard.Render(snapshot);
            if (artifactDirectory != null) { Program.Render(window, Path.Combine(artifactDirectory, "battery-capacity-960.png"), 960, 680); }
        }

        private static Dictionary<string, object> Module(ulong? capacity, uint form)
        {
            return new Dictionary<string, object> { { "Capacity", capacity }, { "FormFactor", form } };
        }

        private static Dictionary<string, object> MemoryArray(uint? slots, uint use)
        {
            return new Dictionary<string, object> { { "MemoryDevices", slots }, { "Use", use } };
        }

        private static void MemorySlotCounts()
        {
            var snapshot = new Snapshot();
            var modules = new List<Dictionary<string, object>> { Module(16UL * 1073741824, 12), Module(16UL * 1073741824, 12), Module(0, 0) };
            HardwareMonitor.ApplyMemorySlots(snapshot, modules, new List<Dictionary<string, object>> { MemoryArray(2, 3) });
            Equal((int?)2, snapshot.RamModuleCount, "Only populated modules count");
            Equal((int?)2, snapshot.RamSlotCount, "Total sockets");
            Equal((int?)0, snapshot.RamFreeSlots, "Fully populated sockets");
            True(snapshot.RamHasModularFormFactor, "SO-DIMM is modular");
            HardwareMonitor.ApplyMemorySlots(snapshot, modules, new List<Dictionary<string, object>> { MemoryArray(2, 3), MemoryArray(2, 3), MemoryArray(8, 4) });
            Equal((int?)4, snapshot.RamSlotCount, "System arrays summed; video memory excluded");
            Equal((int?)2, snapshot.RamFreeSlots, "Free sockets across system arrays");
        }

        private static void MemorySlotUnknowns()
        {
            var snapshot = new Snapshot();
            var modules = new List<Dictionary<string, object>> { Module(1073741824, 8), Module(1073741824, 8) };
            foreach (uint? slots in new uint?[] { null, 0, 1 })
            {
                HardwareMonitor.ApplyMemorySlots(snapshot, modules, new List<Dictionary<string, object>> { MemoryArray(slots, 3) });
                Equal((int?)2, snapshot.RamModuleCount, "Installed count survives missing slot information");
                Equal((int?)null, snapshot.RamFreeSlots, "Invalid totals must not become zero free slots");
            }
            HardwareMonitor.ApplyMemorySlots(snapshot, modules, new List<Dictionary<string, object>> { MemoryArray(2, 3), MemoryArray(null, 3) });
            Equal((int?)null, snapshot.RamFreeSlots, "Partial array data is unknown");
            foreach (uint form in new uint[] { 0, 21 })
            {
                HardwareMonitor.ApplyMemorySlots(snapshot, new List<Dictionary<string, object>> { Module(1073741824, form) }, new List<Dictionary<string, object>> { MemoryArray(4, 3) });
                Equal((int?)1, snapshot.RamModuleCount, "Detected memory devices retained");
                True(!snapshot.RamHasModularFormFactor, "Unknown/BGA does not imply a removable stick");
                Equal((int?)null, snapshot.RamFreeSlots, "Chip packages do not imply empty sockets");
            }
            HardwareMonitor.ApplyMemorySlots(snapshot, new List<Dictionary<string, object>>(), new List<Dictionary<string, object>> { MemoryArray(2, 3) });
            Equal((int?)null, snapshot.RamModuleCount, "Missing memory inventory is not zero");
            HardwareMonitor.ApplyMemorySlots(snapshot, new List<Dictionary<string, object>> { Module(null, 12) }, new List<Dictionary<string, object>> { MemoryArray(2, 3) });
            Equal((int?)null, snapshot.RamFreeSlots, "Unknown capacity is incomplete inventory");
        }

        private static void MemorySlotWindow()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            Snapshot snapshot = Fixture();
            snapshot.RamModuleCount = 2;
            snapshot.RamSlotCount = 2;
            snapshot.RamFreeSlots = 0;
            snapshot.RamHasModularFormFactor = true;
            snapshot.RamSlotsNote = "По данным BIOS: 2 слота";
            snapshot.RamConfiguration = "16 GB DDR5 + 16 GB DDR5";
            snapshot.RamType = "DDR5";
            dashboard.Render(snapshot);
            Equal("Тип памяти: DDR5", Text(window, "RamType"), "Memory generation explicitly labelled");
            Equal("Планок: 2 · Свободно слотов: 0 из 2", Text(window, "RamSlots"), "Installed, free and total slot counts visible");
            Equal(snapshot.RamSlotsNote, ((TextBlock)window.FindName("RamSlots")).ToolTip as string, "BIOS explanation accessible");
            snapshot.RamHasModularFormFactor = false;
            snapshot.RamFreeSlots = null;
            snapshot.RamSlotCount = null;
            snapshot.RamType = "";
            dashboard.Render(snapshot);
            Equal("Тип памяти: Нет данных", Text(window, "RamType"), "Unavailable type is not guessed");
            Equal("Устройств памяти: 2 · Свободно слотов: —", Text(window, "RamSlots"), "Unconfirmed hardware labelled honestly");
            snapshot.RamHasModularFormFactor = true;
            snapshot.RamFreeSlots = 0;
            snapshot.RamType = "DDR5";
            dashboard.Render(snapshot);
            if (artifactDirectory != null) { Program.Render(window, Path.Combine(artifactDirectory, "memory-slots-960.png"), 960, 680); }
        }

        private static void ZeroValues()
        {
            Equal("0 %", Display.Number(0, " %", 0), "Empty battery");
            Equal("0 °C", Display.Number(0, " °C", 0), "Real zero temperature");
            Equal("0,0 MB/s", Display.Speed(0), "Idle disk speed");
            Equal("0,0 GB", Display.Memory(0), "Zero allocated memory");
        }

        private static void LargeMemory()
        {
            Equal("12,0 GB", Display.Memory(12UL * 1024 * 1024 * 1024), "12 GiB GPU");
            Equal("64,0 GB", Display.Memory(64UL * 1024 * 1024 * 1024), "64 GiB RAM");
        }

        private static void Units()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                Equal("3,5 ГГц", Display.Number(3.5, " ГГц", 1), "Russian decimal separator");
                Equal("1,0 GB", Display.Memory(1073741824UL), "Binary memory units");
                Equal("512 GB", Display.DiskSize(512000000000UL), "Decimal disk capacity");
                Equal("1,00 TB", Display.DiskSize(1000000000000UL), "Terabyte boundary");
                Equal("500,0 MB/s", Display.Speed(500000000), "Decimal throughput");
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static void GaugeBounds()
        {
            Equal(0d, Display.Percent(null), "Missing percentage gauge");
            Equal(0d, Display.Percent(double.NaN), "NaN gauge");
            Equal(0d, Display.Percent(double.PositiveInfinity), "Positive infinity gauge");
            Equal(0d, Display.Percent(double.NegativeInfinity), "Negative infinity gauge");
            Equal(0d, Display.Percent(-5), "Negative gauge");
            Equal(0d, Display.Percent(0), "Zero gauge");
            Equal(57.5d, Display.Percent(57.5), "Valid fraction");
            Equal(100d, Display.Percent(125), "Overfull gauge");
        }

        private static Snapshot Fixture()
        {
            return new Snapshot
            {
                CpuName = "Test CPU", CpuLoad = 0, CpuCores = 8, CpuThreads = 16,
                RamTotalBytes = 32UL * 1073741824, RamUsedBytes = 8UL * 1073741824,
                BatteryPresent = false,
                Gpus = new List<GpuSnapshot>
                {
                    new GpuSnapshot { Name = "GPU A", MemoryBytes = 12UL * 1073741824 },
                    new GpuSnapshot { Name = "GPU B", MemoryBytes = 24UL * 1073741824 }
                },
                Disks = new List<DiskSnapshot>
                {
                    new DiskSnapshot { Index = 0, Name = "SSD", ReadBytesPerSecond = 0, WriteBytesPerSecond = 0 },
                    new DiskSnapshot { Index = 1, Name = "SSD", ReadBytesPerSecond = 3000000, WriteBytesPerSecond = 2000000 }
                }
            };
        }

        private static string Text(Window window, string name)
        {
            return ((TextBlock)window.FindName(name)).Text;
        }

        private static void DesktopWindow()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            dashboard.Render(Fixture());
            Equal("Нет батареи", Text(window, "BatteryPercent"), "Desktop battery");
            Equal("Нет данных", Text(window, "CpuTemperature"), "Missing CPU sensor");
            Equal("Нет данных", Text(window, "GpuTemperature"), "Missing GPU sensor");
            True(Convert.ToString(((TextBlock)window.FindName("DiskRead")).ToolTip).Contains("Сейчас: 0,0"), "Valid idle disk throughput remains available in details");
            Snapshot unknown = Fixture();
            unknown.BatteryPresent = null;
            dashboard.Render(unknown);
            Equal("Нет данных", Text(window, "BatteryPercent"), "Unknown battery is not absence");
            unknown.BatteryPresent = true;
            unknown.BatteryPercent = 0;
            dashboard.Render(unknown);
            Equal("0 %", Text(window, "BatteryPercent"), "Empty battery must show zero");
            if (artifactDirectory != null) { Program.Render(window, Path.Combine(artifactDirectory, "edge-cases-960.png"), 960, 680); }
        }

        private static void DeviceSelection()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            dashboard.Render(Fixture());
            var gpu = (ComboBox)window.FindName("GpuSelector");
            var disk = (ComboBox)window.FindName("DiskSelector");
            gpu.SelectedIndex = 1;
            disk.SelectedIndex = 1;
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 1, ReadBytesPerSecond = 3000000000, WriteBytesPerSecond = 2000000000, Stage = "Завершено", Percent = 100 });
            dashboard.Render(Fixture());
            Equal(1, gpu.SelectedIndex, "GPU selection survives refresh");
            Equal(1, disk.SelectedIndex, "Identically named physical disks preserve index");
            Equal("24,0 GB", Text(window, "GpuMemory"), "Selected GPU VRAM");
            Equal("3000,0 MB/s", Text(window, "DiskRead"), "Benchmark must survive live refresh");
            disk.SelectedIndex = 0;
            True(!Text(window, "DiskRead").Contains("3000"), "Benchmark must not leak to another disk");
        }

        private static void BenchmarkControls()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            dashboard.Render(Fixture());
            Equal("—", Text(window, "DiskRead"), "No read test result before checks start");
            Equal("—", Text(window, "DiskWrite"), "No write test result before checks start");
            True(Text(window, "BenchmarkStage").Contains("после показа"), "Pending result explained");
            dashboard.RenderStartupStatus("Характеристики готовы · выполняются проверки");
            dashboard.Render(Fixture());
            Equal("Характеристики готовы · выполняются проверки", Text(window, "StartupStatus"), "Live refresh does not erase startup phase");
            bool stopped = false;
            bool restarted = false;
            dashboard.StopTestRequested += delegate { stopped = true; };
            dashboard.RunTestRequested += delegate { restarted = true; };
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, IsRunning = true, Stage = "Запись", Percent = 20 });
            Equal("Тест…", Text(window, "DiskRead"), "Running test marked instead of fabricated speed");
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, IsRunning = true, Stage = "Чтение", Percent = 60, WriteBytesPerSecond = 500000000 });
            Equal("500,0 MB/s", Text(window, "DiskWrite"), "Write result appears before read test is complete");
            Equal("Тест…", Text(window, "DiskRead"), "Read remains pending while write is available");
            dashboard.Render(Fixture());
            Equal("500,0 MB/s", Text(window, "DiskWrite"), "Partial completed result survives live refresh");
            var stop = (Button)window.FindName("StopTestButton");
            var run = (Button)window.FindName("RunTestButton");
            True(stop.IsEnabled && !run.IsEnabled, "Only stop while benchmark runs");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            True(stopped, "Stop event");
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, IsRunning = false, Stage = "Остановлено" });
            True(!stop.IsEnabled && run.IsEnabled, "Can rerun after cancellation");
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            True(restarted, "Retry event");
        }

        private static void BenchmarkCancellation()
        {
            string temp = Path.GetTempPath();
            var before = new HashSet<string>(Directory.GetFiles(temp, "PcPulse-test-*.tmp"));
            var cancellation = new CancellationTokenSource();
            BenchmarkProgress terminal = null;
            uint diskIndex = StorageTopology.ReadVolumeDiskNumbers(Path.GetPathRoot(temp)).Single();
            DiskBenchmark.RunWithSize(new DiskSnapshot { Index = diskIndex, TestVolumeRoot = Path.GetPathRoot(temp) }, delegate(BenchmarkProgress state)
            {
                if (state.IsRunning && state.Percent > 0) { cancellation.Cancel(); }
                if (!state.IsRunning) { terminal = state; }
            }, cancellation.Token, 16L * 1024 * 1024);
            True(terminal != null && terminal.Stage == "Тест остановлен", "Cancellation must finish cleanly");
            True(!terminal.ReadBytesPerSecond.HasValue && !terminal.WriteBytesPerSecond.HasValue, "Partial speed must not be accepted");
            True(Directory.GetFiles(temp, "PcPulse-test-*.tmp").All(before.Contains), "Test file removed on cancellation");
            cancellation.Dispose();
        }

        private static void BenchmarkUnavailable()
        {
            BenchmarkProgress terminal = null;
            DiskBenchmark.Run(new DiskSnapshot { TestVolumeRoot = "" }, delegate(BenchmarkProgress state) { if (!state.IsRunning) { terminal = state; } }, CancellationToken.None);
            True(terminal != null && terminal.Error.Length > 0, "Unsupported disk must report a reason");
            True(!terminal.ReadBytesPerSecond.HasValue && !terminal.WriteBytesPerSecond.HasValue, "Failure must not fabricate speed");
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            DiskBenchmark.Run(new DiskSnapshot { TestVolumeRoot = "" }, delegate(BenchmarkProgress state) { if (!state.IsRunning) { terminal = state; } }, cancellation.Token);
            Equal("Тест остановлен", terminal.Stage, "Pre-cancel prevents disk access");
            cancellation.Dispose();
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine("FAIL: " + name + "\n" + exception);
            }
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + ": expected [" + expected + "]; actual [" + actual + "].");
            }
        }

        private static void True(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
