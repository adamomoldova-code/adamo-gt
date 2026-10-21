using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmQuickFlowTests
    {
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ADAMO-crm-flow-" + Guid.NewGuid().ToString("N"));
            var transport = new ContractTransport();
            Window page = Program.LoadWindow();
            var dashboard = new Dashboard(page);
            var snapshot = new Snapshot { ComputerManufacturer = "Fixture", ComputerModel = "Laptop", ComputerName = "PRIVATE_HOST",
                CpuName = "Fixture CPU", CpuCores = 4, CpuThreads = 8, RamInstalledBytes = 16UL * 1024 * 1024 * 1024,
                RamType = "DDR4", WindowsName = "Windows 11 Pro" };
            snapshot.Disks.Add(new DiskSnapshot { Name = "Fixture SSD", MediaType = "SSD · NVMe", SizeBytes = 512000000000 });
            dashboard.Render(snapshot); dashboard.SetCrmOfficeAccess(true);
            page.WindowStartupLocation = WindowStartupLocation.Manual; page.Left = -20000; page.Top = -20000;
            page.Width = 1200; page.Height = 760; page.Show(); page.UpdateLayout();
            CrmQuickIntakeWindow dialog = null;
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                byte[] image = CrmDashboardCapture.Capture(page);
                CrmQuickSubmission prepared = dashboard.CreateCrmQuickSubmission(image);
                snapshot.CpuName = "CHANGED_AFTER_CAPTURE";
                using (var client = new CrmQuickIntakeClient(transport, directory))
                {
                    dialog = new CrmQuickIntakeWindow(prepared, client);
                    Field<PasswordBox>(dialog, "password").Password = "fixture-only-password";
                    Drain(Continue(dialog));
                    Require(client.IsAuthenticated && transport.Requests == 1, "One password step authenticates the actual v2 client");
                    Field<TextBox>(dialog, "quantity").Text = "2";
                    Field<ComboBox>(dialog, "condition").SelectedIndex = 2;
                    Drain(Continue(dialog));
                    Require(transport.Requests == 2 && !client.HasPending, "Single minimal submit completes the frozen operation");
                    Require(Field<TextBlock>(dialog, "status").Text.Contains("Produs creat"), "Success is shown without another form");
                    Drain(Continue(dialog));
                    Require(transport.Requests == 2, "Completed UI cannot create a duplicate product or sticker job");
                    Require(transport.Body != null && !transport.Body.Contains("PRIVATE_HOST")
                        && !transport.Body.Contains("CHANGED_AFTER_CAPTURE") && !transport.Body.Contains("fixture-only-password"),
                        "Frozen product payload contains neither later inventory nor hostname nor password");
                    var wire = Json().Deserialize<Dictionary<string, object>>(transport.Body);
                    Require(wire.Count == 5 && !wire.ContainsKey("warehouse_id") && !wire.ContainsKey("print"), "Server choices do not become client controls/overrides");
                    var screenshot = (Dictionary<string, object>)wire["screenshot"];
                    Require((string)screenshot["base64"] == Convert.ToBase64String(image), "The submitted photo is the real captured first page");
                    string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "crm-minimal-20261020"));
                    Directory.CreateDirectory(artifacts);
                    File.WriteAllText(Path.Combine(artifacts, "contract-request.json"), transport.Body, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(artifacts, "contract-response.json"), transport.Response, new UTF8Encoding(false));
                }
            }
            finally
            {
                if (dialog != null) { dialog.Close(); }
                page.Close(); SynchronizationContext.SetSynchronizationContext(previous);
                if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
            }
        }

        private sealed class ContractTransport : ICrmQuickTransport
        {
            internal int Requests;
            internal string Body, Response;
            public Task<CrmHttpResponse> SendAsync(Uri uri, string body, string bearer, string key, CancellationToken token)
            {
                Requests++; token.ThrowIfCancellationRequested();
                Require(uri.Scheme == "https" && uri.Host == "crm.adamo.md", "Fixed CRM origin");
                if (uri.AbsolutePath.EndsWith("/session", StringComparison.Ordinal))
                {
                    var login = Json().Deserialize<Dictionary<string, object>>(body);
                    Require(login.Count == 2 && login.ContainsKey("password") && login.ContainsKey("client_id") && bearer == null && key == null, "Password-only session has no staff username");
                    return Task.FromResult(new CrmHttpResponse { StatusCode = 200, Body = "{\"token\":\"fixture-token\",\"token_type\":\"Bearer\",\"expires_in\":600}" });
                }
                Require(uri.AbsolutePath == "/v1/adamo-gt/v2/product-intakes" && bearer == "fixture-token", "Scoped session is used for intake");
                Body = body;
                var data = Json().Deserialize<Dictionary<string, object>>(body);
                Require(key == (string)data["client_intake_id"], "Idempotency header matches the exact operation");
                var selection = (Dictionary<string, object>)data["selection"];
                Require(Convert.ToInt32(selection["quantity"]) == 2 && (string)selection["condition"] == "used", "Only chosen quantity and condition affect the operation");
                var image = (Dictionary<string, object>)data["screenshot"];
                string hash;
                using (var sha = SHA256.Create()) { hash = BitConverter.ToString(sha.ComputeHash(Convert.FromBase64String((string)image["base64"]))).Replace("-", "").ToLowerInvariant(); }
                Response = Json().Serialize(new {
                    schema_version = 2, intake_id = 11, client_intake_id = key, status = "completed",
                    product = new { id = 12, created = true, is_published = false, needs_enrichment = true },
                    receipt = new { id = 13, quantity = 2 },
                    inventory_units = new[] { new { id = 14, scan_code = "AD0000000014", status = "available" }, new { id = 15, scan_code = "AD0000000015", status = "available" } },
                    screenshot = new { status = "attached", sha256 = hash, media_id = 16 },
                    print = new { status = "queued", job_id = "bd70323c-1128-42e0-a3d7-1bd82d8e1927", total_labels = 2 }, warnings = new string[0]
                });
                return Task.FromResult(new CrmHttpResponse { StatusCode = 201, Body = Response });
            }
        }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 32 }; }
        private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        private static Task Continue(object value) { return (Task)value.GetType().GetMethod("ContinueAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null); }
        private static void Drain(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
            }
            Require(task.IsCompleted, "Flow finishes without blocking the dispatcher"); task.GetAwaiter().GetResult();
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
