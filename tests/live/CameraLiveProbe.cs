using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PcPulse;
using PcPulseTests.Live;

public static class CameraLiveProbe
{
    private static readonly List<object> Observations = new List<object>();
    private static readonly List<string> Errors = new List<string>();
    private static bool finished;
    private static string output;
    private static CameraPreview camera;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--allow-camera")
        { Console.Error.WriteLine("This live probe requires --allow-camera and a metadata-only JSON output path."); return 2; }
        output = Path.GetFullPath(args[1]);
        var app = new Application();
        var window = new Window { Title = "ADAMO GT — проверка камеры", Width = 720, Height = 540,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new DockPanel();
        var status = new TextBlock { Text = "Проверка кадров и повторного запуска. Изображения не сохраняются.", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status);
        var host = new ProbeHost(); panel.Children.Add(host); window.Content = panel;
        window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!finished) { e.Cancel = true; } };
        window.Loaded += async delegate
        {
            camera = new CameraPreview();
            camera.Error += delegate(string message) { lock (Errors) { Errors.Add(message); } };
            bool success = false;
            try
            {
                IList<CameraDevice> devices = await CameraPreview.EnumerateDevicesAsync();
                if (devices.Count == 0) { throw new InvalidOperationException("No camera metadata found."); }
                Console.WriteLine("Device: " + devices[0].Name);
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    status.Text = "Проверка камеры: запуск " + attempt + " из 2. Изображения не сохраняются.";
                    await camera.StartAsync(devices[0], host.Handle, 680, 450);
                    await Task.Delay(1000);
                    CameraEvidenceSnapshot first = await CameraEvidence.ReadAsync(camera);
                    await Task.Delay(2000);
                    CameraEvidenceSnapshot last = await CameraEvidence.ReadAsync(camera);
                    bool frames = CameraEvidence.FramesAdvanced(first, last);
                    Observations.Add(new { Attempt = attempt, First = first, Last = last, FramesAdvanced = frames });
                    Console.WriteLine("Attempt " + attempt + ": " + first.FramesDrawn + " -> " + last.FramesDrawn + ", advancing=" + frames);
                    if (!frames) { throw new InvalidOperationException("Advancing rendered frames were not confirmed."); }
                    await camera.StopAsync();
                    await camera.Completion;
                    if (camera.IsRunning || !camera.Completion.IsCompleted) { throw new InvalidOperationException("Camera stop did not release ownership."); }
                    Observations.Add(new { Attempt = attempt, Stopped = true, ResourcesReleased = true });
                    Console.WriteLine("Attempt " + attempt + ": stopped and released");
                }
                success = true;
            }
            catch (Exception error) { Errors.Add(error.ToString()); Console.Error.WriteLine(error); }
            try
            {
                await camera.StopAsync();
                if (await Task.WhenAny(camera.Completion, Task.Delay(10000)) != camera.Completion)
                { throw new TimeoutException("Native camera cleanup remains pending."); }
            }
            catch (Exception error) { success = false; Errors.Add("Cleanup: " + error); }
            finally
            {
                camera.Dispose();
                var report = new { Passed = success && Errors.Count == 0, Version = typeof(CameraPreview).Assembly.GetName().Version.ToString(),
                    Assembly = typeof(CameraPreview).Assembly.Location, IsRunningAfter = camera.IsRunning, ResourcesReleased = camera.Completion.IsCompleted,
                    ImagesReadOrPersisted = false, Observations = Observations, Errors = Errors };
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, new JavaScriptSerializer().Serialize(report));
                finished = true;
                window.Close();
                app.Shutdown(report.Passed ? 0 : 1);
            }
        };
        app.Run(window);
        return app.Dispatcher.HasShutdownFinished && Errors.Count == 0 ? 0 : 1;
    }

    private sealed class ProbeHost : HwndHost
    {
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            IntPtr child = CreateWindowEx(0, "static", "", 0x50000000, 0, 0, 680, 450, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (child == IntPtr.Zero) { throw new System.ComponentModel.Win32Exception(); }
            return new HandleRef(this, child);
        }
        protected override void DestroyWindowCore(HandleRef window) { DestroyWindow(window.Handle); }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int extended, string kind, string text, int style, int left, int top,
            int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    }
}
