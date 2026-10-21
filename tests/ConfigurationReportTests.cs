using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using PcPulse;

namespace PcPulseTests
{
    internal static class ConfigurationReportTests
    {
        internal static void Run()
        {
            BenchmarkSettings custom = BenchmarkSettings.Parse("{\"SchemaVersion\":1,\"Profile\":\"quick-v1\",\"DiskMiB\":256,\"GpuWarmupSeconds\":2,\"GpuMeasurementSeconds\":10}");
            Require(custom.DiskMiB == 256 && custom.GpuMeasurementSeconds == 10 && custom.Note == "", "Supported profile settings must be retained");
            Require(BenchmarkSettings.Parse("{\"Profile\":\"quick-v1\"}").DiskMiB == 512, "Partial legacy settings keep the original default file size");
            BenchmarkSettings peak = BenchmarkSettings.Parse("{\"SchemaVersion\":1,\"Profile\":\"quick-v2\",\"DiskMiB\":1024}");
            Require(peak.Profile == "quick-v2" && peak.Note == "", "DiskSpd accepts its fixed one GiB profile");
            foreach (string invalid in new[] {
                "null", "not json", "{\"DiskMiB\":-1}", "{\"DiskMiB\":4096}", "{\"DiskMiB\":65}",
                "{\"GpuMeasurementSeconds\":3600}", "{\"GpuWarmupSeconds\":0}", "{\"SchemaVersion\":99}", "{\"Profile\":\"unknown\"}",
                "{\"DiskMiB\":2147483648}", "{\"DiskMiB\":\"wrong\"}", "{\"GpuMeasurementSeconds\":9.5}",
                "{\"Profile\":\"quick-v2\",\"DiskMiB\":256}"
            })
            {
                BenchmarkSettings settings = BenchmarkSettings.Parse(invalid);
                Require(settings.Profile == "quick-v2" && settings.DiskMiB == 1024 && settings.GpuMeasurementSeconds == 15 && settings.Note.Length > 0, "Invalid settings must fall back with an explanation");
            }

            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            bool requested = false;
            dashboard.SaveReportRequested += delegate { requested = true; };
            bool devicesRequested = false;
            dashboard.PeripheralTestsRequested += delegate { devicesRequested = true; };
            ((Button)window.FindName("PeripheralTestsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(devicesRequested, "Device button opens the manual test workflow");
            ((Button)window.FindName("SaveReportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(requested, "Report button must dispatch the save request");
            var snapshot = new Snapshot { ComputerManufacturer = "HP", ComputerModel = "Test model", CpuTemperature = null };
            snapshot.Disks.Add(new DiskSnapshot { Index = 2, Name = "Test drive", HealthPercent = null });
            dashboard.Render(snapshot);
            dashboard.RenderPorts(new PortSpecification { Model = "Test model", SourceUrl = "https://support.hp.com/test", Note = "Optional ports are not verified" });
            var partial = new BenchmarkProgress { DiskIndex = 2, Stage = "Тест остановлен", TestSizeBytes = 536870912, BlockSizeBytes = 4194304,
                Engine = "Fixture engine", EngineVersion = "test", Profile = "fixture-seq", CacheMode = "Fixture cache policy",
                QueueDepth = 4, ThreadCount = 2, WarmupSeconds = 1, MeasurementSeconds = 3, ReadBytes = 12000000, ReadSeconds = 3 };
            dashboard.RenderBenchmark(partial);
            partial.QueueDepth = 99;
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, AdapterName = "Test GPU", Stage = "Недоступен", Error = "Timestamp unavailable", Completed = false });
            dashboard.SetChecksRunning(true);
            dashboard.SetPeripheralResults(new[] { new PeripheralTestResult { Kind = "camera", Outcome = "problem", DeviceId = "fixture", DeviceName = "Fixture camera", Detail = "Manual verdict fixture", TestedAtUtc = DateTime.UtcNow.ToString("o") } });
            DiagnosticReport report = new JavaScriptSerializer().Deserialize<DiagnosticReport>(dashboard.CreateReport());
            Require(report.SchemaVersion == 1 && report.ChecksRunning && report.AppVersion.Length > 0, "Report must preserve its version and partial state");
            Require(!report.Hardware.CpuTemperature.HasValue && !report.Hardware.Disks[0].HealthPercent.HasValue, "Unavailable readings must remain null");
            Require(report.DiskTests.Count == 1 && !report.DiskTests[0].ReadBytesPerSecond.HasValue && report.DiskTests[0].TestSizeBytes == 536870912, "Cancelled disk result and its parameters must survive export");
            BenchmarkProgress saved = report.DiskTests[0];
            Require(saved.Engine == "Fixture engine" && saved.EngineVersion == "test" && saved.Profile == "fixture-seq"
                && saved.CacheMode == "Fixture cache policy" && saved.QueueDepth == 4 && saved.ThreadCount == 2
                && saved.WarmupSeconds == 1 && saved.MeasurementSeconds == 3 && saved.ReadBytes == 12000000 && saved.ReadSeconds == 3,
                "A report preserves measured profile metadata through a copied dashboard result");
            Require(Dashboard.DiskProfileCaption(saved).Contains("Q4T2") && !Dashboard.DiskProfileCaption(saved).Contains("Q8"), "Historical result uses its own profile");
            var oldResult = new JavaScriptSerializer().Deserialize<BenchmarkProgress>("{\"DiskIndex\":2,\"TestSizeBytes\":536870912}");
            Require(oldResult.Method == "" && Dashboard.DiskProfileCaption(oldResult).Contains("параметры не сохранены"), "Old results cannot acquire an invented engine or queue");
            dashboard.SetSnapshotMode();
            ((ComboBox)window.FindName("DiskSelector")).SelectedIndex = 0;
            Require(report.GpuTests.Count == 1 && !report.GpuTests[0].Completed && report.GpuTests[0].Error.Length > 0, "Failed GPU result must not become successful");
            Require(report.Ports.SourceUrl == "https://support.hp.com/test", "Report must retain specification source");
            Require(report.PeripheralTests.Count == 1 && report.PeripheralTests[0].Outcome == "problem" && report.PeripheralTests[0].ConfirmedBy == "user", "Dashboard report preserves manual device verdicts with provenance");
            window.Close();
            Window oldWindow = Program.LoadWindow();
            var oldDashboard = new Dashboard(oldWindow);
            oldDashboard.Render(snapshot);
            oldDashboard.SetSnapshotMode();
            Require(((TextBlock)oldWindow.FindName("DiskTestProfile")).Text == "Параметры теста не сохранены", "A snapshot without results cannot acquire the current profile");
            Require(((TextBlock)oldWindow.FindName("BenchmarkStage")).Text == "Тест скорости не запускался", "A static preview cannot promise automatic tests");
            oldWindow.Close();
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
