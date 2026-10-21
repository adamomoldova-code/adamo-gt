using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using PcPulseTests;

// Source-built integration runner: actual product WPF windows and the real
// session coordinator, with injected controlled workers only. It never starts
// physical CPU/RAM/SSD/GPU stress, camera, microphone, or network activity.
internal static class StressUiE2E
{
    private static readonly List<string> passed = new List<string>(), failed = new List<string>();
    private static int exitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        { failed.Add("Unhandled dispatcher exception: " + e.Exception); e.Handled = true; };
        app.Dispatcher.BeginInvoke(new Action(async delegate
        {
            try { StressWindowTests.RunSession(); passed.Add("Real session coordinator: validation, concurrency, final RAM/FPS telemetry, callback-failure cleanup and thermal governor with controlled workers."); }
            catch (Exception error) { failed.Add("Session: " + error); }
            try { StressWindowTests.Run(); passed.Add("Stress dialog: manual selection, duration, progress, outcomes, cancellation, close wait, bounded layouts and JSON separation from saved speed results."); }
            catch (Exception error) { failed.Add("Dialog: " + error); }
            try { passed.AddRange(await StressWindowTests.RunLive(output)); }
            catch (Exception error) { failed.Add("Live WPF: " + error); }
            File.WriteAllText(Path.Combine(output, "stress-ui-results.json"), new JavaScriptSerializer().Serialize(new
            {
                Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                RunnerSha256 = Hash(Assembly.GetExecutingAssembly().Location),
                TestedAtUtc = DateTime.UtcNow.ToString("o"),
                Passed = passed, Failures = failed,
                Coverage = "Source-built product WPF dialog, real HWND sizing/hit testing and real session lifecycle. Workloads are controlled test doubles. Not a released-binary or physical-hardware stress verification."
            }));
            foreach (string value in passed) { Console.WriteLine("PASS " + value); }
            foreach (string value in failed) { Console.WriteLine("FAIL " + value); }
            Console.WriteLine("RESULT " + passed.Count + " passed; " + failed.Count + " failed.");
            exitCode = failed.Count == 0 ? 0 : 1; app.Shutdown();
        }));
        app.Run(); return exitCode;
    }

    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
