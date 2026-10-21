using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PcPulse
{
    internal sealed class HardwareCatalog
    {
        private const int MaximumBytes = 2 * 1024 * 1024;
        private const int MaximumRecords = 2000;
        private static readonly Lazy<HardwareCatalog> defaultCatalog = new Lazy<HardwareCatalog>(delegate
        {
            return LoadCore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"), true);
        });
        private readonly ModelDocument models;
        private readonly PanelDocument panels;

        private HardwareCatalog(ModelDocument modelDocument, PanelDocument panelDocument)
        {
            models = modelDocument;
            panels = panelDocument;
        }

        internal static HardwareCatalog Default { get { return defaultCatalog.Value; } }

        internal static HardwareCatalog Load(string directory, bool allowEmbedded = false)
        {
            return LoadCore(directory, allowEmbedded);
        }

        internal void Apply(Snapshot computer)
        {
            if (computer == null) { throw new ArgumentNullException("computer"); }
            computer.RamManufacturerMaximumBytes = null;
            computer.RamManufacturerMaximumSource = "";
            ModelRecord model = FindModel(computer);
            if (model != null && model.MaximumRamBytes.HasValue)
            {
                computer.RamManufacturerMaximumBytes = model.MaximumRamBytes;
                computer.RamManufacturerMaximumSource = SourceText(model.Source, models.Revision);
            }
            if (computer.Screens == null) { return; }
            foreach (ScreenSnapshot screen in computer.Screens)
            {
                if (screen == null) { continue; }
                string id = PanelIdentity(screen.InstanceName);
                if (id.Length == 0) { continue; }
                List<PanelRecord> matches = panels.Panels.Where(item => item.PanelId.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (matches.Count != 1) { continue; }
                PanelRecord panel = matches[0];
                if (String.IsNullOrWhiteSpace(screen.MatrixType) || screen.MatrixType == "Не подтверждён") { screen.MatrixType = panel.MatrixType; }
                if (!screen.BrightnessNits.HasValue) { screen.BrightnessNits = panel.BrightnessNits; }
                string source = "Паспорт панели (яркость типовая, не измеренная): " + SourceText(panel.Source, panels.Revision);
                if ((screen.Source ?? "").IndexOf(source, StringComparison.Ordinal) < 0) { screen.Source = (screen.Source ?? "") + "\n" + source; }
            }
        }

        internal PortSpecification FindPorts(Snapshot computer)
        {
            if (computer == null) { throw new ArgumentNullException("computer"); }
            ModelRecord item = FindModel(computer);
            if (item == null || item.Ports.Count == 0) { return null; }
            return new PortSpecification
            {
                Model = computer.ComputerModel,
                Status = "Локальный справочник производителя",
                SourceUrl = item.Source.Url,
                SourceTitle = item.Source.Title,
                CheckedAt = ParseDate(item.Source.CheckedOn),
                IsCached = true,
                Note = "Проверено " + item.Source.CheckedOn + "; каталог " + models.Revision + ". " + item.Note,
                Ports = item.Ports.Select(port => new PortEntry { Name = port.Name, Count = port.Count, Optional = port.Optional, Detail = port.Detail }).ToList()
            };
        }

        private ModelRecord FindModel(Snapshot computer)
        {
            string manufacturer = Identity(computer.ComputerManufacturer), model = Identity(computer.ComputerModel), sku = Identity(computer.ComputerSku);
            if (!UsableIdentity(manufacturer) || !UsableIdentity(model)) { return null; }
            List<ModelRecord> matches = models.Models.Where(item =>
                Identity(item.Manufacturer) == manufacturer &&
                (Identity(item.Model) == model || item.ModelAliases.Any(alias => Identity(alias) == model)) &&
                (item.Skus.Count == 0 || (UsableIdentity(sku) && item.Skus.Any(value => Identity(value) == sku)))).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static HardwareCatalog LoadCore(string directory, bool allowEmbedded)
        {
            ModelDocument modelDocument = ReadDocument<ModelDocument>(directory, "models.json", allowEmbedded, ValidModels)
                ?? new ModelDocument { Models = new List<ModelRecord>() };
            PanelDocument panelDocument = ReadDocument<PanelDocument>(directory, "panels.json", allowEmbedded, ValidPanels)
                ?? new PanelDocument { Panels = new List<PanelRecord>() };
            return new HardwareCatalog(modelDocument, panelDocument);
        }

        private static T ReadDocument<T>(string directory, string filename, bool allowEmbedded, Func<T, bool> validate) where T : class
        {
            T document = null;
            try
            {
                if (!String.IsNullOrWhiteSpace(directory))
                {
                    using (FileStream stream = new FileStream(Path.Combine(directory, filename), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        document = Deserialize<T>(stream, validate);
                    }
                }
            }
            catch (Exception) { }
            if (document != null || !allowEmbedded) { return document; }
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PcPulse.Data." + filename))
                {
                    return Deserialize<T>(stream, validate);
                }
            }
            catch (Exception) { return null; }
        }

        private static T Deserialize<T>(Stream stream, Func<T, bool> validate) where T : class
        {
            if (stream == null || (stream.CanSeek && stream.Length > MaximumBytes)) { return null; }
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] block = new byte[4096];
                int count;
                while ((count = stream.Read(block, 0, block.Length)) > 0)
                {
                    if (buffer.Length + count > MaximumBytes) { return null; }
                    buffer.Write(block, 0, count);
                }
                string json = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
                T document = new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 24 }.Deserialize<T>(json);
                return document != null && validate(document) ? document : null;
            }
        }

        private static bool ValidModels(ModelDocument document)
        {
            if (document.SchemaVersion != 1 || !Text(document.Revision, 40) || document.Models == null || document.Models.Count > MaximumRecords) { return false; }
            foreach (ModelRecord item in document.Models)
            {
                if (item == null || !Text(item.Id, 100) || !UsableIdentity(Identity(item.Manufacturer)) || !UsableIdentity(Identity(item.Model)) ||
                    !ValidIdentities(item.ModelAliases) || !ValidIdentities(item.Skus) || !ValidSource(item.Source) ||
                    !PortLookup.IsAllowedSource(item.Source.Url, item.Manufacturer) || item.Note == null || item.Note.Length > 1000 ||
                    item.Ports == null || item.Ports.Count > 16 || (!item.MaximumRamBytes.HasValue && item.Ports.Count == 0)) { return false; }
                if (item.MaximumRamBytes.HasValue && (item.MaximumRamBytes.Value < (1UL << 30) || item.MaximumRamBytes.Value > (16UL << 40))) { return false; }
                HashSet<string> ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PortEntry port in item.Ports)
                {
                    if (port == null || !Text(port.Name, 80) || port.Count < 1 || port.Count > 32 || port.Detail == null || port.Detail.Length > 500 ||
                        !ports.Add(port.Name.Trim())) { return false; }
                }
            }
            return true;
        }

        private static bool ValidPanels(PanelDocument document)
        {
            if (document.SchemaVersion != 1 || !Text(document.Revision, 40) || document.Panels == null || document.Panels.Count > MaximumRecords) { return false; }
            foreach (PanelRecord item in document.Panels)
            {
                if (item == null || !ValidPanelId(item.PanelId) || !Text(item.MatrixType, 60) || !ValidSource(item.Source) || !PanelSource(item.Source.Url) ||
                    !item.BrightnessNits.HasValue || Double.IsNaN(item.BrightnessNits.Value) || Double.IsInfinity(item.BrightnessNits.Value) ||
                    item.BrightnessNits.Value < 10 || item.BrightnessNits.Value > 10000) { return false; }
            }
            return true;
        }

        private static bool ValidSource(CatalogSource source)
        {
            if (source == null || !Text(source.Url, 2000) || !Text(source.Title, 300) || !Text(source.CheckedOn, 10)) { return false; }
            DateTime checkedOn;
            Uri uri;
            return DateTime.TryParseExact(source.CheckedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out checkedOn) &&
                checkedOn.Year >= 2000 && checkedOn <= DateTime.UtcNow.Date &&
                Uri.TryCreate(source.Url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && !uri.IsLoopback;
        }

        private static bool PanelSource(string url)
        {
            string host = new Uri(url).DnsSafeHost;
            string[] domains = { "auo.com", "lgdisplay.com", "boe.com", "innolux.com", "samsungdisplay.com", "sharp.co.jp", "sharp-nec-displays.com" };
            return domains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) ||
                new[] { "HP", "Dell", "Lenovo", "ASUS", "Acer", "MSI", "Microsoft" }.Any(manufacturer => PortLookup.IsAllowedSource(url, manufacturer));
        }

        private static bool ValidIdentities(List<string> values)
        {
            return values != null && values.Count <= 32 && values.All(value => UsableIdentity(Identity(value)));
        }

        private static string Identity(string value)
        {
            return String.IsNullOrWhiteSpace(value) || value.Length > 160 ? "" : Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
        }

        private static bool UsableIdentity(string value)
        {
            return value.Length > 0 && value != "UNKNOWN" && value != "DEFAULT STRING" && value != "SYSTEM PRODUCT NAME" &&
                value != "SYSTEM MODEL" && value != "TO BE FILLED BY O.E.M." && value != "ALL SERIES" && value != "NOT APPLICABLE" && value != "N/A";
        }

        private static bool Text(string value, int maximum)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(Char.IsControl);
        }

        private static string PanelIdentity(string instance)
        {
            if (String.IsNullOrWhiteSpace(instance) || instance.Length > 512) { return ""; }
            string[] parts = ScreenInventory.NormalizeInstance(instance).Split('\\');
            return parts.Length >= 3 && (parts[0] == "DISPLAY" || parts[0] == "MONITOR") && ValidPanelId(parts[1]) ? parts[1] : "";
        }

        private static bool ValidPanelId(string id)
        {
            return id != null && Regex.IsMatch(id, @"\A[A-Za-z]{3}[0-9A-Fa-f]{4}\z");
        }

        private static DateTime ParseDate(string value)
        {
            return DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        private static string SourceText(CatalogSource source, string revision)
        {
            return source.Title + " · проверено " + source.CheckedOn + " · каталог " + revision + "\n" + source.Url;
        }

        public sealed class ModelDocument
        {
            public int SchemaVersion;
            public string Revision;
            public List<ModelRecord> Models;
        }

        public sealed class PanelDocument
        {
            public int SchemaVersion;
            public string Revision;
            public List<PanelRecord> Panels;
        }

        public sealed class ModelRecord
        {
            public string Id;
            public string Manufacturer;
            public string Model;
            public List<string> ModelAliases = new List<string>();
            public List<string> Skus = new List<string>();
            public ulong? MaximumRamBytes;
            public CatalogSource Source;
            public List<PortEntry> Ports = new List<PortEntry>();
            public string Note = "";
        }

        public sealed class PanelRecord
        {
            public string PanelId;
            public string MatrixType;
            public double? BrightnessNits;
            public CatalogSource Source;
        }

        public sealed class CatalogSource
        {
            public string Url;
            public string Title;
            public string CheckedOn;
        }
    }
}
