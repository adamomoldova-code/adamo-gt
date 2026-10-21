using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class ShutdownCoordinatorTests
    {
        public static int Main(string[] args)
        {
            try
            {
                CompletedWorkers();
                PendingWorkersComplete();
                DeadlineReturns();
                Console.WriteLine("PASS: bounded shutdown (3 groups)");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void CompletedWorkers()
        {
            var failed = new TaskCompletionSource<bool>();
            failed.SetException(new[] { new InvalidOperationException("sensor failed"), new InvalidOperationException("second failure") });
            var cancelled = new TaskCompletionSource<bool>();
            cancelled.SetCanceled();
            Require(ShutdownCoordinator.WaitAsync(new Task[] { Task.FromResult(true), failed.Task, cancelled.Task, null }, TimeSpan.Zero).GetAwaiter().GetResult(),
                "Already-settled success/fault/cancel workers must permit immediate shutdown without throwing.");
            Require(ShutdownCoordinator.WaitAsync(new Task[0], TimeSpan.Zero).GetAwaiter().GetResult(),
                "An empty worker list must not delay closing.");
        }

        public static void PendingWorkersComplete()
        {
            var first = new TaskCompletionSource<bool>();
            var second = new TaskCompletionSource<bool>();
            Task<bool> closing = ShutdownCoordinator.WaitAsync(new Task[] { first.Task, second.Task }, TimeSpan.FromSeconds(30));
            Require(!closing.IsCompleted, "The coordinator waits while active workers are finishing cleanup.");
            first.SetResult(true);
            Require(!closing.IsCompleted, "One completed worker must not hide another active worker.");
            second.SetException(new InvalidOperationException("failure during cleanup"));
            Require(closing.Wait(2000) && closing.GetAwaiter().GetResult(),
                "All settled workers must release shutdown promptly instead of waiting for the 30-second deadline.");
        }

        public static void DeadlineReturns()
        {
            var hung = new TaskCompletionSource<bool>();
            var lateFault = new TaskCompletionSource<bool>();
            var elapsed = Stopwatch.StartNew();
            bool completed = ShutdownCoordinator.WaitAsync(new Task[] { hung.Task, lateFault.Task }, TimeSpan.FromMilliseconds(70)).GetAwaiter().GetResult();
            elapsed.Stop();
            Require(!completed && elapsed.Elapsed < TimeSpan.FromSeconds(3),
                "A driver-like worker that never completes cannot keep the UI waiting past the bounded deadline.");
            Require(!hung.Task.IsCompleted && !lateFault.Task.IsCompleted,
                "The coordinator must not cancel, abort or fake completion of the workers it only observes.");
            Require(!ShutdownCoordinator.WaitAsync(new[] { hung.Task }, TimeSpan.Zero).GetAwaiter().GetResult(),
                "A zero-length deadline reports an active worker immediately.");
            lateFault.SetException(new InvalidOperationException("driver returned after the window closed"));
            hung.SetCanceled();
            Require(lateFault.Task.IsFaulted && hung.Task.IsCanceled,
                "Workers can settle independently after the UI deadline.");
        }

        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
