using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PcPulse;

namespace PcPulseTests
{
    public static class HistoryPresentationTests
    {
        public static void Run()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            dashboard.SetAutomaticTestsEnabled(false);
            Snapshot data = Fixture();
            dashboard.Render(data);
            Require(Text(window, "BenchmarkStage").Contains("запустите кнопкой"), "A missing SSD result does not promise an automatic test");
            Require(Text(window, "GpuBenchmarkResult").Contains("запустите кнопкой"), "A missing GPU result asks for manual action");
            Require(!Tooltip(window, "GpuBenchmarkResult").Contains("начнётся"), "The GPU tooltip also respects manual-only mode");
            Require(Enabled(window, "RunTestButton") && Enabled(window, "RunGpuTestButton"), "Manual-only mode keeps both test buttons enabled");
            int diskRequests = 0, gpuRequests = 0;
            dashboard.RunTestRequested += delegate { diskRequests++; };
            dashboard.RunGpuTestRequested += delegate { gpuRequests++; };
            ((Button)window.FindName("RunTestButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)window.FindName("RunGpuTestButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(diskRequests == 1 && gpuRequests == 1, "Both manual actions remain wired");

            var testedAt = new DateTime(2026, 10, 12, 9, 31, 0, DateTimeKind.Utc);
            string date = testedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
            string savedStage = "Сохранено · " + date;
            var disk = new BenchmarkProgress
            {
                DiskIndex = 0, Stage = savedStage, Percent = 100, FromHistory = true, TestedAtUtc = testedAt,
                ReadBytesPerSecond = 5000000000, WriteBytesPerSecond = 3500000000,
                Engine = "DiskSpd", Profile = "quick-v2", TestSizeBytes = 1073741824, BlockSizeBytes = 1048576,
                QueueDepth = 8, ThreadCount = 1
            };
            var gpu = new GpuBenchmarkProgress
            {
                AdapterIndex = 0, AdapterName = data.Gpus[0].Name, Stage = savedStage,
                Completed = true, Percent = 100, FramesPerSecond = 155.4, FrameTimeMs = 6.435, DurationSeconds = 15,
                MaxTemperature = 56, FromHistory = true, TestedAtUtc = testedAt
            };
            dashboard.RenderBenchmark(disk);
            dashboard.RenderGpuBenchmark(gpu);
            Require(Text(window, "BenchmarkStage") == savedStage, "The SSD history stage preserves the saved date");
            Require(Text(window, "GpuBenchmarkResult").Contains("155,4 FPS") && Text(window, "GpuBenchmarkResult").Contains(savedStage),
                "The GPU history line distinguishes saved FPS from a fresh run");
            foreach (string name in new[] { "DiskRead", "DiskWrite", "GpuBenchmarkResult" })
            {
                Require(Tooltip(window, name).Contains("Сохранённый результат теста") && Tooltip(window, name).Contains(date),
                    name + " exposes the result type and actual test time");
            }
            Require(Tooltip(window, "DiskRead").Contains("Сейчас: 1,0 MB/s"), "Current disk traffic is distinct from the old benchmark");

            // UI progress is copied; provider reuse and later telemetry cannot mutate history.
            disk.FromHistory = false;
            disk.TestedAtUtc = null;
            disk.ReadBytesPerSecond = 1;
            gpu.FromHistory = false;
            gpu.TestedAtUtc = null;
            gpu.FramesPerSecond = 1;
            data.CpuTemperature = 48;
            data.Disks[0].Temperature = 38;
            data.Disks[0].ReadBytesPerSecond = 2000000;
            dashboard.Render(data);
            Require(Text(window, "DiskRead") == "5000,0 MB/s" && Text(window, "GpuBenchmarkResult").Contains("155,4 FPS"),
                "Live sensor refresh preserves recovered benchmark results");
            Require(Tooltip(window, "GpuBenchmarkResult").Contains(date) && Tooltip(window, "GpuBenchmarkResult").Contains("Сохранённый результат теста"),
                "GPU copying includes history metadata");
            Require(Tooltip(window, "DiskRead").Contains("Сейчас: 2,0 MB/s"), "Only the live traffic annotation changes on refresh");
            Require(Enabled(window, "RunTestButton") && Enabled(window, "RunGpuTestButton"), "Recovered results remain manually repeatable");

            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress
            {
                AdapterIndex = 0, AdapterName = data.Gpus[0].Name,
                Stage = "Новый тест не завершён · " + savedStage,
                Completed = true, Percent = 100, FramesPerSecond = 155.4, DurationSeconds = 15,
                FromHistory = true, TestedAtUtc = testedAt, Error = "Synthetic GPU driver failure"
            });
            dashboard.Render(data);
            Require(Text(window, "GpuBenchmarkResult").Contains("155,4 FPS")
                && Text(window, "GpuBenchmarkResult").Contains("Новый тест не завершён"),
                "An unsuccessful repeat keeps the saved FPS visible and labels the new failure");
            Require(Tooltip(window, "GpuBenchmarkResult").Contains("Synthetic GPU driver failure")
                && Tooltip(window, "GpuBenchmarkResult").Contains("Сохранённый результат теста")
                && Tooltip(window, "GpuBenchmarkResult").Contains(date),
                "The repeat failure and the old result's timestamp remain distinct in the tooltip");
            Require(Enabled(window, "RunGpuTestButton"), "An unsuccessful repeat does not prevent a new manual attempt");

            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, IsRunning = true, Stage = "Измерение" });
            Require(Text(window, "DiskRead") == "Тест…" && !Tooltip(window, "DiskRead").Contains("Сохранённый результат теста"),
                "A new manual run replaces the old SSD result instead of mislabelling it as current");
            Require(!Enabled(window, "RunTestButton") && !Enabled(window, "RunGpuTestButton"), "The existing no-concurrent-tests guard remains active");
            dashboard.RenderBenchmark(new BenchmarkProgress
            {
                DiskIndex = 0, Stage = "Завершено", Percent = 100,
                ReadBytesPerSecond = 4900000000, WriteBytesPerSecond = 3400000000, TestedAtUtc = testedAt.AddDays(1)
            });
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress
            {
                AdapterIndex = 0, Completed = true, FramesPerSecond = 188, Stage = "Завершено", TestedAtUtc = testedAt.AddDays(1)
            });
            Require(Text(window, "DiskRead") == "4900,0 MB/s" && Text(window, "GpuBenchmarkResult").Contains("188,0 FPS"), "Fresh completion replaces the recovered values");
            Require(!Tooltip(window, "GpuBenchmarkResult").Contains("Сохранённый результат теста"), "Fresh results lose the history marker");
            Require(!Tooltip(window, "GpuBenchmarkResult").Contains("Synthetic GPU driver failure"), "A later successful run clears the old attempt's error");

            data.Disks[0].StorageIdentity = "new-disk";
            data.Gpus[0].AdapterLuidLow++;
            dashboard.Render(data);
            Require(Text(window, "DiskRead") == "—" && Text(window, "BenchmarkStage").Contains("запустите кнопкой"),
                "Replacement storage cannot inherit a saved speed or trigger an automatic test");
            Require(Text(window, "GpuBenchmarkResult").Contains("запустите кнопкой"), "A replacement GPU returns to the manual-only prompt");
            dashboard.SetAutomaticTestsEnabled(true);
            Require(Text(window, "BenchmarkStage").Contains("начнётся") && Text(window, "GpuBenchmarkResult").Contains("ожидает запуска"),
                "The existing automatic startup mode can still be selected explicitly");
        }

        private static Snapshot Fixture()
        {
            var data = new Snapshot { CpuName = "Test CPU", SensorStatus = "Test sensors" };
            data.Disks.Add(new DiskSnapshot
            {
                Index = 0, StorageIdentity = "disk-one", Name = "NVMe test disk", SizeBytes = 512000000000,
                ReadBytesPerSecond = 1000000, Temperature = 35
            });
            data.Gpus.Add(new GpuSnapshot { AdapterIndex = 0, AdapterLuidLow = 17, AdapterLuidHigh = 1, Name = "Test GPU" });
            return data;
        }

        private static string Text(Window window, string name) { return ((TextBlock)window.FindName(name)).Text; }
        private static string Tooltip(Window window, string name) { return Convert.ToString(((TextBlock)window.FindName(name)).ToolTip); }
        private static bool Enabled(Window window, string name) { return ((Button)window.FindName(name)).IsEnabled; }
        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
