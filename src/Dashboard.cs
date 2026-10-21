using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PcPulse
{
    public sealed class Dashboard
    {
        private readonly Window window;
        private readonly ComboBox gpuSelector;
        private readonly ComboBox diskSelector;
        private readonly ComboBox screenSelector;
        private readonly Dictionary<uint, BenchmarkProgress> benchmarks = new Dictionary<uint, BenchmarkProgress>();
        private readonly Dictionary<uint, GpuBenchmarkProgress> gpuBenchmarks = new Dictionary<uint, GpuBenchmarkProgress>();
        private readonly Dictionary<uint, string> diskIdentities = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> gpuIdentities = new Dictionary<uint, string>();
        private GpuBenchmarkProgress unknownGpuBenchmark;
        private Snapshot snapshot;
        private bool updatingSelectors;
        private string portsModel = "";
        private string portsSourceUrl = "";
        private bool portsLoading;
        private bool checksRunning;
        private bool inventoryRefreshing;
        private string gpuTopology = "";
        private bool snapshotMode;
        private bool crmOfficeAccess;
        private bool automaticTestsEnabled = true;
        private PortSpecification portSpecification;
        private List<PeripheralTestResult> peripheralTests = new List<PeripheralTestResult>();
        private StressRunResult stressResult;

        public event Action RefreshRequested;
        public event Action SensorsHelpRequested;
        public event Action SaveReportRequested;
        public event Action SendToCrmRequested;
        public event Action PeripheralTestsRequested;
        public event Action StressTestsRequested;
        public event Action RunTestRequested;
        public event Action RunGpuTestRequested;
        public event Action StopTestRequested;
        public event Action PortsRefreshRequested;
        public event Action<string> PortsSourceRequested;
        public event Action PortsSearchRequested;

        public Dashboard(Window window)
        {
            this.window = window;
            BrandAssets.Apply(window);
            foreach (string name in new[] { "CpuTemperatureLabel", "CpuTemperature" })
            {
                ((TextBlock)window.FindName(name)).ToolTip = new TextBlock
                {
                    Text = Display.CpuHeatNote,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 340,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(29, 29, 31))
                };
            }
            foreach (string name in new[] { "CpuTemperature", "GpuTemperature" })
            {
                var temperature = (TextBlock)window.FindName(name);
                temperature.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (OpenTemperatureHelp((TextBlock)sender)) { args.Handled = true; }
                };
                temperature.KeyDown += delegate(object sender, KeyEventArgs args)
                {
                    if ((args.Key == Key.Enter || args.Key == Key.Space) && OpenTemperatureHelp((TextBlock)sender)) { args.Handled = true; }
                };
            }
            gpuSelector = (ComboBox)window.FindName("GpuSelector");
            diskSelector = (ComboBox)window.FindName("DiskSelector");
            screenSelector = (ComboBox)window.FindName("ScreenSelector");
            ((Button)window.FindName("RefreshButton")).Click += delegate
            {
                Action handler = RefreshRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("SensorsButton")).Click += delegate
            {
                Action handler = SensorsHelpRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("SaveReportButton")).Click += delegate
            {
                Action handler = SaveReportRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("CrmButton")).Click += delegate
            {
                if (!CanSendToCrm()) { return; }
                Action handler = SendToCrmRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("PeripheralTestsButton")).Click += delegate
            {
                Action handler = PeripheralTestsRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("StressTestButton")).Click += delegate
            {
                Action handler = StressTestsRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("RunTestButton")).Click += delegate
            {
                Action handler = RunTestRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("StopTestButton")).Click += delegate
            {
                Action handler = StopTestRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("RunGpuTestButton")).Click += delegate
            {
                Action handler = RunGpuTestRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("PortsRefreshButton")).Click += delegate
            {
                Action handler = PortsRefreshRequested;
                if (handler != null) { handler(); }
            };
            ((Button)window.FindName("PortsSourceButton")).Click += delegate
            {
                Action<string> handler = PortsSourceRequested;
                if (handler != null && portsSourceUrl.Length > 0) { handler(portsSourceUrl); }
            };
            ((Button)window.FindName("PortsSearchButton")).Click += delegate
            {
                Action handler = PortsSearchRequested;
                if (handler != null) { handler(); }
            };
            gpuSelector.SelectionChanged += delegate { if (!updatingSelectors) { RenderGpu(); } };
            diskSelector.SelectionChanged += delegate { if (!updatingSelectors) { RenderDisk(); } };
            screenSelector.SelectionChanged += delegate { if (!updatingSelectors) { RenderScreen(); } };
        }

        public uint? SelectedGpuAdapterIndex
        {
            get
            {
                int index = gpuSelector.SelectedIndex;
                return snapshot != null && index >= 0 && index < snapshot.Gpus.Count ? snapshot.Gpus[index].AdapterIndex : null;
            }
        }

        public void SetChecksRunning(bool running)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { SetChecksRunning(running); }));
                return;
            }
            checksRunning = running;
            UpdateTestControls();
        }

        public void SetCrmOfficeAccess(bool allowed)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { SetCrmOfficeAccess(allowed); }));
                return;
            }
            crmOfficeAccess = allowed;
            UpdateTestControls();
        }

        public void SetStressResult(StressRunResult result) { window.Dispatcher.VerifyAccess(); stressResult = result; }

        public void Render(Snapshot data)
        {
            if (data == null) { return; }
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { Render(data); }));
                return;
            }

            ReconcileDeviceResults(data);
            snapshot = data;
            SetText("WindowsName", data.WindowsName);
            RenderComputer();
            SetText("CpuName", data.CpuName);
            SetText("CpuTopology", data.CpuCores > 0 && data.CpuThreads > 0
                ? String.Format(CultureInfo.GetCultureInfo("ru-RU"), "Ядра: {0} · потоки: {1}", data.CpuCores, data.CpuThreads)
                : "Сведения о ядрах недоступны");
            SetText("CpuClock", Display.Number(data.CpuClockMhz / 1000d, " ГГц", 2));
            RenderTemperature("CpuTemperature", data.CpuTemperature, data.CpuTemperatureStatus, true);
            bool biosClock = !String.IsNullOrWhiteSpace(data.CpuClockSource) && data.CpuClockSource.IndexOf("BIOS", StringComparison.OrdinalIgnoreCase) >= 0;
            SetText("CpuClockLabel", biosClock ? "Частота BIOS: " : "Частота: ");
            ((TextBlock)window.FindName("CpuClock")).ToolTip = data.CpuClockSource;
            SetText("CpuCache", "Кэш: L1 " + CacheSize(data.CpuL1CacheBytes) + " · L2 " + CacheSize(data.CpuL2CacheBytes) + " · L3 " + CacheSize(data.CpuL3CacheBytes));
            ((TextBlock)window.FindName("CpuCache")).ToolTip = "Суммарный кэш процессора по уровням. " + data.CpuCacheSource;
            ((TextBlock)window.FindName("CpuName")).ToolTip = data.CpuName + "\nЗагрузка: " + Display.Number(data.CpuLoad, " %", 0);
            SetText("RamConfiguration", String.IsNullOrWhiteSpace(data.RamConfiguration) ? "Оперативная память" : data.RamConfiguration);
            SetText("RamType", "Тип памяти: " + (String.IsNullOrWhiteSpace(data.RamType) ? "Нет данных" : data.RamType));
            SetText("RamSlots", (data.RamHasModularFormFactor ? "Планок: " : "Устройств памяти: ")
                + (data.RamModuleCount.HasValue ? data.RamModuleCount.Value.ToString() : "—")
                + " · Свободно слотов: " + (data.RamFreeSlots.HasValue ? data.RamFreeSlots.Value.ToString() : "—")
                + (data.RamSlotCount.HasValue ? " из " + data.RamSlotCount.Value.ToString() : ""));
            ((TextBlock)window.FindName("RamSlots")).ToolTip = data.RamSlotsNote;
            SetText("RamInstalled", MemoryCompact(data.RamInstalledBytes));
            ((TextBlock)window.FindName("RamInstalled")).ToolTip = "Физически установлено: " + MemoryCompact(data.RamInstalledBytes)
                + "\nДоступно Windows: " + Display.Memory(data.RamTotalBytes) + "\nИспользуется: " + Display.Memory(data.RamUsedBytes);
            string rate = (data.RamRate ?? "Нет данных").Replace(" (настроено в BIOS)", "");
            SetText("RamRate", rate);
            ((TextBlock)window.FindName("RamRate")).ToolTip = data.RamRate;
            SetText("RamClock", "Такт DDR · расчёт: " + Display.Number(data.RamClockMhz, " МГц", 0));
            bool manufacturerMaximum = data.RamManufacturerMaximumBytes.HasValue;
            string maximumSource = manufacturerMaximum ? " · паспорт" : (data.RamMaximumSource ?? "").IndexOf("BIOS", StringComparison.OrdinalIgnoreCase) >= 0 ? " · BIOS" : "";
            SetText("RamMaximum", "Максимум: " + MemoryCompact(manufacturerMaximum ? data.RamManufacturerMaximumBytes : data.RamMaximumBytes) + maximumSource);
            ((TextBlock)window.FindName("RamMaximum")).ToolTip = "По спецификации производителя: " + MemoryCompact(data.RamManufacturerMaximumBytes)
                + "\n" + data.RamManufacturerMaximumSource + "\nПо BIOS: " + MemoryCompact(data.RamMaximumBytes)
                + "\n" + data.RamMaximumSource + "\nПоказания BIOS и проверенные производителем конфигурации могут различаться.";

            List<string> gpuNames = new List<string>();
            foreach (GpuSnapshot gpu in data.Gpus) { gpuNames.Add(gpu.Name); }
            List<string> diskNames = new List<string>();
            foreach (DiskSnapshot disk in data.Disks) { diskNames.Add("Диск " + disk.Index + " · " + disk.Name); }
            List<string> screenNames = new List<string>();
            foreach (ScreenSnapshot screen in data.Screens) { screenNames.Add(screen.Name); }
            updatingSelectors = true;
            try
            {
                UpdateSelector(gpuSelector, gpuNames, "Видеокарта не определена");
                UpdateSelector(diskSelector, diskNames, "Накопители не определены");
                UpdateSelector(screenSelector, screenNames, "Экран не определён");
            }
            finally { updatingSelectors = false; }
            RenderGpu();
            RenderDisk();
            RenderScreen();
            RenderBattery(data);

            string sensorStatus = String.IsNullOrWhiteSpace(data.SensorStatus) ? "Данные датчиков недоступны" : data.SensorStatus;
            SetText("StatusText", "Обновлено " + data.Timestamp.ToString("HH:mm:ss") + " · " + sensorStatus);
            ((TextBlock)window.FindName("StatusText")).ToolTip = sensorStatus + (data.Issues.Count > 0 ? "\n\n" + String.Join("\n", data.Issues.ToArray()) : "");
        }

        public void RenderStartupStatus(string status)
        {
            if (String.IsNullOrWhiteSpace(status)) { return; }
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { RenderStartupStatus(status); }));
                return;
            }
            SetText("StartupStatus", status);
            ((TextBlock)window.FindName("StartupStatus")).ToolTip = status;
        }

        private void ReconcileDeviceResults(Snapshot data)
        {
            var signatures = new List<string>();
            foreach (var gpu in data.Gpus) { signatures.Add(gpu.AdapterIndex + "|" + gpu.AdapterLuidHigh + "|" + gpu.AdapterLuidLow + "|" + gpu.Name); }
            signatures.Sort(StringComparer.Ordinal);
            string topology = string.Join("\n", signatures);
            if (topology != gpuTopology) { unknownGpuBenchmark = null; }
            gpuTopology = topology;
            var currentDisks = new Dictionary<uint, string>();
            foreach (var disk in data.Disks)
            {
                string identity = disk.PersistentHardwareId + "|" + disk.StorageIdentity + "|" + disk.Name + "|" + disk.SizeBytes;
                string previous;
                if (diskIdentities.TryGetValue(disk.Index, out previous) && previous != identity) { benchmarks.Remove(disk.Index); }
                currentDisks[disk.Index] = identity;
            }
            foreach (uint key in new List<uint>(benchmarks.Keys)) { if (!currentDisks.ContainsKey(key)) { benchmarks.Remove(key); } }
            diskIdentities.Clear();
            foreach (var pair in currentDisks) { diskIdentities.Add(pair.Key, pair.Value); }

            var currentGpus = new Dictionary<uint, string>();
            bool gpuChanged = false;
            foreach (var gpu in data.Gpus)
            {
                if (!gpu.AdapterIndex.HasValue) { continue; }
                string identity = gpu.PersistentHardwareId + "|" + gpu.AdapterLuidHigh + "|" + gpu.AdapterLuidLow + "|" + gpu.Name;
                string previous;
                if (gpuIdentities.TryGetValue(gpu.AdapterIndex.Value, out previous) && previous != identity)
                {
                    gpuBenchmarks.Remove(gpu.AdapterIndex.Value);
                    gpuChanged = true;
                }
                currentGpus[gpu.AdapterIndex.Value] = identity;
            }
            foreach (uint key in new List<uint>(gpuBenchmarks.Keys))
            {
                if (!currentGpus.ContainsKey(key)) { gpuBenchmarks.Remove(key); gpuChanged = true; }
            }
            if (gpuChanged) { unknownGpuBenchmark = null; }
            gpuIdentities.Clear();
            foreach (var pair in currentGpus) { gpuIdentities.Add(pair.Key, pair.Value); }
        }

        public void RenderBenchmark(BenchmarkProgress progress)
        {
            if (progress == null) { return; }
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { RenderBenchmark(progress); }));
                return;
            }
            benchmarks[progress.DiskIndex] = progress.Copy();
            RenderDisk();
        }

        public void SetSnapshotMode()
        {
            snapshotMode = true;
            RenderDisk();
            RenderGpu();
            UpdateTestControls();
        }

        public void SetAutomaticTestsEnabled(bool enabled)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { SetAutomaticTestsEnabled(enabled); }));
                return;
            }
            automaticTestsEnabled = enabled;
            RenderDisk();
            RenderGpu();
        }

        public void SetInventoryRefreshing(bool refreshing)
        {
            window.Dispatcher.VerifyAccess();
            inventoryRefreshing = refreshing;
            UpdateTestControls();
        }

        public void RenderGpuBenchmark(GpuBenchmarkProgress progress)
        {
            if (progress == null) { return; }
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { RenderGpuBenchmark(progress); }));
                return;
            }
            var copy = new GpuBenchmarkProgress
            {
                WarmupSeconds = progress.WarmupSeconds,
                PlannedMeasurementSeconds = progress.PlannedMeasurementSeconds,
                AdapterIndex = progress.AdapterIndex,
                AdapterName = progress.AdapterName,
                Stage = progress.Stage,
                Preset = progress.Preset,
                Percent = progress.Percent,
                FramesPerSecond = progress.FramesPerSecond,
                FrameTimeMs = progress.FrameTimeMs,
                DurationSeconds = progress.DurationSeconds,
                MaxTemperature = progress.MaxTemperature,
                IsRunning = progress.IsRunning,
                Completed = progress.Completed,
                Error = progress.Error,
                TestedAtUtc = progress.TestedAtUtc,
                FromHistory = progress.FromHistory
            };
            if (copy.AdapterIndex.HasValue) { gpuBenchmarks[copy.AdapterIndex.Value] = copy; }
            else { unknownGpuBenchmark = copy; }
            RenderGpu();
        }

        public void RenderPorts(PortSpecification data)
        {
            if (data == null) { return; }
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { RenderPorts(data); }));
                return;
            }

            portSpecification = data;
            portsModel = data.Model ?? "";
            portsLoading = data.IsLoading;
            Uri source;
            portsSourceUrl = Uri.TryCreate(data.SourceUrl, UriKind.Absolute, out source)
                && (source.Scheme == Uri.UriSchemeHttps || source.Scheme == Uri.UriSchemeHttp) ? source.AbsoluteUri : "";
            var ports = (UniformGrid)window.FindName("PortsList");
            var optionalPorts = (UniformGrid)window.FindName("OptionalPortsList");
            ports.Children.Clear();
            optionalPorts.Children.Clear();
            if (data.Ports != null)
            {
                foreach (PortEntry port in data.Ports)
                {
                    if (port == null || port.Count <= 0 || String.IsNullOrWhiteSpace(port.Name)) { continue; }
                    (port.Optional ? optionalPorts : ports).Children.Add(PortChip(port));
                }
            }
            bool hasPorts = ports.Children.Count + optionalPorts.Children.Count > 0;
            ((ScrollViewer)window.FindName("PortsScroll")).Visibility = hasPorts ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)window.FindName("PortsEmpty")).Visibility = hasPorts ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)window.FindName("OptionalPortsLabel")).Visibility = optionalPorts.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetText("PortsEmpty", data.IsLoading
                ? "Ищем количество и типы портов в спецификации производителя…"
                : "Количество портов пока не подтверждено. Используйте поиск по модели.");
            string status = String.IsNullOrWhiteSpace(data.Status) ? "Спецификация недоступна" : data.Status;
            if (data.IsCached) { status = "Кэш · " + status; }
            SetText("PortsStatus", status);
            string detail = status;
            if (data.CheckedAt.HasValue) { detail += "\nПроверено: " + data.CheckedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm"); }
            if (!String.IsNullOrWhiteSpace(data.SourceTitle)) { detail += "\n" + data.SourceTitle; }
            if (!String.IsNullOrWhiteSpace(data.Note)) { detail += "\n" + data.Note; }
            ((TextBlock)window.FindName("PortsStatus")).ToolTip = detail;
            ((ScrollViewer)window.FindName("PortsScroll")).ToolTip = String.IsNullOrWhiteSpace(data.Note)
                ? "Количество по спецификации модели. Опциональные порты зависят от комплектации." : data.Note;
            Button sourceButton = (Button)window.FindName("PortsSourceButton");
            sourceButton.IsEnabled = portsSourceUrl.Length > 0;
            sourceButton.ToolTip = portsSourceUrl.Length > 0 ? data.SourceTitle + "\n" + portsSourceUrl : "Подтверждённый источник пока не найден";
            RenderComputer();
        }

        private void RenderComputer()
        {
            string manufacturer = snapshot == null ? "" : (snapshot.ComputerManufacturer ?? "").Trim();
            string model = snapshot == null ? "" : (snapshot.ComputerModel ?? "").Trim();
            string displayModel = model;
            if (displayModel.Length == 0) { displayModel = portsModel; }
            if (manufacturer.Length > 0 && !displayModel.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            {
                displayModel = manufacturer + (displayModel.Length > 0 ? " " + displayModel : "");
            }
            SetText("ComputerModel", displayModel.Length > 0 ? displayModel : "Модель не определена");
            string detail = displayModel;
            if (snapshot != null)
            {
                detail += "\nКомпьютер: " + snapshot.ComputerName;
                if (!String.IsNullOrWhiteSpace(snapshot.ComputerSku)) { detail += "\nSKU: " + snapshot.ComputerSku; }
            }
            if (portsModel.Length > 0) { detail += "\nСпецификация: " + portsModel; }
            ((TextBlock)window.FindName("ComputerModel")).ToolTip = detail;
            bool hasModel = model.Length > 0 || portsModel.Length > 0;
            ((Button)window.FindName("PortsRefreshButton")).IsEnabled = hasModel && !portsLoading;
            ((Button)window.FindName("PortsSearchButton")).IsEnabled = hasModel;
        }

        private static Border PortChip(PortEntry port)
        {
            string name = port.Name.Trim().Replace("Thunderbolt ", "TB").Replace("Thunderbolt", "TB").Replace("USB Type-C", "USB-C").Replace("USB Type-A", "USB-A");
            name = name.Replace(" / ", "/").Replace("Слот замка безопасности", "Слот замка").Replace("Аудио · наушники/микрофон", "Аудио");
            if (name == "Аудио" && ((port.Detail ?? "").Contains("3,5") || (port.Detail ?? "").Contains("3.5"))) { name = "Аудио 3,5 мм"; }
            if (name == "HDMI")
            {
                var version = System.Text.RegularExpressions.Regex.Match(port.Detail ?? "", @"^HDMI\s+[0-9]+(?:\.[0-9]+)?[a-c]?(?=$|[\s,;])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (version.Success) { name = version.Value; }
            }
            var text = new TextBlock
            {
                Text = port.Count.ToString(CultureInfo.GetCultureInfo("ru-RU")) + "× " + name,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(53, 59, 65)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            return new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 2),
                Background = new SolidColorBrush(Color.FromRgb(240, 242, 244)),
                ToolTip = port.Count + "× " + port.Name + (port.Optional ? "\nОпционально: зависит от комплектации" : "\nПо спецификации производителя")
                    + (String.IsNullOrWhiteSpace(port.Detail) ? "" : "\n" + port.Detail)
            };
        }

        private static void UpdateSelector(ComboBox selector, IList<string> names, string emptyText)
        {
            string previous = selector.SelectedItem as string;
            bool unchanged = selector.Items.Count == names.Count && names.Count > 0;
            for (int index = 0; unchanged && index < names.Count; index++)
            {
                unchanged = String.Equals(selector.Items[index] as string, names[index], StringComparison.Ordinal);
            }
            if (unchanged) { return; }
            selector.Items.Clear();
            foreach (string name in names) { selector.Items.Add(name); }
            if (names.Count == 0) { selector.Items.Add(emptyText); }
            selector.SelectedIndex = 0;
            if (previous != null && selector.Items.Contains(previous)) { selector.SelectedItem = previous; }
            selector.IsEnabled = names.Count > 1;
            selector.ToolTip = selector.SelectedItem;
        }

        private void RenderGpu()
        {
            if (snapshot == null) { return; }
            int index = gpuSelector.SelectedIndex;
            GpuSnapshot gpu = index >= 0 && index < snapshot.Gpus.Count ? snapshot.Gpus[index] : null;
            SetText("GpuLoad", Display.Number(gpu == null ? null : gpu.Load, " %", 0));
            SetText("GpuMemory", gpu != null && gpu.MemoryBytes.HasValue && gpu.MemoryBytes.Value < 1073741824UL
                ? Display.Number(gpu.MemoryBytes.Value / 1048576d, " MB", 0)
                : Display.Memory(gpu == null ? null : gpu.MemoryBytes));
            RenderTemperature("GpuTemperature", gpu == null ? null : gpu.Temperature, gpu == null ? "Видеокарта не определена." : gpu.TemperatureStatus, false);
            bool integrated = gpu != null && gpu.IsIntegrated == true;
            SetText("GpuMemoryKind", integrated ? "Резерв: " : "Видеопамять: ");
            ((TextBlock)window.FindName("GpuMemory")).ToolTip = gpu == null ? "Данные о видеопамяти недоступны" : gpu.MemoryKind;
            SetText("GpuSharedMemory", gpu != null && gpu.SharedMemoryBytes.HasValue ? "Общая RAM: до " + Display.Memory(gpu.SharedMemoryBytes) : "Общая RAM: нет данных");
            ((TextBlock)window.FindName("GpuSharedMemory")).ToolTip = "Лимит использования системной оперативной памяти адаптером. Это не отдельная видеопамять и не текущий занятый объём.";
            SetText("GpuType", gpu == null || !gpu.IsIntegrated.HasValue ? "Тип не подтверждён" : integrated ? "Встроенная" : "Дискретная");
            gpuSelector.ToolTip = gpu == null ? "Видеокарта не определена" : gpu.Name;
            GpuBenchmarkProgress progress = null;
            if (gpu != null && gpu.AdapterIndex.HasValue) { gpuBenchmarks.TryGetValue(gpu.AdapterIndex.Value, out progress); }
            if (progress == null && gpu != null && !gpu.AdapterIndex.HasValue) { progress = unknownGpuBenchmark; }
            string result = snapshotMode ? "GPU-тест не запускался" : automaticTestsEnabled
                ? "GPU-тест · ожидает запуска" : "Тест ещё не выполнен · запустите кнопкой";
            if (progress != null)
            {
                // A failed repeat can carry an earlier successful result. Keep its
                // saved FPS visible; the stage, warning color and tooltip explain
                // that the latest attempt failed.
                if (!String.IsNullOrWhiteSpace(progress.Error)
                    && !(progress.FromHistory && progress.Completed && progress.FramesPerSecond.HasValue))
                { result = "GPU-тест: " + progress.Stage; }
                else if (progress.Completed && progress.FramesPerSecond.HasValue)
                {
                    result = Display.Number(progress.FramesPerSecond, " FPS", 1) + " · " + Display.Number(progress.DurationSeconds, " с", 0)
                        + " · " + (progress.MaxTemperature.HasValue ? "макс. " + Display.Number(progress.MaxTemperature, " °C", 0) : "темп.: нет данных");
                    if (progress.FromHistory)
                    {
                        result = Display.Number(progress.FramesPerSecond, " FPS", 1) + " · "
                            + (String.IsNullOrWhiteSpace(progress.Stage) ? TestOrigin(true, progress.TestedAtUtc) : progress.Stage);
                    }
                }
                else { result = "GPU · " + progress.Stage + (progress.IsRunning ? " " + Display.Number(progress.Percent, " %", 0) : ""); }
            }
            SetText("GpuBenchmarkResult", result);
            ((TextBlock)window.FindName("GpuBenchmarkResult")).ToolTip = progress == null
                ? (snapshotMode ? "Результат GPU-теста не сохранён в этом снимке."
                    : automaticTestsEnabled ? "Короткий тест Direct3D 11 начнётся после SSD. Его результат не подтверждает долговременную стабильность."
                    : "Для проверки выбранной видеокарты нажмите «Тест GPU». Короткий тест не подтверждает долговременную стабильность.")
                : progress.Preset + "\n" + progress.Stage + "\n" + Display.Number(progress.FramesPerSecond, " FPS", 1)
                    + " · кадр: " + Display.Number(progress.FrameTimeMs, " мс", 2) + "\nДлительность: " + Display.Number(progress.DurationSeconds, " с", 1)
                    + "\nМаксимальная доступная температура: " + Display.Number(progress.MaxTemperature, " °C", 0)
                    + OriginTooltip(progress.FromHistory, progress.TestedAtUtc)
                    + (String.IsNullOrWhiteSpace(progress.Error) ? "" : "\n" + progress.Error);
            ((TextBlock)window.FindName("GpuBenchmarkResult")).Foreground = new SolidColorBrush(progress != null && !String.IsNullOrWhiteSpace(progress.Error) ? Color.FromRgb(145, 97, 17) : Color.FromRgb(29, 29, 31));
            UpdateTestControls();
        }

        private bool OpenTemperatureHelp(TextBlock temperature)
        {
            if (!(temperature.Tag is bool) || !(bool)temperature.Tag) { return false; }
            Action handler = SensorsHelpRequested;
            if (handler != null) { handler(); }
            return true;
        }

        private void RenderTemperature(string name, double? value, string status, bool cpu)
        {
            var text = (TextBlock)window.FindName(name);
            string reason = (status ?? "").Trim();
            bool needsRights = reason.StartsWith("Нужны права администратора", StringComparison.OrdinalIgnoreCase);
            bool needsDriver = reason.StartsWith("Требуется драйвер PawnIO", StringComparison.OrdinalIgnoreCase);
            bool noWindowsSensor = reason.StartsWith("Нет доступного датчика CPU", StringComparison.OrdinalIgnoreCase)
                || reason.StartsWith("Производитель не предоставляет температуру CPU", StringComparison.OrdinalIgnoreCase);
            bool actionable = !value.HasValue && (needsRights || needsDriver || noWindowsSensor);
            if (value.HasValue) { text.Text = Display.Number(value, " °C", 0); }
            else if (needsRights) { text.Text = "Нужны права"; }
            else if (needsDriver || noWindowsSensor) { text.Text = "Датчик недоступен"; }
            else if (reason.StartsWith("Датчики подключаются", StringComparison.OrdinalIgnoreCase)) { text.Text = "Подключение…"; }
            else if (reason.StartsWith("Драйвер Intel", StringComparison.OrdinalIgnoreCase)) { text.Text = "Intel не сообщает"; }
            else if (reason.IndexOf("временно", StringComparison.OrdinalIgnoreCase) >= 0) { text.Text = "Ожидаем датчик"; }
            else if (reason.StartsWith("Драйвер/", StringComparison.OrdinalIgnoreCase)) { text.Text = "Драйвер не сообщает"; }
            else if (reason.StartsWith("Датчик CPU", StringComparison.OrdinalIgnoreCase)) { text.Text = "Датчик не сообщает"; }
            else { text.Text = "Нет данных"; }

            text.Tag = actionable;
            text.Focusable = actionable;
            text.Cursor = actionable ? Cursors.Hand : Cursors.Help;
            text.TextDecorations = actionable ? TextDecorations.Underline : null;
            text.Foreground = new SolidColorBrush(value.HasValue ? Color.FromRgb(29, 29, 31)
                : actionable ? Color.FromRgb(44, 49, 54) : Color.FromRgb(75, 81, 88));
            if (reason.Length == 0 && !value.HasValue)
            {
                reason = snapshotMode ? "Причина недоступности датчика не записана в этом снимке." : "Источник температуры пока не вернул показания.";
            }
            if (actionable) { reason += "\nНажмите, чтобы открыть настройку датчиков."; }
            if (cpu) { reason += (reason.Length > 0 ? "\n\n" : "") + Display.CpuHeatNote; }
            text.ToolTip = new TextBlock
            {
                Text = reason.Length > 0 ? reason : "Температура GPU по доступному датчику.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(29, 29, 31))
            };
        }

        private void RenderScreen()
        {
            if (snapshot == null) { return; }
            int index = screenSelector.SelectedIndex;
            ScreenSnapshot screen = index >= 0 && index < snapshot.Screens.Count ? snapshot.Screens[index] : null;
            bool resolution = screen != null && screen.Width.HasValue && screen.Height.HasValue;
            SetText("ScreenResolution", resolution ? screen.Width + " × " + screen.Height + " · " + Display.Number(screen.RefreshHz, " Гц", 0) : "Разрешение: нет данных");
            SetText("ScreenMatrix", "Тип матрицы: " + (screen == null || String.IsNullOrWhiteSpace(screen.MatrixType) ? "не подтверждён" : screen.MatrixType));
            SetText("ScreenBrightness", "Яркость в нитах: " + (screen != null && screen.BrightnessNits.HasValue ? Display.Number(screen.BrightnessNits, " нит", 0) : "не подтверждена"));
            screenSelector.ToolTip = screen == null ? "Экран не определён" : screen.Name + "\n" + screen.Source + "\n" + screen.InstanceName;
            ((TextBlock)window.FindName("ScreenResolution")).ToolTip = "Текущий режим экрана, сообщаемый Windows. " + (screen == null ? "" : screen.Source);
            ((TextBlock)window.FindName("ScreenBrightness")).ToolTip = "Паспортная яркость панели из подтверждённой спецификации; не измерение. Процент ползунка Windows не является яркостью в нитах.\n" + (screen == null ? "" : screen.Source);
        }

        public string CreateReport()
        {
            window.Dispatcher.VerifyAccess();
            var gpuResults = new List<GpuBenchmarkProgress>(gpuBenchmarks.Values);
            if (unknownGpuBenchmark != null) { gpuResults.Add(unknownGpuBenchmark); }
            return DiagnosticReport.Serialize(snapshot, benchmarks.Values, gpuResults, portSpecification, checksRunning, peripheralTests, stressResult);
        }

        public CrmProductDraft CreateCrmDraft()
        {
            window.Dispatcher.VerifyAccess();
            var gpuResults = new List<GpuBenchmarkProgress>(gpuBenchmarks.Values);
            if (unknownGpuBenchmark != null) { gpuResults.Add(unknownGpuBenchmark); }
            return CrmProductDraftBuilder.Build(snapshot, benchmarks.Values, gpuResults, portSpecification, peripheralTests, stressResult);
        }

        public CrmQuickSubmission CreateCrmQuickSubmission(byte[] screenshot)
        {
            window.Dispatcher.VerifyAccess();
            if (snapshot == null || screenshot == null || screenshot.Length == 0)
                throw new InvalidOperationException("Дождитесь определения характеристик компьютера.");
            return new CrmQuickSubmission
            {
                ClientIntakeId = Guid.NewGuid().ToString("D"),
                Quantity = 1,
                AppVersion = typeof(Program).Assembly.GetName().Version.ToString(3),
                CapturedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                DetectedSpecs = CrmQuickPayload.Build(snapshot, portSpecification),
                ScreenshotBase64 = Convert.ToBase64String(screenshot)
            };
        }

        public void SetPeripheralResults(IEnumerable<PeripheralTestResult> results)
        {
            window.Dispatcher.VerifyAccess();
            peripheralTests = new List<PeripheralTestResult>(results);
            ((Button)window.FindName("PeripheralTestsButton")).ToolTip = "Камера, микрофон, клавиатура и динамики. Ручной запуск. Доступно результатов: " + peripheralTests.Count + "/4. Результаты и даты — в окне устройств и в отчёте.";
        }

        private void RenderDisk()
        {
            if (snapshot == null) { return; }
            int index = diskSelector.SelectedIndex;
            DiskSnapshot disk = index >= 0 && index < snapshot.Disks.Count ? snapshot.Disks[index] : null;
            SetText("DiskType", disk == null ? "Тип не определён" : disk.MediaType);
            SetText("DiskSize", Display.DiskSize(disk == null ? null : disk.SizeBytes));
            string detail = disk == null ? "" : disk.Volumes;
            if (disk != null && disk.Temperature.HasValue)
            {
                detail += (detail.Length > 0 ? " · " : "") + Display.Number(disk.Temperature, " °C", 0);
            }
            diskSelector.ToolTip = disk == null ? "Накопители не определены" : disk.Name + (detail.Length > 0 ? "\n" + detail : "");
            SetText("DiskHealth", Display.Number(disk == null ? null : disk.HealthPercent, " %", 0));
            SetBar("DiskHealthBar", disk == null ? null : disk.HealthPercent);
            ((TextBlock)window.FindName("DiskHealth")).FontSize = disk != null && disk.HealthPercent.HasValue ? 28 : 14;
            ((TextBlock)window.FindName("DiskHealth")).ToolTip = "Оценка оставшегося ресурса по SMART. Это не гарантия исправности. " + (disk == null ? "" : disk.SmartSource);
            SetText("PowerOnHours", disk != null && disk.PowerOnHours.HasValue ? disk.PowerOnHours.Value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) : "Нет данных");
            ((TextBlock)window.FindName("PowerOnHours")).FontSize = disk != null && disk.PowerOnHours.HasValue ? 25 : 14;
            SetText("DiskTemperature", Display.Number(disk == null ? null : disk.Temperature, " °C", 0));
            ((TextBlock)window.FindName("DiskTemperature")).FontSize = disk != null && disk.Temperature.HasValue ? 25 : 14;
            bool critical = disk != null && ((disk.CriticalWarning.HasValue && disk.CriticalWarning.Value != 0) || disk.SmartPassed == false);
            string smart = disk != null && disk.CriticalWarning.HasValue
                ? critical ? "Критические предупреждения: есть · 0x" + disk.CriticalWarning.Value.ToString("X2") : "Критические предупреждения: нет"
                : disk == null || String.IsNullOrWhiteSpace(disk.SmartStatus) ? "SMART: нет данных" : disk.SmartStatus;
            SetText("SmartWarning", smart);
            ((TextBlock)window.FindName("SmartWarning")).Foreground = new SolidColorBrush(critical ? Color.FromRgb(180, 53, 39) : Color.FromRgb(75, 81, 88));
            ((TextBlock)window.FindName("SmartWarning")).ToolTip = smart + "\n" + (disk == null ? "" : disk.SmartSource + "\n" + disk.SmartStatus);
            BenchmarkProgress benchmark = null;
            if (disk != null) { benchmarks.TryGetValue(disk.Index, out benchmark); }
            BenchmarkProgress profile = benchmark ?? (snapshotMode ? null : BenchmarkSettings.Current.Profile == "quick-v1"
                ? new BenchmarkProgress { TestSizeBytes = BenchmarkSettings.Current.DiskMiB * 1048576L, BlockSizeBytes = 4194304, Engine = "ADAMO GT", EngineVersion = "1", QueueDepth = 1, ThreadCount = 1, CacheMode = "Без кэша Windows; write-through" }
                : DiskSpdBenchmark.DescribeProfile(disk == null ? 0 : disk.Index));
            SetText("DiskTestProfile", profile == null ? "Параметры теста не сохранены" : DiskProfileCaption(profile));
            ((TextBlock)window.FindName("DiskTestProfile")).ToolTip = profile == null ? "В снимке отсутствуют параметры и результаты теста скорости." : DiskProfileDetails(profile);
            SetText("DiskRead", BenchmarkValue(benchmark, benchmark == null ? null : benchmark.ReadBytesPerSecond));
            SetText("DiskWrite", BenchmarkValue(benchmark, benchmark == null ? null : benchmark.WriteBytesPerSecond));
            ((TextBlock)window.FindName("DiskRead")).ToolTip = "Последовательное чтение: " + BenchmarkValue(benchmark, benchmark == null ? null : benchmark.ReadBytesPerSecond)
                + (benchmark == null ? "" : OriginTooltip(benchmark.FromHistory, benchmark.TestedAtUtc))
                + "\nСейчас: " + Display.Speed(disk == null ? null : disk.ReadBytesPerSecond);
            ((TextBlock)window.FindName("DiskWrite")).ToolTip = "Последовательная запись: " + BenchmarkValue(benchmark, benchmark == null ? null : benchmark.WriteBytesPerSecond)
                + (benchmark == null ? "" : OriginTooltip(benchmark.FromHistory, benchmark.TestedAtUtc))
                + "\nСейчас: " + Display.Speed(disk == null ? null : disk.WriteBytesPerSecond);
            string stage = benchmark == null ? (snapshotMode ? "Тест скорости не запускался" : automaticTestsEnabled
                ? "Тест начнётся после показа характеристик" : "Тест ещё не выполнен · запустите кнопкой") : benchmark.Stage;
            if (benchmark != null && !String.IsNullOrWhiteSpace(benchmark.Error)) { stage += " · " + benchmark.Error; }
            if (disk == null) { stage = "Накопители для теста не найдены"; }
            SetText("BenchmarkStage", stage);
            ((TextBlock)window.FindName("BenchmarkStage")).ToolTip = stage + (benchmark == null ? "" : OriginTooltip(benchmark.FromHistory, benchmark.TestedAtUtc));
            SetBar("BenchmarkBar", benchmark == null ? null : (double?)benchmark.Percent);
            UpdateTestControls();
        }

        private static string TestOrigin(bool fromHistory, DateTime? testedAtUtc)
        {
            string type = fromHistory ? "Сохранённый результат теста" : "Результат теста";
            return type + (testedAtUtc.HasValue
                ? " · " + testedAtUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
                : " · дата не записана");
        }

        private static string OriginTooltip(bool fromHistory, DateTime? testedAtUtc)
        {
            return fromHistory || testedAtUtc.HasValue ? "\n" + TestOrigin(fromHistory, testedAtUtc) : "";
        }

        internal static string DiskProfileCaption(BenchmarkProgress profile)
        {
            string file = "файл " + TestBytes(profile.TestSizeBytes);
            if (!profile.QueueDepth.HasValue || !profile.ThreadCount.HasValue || !profile.BlockSizeBytes.HasValue)
            {
                return file + " · параметры не сохранены";
            }
            return "SEQ " + TestBytes(profile.BlockSizeBytes) + " · Q" + profile.QueueDepth + "T" + profile.ThreadCount + " · " + file;
        }

        internal static string DiskProfileDetails(BenchmarkProgress profile)
        {
            string engine = String.IsNullOrWhiteSpace(profile.Engine) ? "Движок не сохранён" : profile.Engine + " " + profile.EngineVersion;
            string details = engine + "\n" + DiskProfileCaption(profile);
            details += "\nКэширование: " + (String.IsNullOrWhiteSpace(profile.CacheMode) ? "не сохранено" : profile.CacheMode);
            if (profile.WarmupSeconds.HasValue) { details += "\nРазогрев каждого этапа: " + profile.WarmupSeconds + " с"; }
            if (profile.MeasurementSeconds.HasValue) { details += "\nПлановое измерение каждого этапа: " + profile.MeasurementSeconds + " с"; }
            if (profile.ReadSeconds.HasValue) { details += "\nИзмерение чтения: " + Display.Number(profile.ReadSeconds, " с", 3); }
            if (profile.WriteSeconds.HasValue) { details += "\nИзмерение записи: " + Display.Number(profile.WriteSeconds, " с", 3); }
            return details + "\nРезультат зависит от нагрузки, питания и кэша SSD; это не гарантия максимальной скорости.";
        }

        private static string TestBytes(long? bytes)
        {
            if (!bytes.HasValue) { return "не сохранён"; }
            return bytes.Value >= 1073741824L ? Display.Number(bytes.Value / 1073741824d, " GB", 0)
                : Display.Number(bytes.Value / 1048576d, " MB", 0);
        }

        private bool TestsAreRunning()
        {
            if (checksRunning) { return true; }
            foreach (BenchmarkProgress state in benchmarks.Values) { if (state.IsRunning) { return true; } }
            foreach (GpuBenchmarkProgress state in gpuBenchmarks.Values) { if (state.IsRunning) { return true; } }
            return unknownGpuBenchmark != null && unknownGpuBenchmark.IsRunning;
        }

        private bool CanSendToCrm()
        {
            return crmOfficeAccess && !snapshotMode && !inventoryRefreshing && snapshot != null && !TestsAreRunning();
        }

        private void UpdateTestControls()
        {
            bool running = TestsAreRunning();
            ((Button)window.FindName("RunTestButton")).IsEnabled = !running && !inventoryRefreshing && snapshot != null && snapshot.Disks.Count > 0;
            ((Button)window.FindName("RunGpuTestButton")).IsEnabled = !running && !inventoryRefreshing && SelectedGpuAdapterIndex.HasValue;
            ((Button)window.FindName("StopTestButton")).IsEnabled = running;
            ((Button)window.FindName("StressTestButton")).IsEnabled = !running && !inventoryRefreshing && snapshot != null;
            var crmButton = (Button)window.FindName("CrmButton");
            crmButton.Visibility = crmOfficeAccess ? Visibility.Visible : Visibility.Collapsed;
            crmButton.IsEnabled = CanSendToCrm();
        }

        private static string BenchmarkValue(BenchmarkProgress progress, double? value)
        {
            if (progress == null) { return "—"; }
            if (value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value)) { return Display.Speed(value); }
            return progress.IsRunning ? "Тест…" : "Нет данных";
        }

        private void RenderBattery(Snapshot data)
        {
            HardwareMonitor.ApplyBatteryRuntime(data);
            bool batteryAbsent = data.BatteryPresent.HasValue && !data.BatteryPresent.Value;
            SetText("BatteryPercent", batteryAbsent ? "Нет батареи" : Display.Number(data.BatteryPercent, " %", 0));
            ((TextBlock)window.FindName("BatteryChargeLabel")).Visibility = batteryAbsent ? Visibility.Collapsed : Visibility.Visible;
            SetText("BatteryStatus", batteryAbsent ? "Компьютер работает без батареи" : data.BatteryStatus);
            SetText("BatteryHealth", batteryAbsent ? "—" : Display.Number(data.BatteryHealthPercent, " %", 0));
            SetBar("BatteryHealthBar", batteryAbsent ? null : data.BatteryHealthPercent);
            SetText("BatteryDesignCapacity", batteryAbsent ? "—" : Display.Number(data.BatteryDesignWh, " Вт·ч", 2));
            SetText("BatteryFullCapacity", batteryAbsent ? "—" : Display.Number(data.BatteryFullChargeWh, " Вт·ч", 2));
            SetText("BatteryRuntime", batteryAbsent ? "—" : data.BatteryRuntimeMinHours.HasValue && data.BatteryRuntimeMaxHours.HasValue
                ? "≈ " + Display.Number(data.BatteryRuntimeMinHours, "", 1) + "–" + Display.Number(data.BatteryRuntimeMaxHours, " ч", 1)
                : "Нет данных");
            ((TextBlock)window.FindName("BatteryRuntime")).ToolTip = data.BatteryRuntimeStatus;
            ((TextBlock)window.FindName("BatteryRuntimeLabel")).ToolTip = data.BatteryRuntimeStatus;
            SetText("BatteryCycles", batteryAbsent ? "—" : data.BatteryCyclesPerDevice ? "По батареям"
                : data.BatteryCycleCount.HasValue ? data.BatteryCycleCount.Value.ToString(CultureInfo.GetCultureInfo("ru-RU")) : "Нет данных");
            ((TextBlock)window.FindName("BatteryCycles")).ToolTip = batteryAbsent ? "Батарея не установлена"
                : String.IsNullOrWhiteSpace(data.BatteryCycleDetail) ? "Количество циклов, сообщаемое контроллером батареи. Не каждый контроллер предоставляет эти данные."
                : data.BatteryCycleDetail;
            ((TextBlock)window.FindName("BatteryHealth")).FontSize = !batteryAbsent && !data.BatteryHealthPercent.HasValue ? 14 : 36;
            SetText("BatteryDetail", data.BatteryDetail);
            ((TextBlock)window.FindName("BatteryDetail")).Visibility = String.IsNullOrWhiteSpace(data.BatteryDetail) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string CacheSize(ulong? bytes)
        {
            if (!bytes.HasValue) { return "нет данных"; }
            return bytes.Value < 2097152UL ? Display.Number(bytes.Value / 1024d, " KB", 0)
                : Display.Number(bytes.Value / 1048576d, " MB", bytes.Value % 1048576UL == 0 ? 0 : 1);
        }

        private static string MemoryCompact(ulong? bytes)
        {
            if (!bytes.HasValue) { return "Нет данных"; }
            return Display.Number(bytes.Value / 1073741824d, " GB", bytes.Value % 1073741824UL == 0 ? 0 : 1);
        }

        private void SetText(string name, string value)
        {
            TextBlock text = (TextBlock)window.FindName(name);
            text.Text = value ?? "Нет данных";
            text.ToolTip = text.Text;
        }

        private void SetBar(string name, double? value)
        {
            ProgressBar progress = (ProgressBar)window.FindName(name);
            progress.Value = Display.Percent(value);
            progress.Opacity = value.HasValue ? 1 : 0.35;
            progress.ToolTip = Display.Number(value, " %", 0);
        }
    }
}
