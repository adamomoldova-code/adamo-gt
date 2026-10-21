using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcPulse;

// Links to the built ADAMO-GT.exe. No product sources are compiled here.
// The default production stress runner is installed but Start is never invoked.
internal static class StressBinaryUiE2E
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly List<string> passes = new List<string>(), failures = new List<string>();
    private static int exitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        string snapshotPath = args.Length > 1 ? args[1] : "";
        Snapshot snapshot = String.IsNullOrEmpty(snapshotPath) ? Fixture() : new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Snapshot>(File.ReadAllText(snapshotPath));
        if (snapshot == null) { throw new InvalidOperationException("Inventory fixture could not be loaded."); }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        { failures.Add("Dispatcher: " + e.Exception); e.Handled = true; };
        app.Dispatcher.BeginInvoke(new Action(async delegate
        {
            Window dialog = null;
            try
            {
                Type type = typeof(Snapshot).Assembly.GetType("PcPulse.StressTestsWindow", true);
                int completed = 0;
                dialog = (Window)Activator.CreateInstance(type, Private, null, new object[]
                {
                    snapshot, new Func<Snapshot>(() => null), null,
                    new Action<StressRunResult>(result => completed++), null
                }, null);
                dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = SystemParameters.WorkArea.Left + 32; dialog.Top = SystemParameters.WorkArea.Top + 32;
                dialog.Show(); await Task.Delay(100); dialog.UpdateLayout();
                Require(dialog.IsVisible, "Actual product dialog opens as a real WPF window.");
                Require(!dialog.IsActive, "Fixture window does not take foreground activation.");
                Require(Field<Delegate>(dialog, "run") != null, "Default production runner is configured.");
                AssertIdle(dialog, "Opening");
                foreach (string field in new[] { "cpu", "ram", "ssd", "gpu" })
                { Require(Field<CheckBox>(dialog, field).IsChecked != true, "No implicit selection: " + field + "."); }
                Button start = Field<Button>(dialog, "start"), stop = Field<Button>(dialog, "stop");
                Require(!start.IsEnabled && !stop.IsEnabled, "Empty selection keeps Start and Stop disabled.");
                ComboBox ramSize = Field<ComboBox>(dialog, "ramSizeSelector");
                Require(!ramSize.IsEnabled && (int)((ComboBoxItem)ramSize.SelectedItem).Tag == 0,
                    "RAM defaults to Auto with size selection inactive until RAM is checked.");
                Field<CheckBox>(dialog, "cpu").IsChecked = true;
                Field<CheckBox>(dialog, "ram").IsChecked = true;
                Require(ramSize.IsEnabled, "Checking RAM enables its volume selector.");
                ramSize.SelectedItem = ramSize.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == 8192);
                var selectedOptions = (StressOptions)type.GetMethod("Options", Private).Invoke(dialog, null);
                Require(selectedOptions.RamMiB == 8192, "The built dialog passes an 8 GB request without the former 2 GB limit.");
                Type workloads = typeof(Snapshot).Assembly.GetType("PcPulse.StressWorkloads", true);
                MethodInfo budget = workloads.GetMethod("CalculateRamMiB", BindingFlags.Static | BindingFlags.NonPublic);
                Require((int)budget.Invoke(null, new object[] { 32UL * 1024 * 1024 * 1024, 0 }) == 26214,
                    "The built backend budgets more than 2 GB from 32 GB available while reserving 20 percent.");
                Require((int)budget.Invoke(null, new object[] { 16UL * 1024 * 1024 * 1024, 8192 }) == 8192,
                    "The built backend accepts an 8 GB request within the available safe budget.");
                Require((int)budget.Invoke(null, new object[] { 4UL * 1024 * 1024 * 1024, 8192 }) == 3072,
                    "The built backend reduces an oversized request to preserve Windows memory.");
                if (Field<CheckBox>(dialog, "ssd").IsEnabled) { Field<CheckBox>(dialog, "ssd").IsChecked = true; }
                if (Field<CheckBox>(dialog, "gpu").IsEnabled) { Field<CheckBox>(dialog, "gpu").IsChecked = true; }
                Field<ComboBox>(dialog, "duration").SelectedItem = 5;
                Field<CheckBox>(dialog, "sequential").IsChecked = true;
                Require(start.IsEnabled && !stop.IsEnabled, "Checked components enable only the manual Start action.");
                Require(Convert.ToInt32(Field<ComboBox>(dialog, "duration").SelectedItem) == 5, "Duration selection works in the built assembly.");
                AssertIdle(dialog, "Changing choices and duration");

                foreach (Size size in new[] { new Size(790, 720), new Size(640, 440), new Size(480, 360) })
                {
                    dialog.MinWidth = 0; dialog.MinHeight = 0;
                    dialog.Width = size.Width; dialog.Height = size.Height;
                    await Task.Delay(35); dialog.UpdateLayout();
                    ScrollViewer scroll = Field<ScrollViewer>(dialog, "outerScroll");
                    scroll.ScrollToTop(); await Task.Delay(20); dialog.UpdateLayout();
                    Require(scroll.ViewportHeight > 0, "Body remains scrollable at " + size + ".");
                    AssertAction(dialog, start, "Start", size, "top");
                    AssertAction(dialog, stop, "Stop", size, "top");
                    if (size.Width >= 790)
                    {
                        foreach (string field in new[] { "cpu", "ram", "ssd", "gpu" })
                        {
                            var choice = Field<CheckBox>(dialog, field);
                            Rect bounds = choice.TransformToAncestor(dialog).TransformBounds(new Rect(choice.RenderSize));
                            Require(bounds.Top >= 0 && bounds.Bottom <= dialog.ActualHeight, "Screenshot includes the " + field + " checkbox.");
                        }
                    }
                    SaveImage(dialog, Path.Combine(output, "built-stress-" + size.Width + "x" + size.Height + "-top.png"));
                    scroll.ScrollToBottom(); await Task.Delay(20); dialog.UpdateLayout();
                    AssertAction(dialog, start, "Start", size, "bottom");
                    AssertAction(dialog, stop, "Stop", size, "bottom");
                    AssertIdle(dialog, "Resizing and scrolling " + size);
                }
                bool closed = false; dialog.Closed += delegate { closed = true; };
                dialog.Close(); await Task.Delay(20);
                Require(closed && completed == 0, "Idle close completes and no stress result is fabricated.");
                dialog = null;
            }
            catch (Exception error) { failures.Add(error.ToString()); }
            finally { if (dialog != null) { dialog.Close(); } }
            string productPath = typeof(Snapshot).Assembly.Location;
            File.WriteAllText(Path.Combine(output, "stress-binary-ui-results.json"), new JavaScriptSerializer().Serialize(new
            {
                ProductPath = productPath, ProductVersion = typeof(Snapshot).Assembly.GetName().Version.ToString(),
                ProductSha256 = Hash(productPath), TestedAtUtc = DateTime.UtcNow.ToString("o"), Passed = passes, Failures = failures,
                Inventory = String.IsNullOrEmpty(snapshotPath) ? "Clearly named fixture devices; no physical inventory was queried." : "Previously saved inventory: " + Path.GetFullPath(snapshotPath),
                Coverage = "Actual built product assembly, real WPF dialog and default production runner. Only settings, resize, scrolling and idle close are exercised. Start is never invoked; no physical load, network request, driver installation or foreground activation."
            }));
            foreach (string pass in passes) { Console.WriteLine("PASS " + pass); }
            foreach (string failure in failures) { Console.WriteLine("FAIL " + failure); }
            Console.WriteLine("RESULT " + passes.Count + " passed; " + failures.Count + " failed.");
            exitCode = failures.Count == 0 ? 0 : 1; app.Shutdown();
        }));
        app.Run(); return exitCode;
    }

    private static void AssertIdle(Window dialog, string context)
    {
        Require(!Field<bool>(dialog, "running") && Field<Task>(dialog, "runTask") == null
            && Field<System.Threading.CancellationTokenSource>(dialog, "cancellation") == null,
            context + " does not start a workload or allocate a run cancellation token.");
    }
    private static void AssertAction(Window dialog, Button action, string label, Size size, string position)
    {
        Rect bounds = action.TransformToAncestor(dialog).TransformBounds(new Rect(action.RenderSize));
        Require(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= dialog.ActualWidth && bounds.Bottom <= dialog.ActualHeight,
            label + " is visible at " + size + " with body scrolled to " + position + ".");
        Point center = action.TranslatePoint(new Point(action.ActualWidth / 2, action.ActualHeight / 2), dialog);
        HitTestResult hit = VisualTreeHelper.HitTest(dialog, center);
        Require(hit != null && IsWithin(hit.VisualHit, action), label + " retains its own hit target at " + size + "/" + position + ".");
    }
    private static Snapshot Fixture()
    {
        var result = new Snapshot { ComputerModel = "UI test fixture", CpuName = "Fixture CPU" };
        result.Disks.Add(new DiskSnapshot { Index = 0, Name = "Тестовый SSD · без обращения к диску", MediaType = "SSD", Volumes = "X:", TestVolumeRoot = @"X:\", StorageIdentity = "fixture-only" });
        result.Gpus.Add(new GpuSnapshot { Name = "Тестовый GPU · без запуска нагрузки", AdapterIndex = 0, AdapterLuidLow = 42 });
        return result;
    }
    private static void SaveImage(Window window, string path)
    {
        // Capture the actual WPF client content; non-client title-bar dimensions
        // are not rendered by RenderTargetBitmap and would create empty margins.
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) { png.Save(stream); }
    }
    private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Private).GetValue(value); }
    private static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    { while (child != null) { if (ReferenceEquals(child, ancestor)) { return true; } child = VisualTreeHelper.GetParent(child); } return false; }
    private static string Hash(string path)
    { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); } }
    private static void Require(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } passes.Add(message); }
}
