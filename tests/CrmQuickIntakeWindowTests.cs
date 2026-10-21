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
    public static class CrmQuickIntakeWindowTests
    {
        public static void Run()
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                VerifyMinimalFlowAndRetry();
                VerifyPendingRestoreAndExpiredSession();
                VerifyWrongPasswordAndClose();
                VerifyClosedSubmit();
                string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "crm-minimal-20261020"));
                Directory.CreateDirectory(artifacts);
                // Include a conservative client area for a 320 x 280 window with
                // ordinary Windows title bar/borders, as well as the two previews.
                foreach (Size size in new[] { new Size(304, 240), new Size(320, 300), new Size(440, 300) })
                {
                    VerifyLayout(size, false, Path.Combine(artifacts, "password-" + size.Width + "x" + size.Height + ".png"));
                    VerifyLayout(size, true, Path.Combine(artifacts, "quantity-condition-" + size.Width + "x" + size.Height + ".png"));
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        private static void VerifyMinimalFlowAndRetry()
        {
            using (var client = new FakeClient())
            {
                var window = new CrmQuickIntakeWindow(Prepared(), client);
                Require(client.LoginCalls == 0 && client.SubmitCalls == 0, "Opening the compact form makes no network request or product");
                AssertControls(window, false);
                Drain(Continue(window)); Require(client.LoginCalls == 0, "An empty password does not call CRM");
                SignIn(window); AssertControls(window, true);
                Require(Field<PasswordBox>(window, "password").Password.Length == 0, "Password is cleared after login");
                Require(Field<TextBox>(window, "quantity").Text == "1" && Field<ComboBox>(window, "condition").SelectedIndex == -1,
                    "Quantity defaults to one and condition requires an explicit choice");
                Drain(Continue(window)); Require(client.SubmitCalls == 0, "No product is sent without a condition");
                Field<ComboBox>(window, "condition").SelectedIndex = 2;
                foreach (string invalid in new[] { "0", "501", "-1", "x", "" })
                {
                    Field<TextBox>(window, "quantity").Text = invalid; Drain(Continue(window));
                    Require(client.SubmitCalls == 0, "Invalid quantity cannot be submitted: " + invalid);
                }
                Field<TextBox>(window, "quantity").Text = "2";
                client.SendGate = new TaskCompletionSource<CrmQuickIntakeResult>();
                Task first = Continue(window);
                Require(client.SubmitCalls == 1 && !Field<Button>(window, "primary").IsEnabled && !Field<TextBox>(window, "quantity").IsEnabled,
                    "Sending disables the primary action and selection immediately");
                Drain(Continue(window)); Require(client.SubmitCalls == 1, "Double-click or repeated Enter cannot start another send");
                CrmQuickSubmission exact = client.Pending;
                Require(exact.Quantity == 2 && exact.Condition == "used", "Only the chosen quantity and condition enter the frozen submission");
                client.SendGate.SetException(new CrmException("PRIVATE_SERVER_TRACE")); Drain(first);
                Require(!Field<TextBlock>(window, "status").Text.Contains("PRIVATE_SERVER_TRACE"), "Raw server diagnostics are not displayed");
                Require(!Field<TextBox>(window, "quantity").IsEnabled && !Field<ComboBox>(window, "condition").IsEnabled,
                    "An uncertain send keeps quantity and condition locked");
                Field<TextBox>(window, "quantity").Text = "8"; Field<ComboBox>(window, "condition").SelectedIndex = 0;
                client.SendGate = null; client.Partial = true; Drain(Continue(window));
                Require(client.SubmitCalls == 2 && ReferenceEquals(client.LastSubmission, exact) && exact.Quantity == 2 && exact.Condition == "used",
                    "Retry keeps the exact pending object and cannot apply changed disabled controls");
                Require(Field<Button>(window, "primary").IsEnabled, "Partial completion offers the same primary action for retry");
                client.Partial = false; Drain(Continue(window));
                Require(client.SubmitCalls == 3 && Field<Button>(window, "primary").Visibility == Visibility.Collapsed,
                    "Confirmed completion removes the send action");
                Require(Field<TextBlock>(window, "status").Text.Contains("coada de imprimare"), "Accepted print jobs are described as queued, not physically printed");
                Drain(Continue(window)); Require(client.SubmitCalls == 3, "Success cannot create a second product in the same dialog");
                window.Close(); Pump(); Require(client.Disposed, "Closing releases the client");
            }
        }

        private static void VerifyPendingRestoreAndExpiredSession()
        {
            using (var client = new FakeClient())
            {
                var pending = Prepared(); pending.Quantity = 3; pending.Condition = "like_new"; pending.ScreenshotBase64 = "original-capture";
                client.Restore(pending);
                var window = new CrmQuickIntakeWindow(Prepared(), client);
                AssertControls(window, false); SignIn(window);
                Require(Field<TextBox>(window, "quantity").Text == "3" && Field<ComboBox>(window, "condition").SelectedIndex == 1,
                    "A saved send restores its original quantity and condition");
                Require(!Field<TextBox>(window, "quantity").IsEnabled && !Field<ComboBox>(window, "condition").IsEnabled,
                    "Restored submissions cannot be edited");
                client.SendError = new CrmException("Expired", 401); Drain(Continue(window));
                AssertControls(window, false);
                Require(ReferenceEquals(client.Pending, pending), "An expired session retains the pending operation");
                client.SendError = null; SignIn(window); Drain(Continue(window));
                Require(client.LoginCalls == 2 && ReferenceEquals(client.LastSubmission, pending) && client.LastSubmission.ScreenshotBase64 == "original-capture",
                    "Reauthentication resumes the exact saved operation and original capture");
                window.Close(); Pump();
            }
        }

        private static void VerifyWrongPasswordAndClose()
        {
            using (var client = new FakeClient())
            {
                client.LoginError = new CrmException("PRIVATE_LOGIN", 401);
                var window = new CrmQuickIntakeWindow(Prepared(), client); SignIn(window);
                Require(Field<TextBlock>(window, "status").Text == "Parola nu este corectă." && Field<Button>(window, "primary").IsEnabled,
                    "Wrong password gives a short error and permits retry");
                AssertControls(window, false);
                client.LoginError = null; client.LoginGate = new TaskCompletionSource<bool>();
                Field<PasswordBox>(window, "password").Password = "test-only";
                Task request = Continue(window); Drain(Continue(window));
                Require(client.LoginCalls == 2, "A pending login cannot be started twice");
                window.Close(); Pump();
                Require(client.LastToken.IsCancellationRequested && Field<PasswordBox>(window, "password").Password.Length == 0,
                    "Closing cancels a pending login and clears the password");
                client.LoginGate.SetResult(true); Drain(request);
                Require(!Field<bool>(window, "signedIn") && client.Disposed, "Late login completion cannot reopen the selection step");
            }
        }

        private static void VerifyClosedSubmit()
        {
            using (var client = new FakeClient())
            {
                var window = new CrmQuickIntakeWindow(Prepared(), client); SignIn(window);
                Field<ComboBox>(window, "condition").SelectedIndex = 2;
                client.SendGate = new TaskCompletionSource<CrmQuickIntakeResult>();
                Task request = Continue(window); window.Close(); Pump();
                Require(client.LastToken.IsCancellationRequested, "Closing cancels the pending send");
                client.SendGate.SetResult(Complete()); Drain(request);
                Require(!Field<bool>(window, "completed") && client.Disposed, "A late send result cannot update a closed dialog");
            }
        }

        private static void VerifyLayout(Size size, bool signedIn, string path)
        {
            using (var client = new FakeClient())
            {
                var window = new CrmQuickIntakeWindow(Prepared(), client);
                if (signedIn) { SignIn(window); Field<ComboBox>(window, "condition").SelectedIndex = 2; }
                var content = (FrameworkElement)window.Content; window.Content = null;
                var host = new Border { Child = content, Width = size.Width, Height = size.Height, Background = window.Background, UseLayoutRounding = true };
                System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
                System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
                System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
                TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
                host.Measure(size); host.Arrange(new Rect(new Point(), size)); host.UpdateLayout();
                foreach (FrameworkElement element in signedIn
                    ? new FrameworkElement[] { Field<TextBox>(window, "quantity"), Field<ComboBox>(window, "condition"), Field<Button>(window, "primary"), Field<Button>(window, "close") }
                    : new FrameworkElement[] { Field<PasswordBox>(window, "password"), Field<Button>(window, "primary"), Field<Button>(window, "close") })
                {
                    Rect bounds = element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));
                    Require(bounds.Width > 30 && bounds.Height >= 30 && bounds.Top >= 0 && bounds.Bottom <= size.Height && bounds.Left >= 0 && bounds.Right <= size.Width,
                        "Every visible input and action fits without clipping at " + size);
                }
                var scroller = FindVisual<ScrollViewer>(content);
                Require(scroller.ScrollableHeight < 1 && scroller.ScrollableWidth < 1,
                    "The compact form needs no scrolling at " + size + "; overflow=" + scroller.ScrollableHeight + "x" + scroller.ScrollableWidth);
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path)) { encoder.Save(stream); }
                Field<TextBlock>(window, "status").Text = "Produs creat. Stickerele sunt în coada de imprimare.";
                host.UpdateLayout();
                Rect status = Field<TextBlock>(window, "status").TransformToAncestor(host).TransformBounds(new Rect(Field<TextBlock>(window, "status").RenderSize));
                Rect action = Field<Button>(window, "primary").TransformToAncestor(host).TransformBounds(new Rect(Field<Button>(window, "primary").RenderSize));
                Require(status.Bottom <= action.Top && action.Bottom <= size.Height, "Longer result text cannot cover the actions at " + size);
                Require(client.SubmitCalls == 0, "Visual previews never create a real product or print job");
                window.Close(); Pump();
            }
        }

        private static void AssertControls(CrmQuickIntakeWindow window, bool signedIn)
        {
            var controls = new List<Control>(); CollectLogical((DependencyObject)window.Content, controls);
            Require(Count<TextBox>(controls) == 1 && Count<PasswordBox>(controls) == 1 && Count<ComboBox>(controls) == 1 && Count<Button>(controls) == 2 && Count<CheckBox>(controls) == 0,
                "The entire form contains only password, quantity, condition, primary action and cancel");
            Require(Field<StackPanel>(window, "loginPanel").Visibility == (signedIn ? Visibility.Collapsed : Visibility.Visible)
                && Field<StackPanel>(window, "selectionPanel").Visibility == (signedIn ? Visibility.Visible : Visibility.Collapsed),
                "Each step exposes only its required inputs");
            Require(Convert.ToString(Field<Button>(window, "primary").Content) == (signedIn ? "Trimite și imprimă" : "Continuă"),
                "The same primary button advances the two steps");
        }
        private static void CollectLogical(DependencyObject root, List<Control> controls)
        {
            var control = root as Control; if (control != null) { controls.Add(control); }
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            { var dependency = child as DependencyObject; if (dependency != null) { CollectLogical(dependency, controls); } }
        }
        private static int Count<T>(List<Control> controls) where T : Control
        { int count = 0; foreach (Control control in controls) { if (control is T) { count++; } } return count; }
        private static T FindVisual<T>(DependencyObject root) where T : DependencyObject
        { if (root is T) { return (T)root; } for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { T found = FindVisual<T>(VisualTreeHelper.GetChild(root, i)); if (found != null) { return found; } } return null; }

        private sealed class FakeTransport : ICrmQuickTransport
        {
            public Task<CrmHttpResponse> SendAsync(Uri uri, string body, string bearer, string idempotencyKey, CancellationToken token)
            { throw new InvalidOperationException("UI tests must never use a real transport."); }
        }
        private sealed class FakeClient : CrmQuickIntakeClient
        {
            internal int LoginCalls, SubmitCalls;
            internal bool Disposed, Partial, Authenticated;
            internal Exception LoginError, SendError;
            internal TaskCompletionSource<bool> LoginGate;
            internal TaskCompletionSource<CrmQuickIntakeResult> SendGate;
            internal CrmQuickSubmission LastSubmission;
            internal CancellationToken LastToken;
            internal FakeClient() : base(new FakeTransport(), Path.Combine(Path.GetTempPath(), "ADAMO-GT-CrmQuick-UI", Guid.NewGuid().ToString("N"))) { }
            public override bool IsAuthenticated { get { return Authenticated; } }
            internal void Restore(CrmQuickSubmission pending) { Pending = pending; }
            public override async Task LoginAsync(string password, CancellationToken token)
            {
                LoginCalls++; LastToken = token;
                if (LoginError != null) { throw LoginError; }
                if (LoginGate != null) { await LoginGate.Task; }
                Authenticated = true;
            }
            public override async Task<CrmQuickIntakeResult> SubmitAsync(CrmQuickSubmission submission, CancellationToken token)
            {
                SubmitCalls++; LastToken = token; LastSubmission = submission; Pending = submission;
                if (SendError != null) { Authenticated = false; throw SendError; }
                CrmQuickIntakeResult result = SendGate != null ? await SendGate.Task : (Partial ? new CrmQuickIntakeResult { Completed = false } : Complete());
                if (result.Completed) { Pending = null; }
                return result;
            }
            public override void Dispose() { Disposed = true; base.Dispose(); }
        }
        private static CrmQuickSubmission Prepared()
        { return new CrmQuickSubmission { ClientIntakeId = Guid.NewGuid().ToString(), AppVersion = "2.7.0", CapturedAt = "2026-10-20T00:00:00Z", ScreenshotBase64 = "frozen-test-capture", DetectedSpecs = new Dictionary<string, object>() }; }
        private static CrmQuickIntakeResult Complete()
        { return new CrmQuickIntakeResult { ProductId = "42", IntakeId = "17", ImageStatus = "attached", PrintStatus = "queued", Completed = true }; }
        private static void SignIn(CrmQuickIntakeWindow window)
        { Field<PasswordBox>(window, "password").Password = "test-only"; Drain(Continue(window)); }
        private static Task Continue(CrmQuickIntakeWindow window)
        { return (Task)window.GetType().GetMethod("ContinueAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null); }
        private static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
        private static void Drain(Task task)
        { DateTime deadline = DateTime.UtcNow.AddSeconds(5); while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); } Require(task.IsCompleted, "Fake operation completes promptly"); task.GetAwaiter().GetResult(); }
        private static void Pump()
        { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    }
}
