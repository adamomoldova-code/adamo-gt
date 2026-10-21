using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PcPulse;
using PcPulseTests.Live;

// Opt-in integration probe for the released product. Never part of SmokeTests.
// Camera images/audio/key sequences are not written; only numeric observations.
internal static class LivePeripherals
{
    private static Window window;
    private static string output;
    private static readonly List<object> observations = new List<object>();
    private static readonly List<string> failures = new List<string>();
    private static int exitCode;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length != 2 || args[0] != "--activate-hardware") { Console.Error.WriteLine("Explicit --activate-hardware and output directory are required."); return 2; }
        output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Type type = typeof(CameraPreview).Assembly.GetType("PcPulse.PeripheralTestsWindow", true);
        window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new PeripheralTestResult[0], new Func<PeripheralTestResult, bool>(result => { throw new InvalidOperationException("Live probe cannot confirm manual verdicts."); }), false, "" }, null);
        window.ShowInTaskbar = false; window.ShowActivated = false; window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000;
        window.SourceInitialized += delegate
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L));
        };
        window.Loaded += async delegate
        {
            window.Left = -10000; window.Top = -10000;
            try { await Run(); }
            catch (Exception error) { failures.Add(error.ToString()); }
            // C# 5: asynchronous cleanup follows the catch on every normal path.
            {
                try { await Deadline(Call("StopDevices"), 11000, "Final stop"); } catch (Exception error) { failures.Add("Final stop: " + error.Message); }
                bool closed = false; window.Closed += delegate { closed = true; };
                window.Close();
                for (int i = 0; !closed && i < 100; i++) { await Task.Delay(100); }
                observations.Add(new { Test = "close", Closed = closed });
                if (!closed) { failures.Add("Dialog did not close within 10 seconds."); }
                var report = new
                {
                    Version = typeof(CameraPreview).Assembly.GetName().Version.ToString(),
                    ProductPath = typeof(CameraPreview).Assembly.Location,
                    ProductSha256 = Hash(typeof(CameraPreview).Assembly.Location),
                    TestedAtUtc = DateTime.UtcNow.ToString("o"), Observations = observations, Failures = failures,
                    Note = "Real device activation. Only numeric telemetry persisted. Quality and physical keyboard switches require human confirmation. No manual healthy verdict was saved."
                };
                File.WriteAllText(Path.Combine(output, "live-results.json"), new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Serialize(report));
                Console.WriteLine("RESULT: " + failures.Count + " integration failures. " + Path.Combine(output, "live-results.json"));
                foreach (string failure in failures) { Console.WriteLine("FAIL: " + failure); }
                exitCode = failures.Count == 0 ? 0 : 1;
                app.Shutdown();
            }
        };
        app.Run(window);
        return exitCode;
    }

    private static async Task Run()
    {
        await Step("device enumeration", async delegate
        {
            await Deadline(Call("Discover"), 10000, "Discovery");
            observations.Add(new { Test = "devices", Cameras = Names("cameras"), Microphones = Names("inputs"), Outputs = Names("outputs") });
            Console.WriteLine("CAMERAS: " + String.Join(" | ", Names("cameras")));
            Console.WriteLine("INPUTS: " + String.Join(" | ", Names("inputs")));
            Console.WriteLine("OUTPUTS: " + String.Join(" | ", Names("outputs")));
        });
        var tabs = Field<TabControl>("tabs");
        await Step("camera preview, stop and restart", async delegate
        {
            tabs.SelectedIndex = 0; await Settled();
            Require(Field<ComboBox>("cameras").SelectedItem != null, "No camera is available: " + Field<TextBlock>("cameraStatus").Text);
            await Deadline(Call("StartCamera"), 12000, "Camera startup");
            var camera = Field<CameraPreview>("camera");
            Require(camera.IsRunning, Field<TextBlock>("cameraStatus").Text);
            var first = await CameraEvidence.ReadAsync(camera);
            await Task.Delay(2200);
            var second = await CameraEvidence.ReadAsync(camera);
            observations.Add(new { Test = "camera-stream", First = first, Second = second, Status = Field<TextBlock>("cameraStatus").Text });
            Require(camera.IsRunning, "Camera stopped while waiting for frames.");
            Require(CameraEvidence.FramesAdvanced(first, second), "Camera is running but advancing rendered frames were not confirmed.");
            await Deadline(Call("StopDevices"), 11000, "Camera stop");
            Require(!camera.IsRunning && camera.Completion.IsCompleted && !Field<bool>("releasePending"), "Camera ownership was not released.");
            Require(Field<Button>("cameraStart").IsEnabled, "Camera cannot restart after stop.");
            await Deadline(Call("StartCamera"), 12000, "Camera restart");
            Require(camera.IsRunning, "Camera did not restart: " + Field<TextBlock>("cameraStatus").Text);
            await Task.Delay(500);
            tabs.SelectedIndex = 1; await Settled();
            Require(!camera.IsRunning && camera.Completion.IsCompleted, "Tab change did not release camera.");
            observations.Add(new { Test = "camera-restart-and-tab-stop", Passed = true });
        });
        await Step("microphone capture and playback", async delegate
        {
            tabs.SelectedIndex = 1; await Settled();
            Require(Field<ComboBox>("inputs").SelectedItem != null, "No microphone is available.");
            Stopwatch clock = Stopwatch.StartNew();
            await Deadline(Call("Record"), 13000, "Five-second microphone capture");
            AudioRecording capture = Field<AudioRecording>("recording");
            Require(capture != null, Field<TextBlock>("micStatus").Text);
            byte[] pcm = capture.Data;
            double sum = 0, peak = 0; int nonzero = 0;
            try
            {
                for (int i = 0; i < pcm.Length; i += 2)
                {
                    short sample = (short)(pcm[i] | pcm[i + 1] << 8);
                    double value = sample / 32768.0;
                    sum += value * value; peak = Math.Max(peak, Math.Abs(value)); if (sample != 0) { nonzero++; }
                }
                observations.Add(new { Test = "microphone-capture", Device = Convert.ToString(Field<ComboBox>("inputs").SelectedItem), capture.SampleRate, capture.Channels, capture.ByteCount, capture.DurationSeconds,
                    WallSeconds = clock.Elapsed.TotalSeconds, Rms = Math.Sqrt(sum / (pcm.Length / 2)), Peak = peak, NonzeroSamples = nonzero });
                Console.WriteLine("MIC: " + capture.DurationSeconds + " seconds; " + capture.SampleRate + " Hz; peak " + peak.ToString("0.0000") + "; nonzero " + nonzero);
            }
            finally { Array.Clear(pcm, 0, pcm.Length); }
            Require(capture.DurationSeconds >= 4.9 && capture.DurationSeconds <= 5.01, "Microphone capture duration differs from 5 seconds.");
            Require(!Field<Button>("passed").IsEnabled, "Microphone confirmation is enabled before listening.");
            await Deadline(Call("Play"), 12000, "Microphone playback");
            Require(Field<Button>("passed").IsEnabled, Field<TextBlock>("micStatus").Text);
            observations.Add(new { Test = "microphone-playback", Completed = true, Output = Convert.ToString(Field<ComboBox>("outputs").SelectedItem) });
        });
        await Step("speaker left and right channels", async delegate
        {
            tabs.SelectedIndex = 3; await Settled();
            Require(Field<ComboBox>("outputs").SelectedItem != null, "No audio output is available.");
            await Deadline(Call("Channel", 0), 7000, "Left channel");
            Require(Field<int>("speakerChannels") == 1, Field<TextBlock>("speakerStatus").Text);
            Require(!Field<Button>("passed").IsEnabled, "Speakers can be confirmed before the right-channel test.");
            await Deadline(Call("Channel", 1), 7000, "Right channel");
            Require(Field<int>("speakerChannels") == 3 && Field<Button>("passed").IsEnabled, Field<TextBlock>("speakerStatus").Text);
            observations.Add(new { Test = "speaker-channels", LeftCompleted = true, RightCompleted = true, Output = Convert.ToString(Field<ComboBox>("outputs").SelectedItem) });
        });
        await Step("microphone cancellation and recovery", async delegate
        {
            tabs.SelectedIndex = 1; await Settled();
            Task capture = Call("Record"); await Task.Delay(650);
            await Deadline(Call("StopDevices"), 10000, "Recording cancellation"); await Deadline(capture, 3000, "Canceled capture completion");
            Require(Field<AudioRecording>("recording") == null && !Field<bool>("releasePending") && Field<Button>("record").IsEnabled, "Canceled recording kept a buffer or blocked restart.");
            capture = Call("Record"); await Task.Delay(600);
            tabs.SelectedIndex = 2; await Settled(); await Deadline(capture, 3000, "Capture canceled by tab change");
            Require(Field<AudioRecording>("recording") == null && (Field<Button>("keyboardStart").IsEnabled || Field<KeyboardTestPanel>("keyboard").IsRunning), "Tab cancellation blocks keyboard controls.");
            observations.Add(new { Test = "microphone-cancel-restart-tab-stop", Passed = true, Status = Field<TextBlock>("micStatus").Text });
        });
    }

    private static async Task Settled()
    {
        for (int i = 0; i < 120; i++)
        {
            if (!Field<bool>("busy") && Field<Task>("deviceStopTask").IsCompleted) { return; }
            await Task.Delay(100);
        }
        throw new TimeoutException("UI did not settle.");
    }
    private static async Task Step(string name, Func<Task> action)
    {
        Console.WriteLine("START " + name);
        bool failed = false;
        try { await action(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            string message = name + ": " + error.Message;
            failures.Add(message); observations.Add(new { Test = name, Error = error.ToString() }); Console.WriteLine("FAIL " + message);
            failed = true;
        }
        if (failed) { try { await Deadline(Call("StopDevices"), 11000, "Recovery stop"); } catch (Exception cleanup) { failures.Add("Recovery: " + cleanup.Message); } }
    }
    private static async Task Deadline(Task task, int milliseconds, string name)
    {
        if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) { task.ContinueWith(done => { var ignored = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); throw new TimeoutException(name); }
        await task;
    }
    private static string[] Names(string selector) { return Field<ComboBox>(selector).Items.Cast<object>().Select(Convert.ToString).ToArray(); }
    private static T Field<T>(string name) { return (T)window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window); }
    private static Task Call(string name, params object[] args) { return (Task)window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args); }
    private static string Hash(string path) { using (var algorithm = SHA256.Create()) using (var stream = File.OpenRead(path)) { return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); } }
    private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
}
