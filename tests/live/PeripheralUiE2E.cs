using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PcPulse;

// Integration with the real shipped WPF assembly. Device enumeration disabled;
// no camera/audio APIs invoked. Keyboard messages target this process's HWND.
internal static class PeripheralUiE2E
{
    private static Window window;
    private static readonly List<string> passes = new List<string>(), failures = new List<string>();
    private static readonly List<PeripheralTestResult> saved = new List<PeripheralTestResult>();
    private static int exitCode;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint map);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr state);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr state);

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { failures.Add("Unhandled UI: " + e.Exception); e.Handled = true; };
        window = Create(null); window.Loaded += async delegate
        {
            try { await Run(); }
            catch (Exception e) { failures.Add(e.ToString()); }
            try { if (window != null) { await Close(window); } } catch (Exception e) { failures.Add("Final close: " + e); }
            File.WriteAllText(Path.Combine(output, "ui-results.json"), new JavaScriptSerializer().Serialize(new
            {
                Version = typeof(CameraPreview).Assembly.GetName().Version.ToString(),
                ProductSha256 = Hash(typeof(CameraPreview).Assembly.Location), TestedAtUtc = DateTime.UtcNow.ToString("o"),
                Passed = passes, Failures = failures,
                Coverage = "Real WPF window, UI Automation selection/invocation, native targeted HWND keyboard messages, tab hit testing, owned-window focus, modal consent. No hardware activation."
            }));
            foreach (string failure in failures) { Console.WriteLine("FAIL " + failure); }
            Console.WriteLine("RESULT " + passes.Count + " passed; " + failures.Count + " failed.");
            exitCode = failures.Count == 0 ? 0 : 1; app.Shutdown();
        };
        app.Run(window); return exitCode;
    }

    private static Window Create(IEnumerable<PeripheralTestResult> previous)
    {
        Type type = typeof(CameraPreview).Assembly.GetType("PcPulse.PeripheralTestsWindow", true);
        var result = (Window)Activator.CreateInstance(type, Private, null,
            new object[] { previous, new Func<PeripheralTestResult, bool>(r => { saved.Add(r); return true; }), false, "" }, null);
        result.ShowInTaskbar = false;
        result.WindowStartupLocation = WindowStartupLocation.Manual;
        result.Left = 60; result.Top = 60;
        return result;
    }

    private static async Task Run()
    {
        await Task.Delay(200); window.Activate(); await Task.Delay(200);
        Require(window.IsActive, "Owned test dialog activated");
        Require(!Field<CameraPreview>(window, "camera").IsRunning && Field<PeripheralAudio>(window, "audio").Completion.IsCompleted, "Opening dialog does not start devices");
        var tabs = Field<TabControl>(window, "tabs");
        var keyboard = Field<KeyboardTestPanel>(window, "keyboard");
        string[][] actions = { new[] { "cameraStart" }, new[] { "record", "playback" }, new[] { "keyboardStart", "keyboardStop", "keyboardReset" }, new[] { "left", "right" } };
        for (int index = 0; index < 4; index++)
        {
            await Select(tabs, index);
            Require(tabs.SelectedIndex == index, "UIA selects tab " + index);
            for (int hidden = 0; hidden < 4; hidden++)
            {
                if (hidden == index) { continue; }
                foreach (string field in actions[hidden])
                {
                    var button = Field<Button>(window, field);
                    Require(!button.IsVisible && !button.IsEnabled, "Hidden action cannot receive input: tab " + index + "/" + field);
                }
            }
            var content = (FrameworkElement)((TabItem)tabs.Items[index]).Content;
            foreach (Button button in Descendants(content).OfType<Button>().Where(b => b.IsVisible))
            {
                button.BringIntoView(); await Task.Delay(20); window.UpdateLayout();
                Point center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
                DependencyObject hit = button.IsEnabled ? window.InputHitTest(center) as DependencyObject : VisualTreeHelper.HitTest(window, center).VisualHit;
                Require(IsWithin(hit, button), "Actual hit target matches selected tab button: " + index + "/" + button.Content);
            }
        }
        await Select(tabs, 2);
        Require(keyboard.IsRunning && Field<Border>(keyboard, "captureSurface").IsKeyboardFocusWithin, "Keyboard starts immediately and owns focus on selection");
        int foreignClicks = 0;
        foreach (string action in actions.SelectMany(a => a).Where(a => a != "keyboardStop" && a != "keyboardReset" && a != "keyboardStart"))
        { Field<Button>(window, action).Click += delegate { foreignClicks++; }; }
        int generation = Field<int>(window, "generation");
        foreach (Key key in new[] { Key.A, Key.Tab, Key.Space, Key.Return }) { await KeyMessage(window, key, true); await KeyMessage(window, key, false); }
        Require(keyboard.HasSeen("A") && keyboard.HasSeen("Tab") && keyboard.HasSeen("Space") && keyboard.HasSeen("Enter"), "Targeted native keys reach actual WPF capture");
        Require(keyboard.IsRunning && keyboard.PressedCount == 0 && Field<Border>(keyboard, "captureSurface").IsKeyboardFocusWithin, "Tab Space Enter stay inside keyboard capture");
        Require(foreignClicks == 0 && generation == Field<int>(window, "generation"), "Navigation keys do not trigger another tab or device action");
        await KeyMessage(window, Key.B, true);
        var other = new Window { Width = 280, Height = 120, Left = 80, Top = 80, ShowInTaskbar = false, Title = "Owned E2E focus fixture", Content = new TextBox() };
        other.Show(); other.Activate(); await Task.Delay(150);
        Require(!window.IsActive && keyboard.PressedCount == 0, "Deactivation clears pressed keys");
        int before = keyboard.SeenCount;
        await KeyMessage(window, Key.C, true); await KeyMessage(window, Key.C, false);
        Require(keyboard.SeenCount == before, "Inactive dialog ignores targeted key messages");
        other.Close(); window.Activate(); await Task.Delay(150);
        Require(keyboard.IsRunning && Field<Border>(keyboard, "captureSurface").IsKeyboardFocusWithin, "Return to dialog restores capture");
        await KeyMessage(window, Key.D, true); await KeyMessage(window, Key.D, false);
        Require(keyboard.HasSeen("D"), "Keyboard continues after returning");
        await KeyMessage(window, Key.Escape, true); await KeyMessage(window, Key.Escape, false);
        Require(!keyboard.IsRunning && Field<Button>(window, "keyboardStart").IsEnabled, "Escape stops and enables Resume (active=" + window.IsActive + ", modifiers=" + Keyboard.Modifiers + ", focus=" + (Keyboard.FocusedElement == null ? "null" : Keyboard.FocusedElement.GetType().Name) + ", running=" + keyboard.IsRunning + ")");
        InvokeButton(Field<Button>(window, "keyboardStart")); await Task.Delay(100);
        Require(keyboard.IsRunning, "UIA Resume continues capture");
        await Select(tabs, 3);
        Require(!keyboard.IsRunning && keyboard.PressedCount == 0, "Switching to speakers releases keyboard capture");
        await Select(tabs, 2);
        Require(keyboard.IsRunning && keyboard.HasSeen("D"), "Returning to keyboard retains checked keys and resumes");
        await KeyMessage(window, Key.Escape, true); await KeyMessage(window, Key.Escape, false);
        InvokeButton(Field<Button>(window, "passed")); await Task.Delay(100);
        Require(saved.Count == 1 && saved[0].Kind == "keyboard" && saved[0].Outcome == "passed", "Explicit verdict saves only selected keyboard result");
        var buffer = new AudioRecording(new byte[160], 8000, 1);
        Set(window, "recording", buffer);
        await Close(window);
        Require(buffer.ByteCount == 0 && !keyboard.IsRunning && Field<object>(keyboard, "owner") == null && Field<object>(keyboard, "source") == null, "Close clears recording and detaches keyboard HWND handlers");
        window = Create(saved); window.Show(); window.Activate(); await Task.Delay(100);
        tabs = Field<TabControl>(window, "tabs"); await Select(tabs, 2);
        Require(Field<TextBlock>(window, "saved").Text.Contains("работает") && Field<TextBlock>(window, "saved").Text.Contains("Не подтверждает"), "Reopen restores result as historical evidence");
        Require(!Field<Button>(window, "passed").IsEnabled && Field<KeyboardTestPanel>(window, "keyboard").SeenCount == 0, "Historical verdict cannot bypass a new exercise");
        await Select(tabs, 0);
        await VerifyConsent();
        Require(!Field<CameraPreview>(window, "camera").IsRunning && Field<PeripheralAudio>(window, "audio").Completion.IsCompleted, "Full UI run did not activate camera or audio");
    }

    private static async Task VerifyConsent()
    {
        Type program = typeof(CameraPreview).Assembly.GetType("PcPulse.Program", true);
        Action<string, object> set = (name, value) => program.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
        MethodInfo confirm = program.GetMethod("ConfirmOnlinePortLookup", BindingFlags.Static | BindingFlags.NonPublic);
        set("latest", null); set("stopping", false); set("initialInformationShown", true);
        Require(!(bool)confirm.Invoke(null, new object[] { window }), "Online consent rejects absent inventory without opening a dialog");
        set("latest", new Snapshot { ComputerName = "E2E-private-name", ComputerManufacturer = "E2E", ComputerModel = "Fixture" });
        set("stopping", true);
        Require(!(bool)confirm.Invoke(null, new object[] { window }), "Online consent rejects stopped application");
        set("stopping", false); set("initialInformationShown", false);
        Require(!(bool)confirm.Invoke(null, new object[] { window }), "Online consent rejects unpresented initial inventory");
        set("initialInformationShown", true);
        foreach (int answer in new[] { 7, 6 })
        {
            IntPtr owner = new WindowInteropHelper(window).Handle;
            Task<string> response = Task.Run(async delegate
            {
                for (int i = 0; i < 100; i++)
                {
                    IntPtr dialog = IntPtr.Zero;
                    EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                    {
                        uint process; GetWindowThreadProcessId(hwnd, out process);
                        if (process == Process.GetCurrentProcess().Id && GetWindow(hwnd, 4) == owner && Caption(hwnd) == "Поиск спецификации в интернете") { dialog = hwnd; return false; }
                        return true;
                    }, IntPtr.Zero);
                    if (dialog != IntPtr.Zero)
                    {
                        int defaultId = (int)(SendMessage(dialog, 0x400, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xffff);
                        string text = "";
                        EnumChildWindows(dialog, delegate(IntPtr child, IntPtr unused) { text += Caption(child) + "\n"; return true; }, IntPtr.Zero);
                        IntPtr button = GetDlgItem(dialog, answer);
                        if (button == IntPtr.Zero) { return "Missing consent button"; }
                        PostMessage(button, 0xf5, IntPtr.Zero, IntPtr.Zero);
                        return defaultId == 7 && text.Contains("DuckDuckGo") && text.Contains("IP-адрес") && text.Contains("не отправляются") ? null : "Invalid default answer or missing disclosure";
                    }
                    await Task.Delay(30);
                }
                // Target only the known owner; Escape closes its owned modal if the
                // dialog cannot be identified. The runner has an outer deadline too.
                return "Consent dialog not found";
            });
            bool result = (bool)confirm.Invoke(null, new object[] { window });
            Require(await response == null && result == (answer == 6), "Actual consent dialog defaults to No, discloses data and returns " + (answer == 7 ? "No" : "Yes"));
        }
        set("latest", null); set("initialInformationShown", false);
    }

    private static async Task Select(TabControl tabs, int index)
    {
        var item = (TabItem)tabs.Items[index];
        var peer = new TabItemAutomationPeer(item, new TabControlAutomationPeer(tabs));
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
        await Task.Delay(100); window.UpdateLayout();
    }
    private static void InvokeButton(Button button)
    { ((IInvokeProvider)(UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button)).GetPattern(PatternInterface.Invoke)).Invoke(); }
    private static async Task KeyMessage(Window target, Key key, bool down)
    {
        IntPtr hwnd = new WindowInteropHelper(target).Handle; uint process; GetWindowThreadProcessId(hwnd, out process);
        if (process != Process.GetCurrentProcess().Id) { throw new InvalidOperationException("Input target is not owned by this runner."); }
        long bits = 1 | ((long)MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), 0) << 16);
        if (!down) { bits |= 0xc0000000L; }
        if (!PostMessage(hwnd, down ? 0x100u : 0x101u, new IntPtr(KeyInterop.VirtualKeyFromKey(key)), new IntPtr(bits))) { throw new InvalidOperationException("PostMessage failed."); }
        await Task.Delay(80);
    }
    private static async Task Close(Window target)
    {
        bool closed = false; target.Closed += delegate { closed = true; }; target.Close();
        for (int i = 0; !closed && i < 100; i++) { await Task.Delay(30); }
        Require(closed, "Owned dialog closes within three seconds");
        if (ReferenceEquals(target, window)) { window = null; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { foreach (DependencyObject item in Descendants(VisualTreeHelper.GetChild(root, i))) { yield return item; } } }
    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    { while (node != null) { if (ReferenceEquals(node, ancestor)) { return true; } node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); } return false; }
    private static string Caption(IntPtr hwnd) { var text = new StringBuilder(4096); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
    private static void Require(bool value, string name) { if (!value) { throw new InvalidOperationException(name); } passes.Add(name); Console.WriteLine("PASS " + name); }
    private static string Hash(string path) { using (var algorithm = SHA256.Create()) using (var stream = File.OpenRead(path)) { return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); } }
}
