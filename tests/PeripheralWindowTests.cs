using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class PeripheralWindowTests
    {
        public static void Run()
        {
            VerifyVerdictsAndLifecycle();
            VerifyStaleStopCannotAlterNewerState();
            VerifyCancelledAndFailedOperationsRelease();
            VerifyStoppedStatusAndRecordingRetention();
            VerifyInactiveTabActionsDoNotRun();
            VerifyAudioContinuationStaysOnItsTab();
            VerifyIndependentDiscovery();
            foreach (Size size in new[] { new Size(480, 360), new Size(640, 440), new Size(920, 730) })
            {
                for (int tab = 0; tab < 4; tab++) { VerifyLayout(size, tab, null); }
            }
        }

        public static void WritePreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (Size size in new[] { new Size(480, 360), new Size(640, 440), new Size(920, 730) })
            {
                for (int tab = 0; tab < 4; tab++) { VerifyLayout(size, tab, Path.Combine(directory, "tab-" + tab + "-" + size.Width + "x" + size.Height + ".png")); }
            }
        }

        private static void VerifyVerdictsAndLifecycle()
        {
            var written = new List<PeripheralTestResult>();
            var window = new PeripheralTestsWindow(null, item => { written.Add(item.Copy()); return true; }, false, "История временно недоступна.");
            var tabs = Field<TabControl>(window, "tabs");
            var passed = Field<Button>(window, "passed");
            var problem = Field<Button>(window, "problem");
            Require(tabs.Items.Count == 4, "Camera, microphone, keyboard and speakers each have a tab");
            string[] labels = { "Камера", "Микрофон", "Клавиатура", "Динамики" };
            for (int index = 0; index < 4; index++)
            {
                tabs.SelectedIndex = index;
                Require(Convert.ToString(((TabItem)tabs.Items[index]).Header) == labels[index], "The tab has its Russian name");
                Require(!passed.IsEnabled && problem.IsEnabled, "Passing requires a test; an absent-device problem can be reported");
                Require(Field<TextBlock>(window, "saved").Text.Contains("История временно"), "History failures are not presented as no saved result");
            }
            Require(Field<List<Button>>(window, "refreshButtons").Count == 4, "Every tab can refresh device discovery");
            Require(!Field<Button>(window, "cameraStart").IsEnabled && !Field<Button>(window, "record").IsEnabled
                && !Field<Button>(window, "left").IsEnabled && !Field<Button>(window, "right").IsEnabled, "Without enumeration no device start is enabled");
            tabs.SelectedIndex = 0;
            problem.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(written.Count == 1 && written[0].Kind == "camera" && written[0].Outcome == "problem", "An absent camera can receive an explicit user problem verdict");
            Require(!passed.IsEnabled, "A saved problem verdict does not unlock passing");
            tabs.SelectedIndex = 2;
            var keyboard = Field<KeyboardTestPanel>(window, "keyboard");
            keyboard.Start(); keyboard.ProcessKey(System.Windows.Input.Key.A, true); keyboard.ProcessKey(System.Windows.Input.Key.A, false); keyboard.Stop();
            Require(passed.IsEnabled, "A received keyboard event enables user confirmation");
            passed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(written.Count == 2 && written[1].Kind == "keyboard" && PeripheralTestResult.Valid(written[1]), "Keyboard confirmation produces a valid user result");
            Field<Button>(window, "keyboardReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!passed.IsEnabled && keyboard.SeenCount == 0, "Reset revokes current keyboard evidence");
            keyboard.Start(); keyboard.ProcessKey(System.Windows.Input.Key.B, true);
            tabs.SelectedIndex = 3;
            Require(!keyboard.IsRunning && keyboard.PressedCount == 0, "Changing tabs releases keyboard capture");
            Require(!passed.IsEnabled, "Keyboard evidence cannot enable a speaker verdict");
            var retained = new AudioRecording(new byte[160], 8000, 1);
            Set(window, "recording", retained);
            Invoke(window, "ClearRecording");
            Require(retained.ByteCount == 0 && Field<AudioRecording>(window, "recording") == null, "Clearing uses Dispose on the actual recording, not a defensive Data clone");
            AssertNoDevicesStarted(window);
            CloseWithoutHardware(window);
        }

        private static void VerifyStaleStopCannotAlterNewerState()
        {
            var window = new PeripheralTestsWindow(new PeripheralTestResult[0], null, false);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "deviceStopTask", pending.Task);
            Task first = (Task)Invoke(window, "StopDevices");
            Task second = (Task)Invoke(window, "StopDevices");
            Require(Field<bool>(window, "busy"), "Stopping remains busy until cleanup acknowledges completion");
            int latest = Field<int>(window, "generation");
            var newerCancellation = new CancellationTokenSource();
            // Simulate a newer operation owner while old asynchronous callbacks are
            // still queued. Neither old stop may dispose or clear that owner's state.
            Set(window, "audioCancellation", newerCancellation);
            Set(window, "generation", latest + 1);
            pending.SetResult(true);
            Drain(Task.WhenAll(first, second));
            Require(Field<bool>(window, "busy"), "Stale stops cannot expose newer work as idle");
            Require(ReferenceEquals(Field<CancellationTokenSource>(window, "audioCancellation"), newerCancellation)
                && !newerCancellation.IsCancellationRequested && newerCancellation.Token.CanBeCanceled, "Stale stops cannot cancel/dispose the newer token");
            Require(!(bool)Invoke(window, "CanContinue", latest), "A start continuation from before a newer Stop is rejected");
            Drain((Task)Invoke(window, "StopDevices"));
            Require(!Field<bool>(window, "busy"), "The current stop can release its own state");
            AssertNoDevicesStarted(window);
            CloseWithoutHardware(window);
        }

        private static void VerifyIndependentDiscovery()
        {
            MethodInfo method = typeof(PeripheralTestsWindow).GetMethod("DiscoverOne", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(typeof(AudioDevice));
            Func<Task<IList<AudioDevice>>> failed = delegate { throw new InvalidOperationException("fake failed provider"); };
            Func<Task<IList<AudioDevice>>> healthy = () => Task.FromResult<IList<AudioDevice>>(new List<AudioDevice> { new AudioDevice(42, "Fake device") });
            var pending = new TaskCompletionSource<IList<AudioDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task<IList<AudioDevice>>> stalled = () => pending.Task;
            Task a = (Task)method.Invoke(null, new object[] { failed, 10 });
            Task b = (Task)method.Invoke(null, new object[] { healthy, 10 });
            Task c = (Task)method.Invoke(null, new object[] { stalled, 10 });
            Drain(Task.WhenAll(a, b, c));
            object broken = a.GetType().GetProperty("Result").GetValue(a, null);
            object working = b.GetType().GetProperty("Result").GetValue(b, null);
            object timeout = c.GetType().GetProperty("Result").GetValue(c, null);
            Require(Field<string>(broken, "Error").Contains("fake failed provider"), "A failed discovery reports its own error");
            Require(Field<IList<AudioDevice>>(working, "Devices").Count == 1 && Field<string>(working, "Error") == null, "Another provider succeeds independently");
            Require(Field<string>(timeout, "Error").Contains("не ответил"), "A stalled provider returns a bounded driver-timeout status");
            pending.SetResult(new List<AudioDevice>());
        }

        private static void VerifyCancelledAndFailedOperationsRelease()
        {
            var window = new PeripheralTestsWindow(new PeripheralTestResult[0], null, false);
            Field<TabControl>(window, "tabs").SelectedIndex = 2;
            var audio = Field<PeripheralAudio>(window, "audio");
            Set(audio, "activeTask", Task.FromCanceled(new CancellationToken(true)));
            Drain((Task)Invoke(window, "StopDevices"));
            Require(!Field<bool>(window, "releasePending") && Field<Button>(window, "keyboardStart").IsEnabled,
                "Cancelled audio with acknowledged cleanup does not permanently block later tests");
            Set(audio, "activeTask", Task.FromException(new InvalidOperationException("fake completed audio failure")));
            Drain((Task)Invoke(window, "StopDevices"));
            Require(!Field<bool>(window, "releasePending") && Field<Button>(window, "problem").IsEnabled,
                "Faulted audio with acknowledged cleanup releases controls while preserving manual verdict ability");
            CloseWithoutHardware(window);
        }

        private static void VerifyInactiveTabActionsDoNotRun()
        {
            var window = new PeripheralTestsWindow(null, null, false);
            var tabs = Field<TabControl>(window, "tabs");
            Set(window, "initializing", true);
            // Deliberately invalid metadata cannot open physical hardware even
            // if this regression fails. Selection alone never starts a device.
            Field<ComboBox>(window, "cameras").ItemsSource = new[] { new CameraDevice { Id = "", Name = "Fake camera" } };
            Field<ComboBox>(window, "inputs").ItemsSource = new[] { new AudioDevice(-2, "Fake microphone") };
            Field<ComboBox>(window, "outputs").ItemsSource = new[] { new AudioDevice(-2, "Fake speakers") };
            foreach (string selector in new[] { "cameras", "inputs", "outputs" }) { Field<ComboBox>(window, selector).SelectedIndex = 0; }
            Set(window, "initializing", false);
            var retained = new AudioRecording(new byte[160], 8000, 1);
            Set(window, "recording", retained);
            var keyboard = Field<KeyboardTestPanel>(window, "keyboard");
            keyboard.Start(); keyboard.ProcessKey(System.Windows.Input.Key.A, true); keyboard.Stop();
            string[][] actions = { new[] { "cameraStart" }, new[] { "record", "playback" }, new[] { "keyboardStart", "keyboardStop", "keyboardReset" }, new[] { "left", "right" } };
            for (int selected = 0; selected < 4; selected++)
            {
                tabs.SelectedIndex = selected; Invoke(window, "UpdateButtons");
                int generation = Field<int>(window, "generation");
                for (int hidden = 0; hidden < 4; hidden++)
                {
                    if (hidden == selected) { continue; }
                    foreach (string field in actions[hidden])
                    {
                        Button button = Field<Button>(window, field);
                        Require(!button.IsEnabled, "Inactive-tab action is disabled: " + field);
                        // RaiseEvent bypasses normal disabled-control hit testing,
                        // proving the handler itself rejects a foreign-tab action.
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(Field<int>(window, "generation") == generation, "Inactive-tab action cannot stop/start another test: " + field);
                        Require(!keyboard.IsRunning && keyboard.HasSeen("A"), "Inactive keyboard controls cannot capture or clear checked keys");
                        Require(ReferenceEquals(retained, Field<AudioRecording>(window, "recording")) && retained.ByteCount == 160,
                            "Inactive microphone controls cannot discard the retained recording");
                    }
                }
            }
            Invoke(window, "ClearRecording"); AssertNoDevicesStarted(window); CloseWithoutHardware(window);
        }

        private static void VerifyAudioContinuationStaysOnItsTab()
        {
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
            var window = new PeripheralTestsWindow(null, null, false);
            var tabs = Field<TabControl>(window, "tabs"); tabs.SelectedIndex = 1;
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "deviceStopTask", pending.Task);
            bool invoked = false, completed = false;
            Func<Task> operation = delegate { invoked = true; return Task.FromResult(0); };
            Action finished = delegate { completed = true; };
            Task starting = (Task)Invoke(window, "AudioOperation", "microphone", operation, Field<TextBlock>(window, "micStatus"), "Fake playback", finished);
            // Isolate the selected-tab check from the separate generation guard.
            Set(window, "initializing", true); tabs.SelectedIndex = 3; Set(window, "initializing", false);
            pending.SetResult(true); Drain(starting);
            Require(!invoked && !completed, "A delayed start cannot run after its selected tab changed, even without a generation change");
            AssertNoDevicesStarted(window); CloseWithoutHardware(window);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }

        private static void VerifyStoppedStatusAndRecordingRetention()
        {
            var window = new PeripheralTestsWindow(new PeripheralTestResult[0], null, false);
            foreach (string operation in new[] { "camera", "microphone-record", "microphone-play", "speakers" })
            {
                Set(window, "activeOperation", operation);
                Drain((Task)Invoke(window, "StopDevices"));
                TextBlock status = Field<TextBlock>(window, operation == "camera" ? "cameraStatus" : operation == "speakers" ? "speakerStatus" : "micStatus");
                Require(status.Text.Contains(operation == "camera" ? "выключена" : "остановлен"), "Cancelled operations show an explicit stopped status: " + operation);
            }
            var retained = new AudioRecording(new byte[160], 8000, 1);
            Set(window, "recording", retained);
            Set(window, "activeOperation", "microphone-play");
            Field<TabControl>(window, "tabs").SelectedIndex = 3;
            Require(retained.ByteCount == 160 && ReferenceEquals(retained, Field<AudioRecording>(window, "recording")), "Changing tabs preserves a completed microphone recording for later playback");
            Field<TextBlock>(window, "micStatus").Text = "Микрофон недоступен: тестовая ошибка";
            Drain((Task)Invoke(window, "StopDevices"));
            Require(Field<TextBlock>(window, "micStatus").Text.Contains("тестовая ошибка"), "Stopping without an active operation preserves its completed/error message");
            Invoke(window, "ClearRecording");
            CloseWithoutHardware(window);
        }

        private static void VerifyLayout(Size size, int tab, string imagePath)
        {
            var window = new PeripheralTestsWindow(new PeripheralTestResult[0], null, false);
            Field<TabControl>(window, "tabs").SelectedIndex = tab;
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content, Background = window.Background, Width = size.Width, Height = size.Height, UseLayoutRounding = window.UseLayoutRounding };
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
            TextOptions.SetTextFormattingMode(host, TextOptions.GetTextFormattingMode(window));
            host.Measure(size); host.Arrange(new Rect(new Point(), size)); host.UpdateLayout();
            var outer = Field<ScrollViewer>(window, "outerScroll");
            Require(outer.ViewportHeight > 0 && outer.ViewportHeight <= size.Height + 1, "The complete dialog has a bounded scrollable viewport");
            Require(Field<TabControl>(window, "tabs").ActualHeight >= 240, "Small windows retain a usable tab region instead of collapsing it");
            outer.ScrollToEnd(); host.UpdateLayout();
            Require(Math.Abs(outer.VerticalOffset - outer.ScrollableHeight) < 1, "Bottom verdict/close actions remain reachable at " + size);
            Button close = FindButton(content, "Закрыть");
            Require(close != null, "A close action is present");
            Rect closeBounds = close.TransformToAncestor(outer).TransformBounds(new Rect(close.RenderSize));
            Require(closeBounds.Top >= -1 && closeBounds.Bottom <= outer.ActualHeight + 1, "Close action is visible after scrolling at " + size);
            Button refresh = FindButton(Field<TabControl>(window, "tabs"), "Обновить список");
            Require(refresh != null && refresh.Focusable, "The selected tab has a keyboard-accessible refresh action");
            outer.ScrollToTop(); host.UpdateLayout();
            if (imagePath != null)
            {
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(imagePath)) { png.Save(stream); }
            }
            AssertNoDevicesStarted(window);
            CloseWithoutHardware(window);
        }

        private static void AssertNoDevicesStarted(PeripheralTestsWindow window)
        {
            Require(!Field<CameraPreview>(window, "camera").IsRunning && Field<CameraPreview>(window, "camera").Completion.IsCompleted, "Layout/verdict tests never activate the camera");
            Require(Field<PeripheralAudio>(window, "audio").Completion.IsCompleted && Field<AudioRecording>(window, "recording") == null, "Layout/verdict tests never record or play sound");
        }

        private static void CloseWithoutHardware(Window window)
        {
            bool closed = false; window.Closed += delegate { closed = true; };
            window.Close();
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!closed && DateTime.UtcNow < deadline) { Pump(); }
            Require(closed, "Closing an idle dialog completes without a reentrant Close exception");
        }

        private static void Drain(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); }
            Require(task.IsCompleted, "The fake asynchronous operation completed within the test deadline");
            task.GetAwaiter().GetResult();
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static object Invoke(object target, string name, params object[] args) { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
        private static Button FindButton(DependencyObject root, string caption)
        {
            var button = root as Button;
            if (button != null && Convert.ToString(button.Content) == caption) { return button; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { Button found = FindButton(VisualTreeHelper.GetChild(root, i), caption); if (found != null) { return found; } }
            return null;
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
