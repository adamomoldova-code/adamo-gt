using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PcPulse
{
    internal sealed class SmartctlProcessResult
    {
        internal string Output = "";
        internal int ExitCode;
        internal bool TimedOut;
        internal bool OutputTooLarge;
    }

    internal sealed class SmartctlSmartReader
    {
        internal const int TimeoutMilliseconds = 12000;
        private const int MaximumOutputLength = 1024 * 1024;
        private readonly string directory;

        internal SmartctlSmartReader() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "smartmontools")) { }
        internal SmartctlSmartReader(string runtimeDirectory) { directory = runtimeDirectory; }

        internal SmartReading Read(DiskSnapshot disk)
        {
            if (disk.Index > 255) { return Unavailable("Номер физического диска не поддерживается smartctl для Windows"); }
            string executable = Path.Combine(directory, "smartctl.exe");
            string database = Path.Combine(directory, "drivedb.h");
            if (!File.Exists(executable) || !File.Exists(database))
            {
                return Unavailable("Не найден модуль SMART; распакуйте архив программы полностью");
            }
            try { return Parse(disk.Index, Run(executable, BuildArguments(disk.Index, database))); }
            catch { return Unavailable("Не удалось запустить расширенное чтение SMART"); }
        }

        internal static string BuildArguments(uint diskIndex, string database)
        {
            if (diskIndex > 255 || string.IsNullOrEmpty(database) || database.IndexOf('"') >= 0)
            {
                throw new ArgumentException("Invalid SMART device or database path.");
            }
            // In smartctl 7.5, -l ssd equals -l devstat,7 for ATA and reads the endurance
            // indicator for SCSI. -b exit rejects bad checksums; no settings or tests are changed.
            return "-a -j -b exit -q noserial -l ssd -B \"" + database + "\" /dev/pd" + diskIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static SmartctlProcessResult Run(string executable, string arguments)
        {
            SmartctlProcessResult result = new SmartctlProcessResult();
            StringBuilder output = new StringBuilder();
            object outputLock = new object();
            TaskCompletionSource<bool> outputFinished = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> errorFinished = new TaskCompletionSource<bool>();
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null) { outputFinished.TrySetResult(true); return; }
                    lock (outputLock)
                    {
                        if (output.Length + args.Data.Length + 1 > MaximumOutputLength) { result.OutputTooLarge = true; }
                        else if (!result.OutputTooLarge) { output.AppendLine(args.Data); }
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data == null) { errorFinished.TrySetResult(true); }
                };
                if (!process.Start()) { return new SmartctlProcessResult { ExitCode = 2 }; }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(TimeoutMilliseconds))
                {
                    result.TimedOut = true;
                    try { process.Kill(); } catch { }
                    process.WaitForExit(1000);
                }
                if (!result.TimedOut)
                {
                    result.ExitCode = process.ExitCode;
                    if (!outputFinished.Task.Wait(1000) || !errorFinished.Task.Wait(1000)) { result.OutputTooLarge = true; }
                }
                try { process.CancelOutputRead(); } catch { }
                try { process.CancelErrorRead(); } catch { }
                lock (outputLock) { result.Output = output.ToString(); }
            }
            return result;
        }

        internal static SmartReading Parse(uint diskIndex, SmartctlProcessResult process)
        {
            if (process == null) { return Unavailable("Модуль SMART не вернул результат"); }
            if (process.TimedOut) { return Unavailable("Контроллер не ответил за 12 секунд; чтение SMART остановлено"); }
            if (process.OutputTooLarge || process.Output == null || process.Output.Length > MaximumOutputLength)
            {
                return Unavailable("Ответ SMART превысил допустимый размер");
            }
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer { MaxJsonLength = MaximumOutputLength, RecursionLimit = 32 }.DeserializeObject(process.Output) as Dictionary<string, object>;
            }
            catch { return Unavailable("Модуль SMART вернул некорректный ответ"); }
            if (root == null) { return Unavailable("Модуль SMART вернул пустой ответ"); }

            ulong? jsonExit = Unsigned(Get(root, "smartctl", "exit_status"));
            if (process.ExitCode < 0 || process.ExitCode > 255 || !jsonExit.HasValue || jsonExit.Value != (ulong)process.ExitCode)
            {
                return Unavailable("Код результата SMART не подтверждён");
            }
            if ((process.ExitCode & 3) != 0)
            {
                return Unavailable("Windows или контроллер не разрешает чтение SMART; могут потребоваться права администратора");
            }
            string expectedName = "/dev/pd" + diskIndex.ToString(CultureInfo.InvariantCulture);
            if (diskIndex > 255 || !string.Equals(Text(Get(root, "device", "name")), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return Unavailable("Ответ SMART относится к другому физическому диску");
            }
            if (Boolean(Get(root, "smart_support", "enabled")) == false)
            {
                return Unavailable("SMART отключён на накопителе; программа не меняет его настройки");
            }

            SmartReading reading = new SmartReading();
            reading.PowerOnHours = Unsigned(Get(root, "power_on_time", "hours"));
            double? temperature = Number(Get(root, "temperature", "current"));
            if (temperature >= -40 && temperature <= 150) { reading.Temperature = temperature; }
            string protocol = Text(Get(root, "device", "protocol"));
            string resourceSource = "";
            if (string.Equals(protocol, "NVMe", StringComparison.OrdinalIgnoreCase))
            {
                ulong? used = Unsigned(Get(root, "nvme_smart_health_information_log", "percentage_used"));
                if (used <= 255) { reading.HealthPercent = Math.Max(0, 100d - used.Value); resourceSource = "NVMe Percentage Used"; }
                ulong? warning = Unsigned(Get(root, "nvme_smart_health_information_log", "critical_warning"));
                if (warning <= 255) { reading.CriticalWarning = (byte)warning.Value; }
            }
            else if (string.Equals(protocol, "ATA", StringComparison.OrdinalIgnoreCase))
            {
                reading.HealthPercent = AtaEndurance(root, out resourceSource);
            }
            else if (string.Equals(protocol, "SCSI", StringComparison.OrdinalIgnoreCase))
            {
                ulong? used = Unsigned(Get(root, "scsi_percentage_used_endurance_indicator"));
                if (used <= 255) { reading.HealthPercent = Math.Max(0, 100d - used.Value); resourceSource = "SCSI Percentage Used Endurance Indicator"; }
            }
            bool? passed = Boolean(Get(root, "smart_status", "passed"));
            reading.SmartPassed = (process.ExitCode & 24) != 0 ? false : passed;
            List<string> status = new List<string>();
            if (reading.CriticalWarning > 0) { status.Add("Предупреждение SMART: " + SmartReader.DescribeWarning(reading.CriticalWarning.Value)); }
            else if (passed == false || (process.ExitCode & 8) != 0) { status.Add("SMART сообщает о неисправности накопителя"); }
            else if ((process.ExitCode & 16) != 0) { status.Add("Атрибуты SMART достигли порога неисправности"); }
            else if (passed == true) { status.Add("SMART: текущие пороги неисправности не превышены"); }
            else { status.Add("Общий статус SMART не определён"); }
            if ((process.ExitCode & 32) != 0) { status.Add("ранее были превышены пороги SMART"); }
            if ((process.ExitCode & 64) != 0) { status.Add("есть записи в журнале ошибок"); }
            if ((process.ExitCode & 128) != 0) { status.Add("есть ошибки в журнале прошлых самотестов"); }
            if ((process.ExitCode & 4) != 0) { status.Add("часть команд чтения не поддерживается или завершилась ошибкой"); }
            reading.Status = string.Join("; ", status.ToArray());
            reading.Source = "smartctl 7.5 / drivedb.h" + (resourceSource.Length > 0 ? "; ресурс: " + resourceSource + "; оценка производителя, не гарантия исправности" : "; процент остаточного ресурса не подтверждён");
            return reading;
        }

        private static double? AtaEndurance(Dictionary<string, object> root, out string source)
        {
            source = "";
            foreach (object pageValue in Items(Get(root, "ata_device_statistics", "pages")))
            {
                Dictionary<string, object> page = pageValue as Dictionary<string, object>;
                if (Unsigned(Get(page, "number")) != 7) { continue; }
                foreach (object rowValue in Items(Get(page, "table")))
                {
                    Dictionary<string, object> row = rowValue as Dictionary<string, object>;
                    ulong? value = Unsigned(Get(row, "value"));
                    if (Unsigned(Get(row, "offset")) == 8 && Boolean(Get(row, "flags", "valid")) == true && value <= 255)
                    {
                        source = "ATA Device Statistics, Percentage Used Endurance Indicator";
                        return Math.Max(0, 100d - value.Value);
                    }
                }
            }
            // Generic normalized SMART attributes are not percentages. Accept only named endurance
            // attributes for SSD models recognized by the bundled drive database.
            if (Boolean(Get(root, "in_smartctl_database")) != true || Unsigned(Get(root, "rotation_rate")) != 0) { return null; }
            double? result = null;
            foreach (object attributeValue in Items(Get(root, "ata_smart_attributes", "table")))
            {
                Dictionary<string, object> attribute = attributeValue as Dictionary<string, object>;
                string name = Text(Get(attribute, "name"));
                bool remaining = name == "SSD_Life_Left" || name == "SSD_Life_Left_Perc" || name == "Percent_Lifetime_Remain" || name == "Percent_Life_Remaining" || name == "Lifetime_Remaining%";
                double? value = Number(Get(attribute, "value"));
                if (!remaining || !value.HasValue || value < 0 || value > 100) { continue; }
                if (result.HasValue && result.Value != value.Value) { source = ""; return null; }
                result = value;
                source = "drivedb.h / " + name + " (нормализованный остаток ресурса)";
            }
            return result;
        }

        private static SmartReading Unavailable(string message) { return new SmartReading { Status = "SMART: " + message }; }
        private static object Get(Dictionary<string, object> data, params string[] keys)
        {
            object current = data;
            foreach (string key in keys)
            {
                Dictionary<string, object> dictionary = current as Dictionary<string, object>;
                if (dictionary == null || !dictionary.TryGetValue(key, out current)) { return null; }
            }
            return current;
        }
        private static IEnumerable Items(object value) { return value as object[] ?? new object[0]; }
        private static string Text(object value) { return value as string ?? ""; }
        private static bool? Boolean(object value) { return value is bool ? (bool?)value : null; }
        private static double? Number(object value)
        {
            if (value == null || value is string || value is bool) { return null; }
            double result;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !double.IsNaN(result) && !double.IsInfinity(result) ? (double?)result : null;
        }
        private static ulong? Unsigned(object value)
        {
            if (value == null || value is string || value is bool) { return null; }
            ulong result;
            return ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out result) ? (ulong?)result : null;
        }
    }
}
