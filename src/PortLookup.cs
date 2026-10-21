using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace PcPulse
{
    public sealed class PortLookup
    {
        private const int MaximumHtmlBytes = 2 * 1024 * 1024;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        internal static PortSpecification CachedOnly(Snapshot computer)
        {
            if (computer == null) { throw new ArgumentNullException("computer"); }
            PortSpecification saved = ReadCache(computer);
            if (saved != null) { return AsCached(saved, "Сохранённая спецификация"); }
            PortSpecification catalog = HardwareCatalog.Default.FindPorts(computer);
            if (catalog != null) { return catalog; }
            return new PortSpecification { Model = computer.ComputerModel, Status = "Интернет-поиск — по запросу", Note = "В локальном справочнике пока нет данных. Нажмите обновление или «Поиск», чтобы разрешить поиск по модели в интернете." };
        }

        public PortSpecification Lookup(Snapshot computer, CancellationToken token, bool forceRefresh = false)
        {
            token.ThrowIfCancellationRequested();
            if (computer == null) { throw new ArgumentNullException("computer"); }
            PortSpecification unavailable = new PortSpecification
            {
                Model = computer.ComputerModel,
                Status = "Не удалось подтвердить порты",
                Note = "Откройте поиск спецификации. Количество разъёмов зависит от модели и комплектации."
            };
            if (!UsableModel(computer.ComputerModel) || ManufacturerDomain(computer.ComputerManufacturer).Length == 0)
            {
                unavailable.Note = "Производитель или точная модель не определены. Windows не сообщает все физические разъёмы корпуса.";
                return unavailable;
            }

            PortSpecification saved = ReadCache(computer);
            PortSpecification catalog = HardwareCatalog.Default.FindPorts(computer);
            if (!forceRefresh && saved != null && saved.CheckedAt.HasValue &&
                DateTime.UtcNow - saved.CheckedAt.Value.ToUniversalTime() < TimeSpan.FromDays(7))
            {
                return AsCached(saved, "Сохранённая спецификация");
            }

            Stopwatch elapsed = Stopwatch.StartNew();
            HashSet<string> attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> candidates = new List<string>();
            if (catalog != null) { candidates.Add(catalog.SourceUrl); }
            PortSpecification found = TryCandidates(computer, candidates, attempted, elapsed, token);
            if (found != null) { SaveCache(computer, found); return found; }

            try
            {
                string searchHtml = FetchHtml(SearchUrl(computer), computer.ComputerManufacturer, true, elapsed, token);
                candidates = SearchCandidates(searchHtml, computer);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { candidates = new List<string>(); }
            found = TryCandidates(computer, candidates, attempted, elapsed, token);
            if (found != null) { SaveCache(computer, found); return found; }
            token.ThrowIfCancellationRequested();
            if (saved != null) { return AsCached(saved, "Сайт недоступен · сохранённая спецификация"); }
            if (catalog != null)
            {
                catalog.Status = "Сайт недоступен · локальный справочник";
                catalog.Note += " Интернет-проверка сейчас не удалась.";
                return catalog;
            }
            unavailable.Note = "Официальная спецификация не найдена, недоступна или её формат не распознан. Порты не подсчитываются по подключённым USB-устройствам.";
            return unavailable;
        }

        public static string SearchUrl(Snapshot computer)
        {
            string model = computer == null ? "" : CleanIdentity(computer.ComputerModel);
            string manufacturer = computer == null ? "" : CleanIdentity(computer.ComputerManufacturer);
            string domain = ManufacturerDomain(manufacturer);
            string query = manufacturer + " " + model + " specifications ports" + (domain.Length > 0 ? " site:" + domain : "");
            return "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query.Trim());
        }

        public static bool IsAllowedSource(string url, string manufacturer)
        {
            Uri uri;
            string domain = ManufacturerDomain(manufacturer);
            if (domain.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.IsLoopback)
            {
                return false;
            }
            string host = uri.DnsSafeHost.ToLowerInvariant();
            IPAddress literal;
            if (host.StartsWith("forums.") || host.StartsWith("forum.") || host.StartsWith("community.") ||
                host == "h30434.www3.hp.com" || Match(uri.AbsolutePath, @"/(?:community|forums?|t5)(?:/|$)").Success) { return false; }
            return !IPAddress.TryParse(host, out literal) && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
        }

        internal static bool ModelMatches(string model, string title)
        {
            if (!UsableModel(model)) { return false; }
            if (Match(title ?? "", @"\b(?:vs|versus|comparison|compare|dock(?:ing)?|adapter|accessor(?:y|ies)|cables?|hub)\b").Success) { return false; }
            List<string> expected = ModelTokens(model);
            HashSet<string> actual = new HashSet<string>(ModelTokens(title), StringComparer.OrdinalIgnoreCase);
            return expected.Count >= 2 && expected.Any(t => t.Any(Char.IsDigit)) && expected.All(actual.Contains) &&
                actual.Where(t => t.Any(Char.IsDigit)).All(expected.Contains);
        }

        internal static List<PortEntry> ExtractPorts(string html)
        {
            if (String.IsNullOrEmpty(html) || html.Length > MaximumHtmlBytes) { return new List<PortEntry>(); }
            html = RemoveHiddenHtml(html);
            List<PortSection> values = new List<PortSection>();
            foreach (Match row in Matches(html, @"<tr\b[^>]*>(.*?)</tr>"))
            {
                MatchCollection cells = Matches(row.Groups[1].Value, @"<(?:td|th)\b[^>]*>(.*?)</(?:td|th)>");
                if (cells.Count < 2) { continue; }
                string label = PlainText(cells[0].Groups[1].Value).Trim(' ', ':');
                if (!IsPortLabel(label)) { continue; }
                if (cells.Count != 2) { return new List<PortEntry>(); }
                string content = String.Join("\n", cells.Cast<Match>().Skip(1).Select(c => ClauseText(c.Groups[1].Value)));
                if (content.Length <= 8000 && content.Length > 0) { values.Add(new PortSection { Scope = SectionScope(label), Text = content }); }
            }
            if (values.Count == 0)
            {
                foreach (Match definition in Matches(html, @"<dt\b[^>]*>(.*?)</dt>\s*<dd\b[^>]*>(.*?)</dd>"))
                {
                    string label = PlainText(definition.Groups[1].Value);
                    if (IsPortLabel(label)) { values.Add(new PortSection { Scope = SectionScope(label), Text = ClauseText(definition.Groups[2].Value) }); }
                }
            }
            if (values.Count == 0)
            {
                foreach (Match section in Matches(html, @"<h([2-4])\b[^>]*>(.*?)</h\1>(.*?)(?=<h[1-4]\b|$)"))
                {
                    if (!IsPortLabel(PlainText(section.Groups[2].Value)) || section.Groups[3].Value.Length > 16000) { continue; }
                    values.Add(new PortSection { Scope = SectionScope(PlainText(section.Groups[2].Value)), Text = ClauseText(section.Groups[3].Value) });
                }
            }
            return ParsePortClauses(values);
        }

        private static PortSpecification TryCandidates(Snapshot computer, IEnumerable<string> candidates,
            HashSet<string> attempted, Stopwatch elapsed, CancellationToken token)
        {
            foreach (string url in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (attempted.Count >= 3 || elapsed.ElapsedMilliseconds >= 24000) { break; }
                if (!IsAllowedSource(url, computer.ComputerManufacturer) || !attempted.Add(url)) { continue; }
                try
                {
                    string html = FetchHtml(url, computer.ComputerManufacturer, false, elapsed, token);
                    string title = PageModelTitle(html, computer.ComputerModel);
                    if (title.Length == 0) { continue; }
                    if (!VariantMatches(computer, title)) { continue; }
                    List<PortEntry> ports = ExtractPorts(html);
                    if (ports.Count == 0 || !ports.Any(p => p.Name.StartsWith("USB", StringComparison.Ordinal) || p.Name == "HDMI" || p.Name == "Ethernet / RJ-45")) { continue; }
                    return new PortSpecification
                    {
                        Model = computer.ComputerModel,
                        Status = "По спецификации производителя",
                        SourceUrl = url,
                        SourceTitle = title,
                        CheckedAt = DateTime.UtcNow,
                        Ports = ports,
                        Note = "Разъёмы по спецификации модели. Набор может зависеть от SKU и комплектации; это не проверка свободных портов."
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
            }
            return null;
        }

        private static string FetchHtml(string url, string manufacturer, bool search, Stopwatch elapsed, CancellationToken token)
        {
            Uri current = new Uri(url);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            for (int redirect = 0; redirect <= 2; redirect++)
            {
                token.ThrowIfCancellationRequested();
                bool allowed = search ? current.Scheme == "https" && current.Port == 443 && current.UserInfo.Length == 0 &&
                    current.DnsSafeHost.Equals("html.duckduckgo.com", StringComparison.OrdinalIgnoreCase) : IsAllowedSource(current.AbsoluteUri, manufacturer);
                if (!allowed) { throw new IOException("Untrusted specification address."); }
                int remaining = Remaining(elapsed);
                EnsurePublicHost(current.DnsSafeHost, Math.Min(2500, remaining), token);
                remaining = Remaining(elapsed);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(current);
                request.Method = "GET";
                request.UserAgent = "ADAMO-GT/2.2 (Windows desktop hardware specifications)";
                request.Accept = "text/html,application/xhtml+xml";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.AllowAutoRedirect = false;
                request.Timeout = Math.Min(6500, remaining);
                request.ReadWriteTimeout = Math.Min(3000, remaining);
                request.UseDefaultCredentials = false;
                request.KeepAlive = false;
                using (token.Register(request.Abort))
                using (Timer timer = new Timer(delegate { request.Abort(); }, null, Math.Min(7000, remaining), Timeout.Infinite))
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            int code = (int)response.StatusCode;
                            if (code >= 300 && code < 400)
                            {
                                string location = response.Headers[HttpResponseHeader.Location];
                                if (redirect == 2 || String.IsNullOrEmpty(location)) { throw new IOException("Too many redirects."); }
                                current = new Uri(current, location);
                                continue;
                            }
                            if (code != 200 || response.ContentLength > MaximumHtmlBytes ||
                                (response.ContentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0))
                            {
                                throw new IOException("Unsupported specification response.");
                            }
                            using (Stream stream = response.GetResponseStream())
                            using (MemoryStream buffer = new MemoryStream())
                            {
                                byte[] block = new byte[8192];
                                int read;
                                while ((read = stream.Read(block, 0, block.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    Remaining(elapsed);
                                    if (buffer.Length + read > MaximumHtmlBytes) { throw new IOException("Specification too large."); }
                                    buffer.Write(block, 0, read);
                                }
                                Encoding encoding = Encoding.UTF8;
                                try { if (!String.IsNullOrEmpty(response.CharacterSet)) { encoding = Encoding.GetEncoding(response.CharacterSet); } }
                                catch (ArgumentException) { }
                                return encoding.GetString(buffer.ToArray());
                            }
                        }
                    }
                    catch (WebException) { token.ThrowIfCancellationRequested(); throw; }
                }
            }
            throw new IOException("Specification redirect failed.");
        }

        private static int Remaining(Stopwatch elapsed)
        {
            int remaining = 25000 - (int)elapsed.ElapsedMilliseconds;
            if (remaining <= 0) { throw new TimeoutException("Specification lookup timed out."); }
            return remaining;
        }

        private static void EnsurePublicHost(string host, int milliseconds, CancellationToken token)
        {
            IAsyncResult resolution = Dns.BeginGetHostAddresses(host, null, null);
            int completed = WaitHandle.WaitAny(new[] { resolution.AsyncWaitHandle, token.WaitHandle }, milliseconds);
            token.ThrowIfCancellationRequested();
            if (completed != 0) { throw new TimeoutException("DNS lookup timed out."); }
            IPAddress[] addresses = Dns.EndGetHostAddresses(resolution);
            if (addresses.Length == 0 || addresses.Any(IsPrivateAddress)) { throw new IOException("Non-public specification address."); }
        }

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) { return true; }
            if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
                    (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
            }
            return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc;
        }

        private static List<string> SearchCandidates(string html, Snapshot computer)
        {
            List<string> result = new List<string>();
            foreach (Match anchor in Matches(html, @"<a\b([^>]*\bclass\s*=\s*[""'][^""']*\bresult__a\b[^""']*[""'][^>]*)>(.*?)</a>"))
            {
                if (!ModelMatches(computer.ComputerModel, PlainText(anchor.Groups[2].Value))) { continue; }
                Match href = Match(anchor.Groups[1].Value, @"\bhref\s*=\s*[""']([^""']+)[""']");
                if (!href.Success) { continue; }
                string url = WebUtility.HtmlDecode(href.Groups[1].Value);
                Match destination = Match(url, @"[?&]uddg=([^&]+)");
                if (destination.Success) { url = Uri.UnescapeDataString(destination.Groups[1].Value); }
                if (IsAllowedSource(url, computer.ComputerManufacturer) && !url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { result.Add(url); }
                if (result.Count >= 6) { break; }
            }
            return result;
        }

        private static string PageModelTitle(string html, string model)
        {
            string clean = RemoveHiddenHtml(html);
            string productHeading = "";
            foreach (Match heading in Matches(clean, @"<h1\b[^>]*>(.*?)</h1>"))
            {
                string title = PlainText(heading.Groups[1].Value);
                if (title.Length > 240) { continue; }
                if (ModelMatches(model, title)) { productHeading = title; }
                else if (title.Any(Char.IsDigit) && ModelTokens(title).Count >= 2) { return ""; }
            }
            if (productHeading.Length > 0) { return productHeading; }
            foreach (Match heading in Matches(clean, @"<title\b[^>]*>(.*?)</title>"))
            {
                string title = PlainText(heading.Groups[1].Value);
                if (title.Length <= 240 && ModelMatches(model, title)) { return title; }
            }
            return "";
        }

        private static bool VariantMatches(Snapshot computer, string title)
        {
            bool intel = Match(title, @"\bIntel\b").Success;
            bool amd = Match(title, @"\bAMD\b").Success;
            if (intel && amd) { return false; }
            string cpu = computer.CpuName ?? "";
            if (intel && !Match(cpu, @"\bIntel\b").Success) { return false; }
            if (amd && !Match(cpu, @"\bAMD\b|\bRyzen\b").Success) { return false; }
            return true;
        }

        private static bool IsPortLabel(string text)
        {
            return Match(text.Trim(), @"^(?:(?:external\s+)?(?:i/o\s+)?ports?(?:\s*(?:and|&)\s*connectors)?|ports/slots/bays|ports\s*(?:&|and)\s*slots|(?:front|rear|left|right)(?:\s+side)?\s+ports|(?:left|right)\s+side|interfaces?|connectors?|slots|standard ports)$").Success;
        }

        private static string SectionScope(string label)
        {
            Match side = Match(label, @"^(left|right|front|rear)\b");
            if (side.Success) { return side.Groups[1].Value.ToLowerInvariant(); }
            return label.Equals("Slots", StringComparison.OrdinalIgnoreCase) ? "slots" : "ports";
        }

        private static List<PortEntry> ParsePortClauses(IEnumerable<PortSection> values)
        {
            List<PortEntry> entries = new List<PortEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> usbAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PortSection> sections = values.ToList();
            bool hasAggregate = sections.Any(s => s.Scope == "ports");
            foreach (PortSection section in sections)
            {
                if (hasAggregate && section.Scope != "ports" && section.Scope != "slots") { continue; }
                if (!seenSections.Add(section.Scope + "|" + section.Text)) { continue; }
                string separated = Replace(section.Text, @"(?<!\w)(?=\([1-9][0-9]?\)\s*(?:Thunderbolt|Super\s*Speed|USB|HDMI|DisplayPort|Headphone|Stereo|Audio|Ethernet|RJ|Smart|Nano|SD|MicroSD|Mini\s*DisplayPort))", "\n");
                foreach (string raw in separated.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).SelectMany(SplitCompoundPortClause))
                {
                    Match sidePrefix = Match(raw, @"^\s*(Left|Right|Front|Rear)(?:\s+side)?\s*:\s*");
                    string clauseScope = sidePrefix.Success ? section.Scope + ":" + sidePrefix.Groups[1].Value.ToLowerInvariant() : section.Scope;
                    string clause = Replace(raw, @"^\s*(?:Left|Right|Front|Rear)(?:\s+side)?\s*:?\s*", "").Trim();
                    Match quantity = Match(clause, @"^\s*(?:\(\s*(?<n>[1-9][0-9]?)\s*\)|(?<n>[1-9][0-9]?)\s*(?:x|×|ports?)?\s+|(?<n>one|two|three|four)\s+)(?<description>.+)$");
                    if (!quantity.Success) { continue; }
                    string countText = quantity.Groups["n"].Value.ToLowerInvariant();
                    int count;
                    if (!Int32.TryParse(countText, out count)) { count = new[] { "one", "two", "three", "four" }.ToList().IndexOf(countText) + 1; }
                    if (count < 1 || count > 32) { continue; }
                    string description = quantity.Groups["description"].Value.Trim();
                    if (description.Length > 500 || Match(description, @"\b(?:adapter|dock(?:ing)?|hub|cable|internal|header|optional module)\b").Success) { continue; }
                    // A shared count for Type-A and Type-C does not say how many of
                    // either socket exist. Do not let a later Type-C mention change
                    // the type of the first connector or invent separate quantities.
                    if (Match(description, @"\b(?:USB[\s-]*A|Type[\s-]*A)\b").Success &&
                        Match(description, @"\b(?:USB[\s-]*C|Type[\s-]*C)\b").Success) { return new List<PortEntry>(); }
                    string name = PortName(description);
                    if (name.Length == 0) { continue; }
                    if (name == "USB-C / Thunderbolt")
                    {
                        string aliasKey = clauseScope + "|" + count;
                        string previousAlias;
                        string alias = Match(description, @"^(?:Super\s*Speed\s+)?USB").Success ? "usb" : "thunderbolt";
                        if (usbAliases.TryGetValue(aliasKey, out previousAlias) && previousAlias != alias) { return new List<PortEntry>(); }
                        usbAliases[aliasKey] = alias;
                    }
                    bool optional = Match(description, @"\b(?:optional|option|select models|configuration dependent)\b").Success;
                    string key = clauseScope + "|" + name + "|" + count + "|" + optional + "|" + description;
                    if (!seen.Add(key)) { continue; }
                    PortEntry existing = entries.FirstOrDefault(e => e.Name == name && e.Optional == optional);
                    if (existing == null)
                    {
                        entries.Add(new PortEntry { Name = name, Count = count, Optional = optional, Detail = description });
                    }
                    else if (existing.Count + count <= 32)
                    {
                        existing.Count += count;
                        if (!existing.Detail.Contains(description)) { existing.Detail += "; " + description; }
                    }
                }
            }
            return entries.Take(16).ToList();
        }

        private static IEnumerable<string> SplitCompoundPortClause(string clause)
        {
            // Split only when another explicitly counted connector starts outside a
            // parenthetical description. Versions and alternate-mode capabilities
            // (e.g. DisplayPort 1.4 and Power Delivery 3.0) remain part of the socket.
            const string quantity = @"(?:\(\s*[1-9][0-9]?\s*\)\s*|[1-9][0-9]?\s*(?:x|×|ports?)?\s+|(?:one|two|three|four)\s+)";
            const string connector = @"(?:Thunderbolt|Super\s*Speed|USB|HDMI|(?:Mini\s*)?DisplayPort|Headphone|Microphone|Stereo|Audio|Ethernet|Gigabit|RJ|Smart|Nano|SIM|SD|Micro[\s-]*SD|Security|Kensington|DC[\s-]*in|Power\s+(?:connector|jack)|VGA)\b";
            int start = 0, scanned = 0, parentheses = 0;
            foreach (Match separator in Matches(clause, @"(?:,\s*(?:and\s+)?|\s+and\s+|\s*&\s*)(?=" + quantity + connector + @")"))
            {
                for (; scanned < separator.Index; scanned++)
                {
                    if (clause[scanned] == '(') { parentheses++; }
                    else if (clause[scanned] == ')' && parentheses > 0) { parentheses--; }
                }
                if (parentheses != 0) { continue; }
                yield return clause.Substring(start, separator.Index - start);
                start = separator.Index + separator.Length;
            }
            yield return clause.Substring(start);
        }

        private static string PortName(string description)
        {
            if (Match(description, @"^(?:Super\s*Speed\s+)?(?:Thunderbolt\b|USB(?:[\s-]*C\b|.*?Type[\s-]*C\b))").Success)
            {
                return Match(description, @"\bThunderbolt\b").Success ? "USB-C / Thunderbolt" : "USB-C";
            }
            if (Match(description, @"^(?:Super\s*Speed\s+)?USB(?:[\s-]*A\b|.*?Type[\s-]*A\b)").Success) { return "USB-A"; }
            if (Match(description, @"^USB(?:\s*[23](?:\.\d)?)?\s+ports?\b").Success) { return "USB (тип не указан)"; }
            if (Match(description, @"^HDMI\b").Success) { return "HDMI"; }
            if (Match(description, @"^(?:Mini[\s-]*)?DisplayPort\b").Success) { return description.StartsWith("Mini", StringComparison.OrdinalIgnoreCase) ? "Mini DisplayPort" : "DisplayPort"; }
            if (Match(description, @"^(?:Gigabit\s+)?(?:Ethernet|RJ[\s-]*45)\b").Success) { return "Ethernet / RJ-45"; }
            if (Match(description, @"^(?:(?:Stereo|Audio)\s+)?(?:Headphone|Microphone|Audio).*?(?:combo|combined)\b").Success) { return "Аудио · наушники/микрофон"; }
            if (Match(description, @"^(?:Headphone|Audio\s+out)\b").Success) { return "Выход для наушников"; }
            if (Match(description, @"^Microphone\b").Success) { return "Вход микрофона"; }
            if (Match(description, @"^Micro[\s-]*SD\b").Success) { return "microSD"; }
            if (Match(description, @"^(?:SD|SDXC)\b").Success) { return "SD-кардридер"; }
            if (Match(description, @"^Smart\s*card\b").Success) { return "Smart Card"; }
            if (Match(description, @"^(?:Nano[\s-]*)?SIM\b").Success) { return "SIM"; }
            if (Match(description, @"^(?:Nano\s+)?(?:Security|Kensington)\b").Success) { return "Слот замка безопасности"; }
            if (Match(description, @"^(?:DC[\s-]*in|Power\s+connector|Power\s+jack)\b").Success) { return "Питание"; }
            if (Match(description, @"^VGA\b").Success) { return "VGA"; }
            return "";
        }

        private static string ManufacturerDomain(string manufacturer)
        {
            string value = (manufacturer ?? "").Trim().ToLowerInvariant();
            if (value == "hp" || value.StartsWith("hp ") || value.StartsWith("hewlett")) { return "hp.com"; }
            if (value.StartsWith("dell")) { return "dell.com"; }
            if (value.StartsWith("lenovo")) { return "lenovo.com"; }
            if (value.StartsWith("asus")) { return "asus.com"; }
            if (value.StartsWith("acer")) { return "acer.com"; }
            if (value == "msi" || value.StartsWith("micro-star")) { return "msi.com"; }
            if (value.StartsWith("microsoft")) { return "microsoft.com"; }
            return "";
        }

        private static bool UsableModel(string model)
        {
            string cleaned = CleanIdentity(model).ToLowerInvariant();
            return cleaned.Length >= 3 && cleaned != "system product name" && cleaned != "system model" &&
                cleaned != "to be filled by o.e.m." && cleaned != "default string" && cleaned != "all series";
        }

        private static string CleanIdentity(string value)
        {
            string cleaned = Replace(value ?? "", @"[^\p{L}\p{N}\s().+_-]", " ").Trim();
            return cleaned.Substring(0, Math.Min(160, cleaned.Length));
        }

        private static List<string> ModelTokens(string value)
        {
            HashSet<string> ignored = new HashSet<string>(new[] { "hp", "hewlett", "packard", "dell", "lenovo", "asus", "asustek", "acer", "microsoft", "inc", "corporation", "computer", "computers", "pc", "mobile", "workstation", "notebook", "laptop", "inch", "inches", "specifications", "specification", "support", "technical", "details" });
            return Matches((value ?? "").ToLowerInvariant(), @"[a-z]+[0-9]+[a-z0-9]*|[0-9]+(?:\.[0-9]+)?[a-z]*|[a-z]+").Cast<Match>()
                .Select(m => m.Value).Where(v => !ignored.Contains(v)).Distinct().ToList();
        }

        private static string PlainText(string html)
        {
            return Replace(WebUtility.HtmlDecode(Replace(html ?? "", @"<[^>]*>", " ")), @"\s+", " ").Trim();
        }

        private static string ClauseText(string html)
        {
            string lines = Replace(html, @"<(?:br\b[^>]*|/(?:p|li|div))>", "\n");
            lines = WebUtility.HtmlDecode(Replace(lines, @"<[^>]*>", " "));
            return Replace(lines, @"[^\S\n]+", " ").Replace("\r", "").Trim();
        }

        private static string RemoveHiddenHtml(string html)
        {
            return Replace(html, @"<(script|style|noscript)\b[^>]*>.*?</\1>|<!--.*?-->", "");
        }

        private static Match Match(string input, string pattern) { return Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout); }
        private static MatchCollection Matches(string input, string pattern) { return Regex.Matches(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout); }
        private static string Replace(string input, string pattern, string replacement) { return Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout); }

        private static string Identity(Snapshot computer)
        {
            return (computer.ComputerManufacturer + "|" + computer.ComputerModel + "|" + computer.ComputerSku).Trim().ToLowerInvariant();
        }

        private static string CachePath(Snapshot computer)
        {
            string digest;
            using (SHA256 sha = SHA256.Create()) { digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Identity(computer)))).Replace("-", ""); }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcPulse", "specifications", digest + ".json");
        }

        private static PortSpecification ReadCache(Snapshot computer)
        {
            try
            {
                string path = CachePath(computer);
                if (!File.Exists(path) || new FileInfo(path).Length > 65536) { return null; }
                CacheEntry cache = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<CacheEntry>(File.ReadAllText(path));
                PortSpecification item = cache == null ? null : cache.Specification;
                if (cache == null || cache.Identity != Identity(computer) || item == null || item.Model != computer.ComputerModel ||
                    !item.CheckedAt.HasValue || item.CheckedAt.Value.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5) ||
                    !IsAllowedSource(item.SourceUrl, computer.ComputerManufacturer) || !ModelMatches(computer.ComputerModel, item.SourceTitle) ||
                    !VariantMatches(computer, item.SourceTitle) ||
                    item.Ports == null || item.Ports.Count == 0 || item.Ports.Count > 16 ||
                    item.Ports.Any(p => p == null || p.Count < 1 || p.Count > 32 || String.IsNullOrEmpty(p.Name) || p.Name.Length > 80)) { return null; }
                return item;
            }
            catch (Exception) { return null; }
        }

        private static void SaveCache(Snapshot computer, PortSpecification item)
        {
            try
            {
                string path = CachePath(computer);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(new CacheEntry { Identity = Identity(computer), Specification = item }), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        private static PortSpecification AsCached(PortSpecification item, string status)
        {
            item.IsCached = true;
            item.Status = status;
            item.Note = "Сохранённые данные производителя от " + item.CheckedAt.Value.ToLocalTime().ToString("dd.MM.yyyy") + ". Опции зависят от комплектации.";
            return item;
        }

        public sealed class CacheEntry
        {
            public string Identity;
            public PortSpecification Specification;
        }

        private sealed class PortSection
        {
            public string Scope;
            public string Text;
        }
    }
}
