using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcPulse;

namespace PcPulseTests
{
    // No test in this file calls the production workload dispatcher. RAM session
    // checks use tiny buffers, injected clocks/reserve values and the real pattern
    // routines. The dialog receives a controlled asynchronous backend so that
    // premature release of controls during cancellation is observable.
    public static class StressWindowTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        public static void Run()
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                VerifyManualSelectionAndCancellation();
                VerifyCompletionAndComponentStates();
                VerifyPreviousResultDoesNotSelectOrStart();
                VerifyCloseWaitsForCleanup();
                VerifyDashboardAction(null);
                VerifyStressReportSeparation();
                foreach (Size size in new[] { new Size(480, 360), new Size(640, 440), new Size(840, 640) }) { VerifyLayout(size, null); }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public static void RunSession()
        {
            int calls = 0;
            Action<StressComponent, StressOptions, Action<StressProgress>, CancellationToken> quick = delegate(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                token.ThrowIfCancellationRequested();
                report(new StressProgress { Component = component, Iterations = 1, BytesProcessed = 64 });
            };
            foreach (StressOptions invalid in new[]
            {
                new StressOptions(), new StressOptions { Components = (StressComponent)16 },
                new StressOptions { Components = StressComponent.Cpu, DurationSeconds = 0 },
                new StressOptions { Components = StressComponent.Cpu, DurationSeconds = 901 },
                new StressOptions { Components = StressComponent.Ram, RamMiB = -1 },
                new StressOptions { Components = StressComponent.Ssd }, new StressOptions { Components = StressComponent.Gpu }
            })
            {
                bool rejected = false;
                try { StressTestRunner.RunAsync(invalid, null, null, CancellationToken.None, quick).GetAwaiter().GetResult(); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected && calls == 0, "Invalid options cannot start a workload");
            }
            var order = new List<StressComponent>();
            int active = 0, maximumActive = 0;
            var sequential = new StressOptions { Components = StressComponent.Cpu | StressComponent.Ram, DurationSeconds = 1, Sequential = true };
            StressRunResult serialResult = StressTestRunner.RunAsync(sequential, null, null, CancellationToken.None,
                delegate(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
                {
                    lock (order) { order.Add(component); active++; maximumActive = Math.Max(maximumActive, active); }
                    Thread.Sleep(8); report(new StressProgress { Iterations = 1 });
                    lock (order) { active--; }
                }).GetAwaiter().GetResult();
            Require(order.SequenceEqual(new[] { StressComponent.Cpu, StressComponent.Ram }) && maximumActive == 1, "Sequential mode executes selected components one at a time in a stable order");
            Require(serialResult.Results.Count == 2 && serialResult.Results.All(result => result.Completed && !result.Cancelled && !result.IsRunning), "A finished sequence preserves final independent component states");

            using (var arrived = new CountdownEvent(2))
            {
                active = maximumActive = 0;
                var concurrent = new StressOptions { Components = StressComponent.Cpu | StressComponent.Ram, DurationSeconds = 1 };
                StressRunResult parallelResult = StressTestRunner.RunAsync(concurrent, null, null, CancellationToken.None,
                    delegate(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
                    {
                        lock (order) { active++; maximumActive = Math.Max(maximumActive, active); }
                        arrived.Signal();
                        if (!arrived.Wait(2000, token)) { throw new InvalidOperationException("Fixture simultaneous workers did not overlap"); }
                        report(new StressProgress { Iterations = 1 });
                        lock (order) { active--; }
                    }).GetAwaiter().GetResult();
                Require(maximumActive == 2 && parallelResult.Results.All(result => result.Completed), "Simultaneous mode really overlaps independent workers");
            }
            using (var arrived = new CountdownEvent(2))
            {
                StressRunResult failed = StressTestRunner.RunAsync(new StressOptions { Components = StressComponent.Cpu | StressComponent.Ram, DurationSeconds = 1 }, null, null, CancellationToken.None,
                    delegate(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
                    {
                        arrived.Signal();
                        if (!arrived.Wait(2000, token)) { throw new InvalidOperationException("Fixture worker admission timed out"); }
                        if (component == StressComponent.Cpu) { throw new InvalidOperationException("Fixture calculation mismatch"); }
                        if (!token.WaitHandle.WaitOne(2000)) { throw new InvalidOperationException("Fixture peer cancellation timed out"); }
                        token.ThrowIfCancellationRequested();
                    }).GetAwaiter().GetResult();
                Require(failed.Results.Single(result => result.Component == StressComponent.Cpu).Error.Contains("Fixture calculation mismatch"), "A failing component retains its actual failure");
                Require(failed.Results.Single(result => result.Component == StressComponent.Ram).Cancelled && failed.Cancelled, "A component failure cancels its concurrent peer");
                Require(failed.SafetyStopReason.Contains("Fixture calculation mismatch"), "The run explains why peers were stopped");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim())
            using (var cleanup = new ManualResetEventSlim())
            {
                Task<StressRunResult> pending = StressTestRunner.RunAsync(new StressOptions { Components = StressComponent.Cpu, DurationSeconds = 1 }, null, null, cancellation.Token,
                    delegate(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
                    {
                        started.Set();
                        if (!token.WaitHandle.WaitOne(2000)) { throw new InvalidOperationException("Fixture cancellation not requested"); }
                        if (!cleanup.Wait(2000)) { throw new InvalidOperationException("Fixture cleanup not acknowledged"); }
                        token.ThrowIfCancellationRequested();
                    });
                Require(started.Wait(2000), "The cancellation fixture worker starts");
                cancellation.Cancel(); Thread.Sleep(20);
                Require(!pending.IsCompleted, "Session completion waits for resource cleanup after cancellation");
                cleanup.Set();
                StressRunResult cancelled = pending.GetAwaiter().GetResult();
                Require(cancelled.Cancelled && cancelled.Results.Count == 1 && cancelled.Results[0].Cancelled && !cancelled.Results[0].Completed, "User cancellation never becomes successful completion");
            }
            calls = 0;
            var cpuOnly = new StressOptions { Components = StressComponent.Cpu, DurationSeconds = 1 };
            StressRunResult hot = StressTestRunner.RunAsync(cpuOnly, null, () => new Snapshot { Timestamp = DateTime.Now, CpuTemperature = 95, CpuTemperatureTrustedForStress = true }, CancellationToken.None, quick).GetAwaiter().GetResult();
            Require(calls == 0 && hot.Cancelled && hot.SafetyStopReason.Contains("95") && hot.Results[0].MaxTemperature == 95, "A fresh over-temperature sample blocks load and records the thermal stop");
            StressRunResult stale = StressTestRunner.RunAsync(cpuOnly, null, () => new Snapshot { Timestamp = DateTime.Now.AddMinutes(-1), CpuTemperature = 100, CpuTemperatureTrustedForStress = true }, CancellationToken.None, quick).GetAwaiter().GetResult();
            Require(calls == 1 && stale.Results[0].Completed && !stale.Results[0].MaxTemperature.HasValue, "Old telemetry cannot falsely trigger a thermal stop or become a new peak");
            StressRunResult missing = StressTestRunner.RunAsync(cpuOnly, null, () => new Snapshot { CpuTemperature = null }, CancellationToken.None, quick).GetAwaiter().GetResult();
            Require(missing.Results[0].Completed && !missing.Results[0].MaxTemperature.HasValue, "Missing sensors stay unavailable instead of becoming zero degrees");
            calls = 0;
            StressRunResult ramHot = StressTestRunner.RunAsync(new StressOptions { Components = StressComponent.Ram, DurationSeconds = 1 }, null,
                () => new Snapshot { CpuTemperature = 95, CpuTemperatureTrustedForStress = true }, CancellationToken.None, quick).GetAwaiter().GetResult();
            Require(calls == 0 && ramHot.Cancelled && ramHot.SafetyStopReason.Contains("RAM"), "RAM-only load still respects the CPU thermal cutoff");
            foreach (StressComponent component in new[] { StressComponent.Cpu, StressComponent.Ram })
            {
                calls = 0;
                var experimental = new Snapshot { CpuTemperature = 105, CpuTemperatureExperimental = true };
                StressRunResult untrusted = StressTestRunner.RunAsync(new StressOptions { Components = component, DurationSeconds = 1 }, null,
                    () => experimental, CancellationToken.None, quick).GetAwaiter().GetResult();
                Require(calls == 1 && untrusted.Results[0].Completed && !untrusted.Cancelled && untrusted.SafetyStopReason.Length == 0
                    && !untrusted.Results[0].MaxTemperature.HasValue, "Experimental-only CPU reading is not a thermal-stop sensor or trusted peak for " + component);
                Require(experimental.CpuTemperature == 105 && experimental.CpuTemperatureExperimental, "Excluding a stress reading preserves its clearly labelled dashboard value");
            }
            Require(!StressTestRunner.CpuTemperatureForStress(new Snapshot { CpuTemperature = 100 }).HasValue, "A value without explicit source trust cannot enable CPU thermal control");
            Require(!StressTestRunner.CpuTemperatureForStress(new Snapshot { CpuTemperature = 100, CpuTemperatureTrustedForStress = true, CpuTemperatureExperimental = true }).HasValue,
                "An experimental source cannot override its exclusion with an inconsistent trust bit");
            Require(!StressTestRunner.CpuTemperatureForStress(new Snapshot { CpuTemperature = double.PositiveInfinity, CpuTemperatureTrustedForStress = true }).HasValue,
                "Non-finite values remain unavailable despite a trust flag");
            string experimentalSummary = StressTestsWindow.CpuTemperatureSummary(new Snapshot { CpuTemperature = 72, CpuTemperatureExperimental = true });
            Require(experimentalSummary.Contains("72") && experimentalSummary.Contains("экспериментально") && experimentalSummary.Contains("исключено из автостопа")
                && experimentalSummary.Contains("автостоп CPU недоступен"), "Stress UI labels the experimental reading and missing CPU thermal protection");
            Require(StressTestsWindow.CpuTemperatureSummary(new Snapshot { CpuTemperature = 62, CpuTemperatureTrustedForStress = true }).IndexOf("недоступен", StringComparison.Ordinal) < 0,
                "Established trusted readings retain normal CPU stress status");
            VerifyFinalTelemetry();
            VerifyRamBackendSessionStates();
            VerifyReportCallbackFailureWaitsForCleanup();
        }

        private static void VerifyRamBackendSessionStates()
        {
            var options = new StressOptions { Components = StressComponent.Ram, DurationSeconds = 1 };
            // Exercise genuine RAM-backend failures through the session boundary,
            // with only 2 KB allocated and no live memory-status or timing probe.
            foreach (bool loseReserve in new[] { false, true })
            {
                double elapsed = 0;
                ulong available = ulong.MaxValue;
                int verified = 0;
                var progress = new List<StressProgress>();
                StressRunResult failed = StressTestRunner.RunAsync(options, progress.Add, null, CancellationToken.None,
                    delegate(StressComponent component, StressOptions selected, Action<StressProgress> report, CancellationToken token)
                    {
                        StressWorkloads.RunRamBuffers(new[] { new uint[256], new uint[256] }, selected.DurationSeconds, report,
                            token, delegate { return available; }, delegate { return elapsed; }, StressWorkloads.FillRamPattern,
                            delegate(uint[] buffer, int pattern, uint seed, CancellationToken cancellation)
                            {
                                StressWorkloads.VerifyRamPattern(buffer, pattern, seed, cancellation);
                                verified++;
                                if (loseReserve) { available = 0; }
                                else { elapsed = selected.DurationSeconds; }
                            });
                    }).GetAwaiter().GetResult();
                StressProgress ram = failed.Results.Single();
                string expected = loseReserve ? "резерв 1 GB" : "полный цикл из 3 шаблонов";
                Require(verified == 1 && ram.Component == StressComponent.Ram && ram.Error.Contains(expected)
                    && !ram.Completed && !ram.Cancelled && !ram.IsRunning && ram.Iterations == 0,
                    "A real RAM backend " + (loseReserve ? "runtime reserve failure" : "incomplete cycle") + " cannot become a completed session");
                Require(ram.BytesProcessed > 0 && failed.SafetyStopReason.Contains(ram.Error)
                    && !failed.Cancelled && progress.Last().Error == ram.Error && !progress.Last().Completed,
                    "RAM failure retains completed work and its cause in both the final callback and result");
                Require(ram.RamTargetBytes == 2048 && ram.RamAllocatedBytes == 2048 && ram.RamCoveredBytes == 0
                    && progress.Last().RamAllocatedBytes == 2048 && progress.Last().RamCoveredBytes == 0
                    && ram.Detail.Contains("3 шаблона"),
                    "RAM failure preserves actual allocation and incomplete coverage rather than reporting all memory tested");
            }

            foreach (bool cancelAfterCycle in new[] { false, true })
            using (var cancellation = new CancellationTokenSource())
            {
                double elapsed = 0;
                int verified = 0;
                StressProgress backendFinal = null;
                StressRunResult result = StressTestRunner.RunAsync(options, null, null, cancellation.Token,
                    delegate(StressComponent component, StressOptions selected, Action<StressProgress> report, CancellationToken token)
                    {
                        var buffers = new[] { new uint[256], new uint[256] };
                        StressWorkloads.RunRamBuffers(buffers, selected.DurationSeconds,
                            delegate(StressProgress update)
                            {
                                backendFinal = update.Copy();
                                report(update);
                                if (cancelAfterCycle && update.Iterations == 1) { cancellation.Cancel(); }
                            }, token, delegate { return ulong.MaxValue; }, delegate { return elapsed; }, StressWorkloads.FillRamPattern,
                            delegate(uint[] buffer, int pattern, uint seed, CancellationToken stop)
                            {
                                StressWorkloads.VerifyRamPattern(buffer, pattern, seed, stop);
                                verified++;
                                if (pattern == 2 && Object.ReferenceEquals(buffer, buffers[1])) { elapsed = selected.DurationSeconds; }
                            });
                    }).GetAwaiter().GetResult();
                StressProgress ram = result.Results.Single();
                Require(verified == 6 && backendFinal != null && backendFinal.Iterations == 1
                    && ram.Iterations == 1 && ram.BytesProcessed == 2L * 256 * sizeof(uint) * 6 && ram.WrittenBytes == 0,
                    "One complete RAM cycle means all three patterns across both buffers; runner preserves real counters");
                Require(ram.RamTargetBytes == 2048 && ram.RamAllocatedBytes == 2048 && ram.RamCoveredBytes == 2048
                    && ram.RamTargetBytes == backendFinal.RamTargetBytes && ram.RamAllocatedBytes == backendFinal.RamAllocatedBytes
                    && ram.RamCoveredBytes == backendFinal.RamCoveredBytes,
                    "Successful and cancelled RAM sessions retain target, allocation and verified-byte telemetry");
                if (cancelAfterCycle)
                {
                    Require(result.Cancelled && ram.Cancelled && !ram.Completed && ram.Error.Length == 0 && !ram.IsRunning
                        && ram.Detail.EndsWith(backendFinal.Detail, StringComparison.Ordinal),
                        "Cancellation after the final RAM telemetry still prevents successful session completion");
                }
                else
                {
                    Require(ram.Completed && !ram.Cancelled && !result.Cancelled && ram.Error.Length == 0 && ram.Percent == 100
                        && ram.Detail.EndsWith(backendFinal.Detail, StringComparison.Ordinal)
                        && ram.Detail.Contains("3 шаблона") && ram.Detail.Contains("часть RAM"),
                        "A verified full RAM cycle retains backend coverage and scope in its successful session result");
                }
            }
        }

        private static void VerifyFinalTelemetry()
        {
            var options = new StressOptions { Components = StressComponent.Ram | StressComponent.Gpu, DurationSeconds = 1, Gpu = Fixture().Gpus[0] };
            StressRunResult result = StressTestRunner.RunAsync(options, null, null, CancellationToken.None,
                delegate(StressComponent component, StressOptions selected, Action<StressProgress> report, CancellationToken token)
                {
                    report(new StressProgress
                    {
                        Detail = component == StressComponent.Ram ? "RAM: 512 MB · fixture verified pattern" : "GPU: 123.4 FPS · fixture D3D11",
                        Iterations = component == StressComponent.Ram ? 7 : 31,
                        BytesProcessed = component == StressComponent.Ram ? 512L * 1024 * 1024 : 0
                    });
                }).GetAwaiter().GetResult();
            StressProgress ramResult = result.Results.Single(value => value.Component == StressComponent.Ram);
            StressProgress gpuResult = result.Results.Single(value => value.Component == StressComponent.Gpu);
            Require(ramResult.Completed && ramResult.Detail.Contains("512 MB") && ramResult.Detail.Contains("fixture verified pattern")
                && ramResult.Iterations == 7 && ramResult.BytesProcessed == 512L * 1024 * 1024, "Completed RAM detail retains the tested allocation, verification context and counters");
            Require(gpuResult.Completed && gpuResult.Detail.Contains("123.4 FPS") && gpuResult.Detail.Contains("fixture D3D11") && gpuResult.Iterations == 31,
                "Completed GPU detail retains measured backend telemetry instead of replacing it with a generic success sentence");
        }

        private static void VerifyReportCallbackFailureWaitsForCleanup()
        {
            using (var started = new CountdownEvent(2))
            using (var cleanupStarted = new ManualResetEventSlim())
            using (var releaseCleanup = new ManualResetEventSlim())
            using (var rescueCancellation = new CancellationTokenSource())
            {
                int cleaned = 0;
                Task<StressRunResult> operation = StressTestRunner.RunAsync(new StressOptions { Components = StressComponent.Cpu | StressComponent.Ram, DurationSeconds = 1 },
                    delegate(StressProgress value)
                    {
                        if (value.Component == StressComponent.Cpu && value.Iterations > 0) { throw new InvalidOperationException("Fixture progress consumer failure"); }
                    }, null, rescueCancellation.Token,
                    delegate(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
                    {
                        try
                        {
                            started.Signal();
                            if (!started.Wait(2000, token)) { throw new InvalidOperationException("Fixture callback workers did not overlap"); }
                            if (component == StressComponent.Cpu) { report(new StressProgress { Iterations = 1 }); }
                            else
                            {
                                if (!token.WaitHandle.WaitOne(2000)) { throw new InvalidOperationException("Fixture callback failure did not cancel peer"); }
                                cleanupStarted.Set();
                                if (!releaseCleanup.Wait(2000)) { throw new InvalidOperationException("Fixture peer cleanup was not released"); }
                                token.ThrowIfCancellationRequested();
                            }
                        }
                        finally { Interlocked.Increment(ref cleaned); }
                    });
                try
                {
                    Require(cleanupStarted.Wait(2500), "A report callback failure cancels the other active worker");
                    Require(!operation.IsCompleted, "A callback exception cannot finish the session while another worker is cleaning up");
                }
                finally { releaseCleanup.Set(); rescueCancellation.Cancel(); }
                bool failed = false;
                try { operation.GetAwaiter().GetResult(); }
                catch (InvalidOperationException error) { failed = error.Message.Contains("Fixture progress consumer failure"); }
                Require(failed && Volatile.Read(ref cleaned) == 2, "The callback exception is observed only after every started worker has exited");
            }
        }

        private static void VerifyManualSelectionAndCancellation()
        {
            var backend = new ControlledBackend();
            var window = Create(backend);
            Require(backend.Calls == 0 && !window.IsRunning, "Opening stress settings never starts a workload");
            foreach (string field in new[] { "cpu", "ram", "ssd", "gpu" })
            {
                Require(Field<CheckBox>(window, field).IsChecked != true, "A component requires an explicit selection: " + field);
            }
            Button start = Field<Button>(window, "start"), stop = Field<Button>(window, "stop");
            Require(!start.IsEnabled && !stop.IsEnabled, "No selection means no start or stop action");
            Click(start);
            Require(backend.Calls == 0, "The start handler rejects an empty selection even when the event is raised directly");
            var duration = Field<ComboBox>(window, "duration");
            Require(duration.Items.Cast<object>().Select(Convert.ToInt32).SequenceEqual(new[] { 1, 5, 10, 15 }), "Duration choices are bounded minutes");
            Require(Convert.ToInt32(duration.SelectedItem) == 1, "The initial duration is the shortest offered interval");
            var ramSize = Field<ComboBox>(window, "ramSizeSelector");
            Require((int)((ComboBoxItem)ramSize.SelectedItem).Tag == 0 && !ramSize.IsEnabled,
                "RAM size defaults to automatic and stays disabled until RAM is selected");
            Require(System.Windows.Automation.AutomationProperties.GetAutomationId(ramSize) == "StressRamSize"
                && System.Windows.Automation.AutomationProperties.GetName(ramSize) == "Объём RAM для проверки"
                && ramSize.Items.Cast<ComboBoxItem>().Select(item => (int)item.Tag).SequenceEqual(new[] { 0, 2048, 4096, 8192, 16384, 32768, 65536 }),
                "Accessible RAM size choices carry automatic or the selected GB amount in MB");
            Field<CheckBox>(window, "cpu").IsChecked = true;
            Field<CheckBox>(window, "ram").IsChecked = true;
            Require(ramSize.IsEnabled, "Selecting RAM enables its size control");
            ramSize.SelectedItem = ramSize.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == 8192);
            Field<CheckBox>(window, "ram").IsChecked = false;
            Require(!ramSize.IsEnabled && (int)((ComboBoxItem)ramSize.SelectedItem).Tag == 8192,
                "Unchecking RAM disables its size control without discarding the selected amount");
            Field<CheckBox>(window, "ram").IsChecked = true;
            Require(ramSize.IsEnabled && (int)((ComboBoxItem)ramSize.SelectedItem).Tag == 8192,
                "Reselecting RAM restores the chosen size");
            Field<CheckBox>(window, "sequential").IsChecked = true;
            duration.SelectedItem = 5;
            Require(start.IsEnabled, "An explicit valid component selection enables start");
            Click(start);
            Until(() => backend.Calls == 1 && window.IsRunning, "The selected workload was admitted");
            Require(backend.Options.Components == (StressComponent.Cpu | StressComponent.Ram), "Only checked components are passed to the runner");
            Require(backend.Options.DurationSeconds == 300 && backend.Options.Sequential, "Chosen duration and scheduling mode reach the runner");
            Require(backend.Options.RamMiB == 8192, "The selected 8 GB RAM limit reaches the runner as 8192 MB");
            Require(!start.IsEnabled && stop.IsEnabled, "Only stop remains available during a run");
            foreach (string field in new[] { "cpu", "ram", "ssd", "gpu", "sequential", "duration", "ramSizeSelector", "diskSelector", "gpuSelector" })
            {
                Require(!Field<Control>(window, field).IsEnabled, "Running options cannot change: " + field);
            }
            Click(start);
            Require(backend.Calls == 1, "Repeated start cannot create a second runner");
            backend.Report(new StressProgress { Component = StressComponent.Cpu, IsRunning = true, Percent = 25, Detail = "Fixture CPU progress", ElapsedSeconds = 2 });
            Action<StressProgress> staleReport = backend.Report;
            Pump();
            Require(Field<ProgressBar>(window, "progress").Value >= 0 && Field<ProgressBar>(window, "progress").Value <= 100, "Running progress is bounded");
            Click(stop);
            Require(backend.Token.IsCancellationRequested, "Stop signals the active cancellation token");
            Require(window.IsRunning && !start.IsEnabled, "Cancellation does not release admission before cleanup finishes");
            Require(!ramSize.IsEnabled, "A stop request keeps RAM size locked until cleanup finishes");
            window.RequestStop();
            Require(window.IsRunning, "Repeated stop remains idempotent while cleanup is pending");
            backend.Complete(true, new StressProgress { Component = StressComponent.Cpu, Cancelled = true, Detail = "Fixture cancelled" });
            Until(() => !window.IsRunning, "Acknowledged cancellation releases the dialog");
            Require(start.IsEnabled && !stop.IsEnabled && backend.Completed.Count == 1, "Completion restores controls and records exactly one result");
            Require(backend.Completed[0].Cancelled, "Cancelled work is not reported as a successful run");
            Require(ramSize.IsEnabled && (int)((ComboBoxItem)ramSize.SelectedItem).Tag == 8192,
                "Acknowledged stop restores the RAM control and preserves the selected 8 GB limit");
            Click(start);
            Until(() => backend.Calls == 2 && window.IsRunning, "A cancelled run can be explicitly started again");
            Require(backend.Options.RamMiB == 8192, "Explicit retry keeps the selected RAM size");
            staleReport(new StressProgress { Component = StressComponent.Cpu, IsRunning = true, Percent = 99, Detail = "Fixture stale prior run" });
            Pump();
            Require(!Field<Dictionary<StressComponent, TextBlock>>(window, "statuses")[StressComponent.Cpu].Text.Contains("stale"), "Late progress from the previous run cannot overwrite the new run");
            backend.Complete(false, new StressProgress { Component = StressComponent.Cpu, Completed = true, Percent = 100, Detail = "Fixture retry completed" });
            Until(() => !window.IsRunning, "An explicit retry finishes");
            Require(backend.Completed.Count == 2, "Each admitted run reports one result");
            CloseIdle(window);
        }

        private static void VerifyPreviousResultDoesNotSelectOrStart()
        {
            var backend = new ControlledBackend();
            var previous = new StressRunResult
            {
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2), FinishedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                Options = new StressOptions { Components = StressComponent.Cpu, DurationSeconds = 60 },
                Results = new List<StressProgress> { new StressProgress { Component = StressComponent.Cpu, Completed = true, Percent = 100, Detail = "Fixture previous completed run" } }
            };
            var window = new StressTestsWindow(Fixture(), () => Fixture(), previous, result => backend.Completed.Add(result), backend.Run);
            Require(backend.Calls == 0 && !window.IsRunning && !Field<Button>(window, "start").IsEnabled, "Showing a previous result does not start or preselect a new stress run");
            Require(Field<Dictionary<StressComponent, TextBlock>>(window, "statuses")[StressComponent.Cpu].Text.Contains("Fixture previous"), "The previous result is shown when the dialog is reopened");
            foreach (string field in new[] { "cpu", "ram", "ssd", "gpu" }) { Require(Field<CheckBox>(window, field).IsChecked != true, "A previous run does not implicitly select " + field); }
            CloseIdle(window);
        }

        private static void VerifyCompletionAndComponentStates()
        {
            var backend = new ControlledBackend();
            var window = Create(backend);
            foreach (string field in new[] { "cpu", "ram", "ssd", "gpu" }) { Field<CheckBox>(window, field).IsChecked = true; }
            Field<ComboBox>(window, "duration").SelectedItem = 10;
            Field<CheckBox>(window, "sequential").IsChecked = false;
            Click(Field<Button>(window, "start"));
            Until(() => backend.Calls == 1, "A mixed run starts");
            Require(backend.Options.Components == (StressComponent.Cpu | StressComponent.Ram | StressComponent.Ssd | StressComponent.Gpu)
                && backend.Options.DurationSeconds == 600 && !backend.Options.Sequential, "All selected components and simultaneous mode reach the runner");
            Require(backend.Options.Disk != null && backend.Options.Gpu != null, "Selected SSD and GPU identities are carried into the run");
            Require(backend.Options.RamMiB == 0, "The default automatic RAM choice reaches the runner as zero");
            var outcomes = new[]
            {
                new StressProgress { Component = StressComponent.Cpu, Completed = true, Percent = 100, Detail = "Fixture CPU finished", Iterations = 12 },
                new StressProgress { Component = StressComponent.Ram, Error = "Fixture RAM mismatch", Detail = "Fixture RAM failed", Iterations = 3 },
                new StressProgress { Component = StressComponent.Ssd, Cancelled = true, Detail = "Fixture SSD stopped", WrittenBytes = 4096 },
                new StressProgress { Component = StressComponent.Gpu, Completed = true, Percent = 100, Detail = "Fixture GPU finished", MaxTemperature = 62 }
            };
            foreach (StressProgress outcome in outcomes) { backend.Report(outcome); }
            Pump();
            backend.Complete(false, outcomes);
            Until(() => !window.IsRunning, "Mixed component outcomes finish");
            Require(backend.Completed.Count == 1 && backend.Completed[0].Results.Count == 4, "Each component retains a separate result");
            Require(backend.Completed[0].Results.Any(result => result.Component == StressComponent.Ram && result.Error == "Fixture RAM mismatch" && !result.Completed), "A RAM mismatch cannot become a completed pass");
            Require(backend.Completed[0].Results.Any(result => result.Component == StressComponent.Ssd && result.Cancelled && !result.Completed), "A stopped SSD result remains cancelled");
            string text = String.Join("\n", Field<Dictionary<StressComponent, TextBlock>>(window, "statuses").Values.Select(block => block.Text));
            Require(text.Contains("Fixture CPU finished") && text.Contains("Fixture GPU finished"), "Completed component details are visible independently");
            Require(text.Contains("Fixture RAM") && text.Contains("Fixture SSD"), "Errors and cancellation details remain visible alongside completed results");
            CloseIdle(window);
        }

        private static void VerifyCloseWaitsForCleanup()
        {
            var backend = new ControlledBackend();
            var window = Create(backend);
            Field<CheckBox>(window, "cpu").IsChecked = true;
            Click(Field<Button>(window, "start"));
            Until(() => backend.Calls == 1 && window.IsRunning, "Close fixture starts");
            bool closed = false;
            window.Closed += delegate { closed = true; };
            window.Close();
            Pump();
            Require(!closed && window.IsRunning && backend.Token.IsCancellationRequested, "Closing requests cancellation and waits for the owned workload");
            Require(!Field<Button>(window, "start").IsEnabled, "A closing dialog cannot admit another run");
            backend.Complete(true, new StressProgress { Component = StressComponent.Cpu, Cancelled = true, Detail = "Fixture cleanup acknowledged" });
            Until(() => closed, "The dialog closes once cleanup is acknowledged");
            Require(backend.Completed.Count == 1, "Closing persists the final cancelled run once");
        }

        private static void VerifyLayout(Size size, string output)
        {
            var backend = new ControlledBackend();
            var window = Create(backend);
            FrameworkElement content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content, Width = size.Width, Height = size.Height };
            TextElementPresentation(host, window);
            host.Measure(size); host.Arrange(new Rect(new Point(), size)); host.UpdateLayout();
            var scroll = content as ScrollViewer ?? Descendants(content).OfType<ScrollViewer>().FirstOrDefault();
            Require(scroll != null && scroll.ViewportHeight > 0, "The stress dialog has a bounded scroll viewport at " + size);
            Require(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ExtentWidth <= scroll.ViewportWidth + 1, "Stress controls do not require horizontal scrolling at " + size);
            foreach (bool bottom in new[] { false, true })
            {
                if (bottom) { scroll.ScrollToBottom(); } else { scroll.ScrollToTop(); }
                host.UpdateLayout();
                foreach (string name in new[] { "start", "stop" })
                {
                    Button button = Field<Button>(window, name);
                    Rect bounds = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Top >= -1 && bounds.Bottom <= host.ActualHeight + 1 && bounds.Left >= -1 && bounds.Right <= host.ActualWidth + 1,
                        "The pinned " + name + " action is reachable at " + size + " with body at " + (bottom ? "bottom" : "top"));
                }
            }
            scroll.ScrollToTop(); host.UpdateLayout();
            if (output != null) { SaveImage(host, size, output); }
            Require(backend.Calls == 0, "Layout checks never start hardware workloads");
            CloseIdle(window);
        }

        private static void VerifyDashboardAction(string outputDirectory)
        {
            foreach (Size size in new[] { new Size(960, 680), new Size(640, 440), new Size(832, 500) })
            {
                Window window = Program.LoadWindow();
                var dashboard = new Dashboard(window);
                var stress = (Button)window.FindName("StressTestButton");
                int requested = 0; dashboard.StressTestsRequested += delegate { requested++; };
                Require(!stress.IsEnabled, "Stress action waits for an inventory snapshot");
                Snapshot snapshot = Fixture();
                snapshot.ComputerManufacturer = "HP";
                snapshot.ComputerModel = "ZBook Firefly 14 inch G9 Mobile Workstation PC";
                dashboard.Render(snapshot);
                Require(stress.IsEnabled && requested == 0, "An inventory enables manual stress without launching it");
                Click(stress);
                Require(requested == 1, "The header stress action routes exactly one manual request");
                dashboard.SetChecksRunning(true);
                Require(!stress.IsEnabled, "A running benchmark prevents concurrent stress admission");
                dashboard.SetChecksRunning(false); dashboard.SetInventoryRefreshing(true);
                Require(!stress.IsEnabled, "Inventory refresh prevents admission with changing targets");
                dashboard.SetInventoryRefreshing(false);
                Require(stress.IsEnabled, "Stress admission returns after the existing operation finishes");

                FrameworkElement content = (FrameworkElement)window.Content; window.Content = null;
                var host = new Border { Child = content, Width = size.Width, Height = size.Height, Resources = window.Resources };
                TextElementPresentation(host, window);
                host.Measure(size); host.Arrange(new Rect(new Point(), size)); host.UpdateLayout();
                var model = (FrameworkElement)window.FindName("ComputerModel");
                Rect previous = model.TransformToAncestor(host).TransformBounds(new Rect(model.RenderSize));
                foreach (string name in new[] { "StressTestButton", "SaveReportButton", "RefreshButton" })
                {
                    var action = (Button)window.FindName(name);
                    Rect bounds = action.TransformToAncestor(host).TransformBounds(new Rect(action.RenderSize));
                    Require(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= size.Width + 1 && bounds.Bottom <= size.Height + 1,
                        "Header action " + name + " stays inside the supported viewport " + size);
                    Require(bounds.Left >= previous.Right - 1, "Header action " + name + " does not overlap the preceding model/action at " + size);
                    foreach (TextBlock text in Descendants(action).OfType<TextBlock>().Where(item => !String.IsNullOrWhiteSpace(item.Text)))
                    {
                        var measured = new FormattedText(text.Text, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), text.FlowDirection,
                            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                            null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
                        Require(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1.5 && measured.Height <= text.ActualHeight + 1.5,
                            "Header action label " + name + " remains complete at " + size);
                    }
                    Point center = action.TranslatePoint(new Point(action.ActualWidth / 2, action.ActualHeight / 2), host);
                    HitTestResult hit = VisualTreeHelper.HitTest(host, center);
                    Require(hit != null && IsWithin(hit.VisualHit, action), "Header hit target belongs to " + name + " at " + size);
                    previous = bounds;
                }
                if (outputDirectory != null) { SaveImage(host, size, Path.Combine(outputDirectory, "dashboard-stress-" + size.Width + "x" + size.Height + ".png")); }
                window.Close();
            }
        }

        private static void VerifyStressReportSeparation()
        {
            Window window = Program.LoadWindow();
            var dashboard = new Dashboard(window);
            Snapshot snapshot = Fixture(); dashboard.Render(snapshot);
            DateTime measured = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            dashboard.RenderBenchmark(new BenchmarkProgress
            {
                DiskIndex = snapshot.Disks[0].Index, ReadBytesPerSecond = 1234000000, WriteBytesPerSecond = 987000000,
                TestedAtUtc = measured, FromHistory = true, Stage = "Fixture saved speed", Percent = 100
            });
            dashboard.RenderGpuBenchmark(new GpuBenchmarkProgress
            {
                AdapterIndex = snapshot.Gpus[0].AdapterIndex, FramesPerSecond = 123.4, DurationSeconds = 15,
                TestedAtUtc = measured, FromHistory = true, Completed = true, Stage = "Fixture saved GPU score"
            });
            var stress = new StressRunResult
            {
                StartedAtUtc = measured.AddDays(1), FinishedAtUtc = measured.AddDays(1).AddSeconds(12),
                Options = new StressOptions { Components = StressComponent.Ram | StressComponent.Ssd | StressComponent.Gpu, DurationSeconds = 60,
                    RamMiB = 8192, Disk = snapshot.Disks[0], Gpu = snapshot.Gpus[0] },
                Cancelled = true, SafetyStopReason = "Fixture stress-only thermal limit",
                Results = new List<StressProgress>
                {
                    new StressProgress { Component = StressComponent.Ssd, Cancelled = true, Detail = "Fixture stress SSD stopped", WrittenBytes = 4096 },
                    new StressProgress { Component = StressComponent.Gpu, Cancelled = true, Detail = "Fixture stress GPU stopped", MaxTemperature = 90 },
                    new StressProgress { Component = StressComponent.Ram, Cancelled = true, Detail = "Fixture partially covered RAM",
                        RamTargetBytes = 4L * 1024 * 1024 * 1024, RamAllocatedBytes = 4L * 1024 * 1024 * 1024,
                        RamCoveredBytes = 2L * 1024 * 1024 * 1024 }
                }
            };
            dashboard.SetStressResult(stress);
            var exported = new JavaScriptSerializer().Deserialize<DiagnosticReport>(dashboard.CreateReport());
            Require(exported.StressTests != null && exported.StressTests.Cancelled && exported.StressTests.Results.Count == 3
                && exported.StressTests.SafetyStopReason == stress.SafetyStopReason, "The exported report contains stress outcomes and stop reasons in their own section");
            Require(exported.StressTests.Options.DurationSeconds == 60 && exported.StressTests.Options.Disk.StorageIdentity == "fixture-disk"
                && exported.StressTests.Options.Gpu.AdapterLuidLow == 42, "Stress export retains chosen duration and actual target identities");
            Require(exported.StressTests.Results[0].WrittenBytes == 4096 && exported.StressTests.Results[1].MaxTemperature == 90
                && exported.StressTests.Results.All(result => result.Cancelled && !result.Completed), "Stress cancellation, counters and temperatures stay truthful after JSON round trip");
            StressProgress exportedRam = exported.StressTests.Results.Single(value => value.Component == StressComponent.Ram);
            Require(exported.StressTests.Options.RamMiB == 8192 && exportedRam.RamTargetBytes == 4L * 1024 * 1024 * 1024
                && exportedRam.RamAllocatedBytes == 4L * 1024 * 1024 * 1024 && exportedRam.RamCoveredBytes == 2L * 1024 * 1024 * 1024,
                "JSON keeps the selected RAM limit separate from actual allocation and partial coverage without a 32-bit overflow");
            Require(exported.DiskTests.Count == 1 && exported.DiskTests[0].ReadBytesPerSecond == 1234000000
                && exported.DiskTests[0].WriteBytesPerSecond == 987000000 && exported.DiskTests[0].TestedAtUtc == measured && exported.DiskTests[0].FromHistory,
                "A stress run cannot replace the saved SSD speed measurement or its original date");
            Require(exported.GpuTests.Count == 1 && exported.GpuTests[0].FramesPerSecond == 123.4 && exported.GpuTests[0].DurationSeconds == 15
                && exported.GpuTests[0].TestedAtUtc == measured && exported.GpuTests[0].Completed && exported.GpuTests[0].FromHistory,
                "A stress run cannot replace the saved GPU score or its original date");
            Require(exported.StressTestsNotice.Contains("RAM") && exported.StressTestsNotice.Contains("VRAM"), "The report preserves the scope limitations of the stress checks");
            window.Close();
        }

        public static async Task<IList<string>> RunLive(string outputDirectory)
        {
            var evidence = new List<string>();
            VerifyDashboardAction(outputDirectory);
            evidence.Add("Dashboard Stress action: manual admission and nonoverlapping labels/hit targets at 960x680, 640x440 and 832x500.");
            var backend = new ControlledBackend();
            StressTestsWindow window = Create(backend);
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.WorkArea.Left + 24; window.Top = SystemParameters.WorkArea.Top + 24;
            try
            {
                window.Show(); await Task.Delay(100); window.UpdateLayout();
                Require(window.IsVisible && backend.Calls == 0, "A real WPF dialog opens without starting workloads");
                evidence.Add("Real WPF window opens; no automatic workload.");
                foreach (Size size in new[] { new Size(480, 360), new Size(640, 440), new Size(840, 640) })
                {
                    window.MinWidth = Math.Min(window.MinWidth, size.Width); window.MinHeight = Math.Min(window.MinHeight, size.Height);
                    window.Width = size.Width; window.Height = size.Height; await Task.Delay(35); window.UpdateLayout();
                    var scroll = window.Content as ScrollViewer ?? Descendants(window).OfType<ScrollViewer>().First();
                    scroll.ScrollToBottom(); await Task.Delay(20); window.UpdateLayout();
                    Button start = Field<Button>(window, "start");
                    Point center = start.TranslatePoint(new Point(start.ActualWidth / 2, start.ActualHeight / 2), window);
                    DependencyObject hit = VisualTreeHelper.HitTest(window, center).VisualHit;
                    Require(IsWithin(hit, start), "The real start hit target remains reachable at " + size);
                    SaveImage(window, new Size(window.ActualWidth, window.ActualHeight), Path.Combine(outputDirectory, "stress-" + size.Width + "x" + size.Height + ".png"));
                    evidence.Add("Real HWND resize and Start hit target: " + size);
                }
                Field<CheckBox>(window, "cpu").IsChecked = true;
                Field<ComboBox>(window, "duration").SelectedItem = 1;
                Click(Field<Button>(window, "start")); await Task.Delay(25);
                Require(window.IsRunning && backend.Calls == 1, "The real window admits the fake backend only on Start");
                backend.Report(new StressProgress { Component = StressComponent.Cpu, Percent = 30, IsRunning = true, Detail = "E2E controlled backend; no physical stress", ElapsedSeconds = 18 });
                await Task.Delay(25);
                evidence.Add("Explicit Start routes selected components and displays asynchronous progress.");
                window.RequestStop(); await Task.Delay(25);
                Require(backend.Token.IsCancellationRequested && window.IsRunning, "Stop waits for backend cleanup acknowledgement");
                evidence.Add("Stop signals cancellation while retaining run ownership.");
                bool closed = false; window.Closed += delegate { closed = true; };
                window.Close(); await Task.Delay(25);
                Require(!closed, "Close remains pending while workload cleanup is pending");
                backend.Complete(true, new StressProgress { Component = StressComponent.Cpu, Cancelled = true, Detail = "E2E cleanup acknowledged" });
                for (int i = 0; i < 100 && !closed; i++) { await Task.Delay(10); }
                Require(closed && backend.Completed.Count == 1, "Cleanup acknowledges closure and reports one result");
                evidence.Add("Close waits for cleanup, records cancellation and closes without reentrancy.");
                window = null;
                return evidence;
            }
            finally
            {
                if (window != null)
                {
                    backend.Complete(true, new StressProgress { Component = StressComponent.Cpu, Cancelled = true });
                    window.RequestStop();
                    Until(() => !window.IsRunning, "Fixture cleanup completes after an integration failure");
                    window.Close();
                }
            }
        }

        private static StressTestsWindow Create(ControlledBackend backend)
        {
            Snapshot snapshot = Fixture();
            return new StressTestsWindow(snapshot, () => snapshot, null, result => backend.Completed.Add(result), backend.Run);
        }

        private static Snapshot Fixture()
        {
            var snapshot = new Snapshot { CpuName = "Fixture CPU", CpuThreads = 4, RamTotalBytes = 8UL * 1024 * 1024 * 1024, RamUsedBytes = 2UL * 1024 * 1024 * 1024 };
            snapshot.Disks.Add(new DiskSnapshot { Index = 0, Name = "Fixture SSD", MediaType = "SSD", SizeBytes = 256UL * 1024 * 1024 * 1024, TestVolumeRoot = @"X:\", StorageIdentity = "fixture-disk" });
            snapshot.Gpus.Add(new GpuSnapshot { Name = "Fixture GPU", AdapterIndex = 0, AdapterLuidLow = 42 });
            return snapshot;
        }

        private sealed class ControlledBackend
        {
            internal int Calls;
            internal StressOptions Options;
            internal CancellationToken Token;
            internal Action<StressProgress> Report = delegate { };
            internal readonly List<StressRunResult> Completed = new List<StressRunResult>();
            private TaskCompletionSource<StressRunResult> completion = new TaskCompletionSource<StressRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task<StressRunResult> Run(StressOptions options, Action<StressProgress> report, CancellationToken cancellation)
            {
                if (Calls > 0) { completion = new TaskCompletionSource<StressRunResult>(TaskCreationOptions.RunContinuationsAsynchronously); }
                Calls++; Options = options; Report = report; Token = cancellation;
                return completion.Task;
            }
            internal void Complete(bool cancelled, params StressProgress[] results)
            {
                completion.TrySetResult(new StressRunResult { StartedAtUtc = DateTime.UtcNow.AddSeconds(-2), FinishedAtUtc = DateTime.UtcNow, Options = Options, Cancelled = cancelled, Results = results.ToList() });
            }
        }

        private static void TextElementPresentation(Border host, Window window)
        {
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
            System.Windows.Documents.TextElement.SetForeground(host, window.Foreground);
            host.Background = window.Background;
        }
        private static void SaveImage(Visual visual, Size size, string path)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) { encoder.Save(stream); }
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) { yield break; }
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, i))) { yield return child; }
            }
        }
        private static string TextOf(DependencyObject root) { return String.Join("\n", Descendants(root).OfType<TextBlock>().Select(text => text.Text)); }
        private static bool IsWithin(DependencyObject child, DependencyObject parent)
        {
            while (child != null) { if (ReferenceEquals(child, parent)) { return true; } child = VisualTreeHelper.GetParent(child); }
            return false;
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void CloseIdle(Window window)
        {
            bool closed = false; window.Closed += delegate { closed = true; }; window.Close();
            Until(() => closed, "An idle stress window closes without a reentrant Close exception");
        }
        private static void Until(Func<bool> condition, string message)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (!condition() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(1); }
            Require(condition(), message);
        }
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
