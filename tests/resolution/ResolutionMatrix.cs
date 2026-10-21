using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcPulse;
using PcPulseTests;

public static class ResolutionMatrix
{
    private const double TaskbarDip = 48, FrameWidthDip = 16, TitleFrameHeightDip = 39;
    private static readonly BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo VerifyLayout = typeof(DashboardLayoutTests).GetMethod("VerifyLayout", HiddenStatic);
    private static readonly MethodInfo Fixture = typeof(DashboardLayoutTests).GetMethod("Fixture", HiddenStatic);
    private static readonly MethodInfo Ports = typeof(DashboardLayoutTests).GetMethod("Ports", HiddenStatic);
    private static readonly MethodInfo SetDpi = typeof(VisualTreeHelper).GetMethod("SetRootDpi", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly string[] Cards = { "CpuCard", "RamCard", "GpuCard", "ScreenCard", "PortsCard", "DiskCard", "BatteryCard" };
    private static readonly string[] MeasuredText = { "ComputerModel", "RamInstalled", "RamMaximum", "CpuCache", "DiskHealth", "PowerOnHours", "DiskTemperature", "DiskRead", "DiskWrite", "BatteryHealth", "BatteryFullCapacity", "BatteryDesignCapacity", "BatteryCycles" };
    private static readonly string[] Commands = { "SaveReportButton", "RefreshButton", "PeripheralTestsButton", "SensorsButton", "StopTestButton", "RunTestButton", "RunGpuTestButton", "AppLogo", "BrandFooter" };

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    [STAThread]
    public static int Main(string[] args)
    {
        string output = args.Length == 0 ? Path.Combine(Environment.CurrentDirectory, "tests/artifacts/resolution-2.4.4") : Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var rows = new List<Result>();
        uint systemDpi = 0;
        try { systemDpi = GetDpiForSystem(); } catch (EntryPointNotFoundException) { }
        var actualProbe = new Border();
        DpiScale initialDpi = VisualTreeHelper.GetDpi(actualProbe);
        Console.WriteLine("Actual runtime: " + typeof(VisualTreeHelper).Assembly.Location);
        Console.WriteLine("Actual OS system DPI: " + systemDpi + "; initial WPF DPI: " + initialDpi.DpiScaleX + "x; work area DIP: " + SystemParameters.WorkArea);
        Console.WriteLine("SetRootDpi: " + (SetDpi == null ? "absent" : (SetDpi.IsPublic ? "public" : "nonpublic")) + ". Matrix uses simulated work areas and offscreen DPI; it does not change Windows display settings.");
        int[][] resolutions = {
            new[] {1024,768}, new[] {1280,720}, new[] {1280,800}, new[] {1366,768}, new[] {1440,900},
            new[] {1600,900}, new[] {1920,1080}, new[] {1920,1200}, new[] {2560,1440}, new[] {3840,2160},
            new[] {2560,1080}, new[] {3440,1440}, new[] {1080,1920}, new[] {1440,2560}
        };
        foreach (int[] resolution in resolutions)
        {
            foreach (double dpi in new[] {1.0, 1.25, 1.5, 2.0})
            {
                double workWidth = resolution[0] / dpi;
                double workHeight = resolution[1] / dpi - TaskbarDip;
                foreach (bool maximized in new[] { false, true })
                {
                    Window sizing = Program.LoadWindow();
                    if (maximized) { sizing.Width = workWidth; sizing.Height = workHeight; }
                    // Negative origin additionally checks clamping onto a monitor left of the primary.
                    Rect workArea = new Rect(-workWidth, 32, workWidth, workHeight);
                    sizing.Left = 9000; sizing.Top = -9000;
                    DashboardViewport.FitToWorkArea(sizing, workArea);
                    bool windowFits = workArea.Contains(new Rect(sizing.Left, sizing.Top, sizing.Width, sizing.Height));
                    double contentWidth = Math.Max(1, sizing.Width - FrameWidthDip);
                    double contentHeight = Math.Max(1, sizing.Height - TitleFrameHeightDip);
                    sizing.Close();
                    foreach (bool unavailable in new[] { false, true })
                    {
                        var result = new Result
                        {
                            Resolution = resolution[0] + "x" + resolution[1], DpiPercent = (int)(dpi * 100),
                            Mode = maximized ? "maximized-estimate" : "startup", Fixture = unavailable ? "unavailable" : "populated",
                            WorkWidthDip = workWidth, WorkHeightDip = workHeight, ContentWidthDip = contentWidth, ContentHeightDip = contentHeight,
                            PhysicalContentWidthPx = contentWidth * dpi, PhysicalContentHeightPx = contentHeight * dpi,
                            WindowFits = windowFits
                        };
                        try
                        {
                            Require(windowFits, "Window does not fit the simulated work area");
                            // This is the existing fixture and all of its original strict assertions, unchanged.
                            VerifyLayout.Invoke(null, new object[] {contentWidth, contentHeight, unavailable, (double?)dpi});
                            result.ExistingLayoutPassed = true;
                            using (var host = CreateHost(unavailable))
                            {
                                SetHostDpi(host.Host, dpi);
                                Measure(host.Host, contentWidth, contentHeight);
                                CheckGeometry(host, result);
                            }
                            result.GeometryPassed = true;
                        }
                        catch (Exception error)
                        {
                            result.Error = Unwrap(error).ToString();
                            if (rows.Count(r => !r.GeometryPassed) < 3)
                                Console.WriteLine("FIRST FAILURE " + result.Resolution + "@" + result.DpiPercent + "% " + result.Mode + " " + result.Fixture + ": " + result.Error);
                        }
                        result.Readability = !result.GeometryPassed ? "geometry-failed" : result.MinDataFontDip >= 12 ? "readable-threshold" : "fits-but-small";
                        rows.Add(result);
                        foreach (Window window in app.Windows.Cast<Window>().ToArray()) { window.Close(); }
                    }
                }
            }
            Console.WriteLine(resolution[0] + "x" + resolution[1] + ": " + rows.Count + " cases checked; " + rows.Count(r => !r.GeometryPassed) + " geometry failures so far.");
        }

        var resizeErrors = new List<string>();
        int resizeCount = 0;
        using (var host = CreateHost(false))
        {
            double[][] sizes = { new[] {1200d,720d,1d}, new[] {832d,500d,1.25d}, new[] {640d,400d,2d}, new[] {1440d,850d,1.5d}, new[] {360d,560d,2d}, new[] {2400d,1300d,1d}, new[] {960d,680d,1.25d}, new[] {1200d,720d,1d} };
            for (int cycle = 0; cycle < 3; cycle++)
            {
                foreach (double[] size in sizes)
                {
                    resizeCount++;
                    try
                    {
                        SetHostDpi(host.Host, size[2]);
                        Measure(host.Host, size[0], size[1]);
                        CheckGeometry(host, new Result { ContentWidthDip = size[0], ContentHeightDip = size[1] });
                    }
                    catch (Exception error) { resizeErrors.Add("Resize " + resizeCount + ": " + Unwrap(error)); }
                }
            }
        }
        try { DashboardViewportTests.Run(); } catch (Exception error) { resizeErrors.Add("Existing viewport regressions: " + error); }
        var uiErrors = new List<string>();
        var uiGroups = new Dictionary<string, Action>
        {
            { "Legacy dashboard layout and selection", DashboardLayoutTests.Run },
            { "Temperature presentation", TemperaturePresentationTests.Run },
            { "History presentation", HistoryPresentationTests.Run }
        };
        foreach (KeyValuePair<string, Action> group in uiGroups)
        {
            try { group.Value(); Console.WriteLine("PASS: " + group.Key); }
            catch (Exception error) { uiErrors.Add(group.Key + ": " + error); Console.WriteLine("FAIL: " + uiErrors.Last()); }
        }
        bool dialogPassed = false;
        string dialogError = null;
        try { DialogViewportTests.Run(); dialogPassed = true; Console.WriteLine("PASS: Dialog fitting, scrolling and invalid work areas"); }
        catch (Exception error) { dialogError = error.ToString(); Console.WriteLine("FAIL: Dialog viewport " + dialogError); }
        var report = new
        {
            Version = typeof(Program).Assembly.GetName().Version.ToString(), RunAtUtc = DateTime.UtcNow.ToString("o"),
            ActualSystemDpi = systemDpi, InitialWpfDpiScale = initialDpi.DpiScaleX, ActualWorkAreaDip = SystemParameters.WorkArea.ToString(),
            Runtime = typeof(VisualTreeHelper).Assembly.Location,
            SetRootDpiAvailable = SetDpi != null, SetRootDpiPublic = SetDpi != null && SetDpi.IsPublic,
            InheritedWindowPresentation = "UseLayoutRounding, SnapsToDevicePixels, FlowDirection, font family/size/style/weight/stretch, TextFormattingMode, TextRenderingMode and TextHintingMode copied to every host. FormattedText uses descendant DPI/formatting mode; every visible text drawing is checked against all ancestor clips, excluding whitespace-only font leading.",
            Assumptions = new { TaskbarDip, FrameWidthDip, TitleFrameHeightDip, ReadableMinimumDataDip = 12 },
            Limitations = "Offscreen WPF layout with simulated work areas and explicit root DPI. SetRootDpi alone leaves already-measured offscreen text cached, so the harness explicitly invalidates the visual tree after setting DPI; native HWND transition handling requires a separate test. No physical monitor/DPI switching, screenshot-based reading assessment, hardware probes or benchmarks. Window chrome allowances are estimates; maximized mode conservatively subtracts the same frame. Readability threshold is a diagnostic, not a universal visual-acuity claim. Fit and readable size are reported separately.",
            Cases = rows, GeometryPasses = rows.Count(r => r.GeometryPassed), GeometryFailures = rows.Count(r => !r.GeometryPassed),
            ReadableCases = rows.Count(r => r.Readability == "readable-threshold"), SmallCases = rows.Count(r => r.Readability == "fits-but-small"),
            RepeatedResizeSteps = resizeCount, RepeatedResizeErrors = resizeErrors,
            LegacyUiGroupsPassed = uiGroups.Count - uiErrors.Count, LegacyUiErrors = uiErrors,
            DialogViewportRegressionPassed = dialogPassed, DialogViewportError = dialogError,
            HardwareProbesRun = false, BenchmarksRun = false
        };
        File.WriteAllText(Path.Combine(output, "matrix.json"), new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(report), new UTF8Encoding(false));
        using (var csv = new StreamWriter(Path.Combine(output, "matrix.csv"), false, new UTF8Encoding(true)))
        {
            csv.WriteLine("Resolution,DpiPercent,Mode,Fixture,ContentWidthDip,ContentHeightDip,Downscale,RootDpiScale,InheritedTextDpiScale,Body14Dip,Body14PhysicalPx,MinDataFontDip,FooterDip,Geometry,Readability,Error");
            foreach (Result r in rows)
                csv.WriteLine(String.Join(",", new[] {r.Resolution, r.DpiPercent.ToString(), r.Mode, r.Fixture, N(r.ContentWidthDip), N(r.ContentHeightDip), N(r.Downscale), N(r.RootDpiScale), N(r.InheritedTextDpiScale), N(r.Body14Dip), N(r.Body14PhysicalPx), N(r.MinDataFontDip), N(r.FooterDip), r.GeometryPassed ? "PASS" : "FAIL", r.Readability, "\"" + (r.Error ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"" }));
        }
        Console.WriteLine("RESULT: " + report.GeometryPasses + "/" + rows.Count + " geometry passes, " + report.ReadableCases + " readable-threshold, " + report.SmallCases + " fits-but-small; " + resizeCount + " repeated resize steps, " + resizeErrors.Count + " resize errors.");
        Console.WriteLine("Reports: " + output);
        return report.GeometryFailures == 0 && resizeErrors.Count == 0 && uiErrors.Count == 0 && dialogPassed ? 0 : 1;
    }

    private static HostFixture CreateHost(bool unavailable)
    {
        Window window = Program.LoadWindow();
        var dashboard = new Dashboard(window);
        Snapshot snapshot = (Snapshot)Fixture.Invoke(null, null);
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
        dashboard.RenderPorts((PortSpecification)Ports.Invoke(null, null));
        if (!unavailable)
        {
            dashboard.RenderBenchmark(new BenchmarkProgress { DiskIndex = 0, ReadBytesPerSecond = 5174400000, WriteBytesPerSecond = 4170900000, Percent = 100, Stage = "Завершено" });
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress { AdapterIndex = 0, Completed = true, FramesPerSecond = 123.4, DurationSeconds = 30, Stage = "Завершено" });
        }
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Child = content, Resources = window.Resources };
        DashboardLayoutTests.ApplyWindowPresentation(host, window);
        return new HostFixture { Window = window, Host = host };
    }

