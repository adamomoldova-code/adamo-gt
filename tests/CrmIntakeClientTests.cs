using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmIntakeClientTests
    {
        private const string Context = "{\"user_id\":31,\"office_ip\":\"149.3.190.162\",\"can_print\":true,\"warehouses\":[{\"id\":7,\"name\":\"Oficiu\"}],\"printers\":[{\"id\":\"21f9d29c-c980-49b3-b898-e745cd227934\",\"name\":\"AD sticker\"}]}";
        private const string Success = "{\"id\":42,\"status\":\"completed\",\"products\":[{\"id\":901}],\"print\":{\"requested\":true,\"status\":\"queued\"},\"specification_image\":{\"status\":\"attached\"}}";
        public static void Run()
        {
            RequireThrows<ArgumentException>(delegate { new CrmIntakeClient("http://crm.adamo.md", new FakeTransport(), Temp()); }, "HTTP rejected");
            RequireThrows<ArgumentException>(delegate { new CrmIntakeClient("https://user@crm.adamo.md", new FakeTransport(), Temp()); }, "credential origin rejected");
            TestDenied(); TestBody(); TestTimeoutReplay(); TestPartialPhoto(); TestValidationAndConcurrency(); TestReplayRejectionAndDisposal();
            Console.WriteLine("PASS CRM: office denial, allowed DTO, encrypted replay, image retry, rejection and concurrency");
        }
        private static void TestDenied()
        {
            var transport = new FakeTransport(); transport.Replies.Enqueue(Ok("{\"accessToken\":\"MEMORY_ONLY_SECRET\"}"));
            transport.Replies.Enqueue(new CrmHttpResponse { StatusCode = 403, Body = "{\"message\":\"private server diagnostic\"}" });
            using (var client = Client(transport, Temp()))
            {
                RequireThrows<CrmException>(delegate { client.LoginAsync("operator", "secret-password", CancellationToken.None).GetAwaiter().GetResult(); }, "office denied");
                RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "no submit without office context");
                Require(transport.Paths.Count == 2 && !client.HasPending, "denial made no product call");
            }
        }
        private static void TestBody()
        {
            var value = Submission(); value.Quantity = 3; value.SalePrice = null;
            string body = CrmIntakeClient.BuildBody(value);
            Require(body.Contains("\"quantity\":3") && body.Contains("\"condition\":\"used\"") && !body.Contains("sale_price"), "quantity and absent price truthful");
            Require(body.Contains("specification_image") && body.Contains("name_ru"), "photo and name sent");
            Require(!body.Contains("password") && !body.Contains("ComputerName") && !body.Contains("PersistentHardwareId"), "explicit contract");
            value.Quantity = 0; RequireThrows<CrmException>(delegate { CrmIntakeClient.BuildBody(value); }, "zero qty");
            value.Quantity = 501; RequireThrows<CrmException>(delegate { CrmIntakeClient.BuildBody(value); }, "over qty");
            value.Quantity = 1; value.SalePrice = 12.123m; RequireThrows<CrmException>(delegate { CrmIntakeClient.BuildBody(value); }, "invalid precision");
            value.SalePrice = 0; value.SpecificationImageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("not png"));
            RequireThrows<CrmException>(delegate { CrmIntakeClient.BuildBody(value); }, "invalid photo");
        }
        private static void TestTimeoutReplay()
        {
            string root = Temp(); var first = LoggedIn(); first.FailSubmission = true;
            string original;
            using (var client = Client(first, root))
            {
                Login(client); var submission = Submission();
                RequireThrows<CrmException>(delegate { client.SubmitAsync(submission, CancellationToken.None).GetAwaiter().GetResult(); }, "uncertain timeout");
                Require(client.HasPending, "uncertain POST retained");
                original = first.Bodies.Last();
                var file = Directory.GetFiles(root, "*.pending").Single();
                string raw = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                Require(!raw.Contains("SECRET_PRODUCT") && !raw.Contains("secret-password") && !raw.Contains("MEMORY_ONLY_SECRET"), "journal encrypted and no credentials");
                var changed = Submission();
                RequireThrows<CrmException>(delegate { client.SubmitAsync(changed, CancellationToken.None).GetAwaiter().GetResult(); }, "cannot replace uncertain operation");
                Require(first.Paths.Count == 3, "changed draft never sent");
            }
            var second = LoggedIn(); second.Replies.Enqueue(Ok(Success));
            using (var client = Client(second, root))
            {
                Login(client); Require(client.HasPending, "restart restores pending");
                var result = client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult();
                Require(second.Bodies.Last() == original, "byte-for-byte replay after restart");
                Require(result.ProductUrl == "https://crm.adamo.md/products/901/edit", "trusted CRM product path");
                Require(result.PrintStatus == "queued" && result.ImageStatus == "attached", "queue not completed printing");
                Require(!client.HasPending && Directory.GetFiles(root, "*.pending").Length == 0, "confirmed operation cleared");
            }
        }
        private static void TestPartialPhoto()
        {
            var transport = LoggedIn();
            transport.Replies.Enqueue(Ok(Success.Replace("\"attached\"", "\"failed\"")));
            transport.Replies.Enqueue(Ok(Success.Replace("\"queued\"", "\"failed\"")));
            using (var client = Client(transport, Temp()))
            {
                Login(client); var first = client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult();
                Require(first.ProductId == "901" && first.ImageStatus == "failed" && client.HasPending, "photo failure preserves created product and retry");
                var result = client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult();
                Require(transport.Bodies[2] == transport.Bodies[3], "photo retry same intake");
                Require(!client.HasPending && result.PrintStatus == "failed", "photo complete with truthful print failure");
            }
        }
        private static void TestValidationAndConcurrency()
        {
            var transport = LoggedIn(); transport.Replies.Enqueue(new CrmHttpResponse { StatusCode = 422, Body = "{}" });
            string root = Temp();
            using (var client = Client(transport, root))
            {
                Login(client);
                using (var other = Client(LoggedIn(), root)) { RequireThrows<CrmException>(delegate { Login(other); }, "same staff cannot race journal"); }
                RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "validation rejected");
                Require(!client.HasPending, "pre-mutation validation rejection unlocks draft");
                transport.BlockSubmission = new TaskCompletionSource<CrmHttpResponse>();
                Task<CrmIntakeResult> pending = client.SubmitAsync(Submission(), CancellationToken.None);
                RequireThrows<CrmException>(delegate { client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult(); }, "double click rejected");
                transport.BlockSubmission.SetResult(Ok(Success)); pending.GetAwaiter().GetResult();
                Require(transport.Paths.Count == 4, "double click no extra POST");
            }
        }
        private static CrmSubmission Submission()
        {
            // Full PNG generated by the renderer is checked separately; transport uses a fixture header.
            return new CrmSubmission { IdempotencyKey = Guid.NewGuid().ToString(), Name = "SECRET_PRODUCT", Description = "CPU, RAM, SSD",
                Condition = "used", Quantity = 1, WarehouseId = 7, Print = true, PrinterId = "21f9d29c-c980-49b3-b898-e745cd227934",
                SpecificationImageBase64 = Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 4, 176, 0, 0, 2, 0 }) };
        }
        private static void TestReplayRejectionAndDisposal()
        {
            string root = Temp(); var transport = LoggedIn(); transport.FailSubmission = true;
            var client = Client(transport, root); Login(client);
            RequireThrows<CrmException>(delegate { client.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "uncertain initial post");
            transport.FailSubmission = false; transport.Replies.Enqueue(new CrmHttpResponse { StatusCode = 422, Body = "{}" });
            RequireThrows<CrmException>(delegate { client.SubmitAsync(client.Pending, CancellationToken.None).GetAwaiter().GetResult(); }, "retry validation rejected");
            Require(client.HasPending, "422 cannot erase possibly committed operation");
            transport.BlockSubmission = new TaskCompletionSource<CrmHttpResponse>();
            Task<CrmIntakeResult> sending = client.SubmitAsync(client.Pending, CancellationToken.None);
            client.Dispose();
            using (var other = Client(LoggedIn(), root)) { RequireThrows<CrmException>(delegate { Login(other); }, "dispose retains lock until async completion"); }
            transport.BlockSubmission.SetResult(Ok(Success)); sending.GetAwaiter().GetResult();
            using (var other = Client(LoggedIn(), root)) { Login(other); Require(!other.HasPending, "completed disposed client releases lock safely"); }
            var broken = LoggedIn(); broken.Replies.Enqueue(Ok(Success.Replace("completed", "processing")));
            using (var invalid = Client(broken, Temp()))
            {
                Login(invalid);
                RequireThrows<CrmException>(delegate { invalid.SubmitAsync(Submission(), CancellationToken.None).GetAwaiter().GetResult(); }, "processing response cannot clear receipt");
                Require(invalid.HasPending, "unconfirmed status keeps receipt");
            }
        }
        private static string Temp() { return Path.Combine(Path.GetTempPath(), "AdamoCrmTests", Guid.NewGuid().ToString("N")); }
        private static CrmIntakeClient Client(FakeTransport transport, string root) { return new CrmIntakeClient("https://crm.adamo.md", transport, root); }
        private static CrmHttpResponse Ok(string body) { return new CrmHttpResponse { StatusCode = 200, Body = body }; }
        private static FakeTransport LoggedIn()
        {
            var transport = new FakeTransport(); transport.Replies.Enqueue(Ok("{\"accessToken\":\"MEMORY_ONLY_SECRET\"}")); transport.Replies.Enqueue(Ok(Context)); return transport;
        }
        private static void Login(CrmIntakeClient client) { client.LoginAsync("operator", "secret-password", CancellationToken.None).GetAwaiter().GetResult(); }
        private static void Require(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
        private static void RequireThrows<T>(Action action, string message) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException(message); }
        private sealed class FakeTransport : ICrmTransport
        {
            public readonly Queue<CrmHttpResponse> Replies = new Queue<CrmHttpResponse>();
            public readonly List<string> Paths = new List<string>();
            public readonly List<string> Bodies = new List<string>();
            public bool FailSubmission;
            public TaskCompletionSource<CrmHttpResponse> BlockSubmission;
            public Task<CrmHttpResponse> SendAsync(Uri uri, string method, string body, string bearer, CancellationToken token)
            {
                Paths.Add(uri.AbsolutePath); Bodies.Add(body);
                if (uri.AbsolutePath.EndsWith("product-intakes"))
                {
                    if (FailSubmission) { throw new CrmException("Fixture timeout"); }
                    if (BlockSubmission != null) { return BlockSubmission.Task.ContinueWith(task => WithKey(task.Result, body)); }
                }
                var response = Replies.Dequeue();
                return Task.FromResult(uri.AbsolutePath.EndsWith("product-intakes") ? WithKey(response, body) : response);
            }
            private static CrmHttpResponse WithKey(CrmHttpResponse response, string body)
            {
                if (response.StatusCode == 200)
                {
                    var request = CrmIntakeClient.Json().Deserialize<Dictionary<string, object>>(body);
                    response.Body = response.Body.Substring(0, response.Body.Length - 1) + ",\"idempotency_key\":\"" + request["idempotency_key"] + "\"}";
                    var image = (Dictionary<string, object>)request["specification_image"];
                    string hash;
                    using (var sha = SHA256.Create()) { hash = BitConverter.ToString(sha.ComputeHash(Convert.FromBase64String((string)image["base64"]))).Replace("-", "").ToLowerInvariant(); }
                    response.Body = response.Body.Replace("\"status\":\"attached\"", "\"status\":\"attached\",\"sha256\":\"" + hash + "\"");
                }
                return response;
            }
        }
    }
}
