using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class GpuStartupTests
    {
        public static void Run()
        {
            Task checks = Task.Run(async delegate
            {
                await SequentialStages();
                await StopDuringGpu();
                await FailedGpu();
            });
            if (Task.WhenAny(checks, Task.Delay(5000)).GetAwaiter().GetResult() != checks)
            {
                throw new TimeoutException("GPU startup ordering checks timed out.");
            }
            checks.GetAwaiter().GetResult();
        }

        private static async Task SequentialStages()
        {
            var stages = new List<string>();
            var displayed = new TaskCompletionSource<bool>();
            var disks = new TaskCompletionSource<bool>();
            var gpu = new TaskCompletionSource<bool>();
            var enteredDisks = new TaskCompletionSource<bool>();
            var enteredGpu = new TaskCompletionSource<bool>();
            int ports = 0;
            Task sequence = StartupSequence.Run(
                delegate { stages.Add("display"); return displayed.Task; },
                delegate { stages.Add("disk"); enteredDisks.SetResult(true); return disks.Task; },
                delegate { stages.Add("gpu"); enteredGpu.SetResult(true); return gpu.Task; },
                delegate { stages.Add("ports"); ports++; return Task.FromResult(true); },
                delegate { }, CancellationToken.None);
            Require(stages.Count == 1, "Disk writes must wait until information is displayed.");
            displayed.SetResult(true);
            await enteredDisks.Task;
            Require(stages.Count == 2, "GPU must not overlap disk work.");
            disks.SetResult(true);
            await enteredGpu.Task;
            Require(ports == 0 && !sequence.IsCompleted, "Port stage waits until GPU releases its resources.");
            gpu.SetResult(true);
            await sequence;
            Require(String.Join(",", stages) == "display,disk,gpu,ports", "All checks execute in the intended order.");
        }

        private static async Task StopDuringGpu()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var gpu = new TaskCompletionSource<bool>();
                int ports = 0;
                var reports = new List<string>();
                Task sequence = StartupSequence.Run(
                    delegate { return Task.FromResult(true); },
                    delegate { return Task.FromResult(true); },
                    delegate { return gpu.Task; },
                    delegate { ports++; return Task.FromResult(true); }, reports.Add, cancellation.Token);
                cancellation.Cancel();
                bool cancelled = false;
                try { await sequence; } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && ports == 0, "Stop during GPU prevents subsequent work.");
                Require(!reports.Exists(value => value.StartsWith("Проверки завершены", StringComparison.Ordinal)), "Stopped run must not report completion.");
                gpu.SetResult(true);
            }
        }

        private static async Task FailedGpu()
        {
            int ports = 0;
            var failed = new TaskCompletionSource<bool>();
            failed.SetException(new InvalidOperationException("GPU worker failed"));
            bool propagated = false;
            try
            {
                await StartupSequence.Run(
                    delegate { return Task.FromResult(true); },
                    delegate { return Task.FromResult(true); },
                    delegate { return failed.Task; },
                    delegate { ports++; return Task.FromResult(true); }, delegate { }, CancellationToken.None);
            }
            catch (InvalidOperationException exception) { propagated = exception.Message == "GPU worker failed"; }
            Require(propagated && ports == 0, "Unexpected GPU worker failure is observed by the orchestration layer.");
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
