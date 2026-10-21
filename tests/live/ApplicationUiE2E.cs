using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Web.Script.Serialization;

// External UI Automation against the actual product process. No global input,
// network consent, driver installation, or peripheral healthy verdicts.
internal static class ApplicationUiE2E
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    private static Process product;
    private static AutomationElement main;
    private static string output;
    private static readonly List<string> passed = new List<string>();
    private static readonly List<string> failures = new List<string>();

    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { return 2; }
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        try
        {
            product = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[0]), "--no-elevation")
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0])) });
            main = Wait(delegate { return Windows().FirstOrDefault(w => w.Current.Name.StartsWith("ADAMO GT —")); }, 30000, "dashboard window");
            Wait(delegate { var e = ById(main, "RunTestButton"); return e != null && e.Current.IsEnabled ? e : null; }, 90000, "startup finished");
            SaveTree("dashboard.txt");
            Check(!String.IsNullOrWhiteSpace(Text("ComputerModel")) && !Text("ComputerModel").Contains("Определение"), "hardware model populated");
            string initialStatus = Text("StartupStatus");
            Check(initialStatus.Contains("Сохран") || initialStatus.Contains("сохран") || initialStatus.Contains("Ранее") || initialStatus.Contains("ранее"), "normal startup shows previous results: " + initialStatus);
            Check(!ById(main, "StopTestButton").Current.IsEnabled, "cached startup does not start benchmarks");
            Thread.Sleep(2500);
            Check(!ById(main, "StopTestButton").Current.IsEnabled, "cached startup stays idle after monitor refresh");

            foreach (string button in new[] { "PortsRefreshButton", "PortsSearchButton" })
            {
                string previous = Text("PortsStatus");
                Invoke(ById(main, button));
                var consent = Wait(delegate { return Windows().FirstOrDefault(w => w.Current.Name == "Поиск спецификации в интернете"); }, 8000, button + " consent");
                var decline = ById(consent, "7");
                Check(decline != null, button + " offers No");
                if (decline == null) { throw new InvalidOperationException("No refusal button"); }
                Check(consent.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Any(e => e.Current.Name.Contains("DuckDuckGo")), button + " discloses destination");
                Invoke(decline);
                WaitGone(consent);
                Check(Text("PortsStatus") == previous, button + " refusal preserves local results");
            }

            Invoke(ById(main, "SensorsButton"));
            var sensors = Wait(delegate { return Windows().FirstOrDefault(w => w.Current.Name == "Датчики и точность данных"); }, 8000, "sensor help");
            string sensorText = String.Join("\n", sensors.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            string version = FileVersionInfo.GetVersionInfo(args[0]).FileVersion;
            Check(sensorText.Contains("ADAMO GT " + new Version(version).ToString(3)), "sensor help matches running product version");
            Close(sensors); WaitGone(sensors);

            for (int i = 0; i < 2; i++)
            {
                Invoke(ById(main, "PeripheralTestsButton"));
                var peripheral = Wait(delegate { return Windows().FirstOrDefault(w => w.Current.Name != main.Current.Name && w.Current.ControlType == ControlType.Window); }, 8000, "peripheral dialog");
                Check(peripheral.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Count == 4, "peripheral dialog has four tabs, opening " + (i + 1));
                Close(peripheral); WaitGone(peripheral);
            }

            Invoke(ById(main, "RefreshButton"));
            Wait(delegate { var e = ById(main, "StartupStatus"); return e != null && e.Current.Name.Contains("Состав оборудования и показания обновлены") ? e : null; }, 30000, "inventory refresh");
            Check(!ById(main, "StopTestButton").Current.IsEnabled, "inventory refresh does not rerun benchmarks");

            string savedRead = Text("DiskRead"), savedWrite = Text("DiskWrite");
            CancelManualTest("RunTestButton", "BenchmarkStage");
            Check(Text("DiskRead") == savedRead && Text("DiskWrite") == savedWrite, "SSD cancellation preserves previous successful measurements");
            CancelManualTest("RunGpuTestButton", "GpuBenchmarkResult");
            Check(Text("GpuBenchmarkResult").Contains("FPS"), "GPU cancellation preserves previous successful FPS");

            string reportPath = Path.Combine(output, "ui-export.json");
            if (File.Exists(reportPath)) { throw new InvalidOperationException("Use a fresh output directory to avoid overwriting evidence."); }
            Invoke(ById(main, "SaveReportButton"));
            var save = Wait(delegate { return Windows().FirstOrDefault(w => w.Current.Name == "Сохранить отчёт ADAMO GT"); }, 10000, "save report dialog");
            SaveTree("save-dialog.txt");
            var filenameHost = ById(save, "FileNameControlHost") ?? ById(save, "1148");
            var name = filenameHost == null ? null : filenameHost.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (name == null)
            {
                name = save.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name.StartsWith("Имя файла") || e.Current.Name.StartsWith("File name"));
            }
            if (name == null) { SaveTree("save-dialog.txt"); throw new InvalidOperationException("Save dialog filename field not found."); }
            // IFileDialog can display UIA SetValue text yet keep its previous
            // filename until real edit notifications occur. Deliver characters
            // only to the verified filename HWND; never type into global focus.
            IntPtr filenameHandle = new IntPtr(name.Current.NativeWindowHandle);
            uint filenameProcess; GetWindowThreadProcessId(filenameHandle, out filenameProcess);
            if (filenameHandle == IntPtr.Zero || filenameProcess != product.Id) { throw new InvalidOperationException("Filename HWND is not owned by tested product."); }
            name.SetFocus();
            SendMessage(filenameHandle, 0xb1, IntPtr.Zero, new IntPtr(-1)); // EM_SETSEL
            foreach (char character in reportPath) { SendMessage(filenameHandle, 0x102, new IntPtr(character), new IntPtr(1)); }
            Check(((ValuePattern)name.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == reportPath, "report filename field holds selected path");
            // Shell file-list rows use numeric IDs too; ID 1 alone may select
            // and invoke an existing file instead of the Save button.
            var saveButton = save.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
            if (saveButton == null) { throw new InvalidOperationException("Save dialog action not found."); }
            Invoke(saveButton);
            var saved = Wait(delegate { return Windows().FirstOrDefault(w => w.Current.Name == "ADAMO GT" && ById(w, "2") != null); }, 10000, "report confirmation");
            SaveTree("save-confirmation.txt");
            Invoke(ById(saved, "2")); WaitGone(saved);
            Check(File.Exists(reportPath), "report created through real SaveFileDialog");
            var report = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(reportPath));
            Check(Convert.ToString(report["AppVersion"]) == version, "report carries actual app version");
            Check(report.ContainsKey("Hardware") && report.ContainsKey("DiskTests") && report.ContainsKey("GpuTests") && report.ContainsKey("Ports") && report.ContainsKey("PeripheralTests"), "report contains all data sections");
            Check(!(bool)report["ChecksRunning"], "export labels completed state correctly");
            Close(main);
            Check(product.WaitForExit(15000), "normal close exits product and monitor worker");
            if (product.HasExited) { Check(product.ExitCode == 0, "normal application exit code zero"); }
        }
        catch (Exception error) { failures.Add(error.ToString()); try { SaveTree("failure-ui.txt"); } catch { } }
        finally
        {
            if (product != null && !product.HasExited)
            {
                try { foreach (var w in Windows().Reverse()) { try { Close(w); } catch { } } } catch { }
                if (!product.WaitForExit(12000)) { product.Kill(); failures.Add("Owned process required forced cleanup."); }
            }
            var result = new { Passed = passed, Failures = failures, Product = args[0], TestedAtUtc = DateTime.UtcNow.ToString("o"),
                Limitations = "UI Automation invokes actual controls; no physical mouse/UAC acceptance/first-run history deletion/network packet capture. No healthy peripheral verdicts are recorded." };
            File.WriteAllText(Path.Combine(output, "results.json"), new JavaScriptSerializer().Serialize(result));
        }
        foreach (string s in passed) { Console.WriteLine("PASS " + s); }
        foreach (string s in failures) { Console.WriteLine("FAIL " + s); }
        Console.WriteLine("RESULT " + passed.Count + " passed; " + failures.Count + " failed");
        return failures.Count == 0 ? 0 : 1;
    }
    private static AutomationElement[] Windows()
    {
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, product.Id)).Cast<AutomationElement>().ToArray();
        // UIA exposes Win32 owned dialogs beneath the WPF owner's tree, even
        // though they have separate native HWNDs. Search only our process roots.
        var result = new List<AutomationElement>(roots);
        foreach (var root in roots)
        {
            try
            {
                result.AddRange(root.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                    new PropertyCondition(AutomationElement.ProcessIdProperty, product.Id))).Cast<AutomationElement>());
            }
            catch (ElementNotAvailableException)
            {
                // Owned dialogs can disappear between root enumeration and
                // descendant enumeration. Their replacement is found on the
                // next bounded poll; other UIA failures are still propagated.
            }
        }
        return result.ToArray();
    }
    private static AutomationElement ById(AutomationElement root, string id)
    { return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); }
    private static string Text(string id) { var e = ById(main, id); return e == null ? "" : e.Current.Name; }
    private static void CancelManualTest(string action, string stage)
    {
        string previous = Text(stage);
        Invoke(ById(main, action));
        Wait(delegate { var e = ById(main, "StopTestButton"); return e != null && e.Current.IsEnabled && Text(stage) != previous ? e : null; }, 10000, action + " starts");
        Invoke(ById(main, "StopTestButton"));
        Wait(delegate { var e = ById(main, action); return e != null && e.Current.IsEnabled && !ById(main, "StopTestButton").Current.IsEnabled ? e : null; }, 20000, action + " releases on cancellation");
        Check(Text("StartupStatus").Contains("остановлен"), action + " cancellation completes and returns controls");
    }
    private static void Invoke(AutomationElement element)
    {
        if (element == null) { throw new InvalidOperationException("Control not found."); }
        Task.Run(delegate { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); });
    }
    private static void Close(AutomationElement window) { ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close(); }
    private static AutomationElement Wait(Func<AutomationElement> find, int timeout, string label)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeout)
        {
            if (product.HasExited) { throw new InvalidOperationException("Product exited during " + label + ": " + product.ExitCode); }
            try
            {
                var value = find(); if (value != null) { return value; }
            }
            catch (ElementNotAvailableException)
            {
                // A SaveFileDialog may close while its UIA name is being read.
                // Re-enumerate on the next poll without resetting the deadline.
            }
            Thread.Sleep(150);
        }
        throw new TimeoutException(label);
    }
    private static void WaitGone(AutomationElement window)
    {
        int hwnd;
        try { hwnd = window.Current.NativeWindowHandle; }
        catch (ElementNotAvailableException) { return; }
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000)
        {
            try
            {
                if (!Windows().Any(w => w.Current.NativeWindowHandle == hwnd)) { return; }
            }
            catch (ElementNotAvailableException)
            {
                // Do not treat a stale element as proof that the target closed.
                // Confirm with a fresh enumeration before the existing timeout.
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Modal did not close");
    }
    private static void Check(bool value, string message) { (value ? passed : failures).Add(message); }
    private static void SaveTree(string name)
    {
        var lines = new List<string>();
        foreach (var w in Windows())
        {
            lines.Add("WINDOW " + w.Current.Name);
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            { lines.Add(e.Current.ControlType.ProgrammaticName + " | " + e.Current.AutomationId + " | " + e.Current.Name); }
        }
        File.WriteAllLines(Path.Combine(output, name), lines);
    }
}
