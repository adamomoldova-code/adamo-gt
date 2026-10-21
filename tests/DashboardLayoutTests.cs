using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcPulse;

namespace PcPulseTests
{
    public static class DashboardLayoutTests
    {
        public static void Run()
        {
            VerifyLayout(960, 680, false);
            VerifyLayout(1200, 720, false);
            VerifyLayout(960, 680, true);
            VerifyLayout(832, 500, false);
            VerifyStateTransitions();
        }

        private static void VerifyLayout(double width, double height, bool unavailable, double? dpiScale = null)
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            Snapshot snapshot = Fixture();
            if (unavailable)
            {
                snapshot.CpuL1CacheBytes = snapshot.CpuL2CacheBytes = snapshot.CpuL3CacheBytes = null;
                snapshot.RamInstalledBytes = snapshot.RamMaximumBytes = null;
                snapshot.BatteryHealthPercent = null;
                snapshot.BatteryDesignWh = snapshot.BatteryFullChargeWh = null;
                snapshot.BatteryCycleCount = null;
                snapshot.Disks[0].HealthPercent = snapshot.Disks[0].Temperature = null;
                snapshot.Disks[0].PowerOnHours = null;
                snapshot.Disks[0].CriticalWarning = null;
            }
            dashboard.Render(snapshot);
            dashboard.RenderPorts(Ports());
            if (!unavailable)
            {
                dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, ReadBytesPerSecond = 5174400000, WriteBytesPerSecond = 4170900000, Percent = 100, Stage = "Завершено" });
                dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, Completed = true, FramesPerSecond = 123.4, DurationSeconds = 30, Stage = "Завершено" });
            }
            FrameworkElement content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content, Width = width, Height = height, Resources = window.Resources };
            ApplyWindowPresentation(host, window);
            if (dpiScale.HasValue) { VisualTreeHelper.SetRootDpi(host, new DpiScale(dpiScale.Value, dpiScale.Value)); }
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            var required = new Dictionary<string, string[]>
            {
                { "CpuCard", new[] { "CpuName", "CpuTopology", "CpuClock", "CpuTemperature", "CpuCache" } },
                { "RamCard", new[] { "RamInstalled", "RamType", "RamRate", "RamMaximum", "RamSlots", "RamClock" } },
                { "GpuCard", new[] { "GpuSelector", "GpuMemory", "GpuSharedMemory", "GpuTemperature", "GpuBenchmarkResult" } },
                { "ScreenCard", new[] { "ScreenSelector", "ScreenResolution", "ScreenMatrix", "ScreenBrightness" } },
                { "PortsCard", new[] { "PortsScroll", "PortsStatus", "PortsSourceButton" } },
                { "DiskCard", new[] { "DiskSelector", "DiskSize", "DiskHealth", "PowerOnHours", "DiskTemperature", "SmartWarning", "DiskRead", "DiskWrite", "BenchmarkStage" } },
                { "BatteryCard", new[] { "BatteryHealth", "BatteryPercent", "BatteryFullCapacity", "BatteryDesignCapacity", "BatteryCycles", "BatteryRuntime", "BatteryRuntimeLabel" } }
            };
            foreach (KeyValuePair<string, string[]> entry in required)
            {
                FrameworkElement card = (FrameworkElement)window.FindName(entry.Key);
                AssertInside(card, content, entry.Key + " within the page");
                foreach (string name in entry.Value)
                {
                    FrameworkElement element = (FrameworkElement)window.FindName(name);
                    AssertInside(element, card, name + " within " + entry.Key + " at " + width + "×" + height);
                }
            }
            AssertInside((FrameworkElement)window.FindName("AppLogo"), content, "AppLogo within the page");
            AssertInside((FrameworkElement)window.FindName("ComputerModel"), content, "ComputerModel within the page");
            foreach (string name in new[] { "ComputerModel", "RamInstalled", "RamMaximum", "CpuCache", "DiskHealth", "PowerOnHours", "DiskTemperature", "DiskRead", "DiskWrite", "BatteryHealth", "BatteryFullCapacity", "BatteryDesignCapacity", "BatteryCycles" })
            {
                var text = (TextBlock)window.FindName(name);
                var measured = MeasureText(text);
                Assert(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1.5, name + " must not truncate a measurement at " + width);
                Assert(measured.Height <= text.ActualHeight + 1.5, name + " must not clip a measurement vertically at " + width);
                Assert(text.FontSize >= 12, name + " must remain readable");
            }
            VerifyTextGeometry(content, width);
            VerifyFooterActions(window, content);
            var portsScroll = (ScrollViewer)window.FindName("PortsScroll");
            Assert(portsScroll.ScrollableHeight < 1, "The reference laptop's standard and optional ports fit without scrolling at " + width + "×" + height
                + "; extent " + portsScroll.ExtentHeight + ", viewport " + portsScroll.ViewportHeight);
        }

        private static void VerifyStateTransitions()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            Snapshot data = Fixture();
            dashboard.Render(data);
            Assert(Text(window, "RamInstalled") == "32 GB", "Installed RAM is separate from Windows usable RAM");
            Assert(Text(window, "RamMaximum").Contains("64 GB"), "Maximum RAM remains visible");
            Assert(Text(window, "CpuCache").Contains("1248 KB") && Text(window, "CpuCache").Contains("11,5 MB"), "All cache levels use their actual capacities");
            Assert(Text(window, "CpuClockLabel").Contains("BIOS"), "Static BIOS clock is labelled");
            Assert(Text(window, "DiskHealth") == "95 %", "SSD endurance remains separate from warnings");
            data.Disks[0].CriticalWarning = 1;
            dashboard.Render(data);
            Assert(Text(window, "SmartWarning").Contains("есть"), "A warning remains visible even with high remaining endurance");
            Assert(Text(window, "ScreenMatrix").Contains("подтверждён") && Text(window, "ScreenBrightness").Contains("подтверждена"), "Unconfirmed panel attributes are not guessed");

            dashboard.SetChecksRunning(true);
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, Stage = "Завершено", Percent = 100 });
            dashboard.Render(data);
            Assert(((Button)window.FindName("StopTestButton")).IsEnabled, "Disk completion cannot disable stopping the global sequence");
            Assert(!((Button)window.FindName("RunGpuTestButton")).IsEnabled, "GPU cannot start concurrently with the sequence");
            dashboard.SetChecksRunning(false);
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, IsRunning = true, Stage = "Измерение", Percent = 25 });
            Assert(((Button)window.FindName("StopTestButton")).IsEnabled, "GPU work can be stopped");
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, Completed = true, FramesPerSecond = 123.4, DurationSeconds = 30, Stage = "Завершено" });
            ((ComboBox)window.FindName("GpuSelector")).SelectedIndex = 1;
            Assert(!Text(window, "GpuBenchmarkResult").Contains("123,4"), "GPU result cannot leak into another adapter");
            ((ComboBox)window.FindName("GpuSelector")).SelectedIndex = 0;
            dashboard.Render(data);
            Assert(Text(window, "GpuBenchmarkResult").Contains("123,4 FPS"), "GPU result survives live telemetry");
            Assert(dashboard.SelectedGpuAdapterIndex == 0, "GPU test uses the selected adapter");
            ((ComboBox)window.FindName("ScreenSelector")).SelectedIndex = 1;
            dashboard.Render(data);
            Assert(Text(window, "ScreenResolution").Contains("2560 × 1440"), "Screen selection survives live telemetry");
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = null, Stage = "Недоступен", Error = "No hardware adapter" });
            Assert(Text(window, "GpuBenchmarkResult").Contains("123,4 FPS"), "An unidentified failure cannot replace a known adapter's result");
        }

        private static Snapshot Fixture()
        {
            var data = new Snapshot
            {
                ComputerManufacturer = "HP", ComputerModel = "HP ZBook Firefly 14 inch G9 Mobile Workstation PC", WindowsName = "Windows 11 Pro",
                CpuName = "Intel Core i7-1280P", CpuCores = 14, CpuThreads = 20, CpuClockMhz = 1800, CpuClockSource = "Данные BIOS/SMBIOS, не текущая частота",
                CpuL1CacheBytes = 1248UL * 1024, CpuL2CacheBytes = 115UL * 1048576 / 10, CpuL3CacheBytes = 24UL * 1048576,
                RamInstalledBytes = 32UL * 1073741824, RamTotalBytes = 33986697625, RamMaximumBytes = 64UL * 1073741824,
                RamMaximumSource = "BIOS/SMBIOS", RamType = "DDR5", RamRate = "4800 МТ/с (настроено в BIOS)", RamClockMhz = 2400,
                RamConfiguration = "16 GB DDR5 + 16 GB DDR5", RamModuleCount = 2, RamSlotCount = 2, RamFreeSlots = 0, RamHasModularFormFactor = true,
                BatteryPresent = true, BatteryPercent = 97, BatteryHealthPercent = 62.24, BatteryDesignWh = 51.31, BatteryFullChargeWh = 31.937, BatteryCycleCount = 87,
                BatteryStatus = "Подключено питание", SensorStatus = "Часть датчиков недоступна"
            };
            data.Disks.Add(new DiskSnapshot { Index = 0, Name = "WD PC SN810 SDCQNRY-512G-1006", MediaType = "SSD · NVMe", SizeBytes = 512110190592, HealthPercent = 95, PowerOnHours = 5737, Temperature = 34, CriticalWarning = 0 });
            data.Gpus.Add(new GpuSnapshot { AdapterIndex = 0, Name = "Intel Iris Xe Graphics", IsIntegrated = true, MemoryBytes = 128UL * 1048576, SharedMemoryBytes = 16911433728 });
            data.Gpus.Add(new GpuSnapshot { AdapterIndex = 1, Name = "Second hardware adapter", IsIntegrated = false, MemoryBytes = 8UL * 1073741824 });
            data.Screens.Add(new ScreenSnapshot { Name = "AUO7A99", Width = 1920, Height = 1200, RefreshHz = 60, IsInternal = true });
            data.Screens.Add(new ScreenSnapshot { Name = "External display", Width = 2560, Height = 1440, RefreshHz = 60, IsInternal = false });
            return data;
        }

        private static PortSpecification Ports()
        {
            var data = new PortSpecification { Model = "HP ZBook Firefly 14 G9", Status = "По спецификации HP", SourceUrl = "https://support.hp.com/us-en/document/ish_5881971-5882022-16", IsCached = true };
            data.Ports.Add(new PortEntry { Name = "USB-C / Thunderbolt 4", Count = 2 });
            data.Ports.Add(new PortEntry { Name = "USB-A", Count = 2 });
            data.Ports.Add(new PortEntry { Name = "HDMI 2.0", Count = 1 });
            data.Ports.Add(new PortEntry { Name = "Аудио 3,5 мм", Count = 1 });
            data.Ports.Add(new PortEntry { Name = "Слот замка", Count = 1 });
            data.Ports.Add(new PortEntry { Name = "Nano SIM", Count = 1, Optional = true });
            data.Ports.Add(new PortEntry { Name = "SmartCard", Count = 1, Optional = true });
            return data;
        }

        private static string Text(Window window, string name) { return ((TextBlock)window.FindName(name)).Text; }

        internal static void ApplyWindowPresentation(FrameworkElement host, Window window)
        {
            host.UseLayoutRounding = window.UseLayoutRounding;
            host.SnapsToDevicePixels = window.SnapsToDevicePixels;
            host.FlowDirection = window.FlowDirection;
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
            System.Windows.Documents.TextElement.SetFontStyle(host, window.FontStyle);
            System.Windows.Documents.TextElement.SetFontWeight(host, window.FontWeight);
            System.Windows.Documents.TextElement.SetFontStretch(host, window.FontStretch);
            TextOptions.SetTextFormattingMode(host, TextOptions.GetTextFormattingMode(window));
            TextOptions.SetTextRenderingMode(host, TextOptions.GetTextRenderingMode(window));
            TextOptions.SetTextHintingMode(host, TextOptions.GetTextHintingMode(window));
        }

        internal static FormattedText MeasureText(TextBlock text)
        {
            return new FormattedText(text.Text, CultureInfo.GetCultureInfo("ru-RU"), text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
        }

        internal static void VerifyTextGeometry(FrameworkElement root, double width)
        {
            var clippedLines = new List<string>();
            VerifyTextHeight(root, width, root, clippedLines);
            Assert(clippedLines.Count == 0, String.Join("\n", clippedLines.ToArray()));
        }

        internal static void VerifyFooterActions(Window window, FrameworkElement root)
        {
            var status = (FrameworkElement)window.FindName("StartupStatus");
            Rect previous = status.TransformToAncestor(root).TransformBounds(new Rect(status.RenderSize));
            var footer = (FrameworkElement)window.FindName("BrandFooter");
            AssertInside(footer, root, "Brand footer remains within the page");
            Rect footerBounds = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
            foreach (string name in new[] { "PeripheralTestsButton", "SensorsButton", "StopTestButton", "RunTestButton" })
            {
                var button = (FrameworkElement)window.FindName(name);
                AssertInside(button, root, name + " remains accessible in the footer");
                Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Assert(bounds.Left >= previous.Right - 1, name + " must not overlap the preceding footer item");
                Assert(bounds.Bottom <= footerBounds.Top + 1, name + " must not overlap the lower brand footer");
                VerifyButtonLabels(button, button);
                previous = bounds;
            }
        }

        private static void VerifyButtonLabels(DependencyObject element, FrameworkElement button)
        {
            var text = element as TextBlock;
            if (text != null && !String.IsNullOrWhiteSpace(text.Text))
            {
                AssertInside(text, button, button.Name + " label");
                Assert(MeasureText(text).WidthIncludingTrailingWhitespace <= text.ActualWidth + 1.5,
                    button.Name + " label must remain complete");
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            { VerifyButtonLabels(VisualTreeHelper.GetChild(element, index), button); }
        }

        private static void VerifyTextHeight(DependencyObject visual, double width, FrameworkElement root, List<string> clippedLines)
        {
            var text = visual as TextBlock;
            if (text != null && text.Visibility == Visibility.Visible && !String.IsNullOrWhiteSpace(text.Text) && text.TextWrapping == TextWrapping.NoWrap)
            {
                var measured = MeasureText(text);
                Assert(measured.Height <= text.ActualHeight + 1.5, "Text '" + text.Text + "' needs " + measured.Height + " px but has " + text.ActualHeight + " px at width " + width);
                VerifyVisibleLine(text, root, width, clippedLines);
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            {
                VerifyTextHeight(VisualTreeHelper.GetChild(visual, index), width, root, clippedLines);
            }
        }

        private static void AssertInside(FrameworkElement child, FrameworkElement ancestor, string message)
        {
            Assert(child.Visibility == Visibility.Visible && child.ActualWidth > 0 && child.ActualHeight > 0, message + " is visible");
            Rect bounds = child.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), child.RenderSize));
            Assert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= ancestor.ActualWidth + 1 && bounds.Bottom <= ancestor.ActualHeight + 1, message + " is not clipped");
        }

        private static void VerifyVisibleLine(TextBlock text, FrameworkElement root, double width, List<string> clippedLines)
        {
            // Font leading can lie outside a parent without cutting a letter. Use
            // the actual WPF drawing for ancestor clips; measured text dimensions
            // and control containment remain separate strict checks above.
            DrawingGroup drawing = VisualTreeHelper.GetDrawing(text);
            Assert(drawing != null, "Visible text has no rendered drawing: " + text.Name);
            Rect textLine = drawing.Bounds;
            if (textLine.IsEmpty) { return; }
            DependencyObject current = text;
            while (current != null)
            {
                Visual visual = current as Visual;
                if (visual != null)
                {
                    Geometry clip = VisualTreeHelper.GetClip(visual);
                    if (clip != null)
                    {
                        Rect bounds = ReferenceEquals(visual, text) ? textLine : text.TransformToAncestor(visual).TransformBounds(textLine);
                        if (bounds.Top < clip.Bounds.Top - 1 || bounds.Bottom > clip.Bounds.Bottom + 1)
                        {
                            clippedLines.Add((String.IsNullOrEmpty(text.Name) ? text.Text : text.Name) + " has clipped glyphs at width " + width
                                + " (ink " + bounds.Top + ".." + bounds.Bottom + ", visible " + clip.Bounds.Top + ".." + clip.Bounds.Bottom + ")");
                            break;
                        }
                    }
                }
                if (ReferenceEquals(current, root)) { break; }
                current = VisualTreeHelper.GetParent(current);
            }
        }

        private static void Assert(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
