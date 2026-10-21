using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PcPulse
{
    internal sealed class PeripheralTestsWindow : Window
    {
        private static readonly string[] Kinds = { "camera", "microphone", "keyboard", "speakers" };
        private static readonly string[] Titles = { "Камера", "Микрофон", "Клавиатура", "Динамики" };
        private static readonly object AudioEnumerationGate = new object();
        private static Task<IList<AudioDevice>> inputEnumeration, outputEnumeration;
        private readonly Dictionary<string, PeripheralTestResult> results;
        private readonly Func<PeripheralTestResult, bool> save;
        private readonly string historyError;
        private readonly CameraPreview camera = new CameraPreview();
        private readonly PeripheralAudio audio = new PeripheralAudio();
        private readonly KeyboardTestPanel keyboard = new KeyboardTestPanel();
        private readonly CameraHost cameraHost = new CameraHost();
        private readonly TabControl tabs = new TabControl();
        private readonly ComboBox cameras = Selector("Камера");
        private readonly ComboBox inputs = Selector("Микрофон");
        private readonly ComboBox outputs = Selector("Устройство воспроизведения");
        private readonly TextBlock cameraStatus = Text("Камера выключена. Нажмите «Включить камеру»."), micStatus = Text("Запись не запускалась."), speakerStatus = Text("Выберите устройство и прослушайте оба канала.");
        private readonly TextBlock saved = Text(""), session = Text(""), cameraPlaceholder = Text("Предпросмотр камеры\nФото и видео не сохраняются.");
        private readonly ProgressBar level = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16, Margin = new Thickness(0, 8, 0, 8) };
        private readonly Button passed = MakeButton("Работает"), problem = MakeButton("Проблема"), cameraStart = MakeButton("Включить камеру"), record = MakeButton("Записать 5 секунд"), playback = MakeButton("Прослушать запись"), left = MakeButton("Левый канал"), right = MakeButton("Правый канал"), reload = MakeButton("Обновить список");
        private readonly HashSet<string> exercised = new HashSet<string>();
        private readonly List<Button> refreshButtons = new List<Button>();
        private readonly Button keyboardStart = MakeButton("Продолжить"), keyboardStop = MakeButton("Остановить"), keyboardReset = MakeButton("Сбросить клавиши");
        private ScrollViewer outerScroll, cameraScroll;
        private Grid previewRegion;
        private Action<string> cameraErrorHandler;
        private readonly DispatcherTimer cameraStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private CancellationTokenSource audioCancellation;
        private Task deviceStopTask = Task.FromResult(0);
        private AudioRecording recording;
        private string activeOperation, pendingStoppedOperation;
        private bool initializing = true, closed, closeReady, changingTab, busy, releasePending, cameraStarting;
        private int generation, speakerChannels, keyboardCaptureRequest;
        private bool keyboardWasRunning;

        internal PeripheralTestsWindow(IEnumerable<PeripheralTestResult> previous, Func<PeripheralTestResult, bool> saveResult, bool enumerateOnLoad = true, string historyError = "")
        {
            results = (previous ?? new PeripheralTestResult[0]).Where(PeripheralTestResult.Valid).GroupBy(item => item.Kind).ToDictionary(group => group.Key, group => group.Last().Copy());
            save = saveResult;
            this.historyError = historyError ?? "";
            Title = "ADAMO GT — проверка устройств";
            Width = 920; Height = 730; MinWidth = 480; MinHeight = 360;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            Background = Brush("#F3F4F5"); Foreground = Brush("#1D1D1F");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new Grid { Margin = new Thickness(16) };
            tabs.MinHeight = 240; tabs.MaxHeight = 460;
            foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) { root.RowDefinitions.Add(new RowDefinition { Height = height }); }
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            heading.Children.Add(new TextBlock { Text = "Проверка устройств", FontSize = 22, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(Text("Камера и звук запускаются кнопками; клавиатура проверяется сразу на своей вкладке. После проверки укажите результат. Записи остаются в памяти до закрытия окна."));
            root.Children.Add(heading);
            Grid.SetRow(tabs, 1); root.Children.Add(tabs);
            AddTab(Titles[0], BuildCamera()); AddTab(Titles[1], BuildMicrophone()); AddTab(Titles[2], BuildKeyboard()); AddTab(Titles[3], BuildSpeakers());
            var verdict = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
            var verdictButtons = new WrapPanel();
            verdictButtons.Children.Add(passed); verdictButtons.Children.Add(problem);
            verdict.Children.Add(verdictButtons); verdict.Children.Add(session); verdict.Children.Add(saved);
            saved.FontSize = 12; saved.Foreground = Brush("#4B5158");
            Grid.SetRow(verdict, 2); root.Children.Add(verdict);
            var bottom = new DockPanel { LastChildFill = true };
            var close = MakeButton("Закрыть"); close.Click += delegate { Close(); };
            DockPanel.SetDock(close, Dock.Right); bottom.Children.Add(close);
            var brand = Text(""); brand.FontSize = 12; brand.VerticalAlignment = VerticalAlignment.Center;
            brand.Inlines.Add(new System.Windows.Documents.Run("Creat cu "));
            brand.Inlines.Add(new System.Windows.Documents.Run("❤️") { FontFamily = new FontFamily("Segoe UI Emoji"), Foreground = Brush("#B94A48") });
            brand.Inlines.Add(new System.Windows.Documents.Run(" în Moldova | ADAMO.MD"));
            bottom.Children.Add(brand); Grid.SetRow(bottom, 3); root.Children.Add(bottom);
            outerScroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            outerScroll.ScrollChanged += delegate { UpdateCameraVisibility(); };
            Content = outerScroll;
            passed.Click += delegate { Confirm("passed"); }; problem.Click += delegate { Confirm("problem"); };
            cameraStart.Click += async delegate { await StartCamera(); };
            record.Click += async delegate { await Record(); };
            playback.Click += async delegate { await Play(); };
            left.Click += async delegate { await Channel(0); }; right.Click += async delegate { await Channel(1); };
            reload.Click += async delegate { if (Kind == "camera") { await Discover(); } };
            refreshButtons.Add(reload);
            cameras.SelectionChanged += async delegate { if (!initializing) { ResetExercise("camera"); await StopDevices(); } };
            inputs.SelectionChanged += async delegate { if (!initializing) { ResetExercise("microphone"); ClearRecording(); await StopDevices(); } };
            outputs.SelectionChanged += async delegate { if (!initializing) { ResetExercise("speakers"); speakerChannels = 0; await StopDevices(); } };
            keyboard.ProgressChanged += delegate
            {
                if (keyboardWasRunning && !keyboard.IsRunning) { keyboardCaptureRequest++; }
                keyboardWasRunning = keyboard.IsRunning;
                if (keyboard.SeenCount > 0) { exercised.Add("keyboard"); } else { exercised.Remove("keyboard"); }
                UpdateVerdict(); UpdateButtons();
            };
            tabs.SelectionChanged += async delegate(object sender, SelectionChangedEventArgs args)
            {
                if (args.Source != tabs || initializing || changingTab || closed) { return; }
                changingTab = true;
                try { await StopDevices(); UpdateVerdict(); }
                finally { changingTab = false; QueueKeyboardCapture(); }
            };
            Activated += delegate { QueueKeyboardCapture(); };
            cameraHost.ViewportChanged += delegate { if (camera.IsRunning) { camera.Resize(cameraHost.PixelWidth, cameraHost.PixelHeight); } };
            cameraStatusTimer.Tick += delegate
            {
                if (closed || Kind != "camera" || !camera.IsRunning) { cameraStatusTimer.Stop(); return; }
                if (!String.IsNullOrWhiteSpace(camera.Status)) { cameraStatus.Text = camera.Status; }
            };
            SizeChanged += delegate { UpdateCameraVisibility(); };
            Loaded += async delegate
            {
                DialogViewport.Position(this, DialogViewport.WorkArea(Owner));
                if (enumerateOnLoad) { await Discover(); }
            };
            Closing += async delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (closeReady) { return; }
                args.Cancel = true;
                if (closed) { return; }
                closed = true; IsEnabled = false;
                try { await StopDevices(); }
                finally
                {
                    ClearRecording(); audio.Dispose(); camera.Dispose();
                    closeReady = true;
                    // Stop may complete synchronously when no test was started.
                    // Defer the second Close until this Closing event has returned.
                    Dispatcher.BeginInvoke(new Action(Close));
                }
            };
            initializing = false; tabs.SelectedIndex = 0; UpdateVerdict(); UpdateButtons();
        }

        private UIElement BuildCamera()
        {
            var panel = new StackPanel();
            panel.Children.Add(Text("Проверьте изображение, резкость и отсутствие полос. Откройте шторку камеры. Если доступа нет, проверьте разрешения камеры для классических приложений в параметрах Windows."));
            panel.Children.Add(cameras);
            var stop = MakeButton("Выключить"); stop.Click += async delegate { if (Kind == "camera") { await StopDevices(); } };
            panel.Children.Add(Row(cameraStart, stop, reload));
            var preview = new Grid { Height = 210, Background = Brush("#20252A"), Margin = new Thickness(0, 6, 0, 8), ClipToBounds = true };
            previewRegion = preview;
            cameraHost.Visibility = Visibility.Collapsed;
            preview.Children.Add(cameraHost);
            cameraPlaceholder.HorizontalAlignment = HorizontalAlignment.Center; cameraPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            cameraPlaceholder.Foreground = Brushes.White; cameraPlaceholder.TextAlignment = TextAlignment.Center;
            preview.Children.Add(cameraPlaceholder); panel.Children.Add(preview); panel.Children.Add(cameraStatus);
            var troubleshooting = new Expander { Header = "Если изображение чёрное или камера не открылась", Margin = new Thickness(0, 8, 0, 0) };
            troubleshooting.Content = Text("Откройте шторку и направьте камеру на освещённый предмет. Закройте приложения, которые могут использовать камеру. Проверьте эту же камеру в приложении Windows «Камера», затем закройте его перед повторным запуском теста. На iMac/Mac с Windows также нужны подходящие драйверы камеры из Windows Support Software (Boot Camp). Если изображения нет и в «Камере» Windows, проверьте драйвер и разрешения Windows. Поступление кадров ещё не подтверждает качество изображения — результат отмечаете вы.");
            panel.Children.Add(troubleshooting);
            return panel;
        }

        private UIElement BuildMicrophone()
        {
            var panel = new StackPanel();
            panel.Children.Add(Text("Скажите несколько слов. Запись длится до 5 секунд. Затем прослушайте её и оцените речь, шум и искажения. Автоматического вывода микрофона на динамики нет."));
            panel.Children.Add(inputs);
            var stop = MakeButton("Остановить"); stop.Click += async delegate { if (Kind == "microphone") { await StopDevices(); } };
            panel.Children.Add(Row(record, stop, playback, RefreshButton("microphone")));
            panel.Children.Add(Text("Уровень входного сигнала")); panel.Children.Add(level); panel.Children.Add(micStatus);
            panel.Children.Add(Text("Запись воспроизводится через устройство, выбранное на вкладке «Динамики». Если микрофон недоступен, проверьте его подключение и разрешения для классических приложений в Windows."));
            return panel;
        }

        private UIElement BuildKeyboard()
        {
            var panel = new StackPanel();
            keyboardStart.Click += delegate { if (Kind == "keyboard" && CanUseControls) { keyboard.Start(); } };
            keyboardStop.Click += async delegate { if (Kind == "keyboard") { await StopDevices(); } };
            keyboardReset.Click += delegate { if (Kind == "keyboard" && CanUseControls) { keyboard.Reset(); ResetExercise("keyboard"); } };
            panel.Children.Add(Row(keyboardStart, keyboardStop, keyboardReset, RefreshButton("keyboard"))); panel.Children.Add(keyboard);
            return panel;
        }

        private void QueueKeyboardCapture()
        {
            if (closed || Kind != "keyboard") { return; }
            int request = ++keyboardCaptureRequest;
            int operation = generation;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(delegate
            {
                if (closed || !IsActive || Kind != "keyboard" || request != keyboardCaptureRequest || operation != generation || !CanUseControls) { return; }
                keyboard.Start();
            }));
        }

        private UIElement BuildSpeakers()
        {
            var panel = new StackPanel();
            panel.Children.Add(Text("Установите комфортную громкость. Прозвучит короткий негромкий сигнал отдельно слева и справа. Проверьте оба канала: звук без хрипа и треска. Программа не меняет громкость Windows."));
            panel.Children.Add(outputs);
            var stop = MakeButton("Остановить"); stop.Click += async delegate { if (Kind == "speakers") { await StopDevices(); } };
            panel.Children.Add(Row(left, right, stop, RefreshButton("speakers"))); panel.Children.Add(speakerStatus);
            panel.Children.Add(Text("Для проверки встроенных динамиков отключите наушники и выберите динамики ноутбука. Моноустройство или обработка звука Windows могут смешивать каналы."));
            return panel;
        }

        private void AddTab(string title, UIElement content)
        {
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12) };
            // Native video has its own HWND: hide while scrolled beyond the viewport
            // so it cannot cover WPF controls outside the preview region.
            if (title == Titles[0]) { cameraScroll = scroll; scroll.ScrollChanged += delegate { UpdateCameraVisibility(); }; }
            tabs.Items.Add(new TabItem { Header = title, Content = scroll, Padding = new Thickness(12, 8, 12, 8) });
        }

        private Button RefreshButton(string kind)
        {
            var button = MakeButton("Обновить список");
            button.Click += async delegate { if (Kind == kind) { await Discover(); } };
            refreshButtons.Add(button);
            return button;
        }

        private void UpdateCameraVisibility()
        {
            bool visible = (camera.IsRunning || cameraStarting) && !closed && Kind == "camera"
                && FullyVisible(previewRegion, outerScroll) && FullyVisible(previewRegion, cameraScroll);
            cameraHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            cameraPlaceholder.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            cameraPlaceholder.Text = camera.IsRunning ? "Предпросмотр скрыт при прокрутке.\nПрокрутите область камеры полностью в окно." : "Предпросмотр камеры\nФото и видео не сохраняются.";
        }

        private static bool FullyVisible(FrameworkElement element, ScrollViewer viewport)
        {
            if (element == null || viewport == null || element.ActualWidth <= 0 || viewport.ActualHeight <= 0) { return false; }
            try
            {
                Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
                return bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1;
            }
            catch (InvalidOperationException) { return false; }
        }

        private async Task Discover()
        {
            if (closed) { return; }
            Task stopping = StopDevices(); int request = generation;
            await stopping;
            if (!CanContinue(request)) { return; }
            int current = ++generation; busy = true; UpdateButtons();
            cameraStatus.Text = "Поиск устройств…";
            try
            {
                var cameraList = DiscoverOne(CameraPreview.EnumerateDevicesAsync);
                var inputList = DiscoverOne<AudioDevice>(() => EnumerateAudio(true));
                var outputList = DiscoverOne<AudioDevice>(() => EnumerateAudio(false));
                await Task.WhenAll(cameraList, inputList, outputList);
                if (closed || current != generation) { return; }
                var found = cameraList.Result; var ins = inputList.Result; var outs = outputList.Result;
                initializing = true;
                cameras.ItemsSource = found.Devices; cameras.SelectedIndex = found.Devices.Count > 0 ? 0 : -1;
                inputs.ItemsSource = ins.Devices; inputs.SelectedIndex = ins.Devices.Count > 0 ? 0 : -1;
                outputs.ItemsSource = outs.Devices; outputs.SelectedIndex = outs.Devices.Count > 0 ? 0 : -1;
                initializing = false;
                foreach (string kind in new[] { "camera", "microphone", "speakers" }) { ResetExercise(kind); }
                ClearRecording(); speakerChannels = 0;
                cameraStatus.Text = found.Error ?? (found.Devices.Count == 0 ? "Камера не найдена. Проверьте подключение, шторку и драйвер." : "Камера выключена. Выберите устройство и включите предпросмотр.");
                micStatus.Text = ins.Error ?? (ins.Devices.Count == 0 ? "Микрофон не найден." : "Микрофон готов к ручной проверке.");
                speakerStatus.Text = outs.Error ?? (outs.Devices.Count == 0 ? "Устройство воспроизведения не найдено." : "Проверьте левый и правый канал.");
            }
            catch (Exception ex) { if (!closed && current == generation) { cameraStatus.Text = "Не удалось получить список устройств: " + ex.Message; } }
            finally { if (current == generation) { initializing = false; busy = false; UpdateButtons(); UpdateVerdict(); } }
        }

        private sealed class Discovery<T>
        {
            internal IList<T> Devices = new List<T>();
            internal string Error;
        }

        private static async Task<Discovery<T>> DiscoverOne<T>(Func<Task<IList<T>>> enumerate, int timeoutMilliseconds = 5000)
        {
            try
            {
                Task<IList<T>> pending = enumerate();
                if (await Task.WhenAny(pending, Task.Delay(timeoutMilliseconds)) != pending)
                {
                    Observe(pending);
                    return new Discovery<T> { Error = "Драйвер не ответил при поиске устройств. Попробуйте обновить список позднее." };
                }
                return new Discovery<T> { Devices = await pending ?? new List<T>() };
            }
            catch (Exception error) { return new Discovery<T> { Error = "Не удалось получить список устройств: " + error.Message }; }
        }

        private static Task<IList<AudioDevice>> EnumerateAudio(bool input)
        {
            lock (AudioEnumerationGate)
            {
                Task<IList<AudioDevice>> pending = input ? inputEnumeration : outputEnumeration;
                if (pending == null || pending.IsCompleted)
                {
                    pending = Task.Run<IList<AudioDevice>>(() => input ? PeripheralAudio.GetInputDevices() : PeripheralAudio.GetOutputDevices());
                    if (input) { inputEnumeration = pending; } else { outputEnumeration = pending; }
                }
                return pending;
            }
        }

        private async Task StartCamera()
        {
            if (Kind != "camera") { return; }
            CameraDevice device = cameras.SelectedItem as CameraDevice;
            if (device == null || !CanUseControls) { return; }
            Task stopping = StopDevices(); int request = generation;
            await stopping; if (!CanContinue(request) || Kind != "camera") { return; }
            int current = ++generation; busy = true; UpdateButtons(); cameraStatus.Text = "Подключаем камеру и ждём первые кадры…";
            activeOperation = "camera";
            cameraStarting = true;
            cameraErrorHandler = message => Dispatch(delegate
            {
                if (closed || current != generation) { return; }
                cameraStatusTimer.Stop();
                activeOperation = null; cameraStarting = false;
                ResetExercise("camera"); cameraStatus.Text = message;
                cameraHost.Visibility = Visibility.Collapsed; cameraPlaceholder.Visibility = Visibility.Visible;
                UpdateButtons();
            });
            camera.Error += cameraErrorHandler;
            previewRegion.BringIntoView(); UpdateLayout(); UpdateCameraVisibility(); UpdateLayout();
            try
            {
                await camera.StartAsync(device, cameraHost.Handle, cameraHost.PixelWidth, cameraHost.PixelHeight);
                if (current != generation || closed) { return; }
                exercised.Add("camera"); cameraStatus.Text = camera.Status;
                cameraStatusTimer.Start();
            }
            catch (Exception ex)
            {
                if (current == generation && !closed) { activeOperation = null; cameraStatus.Text = "Камера недоступна: " + ex.Message; cameraHost.Visibility = Visibility.Collapsed; cameraPlaceholder.Visibility = Visibility.Visible; }
            }
            finally { if (current == generation) { cameraStarting = false; busy = false; UpdateCameraVisibility(); UpdateButtons(); UpdateVerdict(); } }
        }

        private async Task Record()
        {
            if (Kind != "microphone") { return; }
            AudioDevice device = inputs.SelectedItem as AudioDevice;
            if (device == null || !CanUseControls) { return; }
            Task stopping = StopDevices(); int request = generation;
            await stopping; if (!CanContinue(request) || Kind != "microphone") { return; }
            ClearRecording(); ResetExercise("microphone");
            int current = ++generation; busy = true; audioCancellation = new CancellationTokenSource(); UpdateButtons();
            activeOperation = "microphone-record";
            micStatus.Text = "Говорите… запись до 5 секунд.";
            try
            {
                AudioRecording captured = await audio.RecordMicrophoneAsync(device.Id, value => Dispatch(delegate
                {
                    if (current != generation || closed) { return; }
                    level.Value = Math.Min(100, value.Peak * 100);
                    micStatus.Text = "Запись: " + value.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " / 5 с · пик " + (value.Peak * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                }), audioCancellation.Token);
                if (current != generation || closed) { if (captured != null) { captured.Dispose(); } return; }
                recording = captured;
                micStatus.Text = "Запись готова. Прослушайте её перед подтверждением исправности.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed && current == generation) { micStatus.Text = "Микрофон недоступен: " + ex.Message; } }
            finally { if (current == generation) { activeOperation = null; busy = false; level.Value = 0; UpdateButtons(); UpdateVerdict(); } }
        }

        private async Task Play()
        {
            if (Kind != "microphone" || recording == null || !CanUseControls) { return; }
            AudioDevice device = outputs.SelectedItem as AudioDevice;
            if (device == null) { micStatus.Text = "Нет устройства для воспроизведения. Подключите динамики или наушники."; return; }
            await AudioOperation("microphone", () => audio.PlaybackAsync(recording, device.Id, audioCancellation.Token), micStatus, "Воспроизводим запись…", delegate
            {
                exercised.Add("microphone"); micStatus.Text = "Воспроизведение завершено. Слышна ли речь без искажений? Укажите результат.";
            });
        }

        private async Task Channel(int channel)
        {
            if (Kind != "speakers") { return; }
            AudioDevice device = outputs.SelectedItem as AudioDevice;
            if (device == null || !CanUseControls) { return; }
            await AudioOperation("speakers", () => audio.TestChannelAsync(device.Id, channel, audioCancellation.Token), speakerStatus, channel == 0 ? "Звучит левый канал…" : "Звучит правый канал…", delegate
            {
                speakerChannels |= 1 << channel;
                if (speakerChannels == 3) { exercised.Add("speakers"); }
                speakerStatus.Text = speakerChannels == 3 ? "Оба канала воспроизведены. Оцените звук и укажите результат." : "Проверьте также второй канал.";
            });
        }

        private async Task AudioOperation(string kind, Func<Task> operation, TextBlock status, string message, Action completed)
        {
            if (Kind != kind) { return; }
            Task stopping = StopDevices(); int request = generation;
            await stopping; if (!CanContinue(request) || Kind != kind) { return; }
            int current = ++generation; busy = true; audioCancellation = new CancellationTokenSource(); UpdateButtons(); status.Text = message;
            activeOperation = ReferenceEquals(status, micStatus) ? "microphone-play" : "speakers";
            try { await operation(); if (!closed && current == generation) { completed(); } }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed && current == generation) { status.Text = "Не удалось воспроизвести звук: " + ex.Message; } }
            finally { if (current == generation) { activeOperation = null; busy = false; UpdateButtons(); UpdateVerdict(); } }
        }

        private async Task StopDevices()
        {
            cameraStatusTimer.Stop();
            // A tab switch can run before WPF detaches the old content. Restoring
            // its remembered button focus here would select that old tab again.
            // Escape stops capture directly and still restores ordinary navigation.
            int current = ++generation; keyboardCaptureRequest++; busy = true; keyboard.Stop(false); cameraStarting = false;
            if (activeOperation != null) { pendingStoppedOperation = activeOperation; }
            activeOperation = null;
            if (cameraErrorHandler != null) { camera.Error -= cameraErrorHandler; cameraErrorHandler = null; }
            CancellationTokenSource cancellation = audioCancellation;
            audioCancellation = null;
            if (cancellation != null) { cancellation.Cancel(); }
            audio.Stop(); UpdateButtons();
            Task cameraStop = camera.StopAsync();
            // Completion acknowledges native ownership release even when the
            // operation ended by cancellation or error. Observe those outcomes;
            // only a still-pending cleanup must keep new tests blocked.
            Task stopping = Task.WhenAll(IgnorePreviousStop(deviceStopTask), IgnorePreviousStop(cameraStop),
                IgnorePreviousStop(camera.Completion), IgnorePreviousStop(audio.Completion));
            deviceStopTask = stopping;
            bool released = false;
            try
            {
                if (await Task.WhenAny(stopping, Task.Delay(8000)) == stopping) { await stopping; released = true; }
            }
            catch (Exception) { }
            finally
            {
                if (cancellation != null) { cancellation.Dispose(); }
                Observe(stopping);
                if (current == generation)
                {
                    releasePending = !released;
                    NormalizeStoppedStatus(pendingStoppedOperation, released);
                    if (released) { pendingStoppedOperation = null; }
                    cameraHost.Visibility = Visibility.Collapsed; cameraPlaceholder.Visibility = Visibility.Visible;
                    level.Value = 0; busy = false; UpdateButtons(); UpdateVerdict();
                }
            }
        }

        private void NormalizeStoppedStatus(string operation, bool released)
        {
            if (operation == null) { return; }
            string text = !released ? "Остановка запрошена. Ожидаем освобождения устройства драйвером."
                : operation == "camera" ? "Камера выключена."
                : operation == "microphone-record" ? "Запись остановлена. Незавершённая запись удалена."
                : "Воспроизведение остановлено.";
            (operation == "camera" ? cameraStatus : operation == "speakers" ? speakerStatus : micStatus).Text = text;
        }

        private static async Task IgnorePreviousStop(Task previous) { try { await previous; } catch { } }
        private static void Observe(Task task) { task.ContinueWith(done => { var ignored = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        private bool CanContinue(int request) { return !closed && request == generation && !releasePending && deviceStopTask.IsCompleted && camera.Completion.IsCompleted && audio.Completion.IsCompleted; }
        private bool CanUseControls { get { return !closed && !busy && !releasePending && deviceStopTask.IsCompleted; } }

        private string Kind { get { return Kinds[Math.Max(0, tabs.SelectedIndex)]; } }

        private void Confirm(string outcome)
        {
            string kind = Kind;
            if (!CanUseControls || (outcome == "passed" && !exercised.Contains(kind))) { return; }
            string id = "local-keyboard", name = "Клавиатура текущего компьютера";
            if (kind == "camera") { var d = cameras.SelectedItem as CameraDevice; id = d == null ? "unavailable" : d.Id; name = d == null ? "Камера не определена" : d.Name; }
            if (kind == "microphone" || kind == "speakers")
            {
                var d = (kind == "microphone" ? inputs : outputs).SelectedItem as AudioDevice;
                name = d == null ? "Устройство не определено" : d.Name;
                // Legacy wave IDs are not hardware identities. Preserve the selected
                // name as historical context, never assert the current device matches.
                id = d == null ? "unavailable" : "wave:" + d.Id.ToString(CultureInfo.InvariantCulture) + ":" + d.Name;
            }
            var result = new PeripheralTestResult
            {
                Kind = kind, Outcome = outcome, DeviceId = id, DeviceName = name,
                Detail = kind == "keyboard" ? "Нажато клавиш: " + keyboard.SeenCount + ". Итог подтверждён пользователем." : "Результат указан пользователем.",
                TestedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            bool persisted = false;
            try { persisted = save != null && save(result); } catch (Exception) { }
            results[kind] = result;
            UpdateVerdict();
            session.Text = persisted ? "Результат сохранён на этом компьютере." : "Результат доступен в этой сессии. Сохранить на диск не удалось.";
        }

        private void ResetExercise(string kind) { exercised.Remove(kind); UpdateVerdict(); }

        private void UpdateVerdict()
        {
            if (tabs.SelectedIndex < 0) { return; }
            passed.IsEnabled = CanUseControls && exercised.Contains(Kind);
            problem.IsEnabled = CanUseControls;
            session.Text = releasePending ? "Драйвер ещё не освободил устройство. Новые тесты заблокированы. Повторите остановку."
                : exercised.Contains(Kind) ? "Оцените устройство и выберите результат." : "«Работает» станет доступно после теста. При отсутствии устройства можно отметить проблему.";
            PeripheralTestResult result;
            saved.Text = results.TryGetValue(Kind, out result)
                ? "Последнее подтверждение пользователя: " + (result.Outcome == "passed" ? "работает" : "проблема") + " · " + DateTime.Parse(result.TestedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g") + " · " + result.DeviceName + ". Не подтверждает текущее состояние устройства."
                : String.IsNullOrWhiteSpace(historyError) ? "Сохранённого результата нет." : historyError;
        }

        private void UpdateButtons()
        {
            cameraStart.IsEnabled = Kind == "camera" && CanUseControls && !camera.IsRunning && cameras.SelectedItem != null;
            record.IsEnabled = Kind == "microphone" && CanUseControls && inputs.SelectedItem != null;
            playback.IsEnabled = Kind == "microphone" && CanUseControls && recording != null && outputs.SelectedItem != null;
            left.IsEnabled = right.IsEnabled = Kind == "speakers" && CanUseControls && outputs.SelectedItem != null;
            foreach (Button refresh in refreshButtons) { refresh.IsEnabled = CanUseControls; }
            cameras.IsEnabled = Kind == "camera" && CanUseControls;
            inputs.IsEnabled = Kind == "microphone" && CanUseControls;
            outputs.IsEnabled = Kind == "speakers" && CanUseControls;
            keyboardStart.IsEnabled = Kind == "keyboard" && CanUseControls && !keyboard.IsRunning;
            keyboardStop.IsEnabled = Kind == "keyboard";
            keyboardReset.IsEnabled = Kind == "keyboard" && CanUseControls;
            passed.IsEnabled = CanUseControls && exercised.Contains(Kind); problem.IsEnabled = CanUseControls;
        }

        private void ClearRecording()
        {
            if (recording != null) { recording.Dispose(); }
            recording = null;
        }

        private void Dispatch(Action action) { if (!Dispatcher.HasShutdownStarted) { Dispatcher.BeginInvoke(action); } }
        private static SolidColorBrush Brush(string color) { return (SolidColorBrush)new BrushConverter().ConvertFromString(color); }
        private static TextBlock Text(string value) { return new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), Foreground = Brush("#343A40") }; }
        private static ComboBox Selector(string label)
        {
            var combo = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 8, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(combo, label); return combo;
        }
        private static Button MakeButton(string caption)
        {
            var button = new Button { Content = caption, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 4), MinHeight = 34, Background = Brush("#FFFFFF"), Foreground = Brush("#20252A"), BorderBrush = Brush("#BAC2C9") };
            AutomationProperties.SetName(button, caption); return button;
        }
        private static WrapPanel Row(params UIElement[] controls) { var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) }; foreach (var control in controls) { row.Children.Add(control); } return row; }
    }

    internal sealed class CameraHost : HwndHost
    {
        internal event Action ViewportChanged;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string name, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            IntPtr handle = CreateWindowEx(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (handle == IntPtr.Zero) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            return new HandleRef(this, handle);
        }
        protected override void DestroyWindowCore(HandleRef handle) { DestroyWindow(handle.Handle); }
        protected override void OnWindowPositionChanged(Rect rectangle) { base.OnWindowPositionChanged(rectangle); if (ViewportChanged != null) { ViewportChanged(); } }
        internal int PixelWidth { get { NativeRect rect; return GetClientRect(Handle, out rect) ? Math.Max(1, rect.Right - rect.Left) : 640; } }
        internal int PixelHeight { get { NativeRect rect; return GetClientRect(Handle, out rect) ? Math.Max(1, rect.Bottom - rect.Top) : 360; } }
    }
}