    private static void SetHostDpi(Border host, double dpi)
    {
        Require(SetDpi != null, "SetRootDpi is unavailable on this runtime");
        SetDpi.Invoke(null, new object[] {host, new DpiScale(dpi, dpi)});
        Require(Math.Abs(VisualTreeHelper.GetDpi(host).DpiScaleX - dpi) < 0.001, "Root DPI was not applied");
        // An offscreen visual has no HwndSource DPI-change event. SetRootDpi updates
        // GetDpi but does not invalidate cached TextBlock measurements/drawings.
        // Recompute this synthetic layout explicitly; native transitions are tested separately.
        InvalidateTree(host);
    }

    private static void InvalidateTree(DependencyObject element)
    {
        var ui = element as UIElement;
        if (ui != null) { ui.InvalidateMeasure(); ui.InvalidateArrange(); ui.InvalidateVisual(); }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            InvalidateTree(VisualTreeHelper.GetChild(element, index));
    }

    private static void Measure(Border host, double width, double height)
    {
        host.Width = width; host.Height = height;
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
    }

    private static void CheckGeometry(HostFixture fixture, Result result)
    {
        FrameworkElement root = (FrameworkElement)fixture.Window.FindName("DashboardRoot");
        Rect bounds = Bounds(root, fixture.Host);
        result.Downscale = bounds.Width / root.ActualWidth;
        double scaleY = bounds.Height / root.ActualHeight;
        Require(Math.Abs(result.Downscale - scaleY) < 0.001 && result.Downscale > 0 && result.Downscale <= 1.001, "Nonuniform or invalid dashboard scale");
        foreach (string name in Cards.Concat(Commands))
        {
            FrameworkElement element = (FrameworkElement)fixture.Window.FindName(name);
            Require(element != null && element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0, name + " is not visible");
            Rect elementBounds = Bounds(element, fixture.Host);
            Require(elementBounds.Left >= -1 && elementBounds.Top >= -1 && elementBounds.Right <= fixture.Host.ActualWidth + 1 && elementBounds.Bottom <= fixture.Host.ActualHeight + 1, name + " is outside the content viewport");
        }
        foreach (string name in MeasuredText)
        {
            var text = (TextBlock)fixture.Window.FindName(name);
            var measured = DashboardLayoutTests.MeasureText(text);
            Require(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1.5, name + " is truncated at explicit root DPI");
            Require(measured.Height <= text.ActualHeight + 1.5, name + " is clipped vertically at explicit root DPI");
            Require(text.FontSize >= 12, name + " loses the original readability threshold");
        }
        var ports = (ScrollViewer)fixture.Window.FindName("PortsScroll");
        Require(ports.ScrollableHeight < 1, "Reference ports require scrolling at explicit DPI");
        DashboardLayoutTests.VerifyTextGeometry(fixture.Host, fixture.Host.ActualWidth);
        DashboardLayoutTests.VerifyFooterActions(fixture.Window, fixture.Host);
        var cpu = (TextBlock)fixture.Window.FindName("CpuTemperature");
        result.RootDpiScale = VisualTreeHelper.GetDpi(fixture.Host).DpiScaleX;
        result.InheritedTextDpiScale = VisualTreeHelper.GetDpi(cpu).DpiScaleX;
        Require(Math.Abs(result.RootDpiScale - result.InheritedTextDpiScale) < 0.001, "Root DPI did not propagate to WPF text layout");
        result.Body14Dip = 14 * result.Downscale;
        result.Body14PhysicalPx = result.Body14Dip * result.RootDpiScale;
        var footer = (TextBlock)fixture.Window.FindName("BrandFooter");
        result.FooterDip = footer.FontSize * result.Downscale;
        List<TextBlock> visibleText = Texts(root).Where(t => t.Visibility == Visibility.Visible && t.ActualWidth > 0 && t.ActualHeight > 0 && !String.IsNullOrWhiteSpace(t.Text) && !ReferenceEquals(t, footer)).ToList();
        result.MinDataFontDip = visibleText.Min(t => t.FontSize) * result.Downscale;
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject element)
    {
        var text = element as TextBlock;
        if (text != null) { yield return text; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (TextBlock child in Texts(VisualTreeHelper.GetChild(element, i))) { yield return child; }
    }
    private static Rect Bounds(FrameworkElement element, FrameworkElement host) { return element.TransformToAncestor(host).TransformBounds(new Rect(new Point(), element.RenderSize)); }
    private static Exception Unwrap(Exception error) { while (error is TargetInvocationException && error.InnerException != null) { error = error.InnerException; } return error; }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static string N(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }

    private sealed class HostFixture : IDisposable
    {
        public Window Window;
        public Border Host;
        public void Dispose() { Window.Close(); }
    }
    public sealed class Result
    {
        public string Resolution, Mode, Fixture, Readability, Error;
        public int DpiPercent;
        public double WorkWidthDip, WorkHeightDip, ContentWidthDip, ContentHeightDip, PhysicalContentWidthPx, PhysicalContentHeightPx;
        public double Downscale, RootDpiScale, InheritedTextDpiScale, Body14Dip, Body14PhysicalPx, MinDataFontDip, FooterDip;
        public bool WindowFits, ExistingLayoutPassed, GeometryPassed;
    }
}
