using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PcPulse
{
    public sealed class BenchmarkSettings
    {
        public int SchemaVersion = 1;
        public string Profile = "quick-v2";
        public int DiskMiB = 1024;
        public int GpuWarmupSeconds = 3;
        public int GpuMeasurementSeconds = 15;
        public string Note = "";

        private static readonly BenchmarkSettings current = Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "benchmarks.json"));
        public static BenchmarkSettings Current { get { return current; } }

        internal static BenchmarkSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) { return new BenchmarkSettings(); }
                if (new FileInfo(path).Length > 16384) { throw new InvalidDataException(); }
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception exception)
            {
                if (!(exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is InvalidOperationException)) { throw; }
                return DefaultsAfterError();
            }
        }

        internal static BenchmarkSettings Parse(string json)
        {
            try
            {
                var settings = new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 8 }.Deserialize<BenchmarkSettings>(json);
                if (settings != null && settings.Profile == "quick-v1")
                {
                    var fields = new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 8 }.Deserialize<Dictionary<string, object>>(json);
                    bool hasDiskSize = false;
                    foreach (string key in fields.Keys)
                    {
                        if (String.Equals(key, "DiskMiB", StringComparison.OrdinalIgnoreCase)) { hasDiskSize = true; break; }
                    }
                    if (!hasDiskSize) { settings.DiskMiB = 512; }
                }
                if (settings == null || settings.SchemaVersion != 1 || (settings.Profile != "quick-v1" && settings.Profile != "quick-v2")
                    || settings.DiskMiB < 64 || settings.DiskMiB > 1024 || settings.DiskMiB % 4 != 0
                    || (settings.Profile == "quick-v2" && settings.DiskMiB != 1024)
                    || settings.GpuWarmupSeconds < 1 || settings.GpuWarmupSeconds > 10
                    || settings.GpuMeasurementSeconds < 5 || settings.GpuMeasurementSeconds > 30)
                {
                    return DefaultsAfterError();
                }
                settings.Note = "";
                return settings;
            }
            catch (Exception) { return DefaultsAfterError(); }
        }

        private static BenchmarkSettings DefaultsAfterError()
        {
            return new BenchmarkSettings { Note = "Настройки тестов не распознаны: использован стандартный короткий профиль." };
        }
    }
}
