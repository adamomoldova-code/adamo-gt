using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using PcPulse;

// A deliberately short physical backend smoke test against the built product assembly.
// It never installs drivers, starts the app's automatic benchmarks, or uses the network.
internal static class StressHardwareSmoke
{
    private static readonly List<object> stages = new List<object>();
    private static readonly List<object> samples = new List<object>();
    private static string outputPath;
    private static object inventorySummary;
    private static string fatalError = "";
    private static readonly DateTime started = DateTime.UtcNow;
    private static int failures;
    private static bool safetyStopped;
    private static bool baselineBlocked;
    private static object baselineGate;
    private static string[] requestedComponents = new string[0];

    [STAThread]
    private static int Main(string[] args)
    {
        if ((args.Length != 2 && args.Length != 3) || args[0] != "--run-hardware")
        {
            Console.Error.WriteLine("Usage: StressHardwareSmoke.exe --run-hardware <existing output directory> [Cpu,Ram,Gpu,Ssd]");
            return 2;
        }
        outputPath = Path.GetFullPath(args[1]);
        if (!Directory.Exists(outputPath)) { Console.Error.WriteLine("Output directory does not exist."); return 2; }
        try
        {
            StressComponent[] selectedComponents = ParseComponents(args.Length == 3 ? args[2] : "Cpu,Ram,Gpu,Ssd");
            requestedComponents = selectedComponents.Select(value => value.ToString()).ToArray();
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                { throw new InvalidOperationException("This physical smoke harness requires administrator rights, matching the application sensor workflow."); }
            }
            using (var monitor = new HardwareMonitor())
            {
                Snapshot inventory = monitor.Read();
                GpuSnapshot gpu = inventory.Gpus.Where(value => value.AdapterIndex.HasValue)
                    .OrderByDescending(value => value.IsIntegrated == false).FirstOrDefault();
                DiskSnapshot disk = inventory.Disks.FirstOrDefault(value => value.MediaType.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0
                    && !string.IsNullOrWhiteSpace(value.TestVolumeRoot));
                inventorySummary = new
                {
                    Manufacturer = inventory.ComputerManufacturer,
                    Model = inventory.ComputerModel,
                    Cpu = inventory.CpuName,
                    LogicalProcessors = Environment.ProcessorCount,
                    RamInstalledBytes = inventory.RamInstalledBytes,
                    IsAdministrator = inventory.IsAdmin,
                    SelectedGpu = gpu == null ? null : gpu.Name,
                    SelectedDisk = disk == null ? null : disk.Name,
                    SelectedDiskType = disk == null ? null : disk.MediaType,
                    SelectedVolume = disk == null ? null : disk.TestVolumeRoot,
                    DriverInstallationRequested = false
                };
                RecordSample("initial", inventory, gpu, disk);
                WriteReport();
                baselineBlocked = !WaitForCoolBaseline(monitor, inventory, gpu, disk);
                foreach (StressComponent component in selectedComponents)
                {
                    string unavailable = component == StressComponent.Gpu && gpu == null ? "No hardware DXGI adapter was enumerated."
                        : component == StressComponent.Ssd && disk == null ? "No identified SSD with an available test volume was enumerated." : "";
                    if (safetyStopped) { unavailable = "A previous stage reached a temperature safety stop; no additional load was started."; }
                    if (baselineBlocked) { unavailable = "A fresh CPU temperature below 80 C was not confirmed within the read-only baseline window; no load was started."; }
                    if (unavailable.Length > 0)
                    {
                        stages.Add(new { Component = component.ToString(), Mode = "completion-and-cancellation", Outcome = "skipped", Reason = unavailable });
                        WriteReport();
                        continue;
                    }
                    RunStage(component, false, monitor, gpu, disk);
                    if (!safetyStopped) { RunStage(component, true, monitor, gpu, disk); }
                    WriteReport();
                }
            }
        }
        catch (Exception exception)
        {
            fatalError = exception.ToString();
            failures++;
            Console.Error.WriteLine(exception);
            File.AppendAllText(Path.Combine(outputPath, "stderr.txt"), exception + Environment.NewLine);
        }
        finally { WriteReport(); }
        Log("Physical stress smoke: " + (failures == 0 ? "completed without unexpected failures" : failures + " unexpected failure(s)") +
            (safetyStopped ? "; temperature safety stop observed" : "") +
            (baselineBlocked ? "; startup temperature gate blocked the run" : "") + ". See stress-hardware-results.json.");
        return failures == 0 && !safetyStopped && !baselineBlocked ? 0 : 1;
    }

    private static StressComponent[] ParseComponents(string selection)
    {
        var allowed = new[] { StressComponent.Cpu, StressComponent.Ram, StressComponent.Gpu, StressComponent.Ssd };
        var selected = new HashSet<StressComponent>();
        foreach (string entry in selection.Split(','))
        {
            string name = entry.Trim();
            StressComponent? match = allowed.Where(value => value.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(value => (StressComponent?)value).FirstOrDefault();
            if (!match.HasValue) { throw new ArgumentException("Unknown component: " + name + ". Choose Cpu,Ram,Gpu,Ssd."); }
            selected.Add(match.Value);
        }
        return allowed.Where(selected.Contains).ToArray();
    }

    private static bool WaitForCoolBaseline(HardwareMonitor monitor, Snapshot initial, GpuSnapshot gpu, DiskSnapshot disk)
    {
        Log("Read-only startup check: require a fresh CPU temperature below 80 C; poll for at most 20 seconds.");
        var timer = Stopwatch.StartNew();
        Snapshot sample = initial;
        int readings = 0;
        bool fresh = false;
        bool passed = false;
        while (true)
        {
            readings++;
            RecordSample("read-only-startup-temperature-gate", sample, gpu, disk);
            fresh = Math.Abs((DateTime.UtcNow - sample.Timestamp.ToUniversalTime()).TotalSeconds) <= 5
                && sample.CpuTemperature.HasValue && !Double.IsNaN(sample.CpuTemperature.Value)
                && !Double.IsInfinity(sample.CpuTemperature.Value);
            if (fresh && sample.CpuTemperature.Value < 80) { passed = true; break; }
            if (timer.Elapsed.TotalSeconds >= 20) { break; }
            int pauseMilliseconds = (int)Math.Min(2000, Math.Max(1, (20 - timer.Elapsed.TotalSeconds) * 1000));
            Thread.Sleep(pauseMilliseconds);
            if (timer.Elapsed.TotalSeconds >= 20) { break; }
            // The bounded window controls additional polls; a Windows sensor call itself can take longer.
            sample = monitor.Read();
        }
        baselineGate = new
        {
            Passed = passed, LimitExclusiveCelsius = 80, PlannedMaximumPollingSeconds = 20,
            ActualElapsedSeconds = timer.Elapsed.TotalSeconds, Readings = readings,
            LastCpuTemperature = sample.CpuTemperature, LastSampleFresh = fresh,
            LastCpuTemperatureStatus = sample.CpuTemperatureStatus,
            Reason = passed ? "Fresh CPU temperature below 80 C confirmed before any workload."
                : fresh ? "CPU did not cool below 80 C during the read-only window."
                : "A fresh CPU temperature was unavailable; the harness refused to start load."
        };
        Log("Startup temperature gate: " + (passed ? "passed" : "blocked") + "; CPU=" +
            (sample.CpuTemperature.HasValue ? sample.CpuTemperature.Value.ToString("0.0", CultureInfo.InvariantCulture) : "unavailable") + " C.");
        WriteReport();
        return passed;
    }

    private static void RunStage(StressComponent component, bool cancel, HardwareMonitor monitor, GpuSnapshot gpu, DiskSnapshot disk)
    {
        string mode = cancel ? "cancel-after-verified-progress" : "two-second-completion";
        Log(component + " / " + mode + ": starting");
        string scratchRoot = component == StressComponent.Ssd ? disk.TestVolumeRoot : null;
        string[] before = ScratchFiles(scratchRoot);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var progress = new List<object>();
        object progressLock = new object();
        int callbacks = 0;
        bool cancellationFromWork = false;
        var timer = Stopwatch.StartNew();
        StressRunResult result;
        using (var cancellation = new CancellationTokenSource())
        {
            // Backstop includes renderer/file preparation; normal component load is only two seconds.
            cancellation.CancelAfter(cancel ? 1500 : 10000);
            var options = new StressOptions
            {
                Components = component, DurationSeconds = 2, Sequential = true, RamMiB = 64, Gpu = gpu, Disk = disk
            };
            result = StressTestRunner.RunAsync(options, delegate(StressProgress update)
            {
                Interlocked.Increment(ref callbacks);
                lock (progressLock)
                {
                    progress.Add(new { update.Detail, update.Iterations, update.BytesProcessed, update.WrittenBytes,
                        update.IsRunning, update.Completed, update.Cancelled, update.Error, update.ElapsedSeconds });
                    if (scratchRoot != null)
                    {
                        foreach (string path in ScratchFiles(scratchRoot))
                        { if (!before.Contains(path, StringComparer.OrdinalIgnoreCase)) { observed.Add(path); } }
                    }
                    if (cancel && update.IsRunning && update.Iterations > 0)
                    {
                        cancellationFromWork = true;
                        cancellation.Cancel();
                    }
                }
            }, delegate
            {
                Snapshot sample = monitor.Read();
                RecordSample(component + " / " + mode, sample, gpu, disk);
                return sample;
            }, cancellation.Token).GetAwaiter().GetResult();
        }
        timer.Stop();
        int callbacksAtReturn = Volatile.Read(ref callbacks);
        Thread.Sleep(100);
        bool noLateCallbacks = callbacksAtReturn == Volatile.Read(ref callbacks);
        string[] after = ScratchFiles(scratchRoot);
        string[] newRemaining = after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        bool previousRetained = before.All(path => after.Contains(path, StringComparer.OrdinalIgnoreCase));
        StressProgress state = result.Results.Single(value => value.Component == component);
        bool stoppedForTemperature = result.SafetyStopReason.Length > 0 && state.Error.Length == 0;
        safetyStopped |= stoppedForTemperature;
        bool outcomeExpected = cancel ? state.Cancelled && !state.Completed && state.Error.Length == 0
            : state.Completed && !state.Cancelled && state.Error.Length == 0 && state.Iterations > 0;
        bool cleanupOk = newRemaining.Length == 0 && previousRetained && noLateCallbacks;
        bool passed = outcomeExpected && cleanupOk && (!cancel || cancellationFromWork);
        if (!passed && !stoppedForTemperature) { failures++; }
        string outcome = stoppedForTemperature ? "temperature-safety-stop" : passed ? "passed" : "failed";
        stages.Add(new
        {
            Component = component.ToString(), Mode = mode, Outcome = outcome,
            PlannedLoadSeconds = 2, ExplicitRamMiB = component == StressComponent.Ram ? (int?)64 : null,
            WallSeconds = timer.Elapsed.TotalSeconds,
            state.Completed, state.Cancelled, state.Error, state.Iterations, state.BytesProcessed, state.WrittenBytes,
            state.MaxTemperature, state.ElapsedSeconds, state.Detail,
            result.SafetyStopReason,
            CancelledAfterObservedVerifiedWork = cancel ? (bool?)cancellationFromWork : null,
            NoCallbacksIn100MsAfterReturn = noLateCallbacks,
            ScratchFilesBefore = before,
            ScratchFilesObservedDuringRun = observed.OrderBy(value => value).ToArray(),
            NewScratchFilesRemaining = newRemaining,
            PreexistingScratchFilesRetained = previousRetained,
            CleanupObservedAfterActualFileCreation = scratchRoot == null ? (bool?)null : observed.Count > 0 && newRemaining.Length == 0,
            Progress = progress.ToArray()
        });
        Log(component + " / " + mode + ": " + outcome + "; iterations=" + state.Iterations +
            "; writtenBytes=" + state.WrittenBytes + (state.Error.Length == 0 ? "" : "; " + state.Error));
        WriteReport();
    }

    private static void RecordSample(string stage, Snapshot snapshot, GpuSnapshot gpu, DiskSnapshot disk)
    {
        GpuSnapshot liveGpu = gpu == null ? null : snapshot.Gpus.FirstOrDefault(value => value.AdapterIndex == gpu.AdapterIndex
            && value.AdapterLuidLow == gpu.AdapterLuidLow && value.AdapterLuidHigh == gpu.AdapterLuidHigh);
        DiskSnapshot liveDisk = disk == null ? null : snapshot.Disks.FirstOrDefault(value => value.Index == disk.Index && value.StorageIdentity == disk.StorageIdentity);
        lock (samples)
        {
            samples.Add(new { Stage = stage, TimestampUtc = snapshot.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                snapshot.CpuTemperature, snapshot.CpuTemperatureStatus, snapshot.CpuLoad,
                GpuTemperature = liveGpu == null ? null : liveGpu.Temperature,
                GpuTemperatureStatus = liveGpu == null ? "No selected GPU" : liveGpu.TemperatureStatus,
                DiskTemperature = liveDisk == null ? null : liveDisk.Temperature,
                snapshot.SensorStatus });
        }
    }

    private static string[] ScratchFiles(string root)
    {
        return root == null ? new string[0] : Directory.GetFiles(root, "ADAMO-GT-stress-*.tmp", SearchOption.TopDirectoryOnly)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        File.AppendAllText(Path.Combine(outputPath, "stdout.txt"), message + Environment.NewLine);
    }

    private static void WriteReport()
    {
        if (outputPath == null) { return; }
        Assembly product = typeof(StressTestRunner).Assembly;
        string sha256;
        using (var hash = SHA256.Create())
        using (var input = File.OpenRead(product.Location))
        { sha256 = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        object[] sensorCopy;
        lock (samples) { sensorCopy = samples.ToArray(); }
        var document = new
        {
            StartedAtUtc = started.ToString("o", CultureInfo.InvariantCulture),
            UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ProductAssembly = product.Location, ProductVersion = product.GetName().Version.ToString(), ProductSha256 = sha256,
            Inventory = inventorySummary, Failures = failures, TemperatureSafetyStop = safetyStopped,
            RequestedComponents = requestedComponents, StartupTemperatureGate = baselineGate, StartupTemperatureGateBlocked = baselineBlocked,
            FatalError = fatalError, Stages = stages.ToArray(), SensorSamples = sensorCopy,
            Coverage = "Real backend calls to the built assembly, one component at a time. Two-second completion and brief cancellation probes. No GUI automation, long-duration stability certification, full RAM/VRAM coverage, driver installation, or network request.",
            TemperatureLimitations = "Unavailable temperature readings remain null. Software temperature stopping is possible only when the hardware and installed driver expose fresh readings. Short probes do not prove safe long-duration temperatures.",
            FileCleanupLimitations = "Only ADAMO-GT-stress-*.tmp names in the selected volume root are observed. The harness never deletes or changes pre-existing files. Cleanup is called observed only when a newly created file was actually seen during the workload."
        };
        File.WriteAllText(Path.Combine(outputPath, "stress-hardware-results.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(document));
    }
}
