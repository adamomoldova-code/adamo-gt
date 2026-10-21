using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PcPulse
{
    public sealed class CrmQuickSubmission
    {
        public string ClientIntakeId { get; set; }
        public int Quantity { get; set; }
        public string Condition { get; set; }
        public string AppVersion { get; set; }
        public string CapturedAt { get; set; }
        public Dictionary<string, object> DetectedSpecs { get; set; }
        public string ScreenshotBase64 { get; set; }
    }

    public sealed class CrmQuickIntakeResult
    {
        public string ProductId { get; set; }
        public string IntakeId { get; set; }
        public string ImageStatus { get; set; }
        public string PrintStatus { get; set; }
        public bool Completed { get; set; }
    }

    internal interface ICrmQuickTransport
    {
        Task<CrmHttpResponse> SendAsync(Uri uri, string body, string bearer, string idempotencyKey, CancellationToken token);
    }

    internal sealed class CrmQuickHttpTransport : ICrmQuickTransport
    {
        public Task<CrmHttpResponse> SendAsync(Uri uri, string body, string bearer, string idempotencyKey, CancellationToken token)
        {
            return Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "POST";
                request.AllowAutoRedirect = false;
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                request.Timeout = 90000; request.ReadWriteTimeout = 90000;
                request.Accept = "application/json"; request.ContentType = "application/json; charset=utf-8";
                if (!String.IsNullOrEmpty(bearer)) { request.Headers["Authorization"] = "Bearer " + bearer; }
                if (!String.IsNullOrEmpty(idempotencyKey)) { request.Headers["Idempotency-Key"] = idempotencyKey; }
                using (token.Register(request.Abort))
                {
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        request.ContentLength = bytes.Length;
                        using (var stream = request.GetRequestStream()) { stream.Write(bytes, 0, bytes.Length); }
                        using (var response = (HttpWebResponse)request.GetResponse()) { return Read(response); }
                    }
                    catch (WebException error)
                    {
                        token.ThrowIfCancellationRequested();
                        var response = error.Response as HttpWebResponse;
                        if (response != null) { using (response) { return Read(response); } }
                        throw new CrmException("CRM не ответил. Повторите сохранённую отправку.");
                    }
                }
            }, token);
        }

        private static CrmHttpResponse Read(HttpWebResponse response)
        {
            using (var stream = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > 1024 * 1024) { throw new CrmException("Ответ CRM не подтверждён. Отправка сохранена для повтора."); }
                    output.Write(buffer, 0, count);
                }
                return new CrmHttpResponse { StatusCode = (int)response.StatusCode, Body = Encoding.UTF8.GetString(output.ToArray()) };
            }
        }
    }

    internal sealed class CrmQuickPendingEnvelope
    {
        public int Version { get; set; }
        public string Origin { get; set; }
        public string ClientId { get; set; }
        public CrmQuickSubmission Submission { get; set; }
        public string Body { get; set; }
    }

    public class CrmQuickIntakeClient : IDisposable
    {
        public const string DefaultBaseUrl = "https://crm.adamo.md";
        private static readonly Uri Origin = new Uri(DefaultBaseUrl);
        private readonly ICrmQuickTransport transport;
        private readonly string journalRoot;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly object lifecycle = new object();
        private FileStream journalLock;
        private string clientId, pendingBody, bearer;
        private string journalPath;
        private bool disposed;
        private int activeOperations;
        private DateTime expiresAtUtc;

        public virtual CrmQuickSubmission Pending { get; protected set; }
        public virtual bool HasPending { get { return Pending != null; } }
        public virtual bool IsAuthenticated { get { return !disposed && !String.IsNullOrEmpty(bearer) && expiresAtUtc > DateTime.UtcNow; } }

        public CrmQuickIntakeClient() : this(new CrmQuickHttpTransport(), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADAMO GT", "crm-v2")) { }

        internal CrmQuickIntakeClient(ICrmQuickTransport transport, string journalRoot)
        {
            if (transport == null) { throw new ArgumentNullException("transport"); }
            if (String.IsNullOrWhiteSpace(journalRoot)) { throw new ArgumentException("journalRoot"); }
            this.transport = transport; this.journalRoot = journalRoot;
            OpenJournal();
        }

        public virtual async Task LoginAsync(string password, CancellationToken token)
        {
            BeginOperation();
            bool entered = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrEmpty(password)) { throw new CrmException("Введите пароль CRM."); }
                entered = await operation.WaitAsync(0, token);
                if (!entered) { throw new CrmException("Запрос CRM уже выполняется."); }
                bearer = null; expiresAtUtc = DateTime.MinValue;
                var response = await Request("/v1/adamo-gt/v2/session", Json().Serialize(new { password = password, client_id = clientId }), null, null, token);
                CheckDisposed();
                var data = Parse(response.Body);
                string candidate = Text(data, "token");
                int seconds;
                if (String.IsNullOrWhiteSpace(candidate) || candidate.Length > 4096 || candidate.IndexOfAny(new[] { '\r', '\n' }) >= 0
                    || Text(data, "token_type") != "Bearer"
                    || !Int32.TryParse(Text(data, "expires_in"), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 600)
                    throw new CrmException("CRM не подтвердил вход. Повторите ввод пароля.");
                bearer = candidate; expiresAtUtc = DateTime.UtcNow.AddSeconds(seconds);
            }
            finally { if (entered) { operation.Release(); } EndOperation(); }
        }

        public virtual async Task<CrmQuickIntakeResult> SubmitAsync(CrmQuickSubmission submission, CancellationToken token)
        {
            BeginOperation();
            bool entered = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsAuthenticated) { throw new CrmException("Введите пароль CRM ещё раз. Сохранённая отправка останется доступной.", 401); }
                entered = await operation.WaitAsync(0, token);
                if (!entered) { throw new CrmException("Отправка уже выполняется."); }
                string body = BuildBody(submission);
                if (Pending != null && body != pendingBody) { throw new CrmException("Есть незавершённая отправка. Повторите её без изменения данных."); }
                if (Pending == null)
                {
                    Pending = Json().Deserialize<CrmQuickSubmission>(Json().Serialize(submission));
                    pendingBody = body;
                    try { SaveJournal(); }
                    catch
                    {
                        Pending = null; pendingBody = null;
                        throw new CrmException("Не удалось сохранить отправку. Данные не переданы в CRM.");
                    }
                }
                token.ThrowIfCancellationRequested();
                var response = await Request("/v1/adamo-gt/v2/product-intakes", pendingBody, bearer, Pending.ClientIntakeId, token);
                CrmQuickIntakeResult result = ParseResult(response.Body, Pending);
                if (result.Completed) { ClearJournal(); }
                return result;
            }
            finally { if (entered) { operation.Release(); } EndOperation(); }
        }

        internal static string BuildBody(CrmQuickSubmission value)
        {
            Guid operationId; DateTime captured;
            if (value == null || !Guid.TryParseExact(value.ClientIntakeId, "D", out operationId) || operationId == Guid.Empty)
                throw new CrmException("Не задан идентификатор отправки.");
            if (value.Quantity < 1 || value.Quantity > 500 || (value.Condition != "new" && value.Condition != "like_new" && value.Condition != "used"))
                throw new CrmException("Выберите состояние и количество от 1 до 500.");
            if (String.IsNullOrWhiteSpace(value.AppVersion) || value.AppVersion.Length > 40 || String.IsNullOrWhiteSpace(value.CapturedAt)
                || !DateTime.TryParse(value.CapturedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out captured) || captured.Kind != DateTimeKind.Utc)
                throw new CrmException("Не удалось подтвердить время сбора характеристик.");
            if (value.DetectedSpecs == null || value.DetectedSpecs.Count == 0 || Json().Serialize(value.DetectedSpecs).Length > 128 * 1024)
                throw new CrmException("Характеристики компьютера ещё не готовы.");
            ValidatePng(value.ScreenshotBase64);
            return Json().Serialize(new Dictionary<string, object> {
                { "client_intake_id", value.ClientIntakeId },
                { "selection", new { quantity = value.Quantity, condition = value.Condition } },
                { "source", new { provider = "ADAMO_GT", app_version = value.AppVersion, captured_at = value.CapturedAt, page = 1 } },
                { "detected_specs", value.DetectedSpecs },
                { "screenshot", new { mime = "image/png", @base64 = value.ScreenshotBase64 } }
            });
        }

        private static CrmQuickIntakeResult ParseResult(string body, CrmQuickSubmission pending)
        {
            var data = Parse(body);
            var product = Object(data, "product"); var receipt = Object(data, "receipt");
            long productId, intakeId, receiptId; int receiptQuantity;
            if (Text(data, "schema_version") != "2" || Text(data, "status") != "completed"
                || !Int64.TryParse(Text(product, "id"), out productId) || productId <= 0
                || !Int64.TryParse(Text(data, "intake_id"), out intakeId) || intakeId <= 0
                || !Int64.TryParse(Text(receipt, "id"), out receiptId) || receiptId <= 0
                || !Int32.TryParse(Text(receipt, "quantity"), out receiptQuantity) || receiptQuantity != pending.Quantity
                || Text(data, "client_intake_id") != pending.ClientIntakeId
                || !BooleanIs(product, "is_published", false) || !BooleanIs(product, "needs_enrichment", true))
                throw new CrmException("CRM не подтвердил созданный товар. Повторите сохранённую отправку.");
            var units = Array(data, "inventory_units");
            var unitIds = new HashSet<long>(); var codes = new HashSet<string>(StringComparer.Ordinal);
            if (units.Count != pending.Quantity) { throw new CrmException("CRM не подтвердил количество единиц. Повторите сохранённую отправку."); }
            foreach (var unitValue in units)
            {
                var unit = unitValue as Dictionary<string, object>; long unitId;
                string code = unit == null ? "" : Text(unit, "scan_code");
                if (unit == null || !Int64.TryParse(Text(unit, "id"), out unitId) || unitId <= 0 || !unitIds.Add(unitId)
                    || String.IsNullOrWhiteSpace(code) || !code.StartsWith("AD", StringComparison.Ordinal) || !codes.Add(code)
                    || Text(unit, "status") != "available")
                    throw new CrmException("CRM не подтвердил коды AD. Повторите сохранённую отправку.");
            }
            var image = Object(data, "screenshot"); var print = Object(data, "print");
            string imageStatus = Text(image, "status"), printStatus = Text(print, "status");
            long mediaId;
            bool imageConfirmed = imageStatus == "attached" && Int64.TryParse(Text(image, "media_id"), out mediaId) && mediaId > 0
                && String.Equals(Text(image, "sha256"), HashPng(pending.ScreenshotBase64), StringComparison.OrdinalIgnoreCase);
            if (imageStatus == "attached" && !imageConfirmed) { throw new CrmException("CRM не подтвердил полученную фотографию. Повторите сохранённую отправку."); }
            if (imageStatus != "attached" && imageStatus != "failed") { throw new CrmException("CRM не подтвердил статус фотографии. Повторите сохранённую отправку."); }
            int count; Guid jobId;
            bool printConfirmed = (printStatus == "queued" || printStatus == "processing" || printStatus == "completed")
                && Guid.TryParse(Text(print, "job_id"), out jobId) && jobId != Guid.Empty
                && Int32.TryParse(Text(print, "total_labels"), out count) && count == pending.Quantity;
            return new CrmQuickIntakeResult { ProductId = productId.ToString(CultureInfo.InvariantCulture), IntakeId = intakeId.ToString(CultureInfo.InvariantCulture),
                ImageStatus = imageStatus, PrintStatus = printStatus, Completed = imageConfirmed && printConfirmed };
        }

        private async Task<CrmHttpResponse> Request(string path, string body, string accessToken, string key, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                CrmHttpResponse response;
                try { response = await transport.SendAsync(new Uri(Origin, path), body, accessToken, key, timeout.Token); }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    throw new CrmException("CRM не ответил вовремя. Повторите сохранённую отправку.");
                }
                if (response != null && (response.StatusCode == 200 || response.StatusCode == 201)) { return response; }
                int status = response == null ? 0 : response.StatusCode;
                string message = "CRM временно недоступен. Повторите сохранённую отправку.";
                switch (status)
                {
                    case 401: bearer = null; expiresAtUtc = DateTime.MinValue; message = "Пароль не принят или сеанс истёк. Введите пароль ещё раз."; break;
                    case 403: bearer = null; expiresAtUtc = DateTime.MinValue; message = "Отправка доступна только из офиса."; break;
                    case 404: message = "Новая интеграция CRM ещё не включена."; break;
                    case 409: message = "CRM обнаружил конфликт отправки. Проверьте товар в CRM; новая отправка не создана."; break;
                    case 422: message = "CRM не принял данные. Отправка сохранена; проверьте состояние и количество."; break;
                    case 429: message = "Слишком много попыток. Подождите и повторите."; break;
                }
                throw new CrmException(message, status);
            }
        }

        private void OpenJournal()
        {
            Directory.CreateDirectory(journalRoot);
            try { journalLock = new FileStream(Path.Combine(journalRoot, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CrmException("Отправка в CRM уже открыта в другом окне ADAMO GT."); }
            try
            {
                string identityPath = Path.Combine(journalRoot, "client.id");
                journalPath = Path.Combine(journalRoot, "intake.pending");
                Guid identity;
                if (File.Exists(identityPath))
                {
                    if (new FileInfo(identityPath).Length > 128 || !Guid.TryParseExact(File.ReadAllText(identityPath, Encoding.UTF8).Trim(), "D", out identity) || identity == Guid.Empty)
                        throw new InvalidDataException();
                    clientId = identity.ToString("D");
                }
                else
                {
                    if (File.Exists(journalPath)) { throw new InvalidDataException(); }
                    clientId = Guid.NewGuid().ToString("D");
                    WriteAtomic(identityPath, Encoding.UTF8.GetBytes(clientId));
                }
                if (!File.Exists(journalPath)) { return; }
                if (new FileInfo(journalPath).Length > 8 * 1024 * 1024) { throw new InvalidDataException(); }
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(journalPath), null, DataProtectionScope.CurrentUser);
                CrmQuickPendingEnvelope saved;
                try { saved = Json().Deserialize<CrmQuickPendingEnvelope>(Encoding.UTF8.GetString(clear)); }
                finally { System.Array.Clear(clear, 0, clear.Length); }
                if (saved == null || saved.Version != 1 || saved.Origin != Origin.AbsoluteUri || saved.ClientId != clientId || BuildBody(saved.Submission) != saved.Body)
                    throw new InvalidDataException();
                Pending = saved.Submission; pendingBody = saved.Body;
            }
            catch
            {
                ReleaseJournal();
                throw new CrmException("Не удалось прочитать сохранённую отправку. Журнал сохранён; проверьте товар в CRM перед новой отправкой.");
            }
        }

        private void SaveJournal()
        {
            byte[] clear = Encoding.UTF8.GetBytes(Json().Serialize(new CrmQuickPendingEnvelope {
                Version = 1, Origin = Origin.AbsoluteUri, ClientId = clientId, Submission = Pending, Body = pendingBody }));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            finally { System.Array.Clear(clear, 0, clear.Length); }
            WriteAtomic(journalPath, encrypted);
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) { File.Replace(temporary, path, null); } else { File.Move(temporary, path); }
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }

        private void ClearJournal()
        {
            if (File.Exists(journalPath)) { File.Delete(journalPath); }
            Pending = null; pendingBody = null;
        }

        internal static byte[] ValidatePng(string encoded)
        {
            byte[] bytes;
            if (encoded == null || encoded.Length > 2796204) { throw new CrmException("Фото должно быть PNG размером до 2 MB."); }
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new CrmException("Фото первой страницы повреждено."); }
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 24 || bytes.Length > 2 * 1024 * 1024) { throw new CrmException("Фото должно быть PNG размером до 2 MB."); }
            for (int i = 0; i < signature.Length; i++) { if (bytes[i] != signature[i]) { throw new CrmException("Фото первой страницы повреждено."); } }
            return bytes;
        }

        internal static string HashPng(string encoded)
        { using (var hash = SHA256.Create()) { return BitConverter.ToString(hash.ComputeHash(ValidatePng(encoded))).Replace("-", "").ToLowerInvariant(); } }
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 32 }; }
        private static Dictionary<string, object> Parse(string body)
        {
            try
            {
                if (String.IsNullOrEmpty(body) || body.Length > 1024 * 1024) { throw new FormatException(); }
                var data = Json().DeserializeObject(body) as Dictionary<string, object>;
                if (data == null) { throw new FormatException(); }
                return data;
            }
            catch { throw new CrmException("Не удалось прочитать ответ CRM. Сохранённую отправку можно повторить."); }
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value is Dictionary<string, object> ? (Dictionary<string, object>)value : new Dictionary<string, object>(); }
        private static List<object> Array(Dictionary<string, object> data, string key)
        {
            object value; var list = new List<object>();
            if (data.TryGetValue(key, out value) && value is IList) { foreach (object item in (IList)value) { list.Add(item); } }
            return list;
        }
        private static bool BooleanIs(Dictionary<string, object> data, string key, bool expected)
        { object value; return data.TryGetValue(key, out value) && value is bool && (bool)value == expected; }
        private static string Text(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private void CheckDisposed() { if (disposed) { throw new ObjectDisposedException("CrmQuickIntakeClient"); } }
        private void BeginOperation() { lock (lifecycle) { CheckDisposed(); activeOperations++; } }
        private void EndOperation() { lock (lifecycle) { activeOperations--; if (disposed && activeOperations == 0) { ReleaseJournal(); } } }
        private void ReleaseJournal() { if (journalLock != null) { journalLock.Dispose(); journalLock = null; } }
        public virtual void Dispose()
        { lock (lifecycle) { disposed = true; bearer = null; expiresAtUtc = DateTime.MinValue; if (activeOperations == 0) { ReleaseJournal(); } } }
    }
}
