using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcPulse
{
    internal sealed class StressTestsWindow : Window
    {
        private readonly CheckBox cpu, ram, ssd, gpu, sequential;
        private readonly ComboBox duration, ramSizeSelector, diskSelector, gpuSelector;
        private readonly Button start, stop;
        private readonly TextBlock summary, temperature;
        private readonly ProgressBar progress;
        private readonly ScrollViewer outerScroll;
        private readonly Dictionary<StressComponent, TextBlock> statuses = new Dictionary<StressComponent, TextBlock>();
        private readonly Dictionary<StressComponent, double> percentages = new Dictionary<StressComponent, double>();
        private readonly Func<StressOptions, Action<StressProgress>, CancellationToken, Task<StressRunResult>> run;
        private readonly Action<StressRunResult> completed;
        private readonly Func<Snapshot> readSensors;
        private readonly DispatcherTimer timer;
        private CancellationTokenSource cancellation;
        private Task runTask;
        private bool running, closingRequested;
        private int generation;
        private DateTime started;
        private StressOptions activeOptions;
        private StressRunResult lastResult;
        internal bool IsRunning { get { return running; } }
        internal StressRunResult LastResult { get { return lastResult; } }

        internal StressTestsWindow(Snapshot snapshot, Func<Snapshot> readSensors, StressRunResult previous,
            Action<StressRunResult> completed,
            Func<StressOptions, Action<StressProgress>, CancellationToken, Task<StressRunResult>> run = null)
        {
            if (snapshot == null) { throw new ArgumentNullException("snapshot"); }
            this.readSensors = readSensors; this.completed = completed;
            this.run = run ?? ((options, report, token) => StressTestRunner.RunAsync(options, report, readSensors, token));
            Title = "Стресс-тесты · ADAMO GT"; Width = 790; Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(243, 244, 245));
            Foreground = new SolidColorBrush(Color.FromRgb(29, 29, 31)); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            UseLayoutRounding = true; TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(Text("Выберите нагрузку", 23, FontWeights.SemiBold));
            panel.Children.Add(Text("Тесты запускаются только кнопкой. При ошибке или достижении температурного предела нагрузка останавливается.", 14));
            var choices = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
            cpu = Choice("CPU", "StressCpu"); ram = Choice("RAM", "StressRam"); ssd = Choice("SSD", "StressSsd"); gpu = Choice("GPU", "StressGpu");
            foreach (CheckBox choice in new[] { cpu, ram, ssd, gpu }) { choices.Children.Add(choice); }
            panel.Children.Add(choices);
            panel.Children.Add(Text("RAM · объём для проверки", 13));
            ramSizeSelector = new ComboBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 28 };
            ramSizeSelector.Items.Add(new ComboBoxItem { Content = "Авто · доступная RAM с резервом", Tag = 0 });
            foreach (int gigabytes in new[] { 2, 4, 8, 16, 32, 64 })
            { ramSizeSelector.Items.Add(new ComboBoxItem { Content = "До " + gigabytes + " GB", Tag = gigabytes * 1024 }); }
            AutomationProperties.SetAutomationId(ramSizeSelector, "StressRamSize");
            AutomationProperties.SetName(ramSizeSelector, "Объём RAM для проверки"); ramSizeSelector.SelectedIndex = 0;
            panel.Children.Add(ramSizeSelector);
            panel.Children.Add(Text("Авто: до 80% свободной физической RAM, с резервом не менее 1 GB для Windows. Выбранный объём ограничивается свободной памятью и лимитом выделения Windows; это не вся установленная RAM.", 13));
            panel.Children.Add(Text("SSD · накопитель для временного файла", 13));
            diskSelector = new ComboBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 28 };
            foreach (DiskSnapshot disk in snapshot.Disks) { diskSelector.Items.Add(new ComboBoxItem { Content = disk.Name + " · " + disk.Volumes, Tag = disk }); }
            AutomationProperties.SetAutomationId(diskSelector, "StressDiskSelector"); diskSelector.SelectedIndex = snapshot.Disks.Count > 0 ? 0 : -1;
            panel.Children.Add(diskSelector);
            panel.Children.Add(Text("GPU · видеоадаптер", 13));
            gpuSelector = new ComboBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 28 };
            foreach (GpuSnapshot adapter in snapshot.Gpus.Where(item => item.AdapterIndex.HasValue)) { gpuSelector.Items.Add(new ComboBoxItem { Content = adapter.Name, Tag = adapter }); }
            AutomationProperties.SetAutomationId(gpuSelector, "StressGpuSelector"); gpuSelector.SelectedIndex = gpuSelector.Items.Count > 0 ? 0 : -1;
            panel.Children.Add(gpuSelector);
            var timeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
            timeRow.Children.Add(Text("Длительность, мин   ", 14));
            duration = new ComboBox { ItemsSource = new[] { 1, 5, 10, 15 }, SelectedIndex = 0, MinWidth = 70, MinHeight = 28 };
            AutomationProperties.SetAutomationId(duration, "StressDuration"); timeRow.Children.Add(duration); panel.Children.Add(timeRow);
            sequential = Choice("По очереди · указанное время для каждого теста", "StressSequential"); panel.Children.Add(sequential);
            panel.Children.Add(Text("Без этой галочки выбранные нагрузки работают одновременно.", 13));
            panel.Children.Add(Text("RAM: три шаблона записи и чтения. Полный цикл проверяет всю выделенную область, но не всю установленную память. GPU: нагрузка D3D11, не полная проверка видеопамяти. SSD: свой файл до 256 MB; запись до 8 GB за запуск, затем чтение. Пользовательские файлы не используются.", 13));
            panel.Children.Add(Text("Автостоп при доступных датчиках: CPU 95 °C, GPU 90 °C, SSD 70 °C. Без показаний температур этот контроль недоступен. Рекомендуется питание от сети.", 13));
            temperature = Text("Температуры: ожидание показаний", 13); panel.Children.Add(temperature);
            foreach (StressComponent component in StressTestRunner.Order)
            {
                var status = Text(StressTestRunner.Label(component) + " · не выбран", 14);
                AutomationProperties.SetAutomationId(status, "StressStatus" + component);
                statuses[component] = status;
                panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215, 219, 222)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 3, 0, 0), Child = status });
            }
            summary = Text("Выберите компоненты и нажмите «Начать».", 14);
            AutomationProperties.SetAutomationId(summary, "StressSummary"); panel.Children.Add(summary);
            progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(82, 120, 91)) };
            var footer = new StackPanel { Margin = new Thickness(22, 0, 22, 12) };
            footer.Children.Add(progress);
            var actions = new WrapPanel(); start = ActionButton("Начать", "StressStart"); stop = ActionButton("Остановить", "StressStop");
            var close = ActionButton("Закрыть", "StressClose"); actions.Children.Add(start); actions.Children.Add(stop); actions.Children.Add(close); footer.Children.Add(actions);
            panel.Children.Add(Text("Результаты текущего запуска включаются в JSON-отчёт. Завершение нагрузки не гарантирует отсутствие всех неисправностей.", 12));
            outerScroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var layout = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer); layout.Children.Add(outerScroll); Content = layout;
            foreach (CheckBox choice in new[] { cpu, ram, ssd, gpu }) { choice.Checked += delegate { UpdateControls(); }; choice.Unchecked += delegate { UpdateControls(); }; }
            ramSizeSelector.SelectionChanged += delegate { UpdateControls(); };
            diskSelector.SelectionChanged += delegate { UpdateControls(); }; gpuSelector.SelectionChanged += delegate { UpdateControls(); };
            start.Click += async delegate { await Start(); }; stop.Click += delegate { RequestStop(); }; close.Click += delegate { Close(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!running) { return; }
                e.Cancel = true; closingRequested = true; RequestStop();
            };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += delegate { RenderTemperatures(); }; timer.Start();
            Closed += delegate { timer.Stop(); if (cancellation != null) { cancellation.Dispose(); cancellation = null; } };
            Loaded += delegate { DialogViewport.Position(this, DialogViewport.WorkArea(this)); RenderTemperatures(); };
            if (previous != null) { ShowResult(previous); }
            UpdateControls();
        }

        private async Task Start()
        {
            if (running || closingRequested) { return; }
            StressOptions options = Options();
            try { options.Validate(); } catch (Exception error) { summary.Text = error.Message; return; }
            running = true; activeOptions = options; started = DateTime.UtcNow; int current = ++generation;
            cancellation = new CancellationTokenSource(); percentages.Clear(); progress.Value = 0;
            foreach (StressComponent component in StressTestRunner.Order)
            {
                bool selected = (options.Components & component) != 0;
                statuses[component].Text = StressTestRunner.Label(component) + (selected ? " · ожидание" : " · не выбран");
                if (selected) { percentages[component] = 0; }
            }
            summary.Text = options.Sequential ? "Нагрузки выполняются по очереди…" : "Выбранные нагрузки выполняются одновременно…";
            UpdateControls();
            try
            {
                Task<StressRunResult> operation = run(options, delegate(StressProgress value)
                {
                    if (value == null) { return; }
                    StressProgress copy = value.Copy();
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (!running || current != generation || !percentages.ContainsKey(copy.Component)) { return; }
                        Render(copy); percentages[copy.Component] = Math.Max(percentages[copy.Component], Math.Max(0, Math.Min(100, copy.Percent)));
                        progress.Value = percentages.Values.Average();
                    }));
                }, cancellation.Token);
                runTask = operation;
                StressRunResult result = await operation;
                ShowResult(result); if (completed != null) { completed(result); }
            }
            catch (OperationCanceledException) { summary.Text = "Стресс-тест остановлен."; }
            catch (Exception error) { summary.Text = "Стресс-тест не завершён: " + error.Message; }
            finally
            {
                generation++; running = false; activeOptions = null; cancellation.Dispose(); cancellation = null; UpdateControls();
                if (closingRequested) { Close(); }
            }
        }

        internal void RequestStop()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RequestStop)); return; }
            if (!running || cancellation == null) { return; }
            cancellation.Cancel(); stop.IsEnabled = false;
            summary.Text = "Останавливаем нагрузку и освобождаем ресурсы…";
        }

        private StressOptions Options()
        {
            return new StressOptions { Components = (cpu.IsChecked == true ? StressComponent.Cpu : 0) | (ram.IsChecked == true ? StressComponent.Ram : 0)
                | (ssd.IsChecked == true ? StressComponent.Ssd : 0) | (gpu.IsChecked == true ? StressComponent.Gpu : 0),
                DurationSeconds = duration.SelectedItem is int ? (int)duration.SelectedItem * 60 : 60, Sequential = sequential.IsChecked == true,
                RamMiB = ramSizeSelector.SelectedItem == null ? 0 : (int)((ComboBoxItem)ramSizeSelector.SelectedItem).Tag,
                Disk = diskSelector.SelectedItem == null ? null : ((ComboBoxItem)diskSelector.SelectedItem).Tag as DiskSnapshot,
                Gpu = gpuSelector.SelectedItem == null ? null : ((ComboBoxItem)gpuSelector.SelectedItem).Tag as GpuSnapshot };
        }

        private void UpdateControls()
        {
            foreach (Control control in new Control[] { cpu, ram, ssd, gpu, duration, sequential, ramSizeSelector, diskSelector, gpuSelector }) { control.IsEnabled = !running; }
            ramSizeSelector.IsEnabled = !running && ram.IsChecked == true;
            ssd.IsEnabled = !running && diskSelector.Items.Count > 0; gpu.IsEnabled = !running && gpuSelector.Items.Count > 0;
            start.IsEnabled = !running && !closingRequested && Options().Components != 0
                && (ssd.IsChecked != true || diskSelector.SelectedItem != null) && (gpu.IsChecked != true || gpuSelector.SelectedItem != null);
            stop.IsEnabled = running && cancellation != null && !cancellation.IsCancellationRequested;
            if (!running && lastResult == null)
            {
                StressComponent selected = Options().Components;
                foreach (StressComponent component in StressTestRunner.Order)
                { statuses[component].Text = StressTestRunner.Label(component) + ((selected & component) != 0 ? " · выбран · ожидание запуска" : " · не выбран"); }
            }
        }

        private void ShowResult(StressRunResult result)
        {
            if (result == null) { return; }
            lastResult = result;
            foreach (StressProgress value in result.Results) { Render(value); }
            bool success = result.Results.Count > 0 && result.Results.All(value => value.Completed && String.IsNullOrEmpty(value.Error));
            if (success) { progress.Value = 100; }
            summary.Text = (success ? "Нагрузка завершена без обнаруженных ошибок" : "Проверка не завершена успешно")
                + " · " + result.FinishedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                + (String.IsNullOrEmpty(result.SafetyStopReason) ? "" : "\n" + result.SafetyStopReason);
        }

        private void Render(StressProgress value)
        {
            TextBlock block; if (!statuses.TryGetValue(value.Component, out block)) { return; }
            block.Text = StressTestRunner.Label(value.Component) + " · " + value.Detail
                + (value.Iterations > 0 ? " · циклов: " + value.Iterations : "")
                + (value.WrittenBytes > 0 ? " · записано " + Display.Number(value.WrittenBytes / 1073741824d, " GB", 2) : "")
                + (value.MaxTemperature.HasValue ? " · максимум " + Display.Number(value.MaxTemperature, " °C", 0) : "");
            block.Foreground = String.IsNullOrEmpty(value.Error) ? Foreground : new SolidColorBrush(Color.FromRgb(150, 49, 49));
        }

        private void RenderTemperatures()
        {
            Snapshot sample = null; try { if (readSensors != null) { sample = readSensors(); } } catch { }
            if (sample == null || Math.Abs((DateTime.UtcNow - sample.Timestamp.ToUniversalTime()).TotalSeconds) > 10)
            { temperature.Text = "Нет свежих температур · автоматический температурный контроль недоступен"; return; }
            StressOptions options = activeOptions ?? Options();
            temperature.Text = CpuTemperatureSummary(sample)
                + " · GPU " + Display.Number(StressTestRunner.Temperature(sample, options, StressComponent.Gpu), " °C", 0)
                + " · SSD " + Display.Number(StressTestRunner.Temperature(sample, options, StressComponent.Ssd), " °C", 0)
                + (running ? " · прошло " + (int)(DateTime.UtcNow - started).TotalSeconds + " с" : "");
        }

        internal static string CpuTemperatureSummary(Snapshot sample)
        {
            string text = "CPU " + Display.Number(sample.CpuTemperature, " °C", 0);
            if (sample.CpuTemperatureExperimental)
            {
                text += " (экспериментально; исключено из автостопа)";
            }
            if (!StressTestRunner.CpuTemperatureForStress(sample).HasValue)
            {
                text += " · температурный автостоп CPU недоступен";
            }
            return text;
        }

        private static TextBlock Text(string value, double size, FontWeight? weight = null)
        { return new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5) }; }
        private static CheckBox Choice(string title, string id)
        { var box = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 4, 24, 4), VerticalContentAlignment = VerticalAlignment.Center }; AutomationProperties.SetAutomationId(box, id); return box; }
        private static Button ActionButton(string title, string id)
        { var button = new Button { Content = title, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 8, 5), Background = Brushes.White, Foreground = new SolidColorBrush(Color.FromRgb(29, 29, 31)) }; AutomationProperties.SetAutomationId(button, id); return button; }
    }
}
