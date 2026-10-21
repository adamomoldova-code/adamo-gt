using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse
{
    internal static class ShutdownCoordinator
    {
        // The caller requests cancellation before waiting. A driver or WMI call may
        // ignore that request; a deadline keeps such a worker from trapping the UI.
        internal static async Task<bool> WaitAsync(IEnumerable<Task> workers, TimeSpan timeout)
        {
            if (workers == null) { throw new ArgumentNullException("workers"); }
            if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout", "Shutdown requires a finite, non-negative timeout.");
            }

            Task[] observed = workers.Where(worker => worker != null).Select(ObserveWorker).ToArray();
            Task completion = Task.WhenAll(observed);
            if (completion.IsCompleted) { return true; }
            if (timeout == TimeSpan.Zero) { return false; }

            using (var deadlineCancellation = new CancellationTokenSource())
            {
                Task deadline = Task.Delay(timeout, deadlineCancellation.Token);
                try
                {
                    await Task.WhenAny(completion, deadline).ConfigureAwait(false);
                    return completion.IsCompleted;
                }
                finally
                {
                    // Release the timer immediately when workers finish before the deadline.
                    deadlineCancellation.Cancel();
                }
            }
        }

        private static async Task ObserveWorker(Task worker)
        {
            try { await worker.ConfigureAwait(false); }
            catch (Exception)
            {
                // Closing must continue after a worker fault/cancellation. This observer
                // also stays attached when the deadline wins, observing any later fault.
                AggregateException observed = worker.Exception;
                GC.KeepAlive(observed);
            }
        }
    }
}
