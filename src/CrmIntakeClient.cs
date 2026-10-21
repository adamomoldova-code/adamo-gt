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
    public sealed class CrmChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class CrmContext
    {
        public List<CrmChoice> Warehouses { get; set; }
        public List<CrmChoice> Printers { get; set; }
        public string OfficeIp { get; set; }
        public string UserId { get; set; }
        public bool CanPrint { get; set; }
        public string PrintReason { get; set; }
        public CrmContext() { Warehouses = new List<CrmChoice>(); Printers = new List<CrmChoice>(); }
    }
    public sealed class CrmSubmission
    {
        public string IdempotencyKey { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Condition { get; set; }
        public long WarehouseId { get; set; }
        public decimal? SalePrice { get; set; }
        public int Quantity { get; set; }
        public bool Print { get; set; }
        public string PrinterId { get; set; }
        public string SpecificationImageBase64 { get; set; }
    }
    public sealed class CrmIntakeResult
    {
        public string IntakeId { get; set; }
        public string ProductId { get; set; }
        public string Status { get; set; }
        public string PrintStatus { get; set; }
        public string PrintMessage { get; set; }
        public bool PrintRequested { get; set; }
        public string ProductUrl { get; set; }
        public string ImageStatus { get; set; }
        public string ImageMessage { get; set; }
    }
    public sealed class CrmException : Exception
    {
        public int StatusCode { get; private set; }
        public CrmException(string message, int statusCode = 0) : base(message) { StatusCode = statusCode; }
    }
    internal sealed class CrmHttpResponse
    {
        public int StatusCode;
        public string Body;
    }
    internal interface ICrmTransport
    {
        Task<CrmHttpResponse> SendAsync(Uri uri, string method, string body, string bearer, CancellationToken token);
    }
    internal sealed class CrmHttpTransport : ICrmTransport
    {
        public Task<CrmHttpResponse> SendAsync(Uri uri, string method, string body, string bearer, CancellationToken token)
        {
            return Task.Run(delegate
            {
                token.ThrowIfCancellationRequested();
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = method; request.AllowAutoRedirect = false;
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                request.Timeout = 90000; request.ReadWriteTimeout = 90000;
                request.Accept = "application/json"; request.ContentType = "application/json; charset=utf-8";
                request.Headers["X-ADAMO-AUTH-MODE"] = "access-only";
                request.Headers["X-ADAMO-ID-NAMESPACE"] = "local";
                if (!String.IsNullOrEmpty(bearer)) { request.Headers["Authorization"] = "Bearer " + bearer; }
                using (token.Register(request.Abort))
                {
                    try
                    {
                        if (body != null)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(body);
                            request.ContentLength = bytes.Length;
                            using (Stream stream = request.GetRequestStream()) { stream.Write(bytes, 0, bytes.Length); }
                        }
                        using (var response = (HttpWebResponse)request.GetResponse()) { return Read(response); }
                    }
                    catch (WebException error)
                    {
                        token.ThrowIfCancellationRequested();
                        var response = error.Response as HttpWebResponse;
                        if (response != null) { using (response) { return Read(response); } }
                        throw new CrmException("CRM не ответил. Если отправка уже началась, повторите её: сохранённая операция не создаст дубликат.");
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
                    if (output.Length + count > 1024 * 1024) { throw new CrmException("Ответ CRM слишком большой. Операция сохранена для повторной проверки."); }
                    output.Write(buffer, 0, count);
                }
                return new CrmHttpResponse { StatusCode = (int)response.StatusCode, Body = Encoding.UTF8.GetString(output.ToArray()) };
            }
        }
    }
    internal sealed class CrmPendingEnvelope
    {
        public string Origin { get; set; }
        public string UserId { get; set; }
        public CrmSubmission Submission { get; set; }
        public string Body { get; set; }
    }
    public class CrmIntakeClient : IDisposable
    {
        public const string DefaultBaseUrl = "https://crm.adamo.md";
        private readonly Uri origin;
        private readonly ICrmTransport transport;
        private readonly string journalRoot;
        private readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
        private readonly object lifecycle = new object();
        private int activeOperations;
        private string bearer, userId, journalPath, pendingBody;
        private FileStream journalLock;
        private bool disposed;
        public CrmSubmission Pending { get; protected set; }
        public bool HasPending { get { return Pending != null; } }

        public CrmIntakeClient() : this(DefaultBaseUrl, new CrmHttpTransport(), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADAMO GT", "crm")) { }

        internal CrmIntakeClient(string baseUrl, ICrmTransport transport, string journalRoot)
        {
            Uri parsed;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed) || parsed.Scheme != "https"
                || parsed.AbsolutePath != "/" || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 || parsed.UserInfo.Length != 0)
                throw new ArgumentException("CRM требует HTTPS-адрес без пути и учётных данных.");
            origin = parsed; this.transport = transport; this.journalRoot = journalRoot;
        }
        public virtual async Task<CrmContext> LoginAsync(string email, string password, CancellationToken token)
        {
            CheckDisposed();
            if (String.IsNullOrWhiteSpace(email) || String.IsNullOrEmpty(password)) { throw new CrmException("Введите логин и пароль CRM."); }
            if (journalLock != null) { throw new CrmException("Для смены сотрудника закройте и снова откройте окно CRM."); }
            var response = await Request("/v1/auth/login", "POST", Json().Serialize(new { login = email.Trim(), password = password }), null, token);
            CheckDisposed();
            var data = Parse(response.Body);
            bearer = Text(data, "accessToken");
            if (String.IsNullOrWhiteSpace(bearer) || bearer.IndexOfAny(new[] { '\r', '\n' }) >= 0) { bearer = null; throw new CrmException("CRM вернул некорректный сеанс входа."); }
            try { return await GetContextAsync(token); }
            catch { bearer = null; throw; }
        }
        public virtual async Task<CrmContext> GetContextAsync(CancellationToken token)
        {
            BeginOperation();
            try { return await GetContextCoreAsync(token); }
            finally { EndOperation(); }
        }
        private async Task<CrmContext> GetContextCoreAsync(CancellationToken token)
        {
            CheckAuthenticated();
            var response = await Request("/v1/adamo-gt/context", "GET", null, bearer, token);
            CheckDisposed();
            var data = Parse(response.Body);
            string actor = Text(data, "user_id"); long numericActor;
            if (!Int64.TryParse(actor, out numericActor) || numericActor <= 0) { throw new CrmException("CRM не подтвердил сотрудника."); }
            if (userId != null && userId != actor) { throw new CrmException("Сотрудник CRM изменился. Откройте окно заново."); }
            userId = actor;
            OpenJournal();
            return new CrmContext { UserId = actor, OfficeIp = Text(data, "office_ip"), CanPrint = Boolean(data, "can_print"),
                PrintReason = Text(data, "print_reason"), Warehouses = Choices(data, "warehouses"), Printers = Choices(data, "printers") };
        }
        public virtual async Task<CrmIntakeResult> SubmitAsync(CrmSubmission submission, CancellationToken token)
        {
            BeginOperation();
            try { return await SubmitCoreAsync(submission, token); }
            finally { EndOperation(); }
        }
        private async Task<CrmIntakeResult> SubmitCoreAsync(CrmSubmission submission, CancellationToken token)
        {
            CheckAuthenticated();
            if (userId == null || journalLock == null) { throw new CrmException("Сначала подтвердите доступ к CRM из офиса."); }
            if (!await sending.WaitAsync(0, token)) { throw new CrmException("Отправка уже выполняется."); }
            try
            {
                string body = BuildBody(submission);
                bool replay = Pending != null;
                if (Pending != null && body != pendingBody) { throw new CrmException("Есть незавершённая отправка. Повторите её без изменения данных."); }
                if (Pending == null)
                {
                    Pending = Json().Deserialize<CrmSubmission>(Json().Serialize(submission));
                    pendingBody = body;
                    try { SaveJournal(); }
                    catch { Pending = null; pendingBody = null; throw new CrmException("Не удалось сохранить операцию. Товар не отправлен в CRM."); }
                }
                CrmHttpResponse response;
                try { response = await Request("/v1/adamo-gt/product-intakes", "POST", pendingBody, bearer, token); }
                catch (CrmException error)
                {
                    // A replay may be rejected after an earlier request committed but lost its reply.
                    if (error.StatusCode == 422 && !replay) { ClearJournal(); }
                    throw;
                }
                var data = Parse(response.Body);
                var products = Array(data, "products");
                var product = products.Count > 0 ? products[0] as Dictionary<string, object> : null;
                string productId = product == null ? "" : Text(product, "id");
                if (String.IsNullOrEmpty(productId) && product != null) { productId = Text(product, "product_id"); }
                long numericId;
                long intakeId;
                if (products.Count != 1 || !Int64.TryParse(productId, out numericId) || numericId <= 0
                    || !Int64.TryParse(Text(data, "id"), out intakeId) || intakeId <= 0
                    || Text(data, "idempotency_key") != Pending.IdempotencyKey
                    || (Text(data, "status") != "completed" && Text(data, "status") != "print_failed"))
                    throw new CrmException("CRM не подтвердил созданный товар. Повторите сохранённую операцию.");
                var print = Object(data, "print"); var photo = Object(data, "specification_image");
                string printStatus = Text(print, "status");
                bool recognizedPrint = printStatus == "queued" || printStatus == "processing" || printStatus == "completed"
                    || printStatus == "failed" || printStatus == "paused" || printStatus == "cancelled";
                if (Boolean(print, "requested") != Pending.Print || (Pending.Print ? !recognizedPrint : printStatus != "not_requested"))
                    throw new CrmException("CRM не подтвердил статус стикеров. Повторите сохранённую операцию.");
                if (Text(photo, "status") == "attached")
                {
                    string expectedHash;
                    using (var sha = SHA256.Create()) { expectedHash = BitConverter.ToString(sha.ComputeHash(Convert.FromBase64String(Pending.SpecificationImageBase64))).Replace("-", "").ToLowerInvariant(); }
                    if (!String.Equals(Text(photo, "sha256"), expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new CrmException("CRM не подтвердил загруженное фото. Повторите сохранённую операцию.");
                }
                var result = new CrmIntakeResult { IntakeId = Text(data, "id"), ProductId = productId,
                    Status = Text(data, "status"), PrintRequested = Boolean(print, "requested"), PrintStatus = Text(print, "status"),
                    ProductUrl = new Uri(origin, "/products/" + numericId.ToString(CultureInfo.InvariantCulture) + "/edit").AbsoluteUri,
                    ImageStatus = Text(photo, "status") };
                result.PrintMessage = !result.PrintRequested ? "Печать не запрошена."
                    : result.PrintStatus == "failed" || result.PrintStatus == "cancelled" || result.PrintStatus == "paused"
                    ? "Товар сохранён; печать требует внимания. Проверьте задание в CRM."
                    : result.PrintStatus == "completed" ? "Print Bridge подтвердил выполнение задания."
                    : "Стикеры переданы в очередь CRM. Фактическую печать подтверждает Print Bridge.";
                result.ImageMessage = result.ImageStatus == "attached" ? "Фото характеристик прикреплено."
                    : "Товар создан, но загрузка фото не подтверждена. Повторите эту операцию для загрузки фото.";
                if (result.ImageStatus == "attached") { ClearJournal(); }
                return result;
            }
            finally { sending.Release(); }
        }
        internal static string BuildBody(CrmSubmission value)
        {
            Guid key, printer;
            if (value == null || !Guid.TryParse(value.IdempotencyKey, out key) || key == Guid.Empty)
                throw new CrmException("Не задан идентификатор операции.");
            if (String.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 255 || String.IsNullOrWhiteSpace(value.Description) || value.Description.Length > 16000)
                throw new CrmException("Проверьте название и характеристики товара.");
            if (value.WarehouseId <= 0 || value.Quantity < 1 || value.Quantity > 500 ||
                (value.Condition != "new" && value.Condition != "like_new" && value.Condition != "used"))
                throw new CrmException("Выберите склад, состояние и количество от 1 до 500.");
            if (value.SalePrice.HasValue && (value.SalePrice.Value < 0 || value.SalePrice.Value > 999999999999.99m
                || Decimal.Round(value.SalePrice.Value, 2) != value.SalePrice.Value)) { throw new CrmException("Проверьте цену в MDL (не более двух знаков после запятой)."); }
            if (value.Print && !Guid.TryParse(value.PrinterId, out printer)) { throw new CrmException("Выберите принтер CRM."); }
            byte[] png;
            if (value.SpecificationImageBase64 == null || value.SpecificationImageBase64.Length > 2796204)
                throw new CrmException("Фото должно быть PNG размером до 2 MB.");
            try { png = Convert.FromBase64String(value.SpecificationImageBase64 ?? ""); }
            catch (FormatException) { throw new CrmException("Фото характеристик повреждено."); }
            if (png.Length < 24 || png.Length > 2 * 1024 * 1024 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71
                || png[4] != 13 || png[5] != 10 || png[6] != 26 || png[7] != 10) { throw new CrmException("Фото должно быть PNG размером до 2 MB."); }
            var item = new Dictionary<string, object> { { "name_ru", value.Name }, { "description", value.Description },
                { "condition", value.Condition }, { "quantity", value.Quantity } };
            if (value.SalePrice.HasValue) { item["sale_price"] = value.SalePrice.Value; }
            var print = new Dictionary<string, object> { { "enabled", value.Print } };
            if (value.Print) { print["device_id"] = value.PrinterId; }
            return Json().Serialize(new Dictionary<string, object> { { "idempotency_key", value.IdempotencyKey }, { "warehouse_id", value.WarehouseId },
                { "items", new[] { item } }, { "print", print }, { "specification_image", new { mime = "image/png", @base64 = value.SpecificationImageBase64 } } });
        }
        private async Task<CrmHttpResponse> Request(string path, string method, string body, string accessToken, CancellationToken token)
        {
            var response = await transport.SendAsync(new Uri(origin, path), method, body, accessToken, token);
            if (response.StatusCode >= 200 && response.StatusCode <= 299) { return response; }
            string message = "CRM временно недоступен. Сохранённую отправку можно повторить.";
            switch (response.StatusCode)
            {
                case 400: message = "CRM отклонил запрос. Проверьте введённые данные."; break;
                case 401: message = "Вход не выполнен или сеанс истёк. Проверьте логин и пароль; сохранённая отправка останется доступной после повторного входа."; break;
                case 403: message = "CRM отказал в доступе. Отправка и печать доступны только из офиса и сотрудникам с нужными правами."; break;
                case 404: message = "Интеграция ADAMO GT ещё не включена на сервере CRM."; break;
                case 409: message = "CRM обнаружил конфликт операции. Не создавайте новую отправку: проверьте товар в CRM."; break;
                case 422: message = "CRM не принял данные. Проверьте состояние, количество, склад, цену и доступность принтера."; break;
                case 429: message = "Слишком много запросов. Подождите и повторите сохранённую операцию."; break;
            }
            throw new CrmException(message, response.StatusCode);
        }
        private void OpenJournal()
        {
            if (journalLock != null) { return; }
            Directory.CreateDirectory(journalRoot);
            string name;
            using (var hash = SHA256.Create()) { name = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(origin.AbsoluteUri + "|" + userId))).Replace("-", ""); }
            journalPath = Path.Combine(journalRoot, name + ".pending");
            try { journalLock = new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new CrmException("Отправка в CRM уже открыта в другом окне ADAMO GT."); }
            try
            {
                if (!File.Exists(journalPath)) { return; }
                if (new FileInfo(journalPath).Length > 6 * 1024 * 1024) { throw new InvalidDataException(); }
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(journalPath), null, DataProtectionScope.CurrentUser);
                CrmPendingEnvelope envelope;
                try { envelope = Json().Deserialize<CrmPendingEnvelope>(Encoding.UTF8.GetString(clear)); }
                finally { System.Array.Clear(clear, 0, clear.Length); }
                if (envelope == null || envelope.Origin != origin.AbsoluteUri || envelope.UserId != userId || BuildBody(envelope.Submission) != envelope.Body) { throw new InvalidDataException(); }
                Pending = envelope.Submission; pendingBody = envelope.Body;
            }
            catch { journalLock.Dispose(); journalLock = null; throw new CrmException("Не удалось прочитать незавершённую операцию. Проверьте товар в CRM перед новой отправкой; журнал сохранён."); }
        }
        private void SaveJournal()
        {
            byte[] clear = Encoding.UTF8.GetBytes(Json().Serialize(new CrmPendingEnvelope { Origin = origin.AbsoluteUri, UserId = userId, Submission = Pending, Body = pendingBody }));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            finally { System.Array.Clear(clear, 0, clear.Length); }
            string temporary = journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true); }
                if (File.Exists(journalPath)) { File.Replace(temporary, journalPath, null); } else { File.Move(temporary, journalPath); }
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }
        private void ClearJournal()
        {
            // A failure to remove the receipt keeps the same key available for safe replay.
            if (File.Exists(journalPath)) { File.Delete(journalPath); }
            Pending = null; pendingBody = null;
        }
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 32 }; }
        private static Dictionary<string, object> Parse(string body)
        {
            try
            {
                var data = Json().DeserializeObject(body) as Dictionary<string, object>;
                if (data == null) { throw new FormatException(); }
                if (data.ContainsKey("data") && data["data"] is Dictionary<string, object>) { return (Dictionary<string, object>)data["data"]; }
                return data;
            }
            catch { throw new CrmException("Не удалось прочитать ответ CRM. Повторите сохранённую операцию."); }
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value is Dictionary<string, object> ? (Dictionary<string, object>)value : new Dictionary<string, object>(); }
        private static List<object> Array(Dictionary<string, object> data, string key)
        { object value; var list = new List<object>(); if (data.TryGetValue(key, out value) && value is IEnumerable && !(value is string)) { foreach (object item in (IEnumerable)value) { list.Add(item); } } return list; }
        private static string Text(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        private static bool Boolean(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value is bool && (bool)value; }
        private static List<CrmChoice> Choices(Dictionary<string, object> data, string key)
        {
            var choices = new List<CrmChoice>();
            foreach (object item in Array(data, key))
            {
                var entry = item as Dictionary<string, object>;
                if (entry != null && Text(entry, "id").Length > 0) { choices.Add(new CrmChoice { Id = Text(entry, "id"), Name = Text(entry, "name") }); }
            }
            return choices;
        }
        private void CheckAuthenticated() { CheckDisposed(); if (String.IsNullOrEmpty(bearer)) { throw new CrmException("Войдите в CRM своей учётной записью."); } }
        private void CheckDisposed() { if (disposed) { throw new ObjectDisposedException("CrmIntakeClient"); } }
        private void BeginOperation() { lock (lifecycle) { CheckDisposed(); activeOperations++; } }
        private void EndOperation() { lock (lifecycle) { activeOperations--; if (disposed && activeOperations == 0) { ReleaseJournalLock(); } } }
        private void ReleaseJournalLock() { if (journalLock != null) { journalLock.Dispose(); journalLock = null; } }
        public virtual void Dispose() { lock (lifecycle) { disposed = true; bearer = null; if (activeOperations == 0) { ReleaseJournalLock(); } } }
    }
}
