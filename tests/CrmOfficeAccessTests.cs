using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmOfficeAccessTests
    {
        public static void Run()
        {
            TestStrictResponseAndRequest();
            TestOfflineAndCancellation();
            TestDeadline();
            TestInvalidatedResultAndSingleFlight();
            TestDisposal();
            TestPrivacyDisabled();
            Console.WriteLine("PASS CRM availability: fixed anonymous endpoint, strict boolean, timeout, cancellation, single flight and stale results");
        }

        private static void TestStrictResponseAndRequest()
        {
            string[] deniedBodies = { null, "", "{}", "null", "true", "[]", "{\"available\":false}", "{\"available\":\"true\"}",
                "{\"available\":1}", "{\"available\":null}", "{available:true}", "{'available':true}", "{\"available\":True}",
                "{\"available\":true,}", "{\"available\":false,\"available\":true}", "{\"available\":true}garbage" };
            foreach (string body in deniedBodies) { Expect(body, 200, false); }
            foreach (int status in new[] { 201, 204, 301, 302, 307, 308, 403, 404, 500 }) { Expect("{\"available\":true}", status, false); }
            Expect("{\"available\":true}", 200, true);
            Expect(" \r\n { \"available\" : true } \t", 200, true);
        }

        private static void Expect(string body, int status, bool expected)
        {
            var transport = new RecordingTransport();
            transport.Enqueue(Task.FromResult(new CrmHttpResponse { StatusCode = status, Body = body }));
            using (var access = new CrmOfficeAccess(transport))
            {
                Require(!access.IsAllowed, "Every launch starts denied without a persisted allow cache");
                Complete(access.RefreshAsync(CancellationToken.None));
                Require(access.IsAllowed == expected, "Only the strict HTTP 200 boolean true contract permits visibility");
                Require(transport.Calls == 1 && transport.LastUri.AbsoluteUri == "https://crm.adamo.md/v1/adamo-gt/availability", "Only the fixed HTTPS CRM endpoint is queried");
                Require(transport.LastMethod == "GET" && transport.LastBody == null && transport.LastBearer == null, "Availability sends no password, token, report or body");
            }
        }

        private static void TestOfflineAndCancellation()
        {
            var offline = new RecordingTransport { ThrowOffline = true };
            using (var access = new CrmOfficeAccess(offline))
            {
                Complete(access.RefreshAsync(CancellationToken.None));
                Require(!access.IsAllowed, "Offline is denied without throwing into the UI");
            }
            var transport = new RecordingTransport();
            var response = new TaskCompletionSource<CrmHttpResponse>();
            transport.Enqueue(response.Task);
            using (var source = new CancellationTokenSource())
            using (var access = new CrmOfficeAccess(transport))
            {
                Task refresh = access.RefreshAsync(source.Token);
                transport.WaitForCalls(1);
                source.Cancel();
                Complete(refresh);
                Require(!access.IsAllowed && transport.LastToken.IsCancellationRequested, "Cancellation immediately denies and reaches the transport");
                response.SetResult(Available(true));
                Require(!access.IsAllowed, "Late success after cancellation cannot restore access");
            }
            var untouched = new RecordingTransport();
            using (var source = new CancellationTokenSource())
            using (var access = new CrmOfficeAccess(untouched))
            {
                source.Cancel();
                Complete(access.RefreshAsync(source.Token));
                Require(!access.IsAllowed && untouched.Calls == 0, "An already cancelled check makes no request");
            }
        }

        private static void TestDeadline()
        {
            var transport = new RecordingTransport();
            var response = new TaskCompletionSource<CrmHttpResponse>(); transport.Enqueue(response.Task);
            var deadline = new TaskCompletionSource<bool>();
            var deadlineStarted = new ManualResetEventSlim();
            TimeSpan requestedDeadline = TimeSpan.Zero;
            using (var access = new CrmOfficeAccess(transport, delegate(TimeSpan duration, CancellationToken token)
            {
                requestedDeadline = duration; deadlineStarted.Set(); return deadline.Task;
            }))
            {
                Task refresh = access.RefreshAsync(CancellationToken.None);
                Require(deadlineStarted.Wait(3000), "The deadline is scheduled without waiting for network I/O");
                Require(requestedDeadline == TimeSpan.FromSeconds(8), "The availability deadline is exactly eight seconds");
                deadline.SetResult(true);
                Complete(refresh);
                Require(!access.IsAllowed && transport.LastToken.IsCancellationRequested, "Deadline denies immediately and aborts the transport");
                response.SetResult(Available(true));
                Require(!access.IsAllowed, "A response arriving after the deadline stays denied");
            }
            deadlineStarted.Dispose();
        }

        private static void TestInvalidatedResultAndSingleFlight()
        {
            var transport = new RecordingTransport();
            var stale = new TaskCompletionSource<CrmHttpResponse>();
            var fresh = new TaskCompletionSource<CrmHttpResponse>();
            transport.Enqueue(Task.FromResult(Available(true))); transport.Enqueue(stale.Task); transport.Enqueue(fresh.Task);
            int allowedNotifications = 0;
            using (var freshDenied = new ManualResetEventSlim())
            using (var access = new CrmOfficeAccess(transport))
            {
                access.AccessChanged += delegate(bool value)
                {
                    if (value) { Interlocked.Increment(ref allowedNotifications); }
                    if (!value && transport.Calls == 3) { freshDenied.Set(); }
                };
                Complete(access.RefreshAsync(CancellationToken.None));
                Require(access.IsAllowed, "Office confirmation permits access");
                Task running = access.RefreshAsync(CancellationToken.None);
                transport.WaitForCalls(2);
                Task duplicate = access.RefreshAsync(CancellationToken.None);
                Require(Object.ReferenceEquals(running, duplicate) && transport.Calls == 2, "Concurrent refresh calls share one request");
                access.Invalidate();
                Require(!access.IsAllowed, "A network change hides CRM synchronously");
                Task replacement = access.RefreshAsync(CancellationToken.None);
                Complete(running); Complete(replacement);
                Require(transport.Calls == 2 && !access.IsAllowed, "A new check waits until the old physical transport has stopped");
                stale.SetResult(Available(true));
                transport.WaitForCalls(3);
                Require(!access.IsAllowed && allowedNotifications == 1, "A stale office reply cannot undo revocation");
                fresh.SetResult(Available(false));
                Require(freshDenied.Wait(3000), "One queued refresh runs for the new network");
                Require(!access.IsAllowed && allowedNotifications == 1 && transport.Calls == 3, "The fresh denial remains authoritative");
                Require(!transport.OverlappedRequests, "No requests overlap even when cancellation is ignored by the old transport");
            }
        }

        private static void TestDisposal()
        {
            var transport = new RecordingTransport(); var response = new TaskCompletionSource<CrmHttpResponse>(); transport.Enqueue(response.Task);
            var access = new CrmOfficeAccess(transport);
            Task refresh = access.RefreshAsync(CancellationToken.None); transport.WaitForCalls(1);
            access.Dispose(); Complete(refresh);
            response.SetResult(Available(true));
            Complete(access.RefreshAsync(CancellationToken.None));
            Require(!access.IsAllowed && transport.Calls == 1, "Disposal cancels monitoring and prevents late reactivation or new requests");
        }

        private static void TestPrivacyDisabled()
        {
            var transport = new RecordingTransport();
            var late = new TaskCompletionSource<CrmHttpResponse>();
            transport.Enqueue(late.Task);
            transport.Enqueue(Task.FromResult(Available(true)));
            using (var access = new CrmOfficeAccess(transport))
            {
                access.SetEnabled(false);
                for (int i = 0; i < 5; i++) { Complete(access.RefreshAsync(CancellationToken.None)); access.Invalidate(); }
                Require(transport.Calls == 0 && !access.IsAllowed, "A disabled CRM preference prevents timer, activation and network-change requests");
                access.SetEnabled(true);
                Task request = access.RefreshAsync(CancellationToken.None); transport.WaitForCalls(1);
                access.SetEnabled(false); Complete(request);
                Require(transport.LastToken.IsCancellationRequested && !access.IsAllowed, "Disabling CRM cancels in-flight access and hides the button immediately");
                late.SetResult(Available(true));
                Complete(access.RefreshAsync(CancellationToken.None));
                Require(!access.IsAllowed && transport.Calls == 1, "A late successful response cannot override a disabled preference");
                access.SetEnabled(true); Complete(access.RefreshAsync(CancellationToken.None));
                Require(access.IsAllowed && transport.Calls == 2, "Explicitly enabling CRM still requires a new server decision");
                access.SetEnabled(false);
                Require(!access.IsAllowed, "A previously allowed office decision is cleared on opt-out");
            }
        }

        private static CrmHttpResponse Available(bool value) { return new CrmHttpResponse { StatusCode = 200, Body = value ? "{\"available\":true}" : "{\"available\":false}" }; }
        private static void Complete(Task task)
        {
            Require(task.Wait(3000), "The availability operation completed within the test boundary");
            task.GetAwaiter().GetResult();
        }
        private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }

        private sealed class RecordingTransport : ICrmTransport
        {
            private readonly object gate = new object();
            private readonly Queue<Task<CrmHttpResponse>> responses = new Queue<Task<CrmHttpResponse>>();
            private Task<CrmHttpResponse> previous;
            private int calls;
            public Uri LastUri;
            public string LastMethod, LastBody, LastBearer;
            public CancellationToken LastToken;
            public bool ThrowOffline, OverlappedRequests;
            public int Calls { get { lock (gate) { return calls; } } }
            public void Enqueue(Task<CrmHttpResponse> task) { lock (gate) { responses.Enqueue(task); } }
            public void WaitForCalls(int count) { Require(SpinWait.SpinUntil(delegate { return Calls >= count; }, 3000), "The expected availability request started"); }
            public Task<CrmHttpResponse> SendAsync(Uri uri, string method, string body, string bearer, CancellationToken token)
            {
                lock (gate)
                {
                    if (previous != null && !previous.IsCompleted) { OverlappedRequests = true; }
                    LastUri = uri; LastMethod = method; LastBody = body; LastBearer = bearer; LastToken = token; calls++;
                    if (ThrowOffline) { throw new CrmException("Fixture network unavailable"); }
                    previous = responses.Dequeue(); return previous;
                }
            }
        }
    }
}
