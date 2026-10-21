using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmQuickIntakeClientTests
    {
        public static void Run()
        {
            TestContract(); TestSessionFailures(); TestEncryptedExactReplay(); TestReauthentication();
            TestResponseVerification(); TestConcurrencyAndDisposal(); TestCancellation(); TestDamagedJournal();
            Console.WriteLine("PASS quick CRM: scoped login, minimal wire contract, encrypted exact replay, 401 recovery, receipt/image/print verification and concurrency");
        }

        private static void TestContract()
        {
            var transport = new FakeTransport(); string root = Temp();
            using (var client = new CrmQuickIntakeClient(transport, root))
            {
                Require(!client.IsAuthenticated && !client.HasPending, "initial state");
                RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "login required");
                Login(client); var value = Submission(); value.Quantity = 3;
                var result = client.SubmitAsync(value, CancellationToken.None).GetAwaiter().GetResult();
                var session = Parse(transport.Calls[0].Body); var sent = transport.Calls[1]; var body = Parse(sent.Body);
                Require(session.Keys.OrderBy(x => x).SequenceEqual(new[] { "client_id", "password" }), "session has only password and random client id");
                Guid id; Require(Guid.TryParse((string)session["client_id"], out id) && id != Guid.Empty, "random installation identity");
                Require(transport.Calls.All(c => c.Uri.GetLeftPart(UriPartial.Authority) == "https://crm.adamo.md"), "fixed trusted HTTPS origin");
                Require(transport.Calls[0].Uri.AbsolutePath == "/v1/adamo-gt/v2/session" && sent.Uri.AbsolutePath == "/v1/adamo-gt/v2/product-intakes", "v2 endpoints only");
                Require(sent.Key == value.ClientIntakeId && (string)body["client_intake_id"] == sent.Key, "idempotency header matches body");
                Require(body.Keys.OrderBy(x => x).SequenceEqual(new[] { "client_intake_id", "detected_specs", "screenshot", "selection", "source" }), "no surplus form/server override fields");
                var source = (Dictionary<string, object>)body["source"];
                Require(source.Count == 4 && (string)source["provider"] == "ADAMO_GT" && Convert.ToInt32(source["page"]) == 1, "minimal source attribution");
                Require(result.Completed && result.ProductId == "901" && result.IntakeId == "42" && result.PrintStatus == "queued", "queued print accepted without claiming physical print");
                Require(!client.HasPending && !File.Exists(Path.Combine(root, "intake.pending")), "verified result clears receipt");
            }
            var invalid = Submission(); invalid.Quantity = 0;
            RequireThrows<CrmException>(delegate { CrmQuickIntakeClient.BuildBody(invalid); }, "zero quantity rejected");
            invalid.Quantity = 501; RequireThrows<CrmException>(delegate { CrmQuickIntakeClient.BuildBody(invalid); }, "oversized quantity rejected");
            invalid.Quantity = 1; invalid.Condition = "refurbished"; RequireThrows<CrmException>(delegate { CrmQuickIntakeClient.BuildBody(invalid); }, "unknown condition rejected");
            invalid.Condition = "used"; invalid.CapturedAt = "not-time"; RequireThrows<CrmException>(delegate { CrmQuickIntakeClient.BuildBody(invalid); }, "invalid capture time rejected");
            invalid.CapturedAt = "2026-10-20T04:00:00Z"; invalid.ScreenshotBase64 = "not-png";
            RequireThrows<CrmException>(delegate { CrmQuickIntakeClient.BuildBody(invalid); }, "invalid PNG rejected");
        }

        private static void TestSessionFailures()
        {
            foreach (string body in new[] { "{}", "{\"token\":\"x\",\"token_type\":\"Bearer\",\"expires_in\":0}",
                "{\"token\":\"x\\r\\nHeader: value\",\"token_type\":\"Bearer\",\"expires_in\":600}",
                "{\"token\":\"x\",\"token_type\":\"Basic\",\"expires_in\":600}" })
            {
                var transport = new FakeTransport(); transport.SessionResponse = new CrmHttpResponse { StatusCode = 200, Body = body };
                using (var client = new CrmQuickIntakeClient(transport, Temp()))
                {
                    RequireThrows<CrmException>(delegate { Login(client); }, "malformed session rejected");
                    Require(!client.IsAuthenticated && !client.HasPending && transport.Calls.Count == 1, "malformed session no business call");
                }
            }
            foreach (int status in new[] { 301, 302, 401, 403, 404, 429, 503 })
            {
                var transport = new FakeTransport(); transport.SessionResponse = new CrmHttpResponse { StatusCode = status, Body = "{\"diagnostic\":\"PRIVATE_SERVER_DETAIL\"}" };
                using (var client = new CrmQuickIntakeClient(transport, Temp()))
                {
                    var error = RequireThrows<CrmException>(delegate { Login(client); }, "session status rejected");
                    Require(error.StatusCode == status && !error.Message.Contains("PRIVATE_SERVER_DETAIL") && !client.IsAuthenticated, "generic failure does not echo server diagnostics");
                }
            }
        }

        private static void TestEncryptedExactReplay()
        {
            string root = Temp(); var first = new FakeTransport { FailIntake = true }; string original, identity;
            using (var client = new CrmQuickIntakeClient(first, root))
            {
                Login(client);
                RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "uncertain request");
                original = first.Calls.Last().Body; identity = (string)Parse(first.Calls[0].Body)["client_id"];
                Require(client.HasPending, "uncertain request kept");
                string encrypted = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "intake.pending")));
                Require(!encrypted.Contains("FIXTURE_MODEL") && !encrypted.Contains("fixture-password") && !encrypted.Contains("MEMORY_TOKEN"), "DPAPI receipt contains no plaintext credentials/data");
                var different = Submission();
                RequireThrows<CrmException>(delegate { client.SubmitAsync(different, CancellationToken.None).GetAwaiter().GetResult(); }, "pending request cannot be replaced");
                Require(first.Calls.Count == 2, "changed data never sent");
            }
            var second = new FakeTransport();
            using (var client = new CrmQuickIntakeClient(second, root))
            {
                Require(client.HasPending && !client.IsAuthenticated, "pending restored without restoring token");
                Login(client);
                Require((string)Parse(second.Calls[0].Body)["client_id"] == identity, "installation identity stable");
                var result = client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult();
                Require(result.Completed && second.Calls.Last().Body == original, "restart replays exact serialized request");
                Require(second.Calls.Last().Key == first.Calls.Last().Key, "restart retains same idempotency key");
            }
        }

        private static void TestReauthentication()
        {
            var transport = new FakeTransport(); transport.IntakeStatus = 401;
            using (var client = new CrmQuickIntakeClient(transport, Temp()))
            {
                Login(client); var value = Submission();
                var error = RequireThrows<CrmException>(delegate { client.SubmitAsync(value, CancellationToken.None).GetAwaiter().GetResult(); }, "expired session");
                string body = transport.Calls.Last().Body;
                Require(error.StatusCode == 401 && !client.IsAuthenticated && client.HasPending, "401 clears token and preserves request");
                Login(client); transport.IntakeStatus = 201;
                Require(client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult().Completed, "password reauthentication resumes request");
                Require(transport.Calls.Last().Body == body, "reauthentication preserves exact payload");
            }
        }

        private static void TestResponseVerification()
        {
            Action<Dictionary<string, object>>[] corrupt = {
                d => d["client_intake_id"] = Guid.NewGuid().ToString(),
                d => d["status"] = "processing",
                d => ((Dictionary<string, object>)d["product"])["is_published"] = true,
                d => ((Dictionary<string, object>)d["product"])["needs_enrichment"] = "true",
                d => ((Dictionary<string, object>)d["receipt"])["quantity"] = 9,
                d => ((Dictionary<string, object>)d["screenshot"])["sha256"] = new string('0', 64),
                d => ((Dictionary<string, object>)d["screenshot"])["media_id"] = null,
                d => d["inventory_units"] = new object[0],
                d => { var units = (object[])d["inventory_units"]; ((Dictionary<string, object>)units[1])["scan_code"] = ((Dictionary<string, object>)units[0])["scan_code"]; },
                d => { var units = (object[])d["inventory_units"]; ((Dictionary<string, object>)units[1])["id"] = ((Dictionary<string, object>)units[0])["id"]; }
            };
            foreach (var mutate in corrupt)
            {
                var transport = new FakeTransport { MutateIntake = mutate };
                using (var client = new CrmQuickIntakeClient(transport, Temp()))
                {
                    Login(client); var value = Submission(); value.Quantity = 2;
                    RequireThrows<CrmException>(delegate { client.SubmitAsync(value, CancellationToken.None).GetAwaiter().GetResult(); }, "unverified server response");
                    Require(client.HasPending, "unverified response retains safe retry");
                }
            }
            foreach (var mutate in new Action<Dictionary<string, object>>[] {
                d => ((Dictionary<string, object>)d["screenshot"])["status"] = "failed",
                d => ((Dictionary<string, object>)d["print"])["status"] = "failed",
                d => ((Dictionary<string, object>)d["print"])["total_labels"] = 9,
                d => ((Dictionary<string, object>)d["print"])["job_id"] = "invalid-job"
            })
            {
                var transport = new FakeTransport { MutateIntake = mutate };
                using (var client = new CrmQuickIntakeClient(transport, Temp()))
                {
                    Login(client); var result = client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult();
                    Require(!result.Completed && client.HasPending, "partial image or print keeps request");
                    string exact = transport.Calls.Last().Body; transport.MutateIntake = null;
                    result = client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult();
                    Require(result.Completed && !client.HasPending && transport.Calls.Last().Body == exact, "partial result repair replays same operation");
                }
            }
            foreach (int status in new[] { 202, 302, 409, 413, 422, 503 })
            {
                var transport = new FakeTransport { IntakeStatus = status };
                using (var client = new CrmQuickIntakeClient(transport, Temp()))
                {
                    Login(client); RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "unaccepted intake status");
                    Require(client.HasPending, "rejection does not erase possible earlier receipt");
                }
            }
        }

        private static void TestConcurrencyAndDisposal()
        {
            string root = Temp(); var transport = new FakeTransport { BlockIntake = new TaskCompletionSource<CrmHttpResponse>() };
            var client = new CrmQuickIntakeClient(transport, root); Login(client);
            RequireThrows<CrmException>(delegate { new CrmQuickIntakeClient(new FakeTransport(), root); }, "exclusive process journal");
            var task = client.SubmitAsync(Submission(), CancellationToken.None);
            Require(client.HasPending && !task.IsCompleted, "journal committed before network reply");
            RequireThrows<CrmException>(delegate { client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult(); }, "double-click rejected");
            RequireThrows<CrmException>(delegate { Login(client); }, "login cannot race submission");
            Require(transport.Calls.Count == 2, "concurrent actions cause no extra calls");
            client.Dispose();
            RequireThrows<CrmException>(delegate { new CrmQuickIntakeClient(new FakeTransport(), root); }, "disposing active request retains lock");
            transport.BlockIntake.SetResult(transport.Success(transport.Calls.Last().Body));
            Require(task.GetAwaiter().GetResult().Completed, "late confirmed reply completes safely");
            using (var next = new CrmQuickIntakeClient(new FakeTransport(), root)) { Require(!next.HasPending, "lock released only after completion"); }
        }

        private static void TestCancellation()
        {
            var transport = new FakeTransport { CancelIntake = true };
            using (var client = new CrmQuickIntakeClient(transport, Temp()))
            using (var cancellation = new CancellationTokenSource())
            {
                Login(client); var task = client.SubmitAsync(Submission(), cancellation.Token); cancellation.Cancel();
                RequireThrows<OperationCanceledException>(delegate { task.GetAwaiter().GetResult(); }, "cancel pending operation");
                Require(client.HasPending, "cancelled send keeps journal because server may have received it");
                string body = transport.Calls.Last().Body; transport.CancelIntake = false;
                Require(client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult().Completed && body == transport.Calls.Last().Body, "cancelled send safely replayed");
            }
            transport = new FakeTransport();
            using (var client = new CrmQuickIntakeClient(transport, Temp()))
            using (var cancellation = new CancellationTokenSource())
            {
                Login(client); cancellation.Cancel();
                RequireThrows<OperationCanceledException>(delegate { client.SubmitAsync(Submission(), cancellation.Token).GetAwaiter().GetResult(); }, "cancel before send");
                Require(!client.HasPending && transport.Calls.Count == 1, "pre-cancel no business call or pending receipt");
            }
        }

        private static void TestDamagedJournal()
        {
            string root = Temp(); var transport = new FakeTransport { FailIntake = true };
            using (var client = new CrmQuickIntakeClient(transport, root))
            { Login(client); RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "create pending fixture"); }
            string file = Path.Combine(root, "intake.pending"); File.WriteAllText(file, "damaged fixture");
            RequireThrows<CrmException>(delegate { new CrmQuickIntakeClient(new FakeTransport(), root); }, "damaged receipt fails closed");
            Require(File.ReadAllText(file) == "damaged fixture", "damaged receipt preserved, not silently replaced");
        }

        internal static CrmQuickSubmission Submission()
        {
            return new CrmQuickSubmission { ClientIntakeId = Guid.NewGuid().ToString("D"), Quantity = 1, Condition = "used", AppVersion = "2.7.1",
                CapturedAt = "2026-10-20T04:00:00.0000000Z", DetectedSpecs = new Dictionary<string, object> { { "schema_version", 1 }, { "model", "FIXTURE_MODEL" }, { "unknown_fields", new[] { "panel_nits" } } },
                ScreenshotBase64 = Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 4, 176, 0, 0, 2, 0 }) };
        }
        private static string Temp() { return Path.Combine(Path.GetTempPath(), "AdamoQuickCrmTests", Guid.NewGuid().ToString("N")); }
        private static void Login(CrmQuickIntakeClient client) { client.LoginAsync("fixture-password", CancellationToken.None).GetAwaiter().GetResult(); }
        private static Dictionary<string, object> Parse(string body) { return (Dictionary<string, object>)CrmQuickIntakeClient.Json().DeserializeObject(body); }
        private static void Require(bool condition, string reason) { if (!condition) { throw new InvalidOperationException(reason); } }
        private static T RequireThrows<T>(Action action, string reason) where T : Exception
        { try { action(); } catch (T error) { return error; } throw new InvalidOperationException(reason); }
        private sealed class Call
        { public Uri Uri; public string Body, Bearer, Key; }
        private sealed class FakeTransport : ICrmQuickTransport
        {
            public readonly List<Call> Calls = new List<Call>();
            public CrmHttpResponse SessionResponse = new CrmHttpResponse { StatusCode = 200, Body = "{\"token\":\"MEMORY_TOKEN\",\"token_type\":\"Bearer\",\"expires_in\":600}" };
            public int IntakeStatus = 201;
            public bool FailIntake, CancelIntake;
            public TaskCompletionSource<CrmHttpResponse> BlockIntake;
            public Action<Dictionary<string, object>> MutateIntake;
            public Task<CrmHttpResponse> SendAsync(Uri uri, string body, string bearer, string key, CancellationToken token)
            {
                Calls.Add(new Call { Uri = uri, Body = body, Bearer = bearer, Key = key });
                if (uri.AbsolutePath.EndsWith("/session")) { return Task.FromResult(SessionResponse); }
                if (FailIntake) { throw new CrmException("Fixture lost reply"); }
                if (CancelIntake)
                {
                    var cancelled = new TaskCompletionSource<CrmHttpResponse>(); token.Register(delegate { cancelled.TrySetCanceled(); }); return cancelled.Task;
                }
                if (BlockIntake != null) { return BlockIntake.Task; }
                return Task.FromResult(Success(body));
            }
            public CrmHttpResponse Success(string body)
            {
                var request = Parse(body); var selection = (Dictionary<string, object>)request["selection"]; var screenshot = (Dictionary<string, object>)request["screenshot"];
                int quantity = Convert.ToInt32(selection["quantity"], CultureInfo.InvariantCulture);
                var units = new List<object>();
                for (int i = 0; i < quantity; i++) { units.Add(new Dictionary<string, object> { { "id", i + 100 }, { "scan_code", "AD" + (i + 100).ToString("D10") }, { "status", "available" } }); }
                var response = new Dictionary<string, object> {
                    { "schema_version", 2 }, { "intake_id", 42 }, { "client_intake_id", request["client_intake_id"] }, { "status", "completed" },
                    { "product", new Dictionary<string, object> { { "id", 901 }, { "created", true }, { "is_published", false }, { "needs_enrichment", true } } },
                    { "receipt", new Dictionary<string, object> { { "id", 72 }, { "quantity", quantity } } }, { "inventory_units", units.ToArray() },
                    { "screenshot", new Dictionary<string, object> { { "status", "attached" }, { "sha256", CrmQuickIntakeClient.HashPng((string)screenshot["base64"]) }, { "media_id", 88 } } },
                    { "print", new Dictionary<string, object> { { "status", "queued" }, { "job_id", "2888cd99-93cf-4c18-81a8-4be12af7355b" }, { "total_labels", quantity } } }, { "warnings", new object[0] }
                };
                if (MutateIntake != null) { MutateIntake(response); }
                return new CrmHttpResponse { StatusCode = IntakeStatus, Body = CrmQuickIntakeClient.Json().Serialize(response) };
            }
        }
    }
}
