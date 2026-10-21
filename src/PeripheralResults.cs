using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace PcPulse
{
    // Only a user's verdict is persisted. No images, audio or individual keystrokes.
    public sealed class PeripheralTestResult
    {
        public string Kind;
        public string Outcome;
        public string DeviceId;
        public string DeviceName;
        public string Detail;
        public string TestedAtUtc;
        public string ConfirmedBy = "user";

        internal static bool Valid(PeripheralTestResult value)
        {
            DateTime date;
            return value != null && new[] { "camera", "microphone", "keyboard", "speakers" }.Contains(value.Kind)
                && (value.Outcome == "passed" || value.Outcome == "problem") && value.ConfirmedBy == "user"
                && Safe(value.DeviceId, 4096) && Safe(value.DeviceName, 512) && Safe(value.Detail, 1024)
                && DateTime.TryParseExact(value.TestedAtUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date)
                && date.Kind == DateTimeKind.Utc && date.Year >= 2020 && date <= DateTime.UtcNow.AddMinutes(5);
        }

        private static bool Safe(string text, int maximum)
        {
            return text != null && text.Length <= maximum && !text.Any(c => Char.IsControl(c));
        }

        internal PeripheralTestResult Copy()
        {
            return (PeripheralTestResult)MemberwiseClone();
        }
    }

    internal sealed partial class BenchmarkHistory
    {
        private const int PeripheralMaximumBytes = 65536;

        private sealed class PeripheralDocument
        {
            public int SchemaVersion = 1;
            public List<PeripheralTestResult> Results = new List<PeripheralTestResult>();
        }

        internal List<PeripheralTestResult> LoadPeripherals(out string error)
        {
            error = "";
            var results = new List<PeripheralTestResult>();
            var store = new BenchmarkHistory(path + ".peripherals", machineWide);
            try { store.WithLock(delegate { results = store.ReadPeripherals().Results; }); }
            catch (Exception) { error = "Сохранённые проверки устройств недоступны."; }
            return results;
        }

        internal bool RecordPeripheral(PeripheralTestResult result)
        {
            if (!PeripheralTestResult.Valid(result)) { return false; }
            var store = new BenchmarkHistory(path + ".peripherals", machineWide);
            try
            {
                store.WithLock(delegate
                {
                    // Never alter the SSD/GPU attempt marker from a manual device test.
                    PeripheralDocument document;
                    try { document = store.ReadPeripherals(); }
                    catch (InvalidDataException) { document = new PeripheralDocument(); }
                    document.Results.RemoveAll(item => item.Kind == result.Kind);
                    document.Results.Add(result.Copy());
                    string json = new JavaScriptSerializer().Serialize(document);
                    // Match the reader's byte limit before replacing any valid cache.
                    // Character counts alone underestimate non-ASCII JSON in UTF-8.
                    if (Encoding.UTF8.GetByteCount(json) > PeripheralMaximumBytes) { throw new InvalidDataException(); }
                    store.WriteJson(json);
                });
                return true;
            }
            catch (Exception) { return false; }
        }

        private PeripheralDocument ReadPeripherals()
        {
            if (machineWide) { PrepareSharedStorage(false); }
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new PeripheralDocument(); }
            catch (DirectoryNotFoundException) { return new PeripheralDocument(); }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { throw new InvalidDataException(); }
            if (machineWide && !IsSecureStorageAcl(File.GetAccessControl(path), false)) { throw new UnauthorizedAccessException(); }
            try
            {
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > PeripheralMaximumBytes) { throw new InvalidDataException(); }
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true)) { json = reader.ReadToEnd(); }
                }
                var document = new JavaScriptSerializer { MaxJsonLength = PeripheralMaximumBytes }.Deserialize<PeripheralDocument>(json);
                if (document == null || document.SchemaVersion != 1 || document.Results == null || document.Results.Count > 4
                    || document.Results.Any(item => !PeripheralTestResult.Valid(item))
                    || document.Results.Select(item => item.Kind).Distinct().Count() != document.Results.Count) { throw new InvalidDataException(); }
                return document;
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid peripheral history.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Invalid peripheral history.", ex); }
        }
    }
}
