using System;
using System.Collections.Generic;
using System.Globalization;
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
    public static class CrmProductDraftTests
    {
        public static void Run()
        {
            VerifyAllowlistAndSelection();
            VerifyMissingAndUnfinishedResults();
            decimal? price;
            Require(CrmIntakeWindow.ParsePrice("", out price) && !price.HasValue, "Blank price remains null, never an invented zero");
            Require(CrmIntakeWindow.ParsePrice("4 500,50", out price) && price == 4500.50m, "Operator price accepts MDL decimal comma and spaced thousands");
            Require(CrmIntakeWindow.ParsePrice("4500.50", out price) && price == 4500.50m, "Dot decimal is also supported");
            foreach (string input in new[] { "-1", "NaN", "1,000", "1.2.3", "1e3", "1,234.50", "9999999999" })
            { Require(!CrmIntakeWindow.ParsePrice(input, out price), "Ambiguous/invalid price is rejected: " + input); }
            string queued = CrmIntakeWindow.ResultMessage(new CrmIntakeResult { ProductId = "42", ImageStatus = "attached", PrintRequested = true, PrintStatus = "queued" });
            Require(queued.Contains("очередь") && !queued.Contains("напечатан"), "Queued printing never claims physical printing completed");
            string unknown = CrmIntakeWindow.ResultMessage(new CrmIntakeResult { ProductId = "42", ImageStatus = "", PrintRequested = false });
            Require(unknown.Contains("не подтверждена") && unknown.Contains("повторите"), "Unknown photo status explicitly requires retry");
        }

        public static void RunUi()
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                VerifyFormAndExactRetry();
                VerifyRestoredPending();
                VerifyClosedLogin();
                VerifyOfficeRestriction();
                VerifyLayout(new Size(850, 740), null);
                VerifyLayout(new Size(500, 400), null);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public static void WritePreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            VerifyLayout(new Size(850, 740), Path.Combine(directory, "crm-ui.png"));
            VerifyLayout(new Size(500, 400), Path.Combine(directory, "crm-ui-small.png"));
            VerifyLayout(new Size(850, 740), Path.Combine(directory, "crm-ui-ready.png"), true);
            VerifyLayout(new Size(500, 400), Path.Combine(directory, "crm-ui-small-bottom.png"), true);
            CrmProductDraft photo = Draft(); photo.Disks[0].Include = true;
            File.WriteAllBytes(Path.Combine(directory, "specification-photo.png"), CrmProductImage.RenderPng(photo.Title, photo.BuildDescription()));
        }

        private static void VerifyAllowlistAndSelection()
        {
            var sample = Sample();
            sample.ComputerName = "PRIVATE_MACHINE"; sample.SensorStatus = "PRIVATE_SENSOR"; sample.Issues.Add("PRIVATE_ISSUE");
            sample.Disks[0].StorageIdentity = "PRIVATE_STORAGE"; sample.Disks[0].PersistentHardwareId = "PRIVATE_DISK_ID";
            sample.Disks[0].TestVolumeRoot = "PRIVATE_ROOT"; sample.Disks[0].Volumes = "PRIVATE_VOLUME"; sample.Disks[0].SmartStatus = "PRIVATE_SMART_STATUS";
            sample.Gpus[0].PersistentHardwareId = "PRIVATE_GPU_ID"; sample.Gpus[0].TemperatureStatus = "PRIVATE_GPU_STATUS";
            sample.Screens[0].InstanceName = "PRIVATE_SCREEN_ID"; sample.Screens[0].Source = "PRIVATE_SCREEN_SOURCE";
            sample.BatteryDetail = "PRIVATE_BATTERY"; sample.CpuClockSource = "PRIVATE_CLOCK_SOURCE";
            var diskTest = new BenchmarkProgress { DiskIndex = 0, Percent = 100, ReadBytesPerSecond = 3500000000, WriteBytesPerSecond = 2100000000,
                FromHistory = true, TestedAtUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc), Stage = "PRIVATE_STAGE", QueueDepth = 8, ThreadCount = 1 };
            var peripheral = new PeripheralTestResult { Kind = "keyboard", Outcome = "passed", DeviceId = "PRIVATE_PERIPHERAL_ID", DeviceName = "PRIVATE_PERIPHERAL_NAME",
                Detail = "PRIVATE_PERIPHERAL_DETAIL", TestedAtUtc = DateTime.UtcNow.AddDays(-1).ToString("o"), ConfirmedBy = "user" };
            var ports = new PortSpecification { SourceUrl = "https://support.hp.com/test?private=SECRET_QUERY", Note = "PRIVATE_PORT_NOTE",
                Ports = new List<PortEntry> { new PortEntry { Name = "HDMI", Count = 1, Optional = true, Detail = "PRIVATE_PORT_DETAIL" } } };
            CrmProductDraft draft = CrmProductDraftBuilder.Build(sample, new[] { diskTest }, null, ports, new[] { peripheral }, null);
            Require(draft.Title == "HP EliteBook 840 G8" && !draft.Title.Contains(sample.ComputerName), "Product title is model-based, not machine name");
            Require(draft.Disks.Count == 2 && !draft.Disks[0].Include && !draft.Disks[1].Include, "Every disk requires explicit inclusion");
            Require(draft.Screens.Count == 1, "External and unclassified screens are omitted");
            string text = draft.BuildDescription();
            Require(!text.Contains("Internal SSD") && !text.Contains("External USB SSD"), "Unselected attached disks cannot enter the sales description");
            draft.Disks[0].Include = true;
            text = draft.BuildDescription();
            Require(text.Contains("Internal SSD") && !text.Contains("External USB SSD"), "Only the selected sale disk is described");
            Require(text.Contains("3500 MB/s") && text.Contains("2026-01-02") && text.Contains("сохранённый замер"), "Cached test preserves original measurement and date");
            Require(text.Contains("опция") && text.Contains("не измерение") && text.Contains("подтверждено пользователем"), "Specification and manual-verdict limitations survive projection");
            Require(!text.Contains("PRIVATE_") && !text.Contains("SECRET_QUERY"), "Identifiers, paths, raw diagnostics and URL query data never enter projection");
            sample.CpuName = "Changed after projection"; sample.Disks[0].Name = "Changed disk";
            Require(!draft.BuildDescription().Contains("Changed"), "The draft is a detached copy, not a live hardware reference");
            draft.Gpus[0].Include = false;
            Require(!draft.BuildDescription().Contains("Iris Xe"), "Deselected GPU is omitted");
        }

        private static void VerifyMissingAndUnfinishedResults()
        {
            var unknown = new Snapshot { ComputerName = "PRIVATE_MACHINE", ComputerManufacturer = "To be filled by O.E.M.", ComputerModel = "System Product Name" };
            var draft = CrmProductDraftBuilder.Build(unknown, null, null, null, null, null);
            Require(draft.Title == "Компьютер" && draft.Cpu.Length == 0 && draft.Memory.Length == 0 && draft.Battery.Length == 0,
                "Unavailable values and BIOS placeholders are not invented as commercial facts");
            var sample = Sample();
            sample.BatteryHealthPercent = Double.NaN; sample.BatteryFullChargeWh = Double.PositiveInfinity;
            var disk = new BenchmarkProgress { DiskIndex = 0, Percent = 70, ReadBytesPerSecond = 3500000000, Error = "PRIVATE_ERROR_PATH" };
            var gpu = new GpuBenchmarkProgress { AdapterIndex = 0, Completed = false, FramesPerSecond = 100, Error = "PRIVATE_GPU_ERROR" };
            draft = CrmProductDraftBuilder.Build(sample, new[] { disk }, new[] { gpu }, null, null, null); draft.Disks[0].Include = true;
            string text = draft.BuildDescription();
            Require(text.Contains("ошибка / остановлен") && !text.Contains("3500 MB/s") && !text.Contains("100 FPS"), "Partial/failed runs cannot become successful sale benchmarks");
            Require(!text.Contains("PRIVATE_") && !text.Contains("NaN") && !text.Contains("Infinity"), "Invalid numeric readings and raw errors are omitted");
        }

        private static void VerifyFormAndExactRetry()
        {
            var client = new FakeClient(); var window = new CrmIntakeWindow(Draft(), client);
            Require(client.LoginCalls == 0 && client.SubmitCalls == 0, "Opening a draft performs no automatic login/network write");
            SignIn(window); Require(client.LoginCalls == 1 && Field<PasswordBox>(window, "password").Password.Length == 0, "Explicit login clears the password field");
            Require(Field<ComboBox>(window, "condition").SelectedIndex == -1, "Condition is never inferred from hardware age");
            Drain((Task)Invoke(window, "Submit")); Require(client.SubmitCalls == 0, "A missing condition blocks sending");
            Field<ComboBox>(window, "condition").SelectedIndex = 2;
            Field<TextBox>(window, "quantity").Text = "2"; Field<TextBox>(window, "price").Text = "4 500,50";
            Field<CheckBox>(window, "reviewed").IsChecked = true;
            Drain((Task)Invoke(window, "Submit")); Require(client.SubmitCalls == 0, "A photo must be previewed before the first send");
            MarkPhotoReviewed(window);
            client.SendGate = new TaskCompletionSource<CrmIntakeResult>();
            Task first = (Task)Invoke(window, "Submit");
            Require(client.SubmitCalls == 1 && !Field<Button>(window, "send").IsEnabled, "Only one send can run at a time");
            Drain((Task)Invoke(window, "Submit")); Require(client.SubmitCalls == 1, "A second click cannot create a second request");
            CrmSubmission exact = client.Pending;
            Require(exact.Quantity == 2 && exact.SalePrice == 4500.5m && exact.Condition == "used", "Reviewed commercial fields reach the request unchanged");
            Require(Convert.FromBase64String(exact.SpecificationImageBase64).Length > 24, "Submission contains an actual generated PNG");
            client.SendGate.SetException(new CrmException("Тестовая потеря ответа")); Drain(first);
            Require(!Field<TextBox>(window, "name").IsEnabled && client.Pending == exact, "Unknown outcome locks the exact pending submission");
            client.SendGate = null; client.PartialImage = true;
            Drain((Task)Invoke(window, "Submit"));
            Require(client.SubmitCalls == 2 && ReferenceEquals(client.LastSubmission, exact), "Retry passes the exact pending object without a new idempotency key");
            Require(Convert.ToString(Field<Button>(window, "send").Content).Contains("загрузку фото"), "Partial image result offers only continuation of its photo upload");
            client.PartialImage = false;
            Drain((Task)Invoke(window, "Submit"));
            Require(client.SubmitCalls == 3 && !Field<Button>(window, "send").IsEnabled, "Confirmed completion locks creation for the remainder of this window");
            Drain((Task)Invoke(window, "Submit")); Require(client.SubmitCalls == 3, "Success cannot be submitted again from the same window");
            window.Close(); Pump(); Require(client.Disposed, "Closing disposes the client's login/session resources");
        }

        private static void VerifyRestoredPending()
        {
            var client = new FakeClient();
            var pending = new CrmSubmission { IdempotencyKey = Guid.NewGuid().ToString(), Name = "Previously reviewed computer", Description = "Original exact description",
                Condition = "like_new", Quantity = 3, WarehouseId = 7, SpecificationImageBase64 = "unchanged", Print = false };
            client.Restore(pending);
            var window = new CrmIntakeWindow(Draft(), client); SignIn(window);
            Require(Field<TextBox>(window, "name").Text == pending.Name && Field<TextBox>(window, "summary").Text == pending.Description,
                "Recovery displays the pending product, not the current computer's specs");
            Require(!Field<TextBox>(window, "name").IsEnabled && Field<StackPanel>(window, "components").Visibility == Visibility.Collapsed,
                "Recovered submission cannot be changed by current component selection");
            Drain((Task)Invoke(window, "Submit"));
            Require(ReferenceEquals(client.LastSubmission, pending) && client.LastSubmission.SpecificationImageBase64 == "unchanged",
                "A restored request keeps its original photo and idempotency data");
            window.Close(); Pump();
        }

        private static void VerifyClosedLogin()
        {
            var client = new FakeClient { LoginGate = new TaskCompletionSource<CrmContext>() };
            var window = new CrmIntakeWindow(Draft(), client);
            Field<TextBox>(window, "email").Text = "operator"; Field<PasswordBox>(window, "password").Password = "fake-only";
            Task login = (Task)Invoke(window, "SignIn"); window.Close(); Pump();
            Require(client.LastToken.IsCancellationRequested, "Closing requests cancellation of an unfinished login");
            client.LoginGate.SetResult(FakeClient.Context()); Drain(login);
            Require(Field<CrmContext>(window, "context") == null && client.Disposed, "Late login completion cannot reactivate a closed form and is disposed");
        }

        private static void VerifyOfficeRestriction()
        {
            var client = new FakeClient { PrintingDenied = true };
            var window = new CrmIntakeWindow(Draft(), client); SignIn(window);
            Require(!Field<CheckBox>(window, "print").IsEnabled && !Field<ComboBox>(window, "printer").IsEnabled,
                "The server's office denial disables printing without trusting the client's local IP");
            Field<ComboBox>(window, "condition").SelectedIndex = 2;
            Field<CheckBox>(window, "reviewed").IsChecked = true;
            Field<CheckBox>(window, "print").IsChecked = true;
            MarkPhotoReviewed(window); Drain((Task)Invoke(window, "Submit"));
            Require(client.SubmitCalls == 0, "Even a programmatically selected disabled print checkbox cannot bypass the context gate");
            window.Close(); Pump();
        }

        private static void VerifyLayout(Size size, string path, bool signedIn = false)
        {
            var client = new FakeClient(); var window = new CrmIntakeWindow(Draft(), client);
            if (signedIn)
            {
                SignIn(window);
                Field<ComboBox>(window, "condition").SelectedIndex = 2;
                Field<TextBox>(window, "price").Text = "4500";
                Field<TextBox>(window, "notes").Text = "Пример: зарядное устройство в комплекте. Состояние корпуса и гарантию заполнит сотрудник.";
                Field<CheckBox>(window, "reviewed").IsChecked = true;
                Field<CheckBox>(window, "print").IsChecked = true;
            }
            var content = (FrameworkElement)window.Content; window.Content = null;
            var host = new Border { Child = content, Width = size.Width, Height = size.Height, Background = window.Background, UseLayoutRounding = true };
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily); System.Windows.Documents.TextElement.SetFontSize(host, 14);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            host.Measure(size); host.Arrange(new Rect(new Point(), size)); host.UpdateLayout();
            var scroller = Find<ScrollViewer>(content);
            Require(scroller != null && scroller.ViewportHeight > 50, "A small dialog retains a usable scrollable body");
            Rect send = Field<Button>(window, "send").TransformToAncestor(content).TransformBounds(new Rect(Field<Button>(window, "send").RenderSize));
            Require(send.Top >= 0 && send.Bottom <= content.ActualHeight + 1 && send.Left >= 0 && send.Right <= content.ActualWidth + 1,
                "Primary send action stays reachable at " + size);
            scroller.ScrollToEnd(); host.UpdateLayout();
            Rect printer = Field<ComboBox>(window, "printer").TransformToAncestor(scroller).TransformBounds(new Rect(Field<ComboBox>(window, "printer").RenderSize));
            Require(printer.Bottom <= scroller.ActualHeight + 1, "Printer remains reachable by scrolling at " + size);
            if (!signedIn) { scroller.ScrollToTop(); } host.UpdateLayout();
            if (path != null)
            {
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(path)) { png.Save(stream); }
            }
            Require(client.LoginCalls == (signedIn ? 1 : 0) && client.SubmitCalls == 0, "Layout checks use only a fake client and never submit a product");
            window.Close(); Pump();
        }

        private sealed class FakeClient : CrmIntakeClient
        {
            internal int LoginCalls, SubmitCalls;
            internal bool Disposed, PartialImage, PrintingDenied;
            internal CrmSubmission LastSubmission;
            internal CancellationToken LastToken;
            internal TaskCompletionSource<CrmContext> LoginGate;
            internal TaskCompletionSource<CrmIntakeResult> SendGate;
            internal void Restore(CrmSubmission submission) { Pending = submission; }
            internal static CrmContext Context()
            { return new CrmContext { UserId = "5", OfficeIp = "149.3.190.162", CanPrint = true,
                Warehouses = new List<CrmChoice> { new CrmChoice { Id = "7", Name = "Тестовый склад" } },
                Printers = new List<CrmChoice> { new CrmChoice { Id = "00000000-0000-0000-0000-000000000001", Name = "Тестовый принтер (без печати)" } } }; }
            public override Task<CrmContext> LoginAsync(string email, string password, CancellationToken token)
            {
                LoginCalls++; LastToken = token; CrmContext context = Context();
                if (PrintingDenied) { context.CanPrint = false; context.PrintReason = "Печать разрешена только из офиса ADAMO."; }
                return LoginGate == null ? Task.FromResult(context) : LoginGate.Task;
            }
            public override Task<CrmIntakeResult> SubmitAsync(CrmSubmission submission, CancellationToken token)
            {
                SubmitCalls++; LastSubmission = submission; LastToken = token; Pending = submission;
                if (SendGate != null) { return SendGate.Task; }
                if (!PartialImage) { Pending = null; }
                return Task.FromResult(new CrmIntakeResult { IntakeId = "44", ProductId = "55", Status = "created", ImageStatus = PartialImage ? "failed" : "attached",
                    PrintRequested = submission.Print, PrintStatus = submission.Print ? "queued" : "not_requested", ProductUrl = "https://crm.adamo.md/products/55/edit" });
            }
            public override void Dispose() { Disposed = true; base.Dispose(); }
        }

        private static CrmProductDraft Draft() { return CrmProductDraftBuilder.Build(Sample(), null, null, null, null, null); }
        private static Snapshot Sample()
        {
            var sample = new Snapshot { ComputerManufacturer = "HP", ComputerModel = "EliteBook 840 G8", ComputerSku = "Fixture SKU",
                CpuName = "Intel Core i5-1135G7", CpuCores = 4, CpuThreads = 8, RamInstalledBytes = 16UL * 1073741824, RamType = "DDR4", RamRate = "3200 MT/s",
                BatteryPresent = true, BatteryFullChargeWh = 45, BatteryDesignWh = 53, BatteryHealthPercent = 85, BatteryCycleCount = 120 };
            sample.Disks.Add(new DiskSnapshot { Index = 0, Name = "Internal SSD", MediaType = "SSD · NVMe", SizeBytes = 512000000000, HealthPercent = 97, PowerOnHours = 1234 });
            sample.Disks.Add(new DiskSnapshot { Index = 1, Name = "External USB SSD", MediaType = "SSD · USB", SizeBytes = 1000000000000 });
            sample.Gpus.Add(new GpuSnapshot { Name = "Intel Iris Xe", AdapterIndex = 0, IsIntegrated = true, MemoryBytes = 128UL * 1048576, SharedMemoryBytes = 8UL * 1073741824 });
            sample.Screens.Add(new ScreenSnapshot { IsInternal = true, Width = 1920, Height = 1080, RefreshHz = 60, MatrixType = "IPS", BrightnessNits = 250 });
            sample.Screens.Add(new ScreenSnapshot { IsInternal = false, Width = 3840, Height = 2160, MatrixType = "PRIVATE_EXTERNAL" });
            sample.Screens.Add(new ScreenSnapshot { IsInternal = null, Name = "PRIVATE_UNCLASSIFIED" });
            return sample;
        }
        private static void SignIn(CrmIntakeWindow window)
        { Field<TextBox>(window, "email").Text = "operator"; Field<PasswordBox>(window, "password").Password = "fake-only"; Drain((Task)Invoke(window, "SignIn")); }
        private static void MarkPhotoReviewed(CrmIntakeWindow window)
        { Set(window, "reviewedImageFingerprint", Field<TextBox>(window, "name").Text.Trim() + "\0" + (string)Invoke(window, "BuildDescription")); }
        private static void Drain(Task task)
        { DateTime until = DateTime.UtcNow.AddSeconds(5); while (!task.IsCompleted && DateTime.UtcNow < until) { Pump(); } Require(task.IsCompleted, "Fake operation completes within the test deadline"); task.GetAwaiter().GetResult(); }
        private static void Pump()
        { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static T Find<T>(DependencyObject root) where T : DependencyObject
        { if (root is T) { return (T)root; } for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { T found = Find<T>(VisualTreeHelper.GetChild(root, i)); if (found != null) { return found; } } return null; }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static object Invoke(object target, string method) { return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
