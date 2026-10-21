using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse
{
    // This is a UI visibility hint only. CRM must enforce the office restriction again
    // on every authenticated intake request; no permission is stored between launches.
    public sealed class CrmOfficeAccess : IDisposable
    {
        private static readonly Uri AvailabilityUri = new Uri("https://crm.adamo.md/v1/adamo-gt/availability");
        private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(8);
        private readonly object gate = new object();
        private readonly ICrmTransport transport;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private Task refreshTask;
        private Task<CrmHttpResponse> pendingRequest;
        private CancellationTokenSource refreshCancellation;
        private long generation, activeGeneration;
        private bool allowed, disposed, queuedRefresh;
        private bool enabled = true;
        private CancellationToken queuedCancellation;

        public event Action<bool> AccessChanged;
        public bool IsAllowed { get { lock (gate) { return allowed; } } }

        public CrmOfficeAccess() : this(new CrmHttpTransport()) { }

        public void SetEnabled(bool value)
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (disposed || enabled == value) { return; }
                enabled = value; generation++; queuedRefresh = false;
                cancellation = refreshCancellation;
                PublishLocked(false);
            }
            Cancel(cancellation);
        }
        internal CrmOfficeAccess(ICrmTransport transport)
            : this(transport, delegate(TimeSpan duration, CancellationToken token) { return Task.Delay(duration, token); }) { }
        internal CrmOfficeAccess(ICrmTransport transport, Func<TimeSpan, CancellationToken, Task> delay)
        {
            if (transport == null) { throw new ArgumentNullException("transport"); }
            if (delay == null) { throw new ArgumentNullException("delay"); }
            this.transport = transport; this.delay = delay;
        }

        public Task RefreshAsync(CancellationToken token)
        {
            lock (gate)
            {
                if (disposed || !enabled) { return Task.FromResult(false); }
                if (token.IsCancellationRequested)
                {
                    PublishLocked(false);
                    return Task.FromResult(false);
                }
                if (refreshTask != null || (pendingRequest != null && !pendingRequest.IsCompleted))
                {
                    // A network change requests one fresh check after the cancelled
                    // transport has actually stopped. Repeated timer ticks coalesce.
                    if (activeGeneration != generation || refreshTask == null)
                    {
                        queuedRefresh = true;
                        queuedCancellation = token;
                    }
                    return refreshTask ?? Task.FromResult(false);
                }
                return StartRefreshLocked(token);
            }
        }

        public void Invalidate()
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (disposed) { return; }
                generation++;
                queuedRefresh = false;
                cancellation = refreshCancellation;
                PublishLocked(false);
            }
            Cancel(cancellation);
        }

        private Task StartRefreshLocked(CancellationToken token)
        {
            queuedRefresh = false;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            long requestedGeneration = generation;
            activeGeneration = requestedGeneration;
            refreshCancellation = cancellation;
            // Start outside the caller's synchronization context; notifications may
            // originate on a worker and Dashboard marshals them to its dispatcher.
            refreshTask = Task.Run(delegate { return RefreshCoreAsync(requestedGeneration, cancellation); });
            return refreshTask;
        }

        private async Task RefreshCoreAsync(long requestedGeneration, CancellationTokenSource cancellation)
        {
            bool result = false;
            try
            {
                CancellationToken token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                Task<CrmHttpResponse> request;
                lock (gate)
                {
                    if (disposed || !enabled || generation != requestedGeneration || token.IsCancellationRequested) { return; }
                    request = transport.SendAsync(AvailabilityUri, "GET", null, null, token);
                    if (request == null) { return; }
                    pendingRequest = request;
                }
                Task observer = request.ContinueWith(delegate(Task<CrmHttpResponse> completed)
                {
                    // A timeout can finish the visibility check before an uncooperative
                    // transport settles. Observe its error and never overlap requests.
                    if (completed.IsFaulted) { var ignored = completed.Exception; }
                    lock (gate) { StartQueuedRefreshLocked(); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Task deadline = delay(RequestDeadline, token);
                Task winner = await Task.WhenAny(request, deadline).ConfigureAwait(false);
                if (winner == request && !token.IsCancellationRequested)
                {
                    CrmHttpResponse response = await request.ConfigureAwait(false);
                    result = response != null && response.StatusCode == 200 && IsExplicitlyAvailable(response.Body);
                }
            }
            catch (Exception)
            {
                // Offline, timeout, cancellation and malformed responses all deny access.
                result = false;
            }
            finally
            {
                try
                {
                    lock (gate)
                    {
                        try
                        {
                            if (!disposed && generation == requestedGeneration)
                                PublishLocked(result && !cancellation.IsCancellationRequested);
                        }
                        finally
                        {
                            refreshCancellation = null;
                            refreshTask = null;
                            StartQueuedRefreshLocked();
                        }
                    }
                }
                finally { Cancel(cancellation); cancellation.Dispose(); }
            }
        }

        private void StartQueuedRefreshLocked()
        {
            if (disposed || !enabled || !queuedRefresh || refreshTask != null || (pendingRequest != null && !pendingRequest.IsCompleted)) { return; }
            if (queuedCancellation.IsCancellationRequested) { queuedRefresh = false; return; }
            StartRefreshLocked(queuedCancellation);
        }

        private static bool IsExplicitlyAvailable(string body)
        {
            // The endpoint has exactly one boolean field. Matching this narrow JSON
            // shape also rejects duplicate keys and JavaScript-only parser extensions.
            return body != null && body.Length <= 512
                && Regex.IsMatch(body, "\\A[ \\t\\r\\n]*\\{[ \\t\\r\\n]*\"available\"[ \\t\\r\\n]*:[ \\t\\r\\n]*true[ \\t\\r\\n]*\\}[ \\t\\r\\n]*\\z", RegexOptions.CultureInvariant);
        }

        private void PublishLocked(bool value)
        {
            allowed = value;
            Action<bool> handler = AccessChanged;
            if (handler != null) { handler(value); }
        }

        private static void Cancel(CancellationTokenSource cancellation)
        {
            if (cancellation == null) { return; }
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (disposed) { return; }
                disposed = true;
                generation++;
                queuedRefresh = false;
                cancellation = refreshCancellation;
                PublishLocked(false);
                AccessChanged = null;
            }
            Cancel(cancellation);
        }
    }
}
