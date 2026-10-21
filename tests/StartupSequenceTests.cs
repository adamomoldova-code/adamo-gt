using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class StartupSequenceTests
    {
        public static void Run()
        {
            Task tests = Task.Run(async delegate
            {
                await StagesWaitForPresentationAndExistingDiskTask();
                await CancellationBeforePresentationStartsNothing();
                await CancellationFromStatusPreventsNextStage(1);
                await CancellationFromStatusPreventsNextStage(2);
                await CancellationWhileWaitingStopsFollowingStages(0);
                await CancellationWhileWaitingStopsFollowingStages(1);
                await CancellationWhileWaitingStopsFollowingStages(2);
                await FailedDiskTaskPropagatesAndPreventsPorts();
                await OfflineStartupKeepsHardwareChecks();
            });
            if (Task.WhenAny(tests, Task.Delay(5000)).GetAwaiter().GetResult() != tests)
            {
                throw new TimeoutException("Startup sequence tests did not finish within five seconds.");
            }
            tests.GetAwaiter().GetResult();
        }

        private static async Task OfflineStartupKeepsHardwareChecks()
        {
            foreach (bool runBenchmarks in new[] { true, false })
            {
                var stages = new List<string>();
                var reports = new List<string>();
                await StartupSequence.Run(
                    delegate { stages.Add("present"); return Task.FromResult(true); },
                    delegate { stages.Add("disk"); return Task.FromResult(true); },
                    delegate { stages.Add("gpu"); return Task.FromResult(true); },
                    null, reports.Add, CancellationToken.None, runBenchmarks);
                Equal(runBenchmarks ? 3 : 1, stages.Count, "Offline startup preserves the first-run/history decision");
                True(stages[0] == "present", "Hardware information is shown before offline tests");
                if (runBenchmarks) { True(stages[1] == "disk" && stages[2] == "gpu", "Offline checks stay sequential"); }
                True(reports.TrueForAll(status => !status.Contains("интернет")), "Offline startup never advertises a network request");
            }
        }

        public static void UiContext()
        {
            True(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "WPF context test requires an STA thread");
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher);
            Exception failure = null;
            bool completed = false;
            int diskRequests = 0;
            int portRequests = 0;
            int reports = 0;
            using (var cancellation = new CancellationTokenSource())
            {
                timeout.Interval = TimeSpan.FromSeconds(5);
                EventHandler elapsed = delegate
                {
                    failure = new TimeoutException("Startup sequence did not resume on the WPF dispatcher within five seconds.");
                    cancellation.Cancel();
                    frame.Continue = false;
                };
                timeout.Tick += elapsed;
                dispatcher.BeginInvoke(new Action(async delegate
                {
                    try
                    {
                        await StartupSequence.Run(
                            delegate
                            {
                                True(dispatcher.CheckAccess(), "Presentation starts on the WPF dispatcher");
                                return CompleteFromThreadPoolAfterDispatcherYield(dispatcher);
                            },
                            delegate
                            {
                                True(dispatcher.CheckAccess(), "Disk factory retains the WPF context after presentation");
                                diskRequests++;
                                return CompleteFromThreadPoolAfterDispatcherYield(dispatcher);
                            },
                            delegate
                            {
                                True(dispatcher.CheckAccess(), "Port factory retains the WPF context after disk completion");
                                portRequests++;
                                return CompleteFromThreadPoolAfterDispatcherYield(dispatcher);
                            },
                            delegate(string status)
                            {
                                True(dispatcher.CheckAccess(), "Status updates retain the WPF context");
                                reports++;
                            }, cancellation.Token);
                        completed = true;
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { frame.Continue = false; }
                }));
                timeout.Start();
                try { Dispatcher.PushFrame(frame); }
                finally
                {
                    timeout.Stop();
                    timeout.Tick -= elapsed;
                }
            }
            if (failure != null) { throw failure; }
            True(completed, "All startup stages completed while pumping the WPF dispatcher");
            Equal(1, diskRequests, "WPF disk factory is called once");
            Equal(1, portRequests, "WPF port factory is called once");
            Equal(3, reports, "All stage statuses are delivered on the dispatcher");
        }

        private static Task CompleteFromThreadPoolAfterDispatcherYield(Dispatcher dispatcher)
        {
            var completed = new TaskCompletionSource<bool>();
            dispatcher.BeginInvoke(new Action(delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (dispatcher.CheckAccess())
                    {
                        completed.TrySetException(new InvalidOperationException("Fixture completion must run outside the WPF dispatcher."));
                    }
                    else { completed.TrySetResult(true); }
                });
            }), DispatcherPriority.Background);
            return completed.Task;
        }

        private static async Task StagesWaitForPresentationAndExistingDiskTask()
        {
            var presented = new TaskCompletionSource<bool>();
            var diskFinished = new TaskCompletionSource<bool>();
            var portsFinished = new TaskCompletionSource<bool>();
            var diskEntered = new TaskCompletionSource<bool>();
            var portsEntered = new TaskCompletionSource<bool>();
            Task existingDiskTask = diskFinished.Task;
            int presentations = 0;
            int diskRequests = 0;
            int portRequests = 0;
            var reports = new List<string>();

            Task sequence = StartupSequence.Run(
                delegate { presentations++; return presented.Task; },
                delegate { diskRequests++; diskEntered.TrySetResult(true); return existingDiskTask; },
                delegate { portRequests++; portsEntered.TrySetResult(true); return portsFinished.Task; },
                reports.Add, CancellationToken.None);

            Equal(1, presentations, "Presentation is requested once");
            Equal(0, diskRequests, "Disk test waits for the presentation to finish");
            Equal(0, portRequests, "Port lookup cannot start before presentation");
            Equal(0, reports.Count, "No completed-stage message before presentation");

            presented.SetResult(true);
            await diskEntered.Task;
            Equal(1, diskRequests, "Existing disk task is requested once");
            Equal(0, portRequests, "Port lookup waits for the existing disk task");
            Equal(1, reports.Count, "Only presentation is complete while disks run");
            True(!sequence.IsCompleted, "Startup remains pending while an existing disk task runs");
            True(!HasCompletion(reports), "Completion cannot be reported while disks run");

            diskFinished.SetResult(true);
            await portsEntered.Task;
            Equal(1, diskRequests, "Joining the disk task does not request a second run");
            Equal(1, portRequests, "Port lookup starts once after disks finish");
            Equal(2, reports.Count, "Port lookup has its own pending stage message");
            True(!sequence.IsCompleted, "Startup remains pending until port lookup finishes");
            True(!HasCompletion(reports), "Completion cannot be reported while ports are pending");

            portsFinished.SetResult(true);
            await sequence;
            Equal(1, presentations, "Presentation is not repeated");
            Equal(1, diskRequests, "Disk task is joined exactly once");
            Equal(1, portRequests, "Port lookup is not repeated");
            Equal(3, reports.Count, "Each completed stage produces one status update");
            True(HasCompletion(reports), "Completion is reported after the final task");
        }

        private static async Task CancellationBeforePresentationStartsNothing()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                int requests = 0;
                int reports = 0;
                Func<Task> stage = delegate { requests++; return Task.FromResult(true); };
                Task sequence = StartupSequence.Run(stage, stage, stage, delegate { reports++; }, cancellation.Token);
                await ExpectCancellation(sequence);
                Equal(0, requests, "Pre-cancelled startup does not invoke any stage");
                Equal(0, reports, "Pre-cancelled startup does not announce completion");
            }
        }

        private static async Task CancellationWhileWaitingStopsFollowingStages(int waitingStage)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var presented = new TaskCompletionSource<bool>();
                var diskFinished = new TaskCompletionSource<bool>();
                var portsFinished = new TaskCompletionSource<bool>();
                var diskEntered = new TaskCompletionSource<bool>();
                var portsEntered = new TaskCompletionSource<bool>();
                var reports = new List<string>();
                int diskRequests = 0;
                int portRequests = 0;
                Task sequence = StartupSequence.Run(
                    delegate { return presented.Task; },
                    delegate { diskRequests++; diskEntered.TrySetResult(true); return diskFinished.Task; },
                    delegate { portRequests++; portsEntered.TrySetResult(true); return portsFinished.Task; },
                    reports.Add, cancellation.Token);

                if (waitingStage >= 1)
                {
                    presented.SetResult(true);
                    await diskEntered.Task;
                }
                if (waitingStage >= 2)
                {
                    diskFinished.SetResult(true);
                    await portsEntered.Task;
                }

                cancellation.Cancel();
                await ExpectCancellation(sequence);
                Equal(waitingStage >= 1 ? 1 : 0, diskRequests, "Cancellation blocks the next disk stage");
                Equal(waitingStage >= 2 ? 1 : 0, portRequests, "Cancellation blocks the next port stage");
                Equal(waitingStage, reports.Count, "Cancellation produces no later-stage status");
                True(!HasCompletion(reports), "Cancelled startup is never reported as complete");

                presented.TrySetResult(true);
                diskFinished.TrySetResult(true);
                portsFinished.TrySetResult(true);
                Equal(waitingStage >= 1 ? 1 : 0, diskRequests, "Late presentation completion cannot restart cancelled startup");
                Equal(waitingStage >= 2 ? 1 : 0, portRequests, "Late disk completion cannot restart cancelled startup");
                True(sequence.IsCanceled, "Cancellation is preserved after late stage completion");
            }
        }

        private static async Task CancellationFromStatusPreventsNextStage(int cancelAtReport)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                int presentations = 0;
                int diskRequests = 0;
                int portRequests = 0;
                int reports = 0;
                Task sequence = StartupSequence.Run(
                    delegate { presentations++; return Task.FromResult(true); },
                    delegate { diskRequests++; return Task.FromResult(true); },
                    delegate { portRequests++; return Task.FromResult(true); },
                    delegate(string status)
                    {
                        reports++;
                        if (reports == cancelAtReport) { cancellation.Cancel(); }
                    }, cancellation.Token);
                await ExpectCancellation(sequence);
                Equal(1, presentations, "Presentation completes before cancellation from status");
                Equal(cancelAtReport == 2 ? 1 : 0, diskRequests, "Cancellation in presentation status prevents the disk factory");
                Equal(0, portRequests, "Cancellation in disk status prevents the port factory");
                Equal(cancelAtReport, reports, "Cancellation in status prevents later reports");
            }
        }

        private static async Task FailedDiskTaskPropagatesAndPreventsPorts()
        {
            var diskFinished = new TaskCompletionSource<bool>();
            var expected = new InvalidOperationException("Fixture disk failure");
            var reports = new List<string>();
            int portRequests = 0;
            Task sequence = StartupSequence.Run(
                delegate { return Task.FromResult(true); },
                delegate { return diskFinished.Task; },
                delegate { portRequests++; return Task.FromResult(true); },
                reports.Add, CancellationToken.None);

            diskFinished.SetException(expected);
            Exception actual = null;
            try { await sequence; }
            catch (Exception exception) { actual = exception; }

            True(object.ReferenceEquals(expected, actual), "A failed stage preserves the original exception");
            Equal(0, portRequests, "A failed disk task prevents port lookup");
            Equal(1, reports.Count, "A failed disk task cannot announce successful disk completion");
            True(!HasCompletion(reports), "A failed stage cannot announce startup completion");
        }

        private static async Task ExpectCancellation(Task task)
        {
            bool cancelled = false;
            try { await task; }
            catch (OperationCanceledException) { cancelled = true; }
            True(cancelled && task.IsCanceled, "Startup propagates cancellation promptly");
        }

        private static bool HasCompletion(List<string> reports)
        {
            return reports.Exists(message => message.IndexOf("Проверки завершены", StringComparison.Ordinal) >= 0);
        }

        private static void Equal(int expected, int actual, string message)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual + ".");
            }
        }

        private static void True(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
