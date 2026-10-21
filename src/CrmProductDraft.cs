using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PcPulse
{
    // A deliberately small sales projection. Never retain Snapshot, device IDs,
    // paths, exception messages, peripheral recordings or the full JSON report.
    public sealed class CrmProductDraft
    {
        public string Title = "Компьютер";
        public string Manufacturer = "";
        public string Model = "";
        public string Sku = "";
        public string Cpu = "";
        public string Memory = "";
        public string Battery = "";
        public string CapturedAt = "";
        public readonly List<CrmDiskDraft> Disks = new List<CrmDiskDraft>();
        public readonly List<CrmGpuDraft> Gpus = new List<CrmGpuDraft>();
        public readonly List<string> Screens = new List<string>();
        public readonly List<string> Ports = new List<string>();
        public readonly List<string> DeviceChecks = new List<string>();
        public readonly List<string> StressChecks = new List<string>();

        public string ReviewSummary { get { return BuildDescription(); } }

        public string BuildDescription()
        {
            var lines = new List<string>();
            Add(lines, "Производитель", Manufacturer); Add(lines, "Модель", Model); Add(lines, "Модификация (SKU)", Sku);
            Add(lines, "CPU", Cpu); Add(lines, "RAM", Memory);
            foreach (CrmDiskDraft disk in Disks.Where(item => item.Include))
            { Add(lines, "Накопитель", disk.Caption); Add(lines, "  Состояние", disk.Details); Add(lines, "  Тест SSD", disk.Test); }
            foreach (CrmGpuDraft gpu in Gpus.Where(item => item.Include))
            { Add(lines, "GPU", gpu.Caption); Add(lines, "  Тест GPU", gpu.Test); }
            foreach (string screen in Screens) { Add(lines, "Встроенный экран", screen); }
            Add(lines, "Батарея", Battery);
            foreach (string port in Ports) { Add(lines, "Порты", port); }
            foreach (string check in DeviceChecks) { Add(lines, "Ручная проверка", check); }
            foreach (string check in StressChecks) { Add(lines, "Нагрузка", check); }
            Add(lines, "Данные собраны", CapturedAt);
            lines.Add("Краткая диагностика ADAMO GT. Отсутствие данных и незавершённые тесты не подтверждают исправность. Сохранённые замеры относятся к указанной дате.");
            return String.Join(Environment.NewLine, lines);
        }

        private static void Add(List<string> lines, string label, string value)
        { if (!String.IsNullOrWhiteSpace(value)) { lines.Add(label + ": " + value); } }
    }

    public sealed class CrmDiskDraft
    {
        public bool Include;
        public string Caption = "";
        public string Details = "";
        public string Test = "";
        public override string ToString() { return Caption; }
    }

    public sealed class CrmGpuDraft
    {
        public bool Include = true;
        public string Caption = "";
        public string Test = "";
        public override string ToString() { return Caption; }
    }

    public static class CrmProductDraftBuilder
    {
        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

        public static CrmProductDraft Build(Snapshot snapshot, IEnumerable<BenchmarkProgress> disks,
            IEnumerable<GpuBenchmarkProgress> gpus, PortSpecification ports,
            IEnumerable<PeripheralTestResult> peripherals, StressRunResult stress)
        {
            if (snapshot == null) { throw new InvalidOperationException("Дождитесь определения характеристик компьютера."); }
            var draft = new CrmProductDraft
            {
                Manufacturer = Known(snapshot.ComputerManufacturer), Model = Known(snapshot.ComputerModel),
                Sku = Known(snapshot.ComputerSku), CapturedAt = Date(snapshot.Timestamp)
            };
            draft.Title = Join(" ", draft.Manufacturer, draft.Model);
            if (draft.Title.Length == 0) { draft.Title = "Компьютер"; }
            var cpu = new List<string>(); Put(cpu, Known(snapshot.CpuName));
            if (snapshot.CpuCores > 0) { cpu.Add(snapshot.CpuCores + " ядер"); }
            if (snapshot.CpuThreads > 0) { cpu.Add(snapshot.CpuThreads + " потоков"); }
            if (Positive(snapshot.CpuMaxClockMhz)) { cpu.Add("паспортная частота до " + Number(snapshot.CpuMaxClockMhz.Value / 1000, " ГГц")); }
            AddBytes(cpu, "L1 ", snapshot.CpuL1CacheBytes); AddBytes(cpu, "L2 ", snapshot.CpuL2CacheBytes); AddBytes(cpu, "L3 ", snapshot.CpuL3CacheBytes);
            draft.Cpu = String.Join(" · ", cpu);
            var ram = new List<string>();
            AddBytes(ram, "установлено ", snapshot.RamInstalledBytes); Put(ram, Known(snapshot.RamType)); Put(ram, Known(snapshot.RamRate));
            if (snapshot.RamModuleCount.HasValue && snapshot.RamModuleCount >= 0) { ram.Add("модулей: " + snapshot.RamModuleCount); }
            if (snapshot.RamSlotCount.HasValue && snapshot.RamSlotCount >= 0) { ram.Add("слотов: " + snapshot.RamSlotCount); }
            if (snapshot.RamFreeSlots.HasValue && snapshot.RamFreeSlots >= 0) { ram.Add("свободно: " + snapshot.RamFreeSlots); }
            AddBytes(ram, "предел по BIOS ", snapshot.RamMaximumBytes);
            AddBytes(ram, "предел производителя ", snapshot.RamManufacturerMaximumBytes);
            draft.Memory = String.Join(" · ", ram);

            var diskResults = (disks ?? new BenchmarkProgress[0]).Where(item => item != null).ToList();
            foreach (DiskSnapshot disk in (snapshot.Disks ?? new List<DiskSnapshot>()).Where(item => item != null).Take(32))
            {
                var item = new CrmDiskDraft { Caption = Join(" · ", Known(disk.Name), Known(disk.MediaType), DiskSize(disk.SizeBytes)) };
                if (item.Caption.Length == 0) { item.Caption = "Накопитель (модель не определена)"; }
                var details = new List<string>();
                if (Percentage(disk.HealthPercent)) { details.Add("ресурс " + Number(disk.HealthPercent.Value, "%")); }
                if (disk.PowerOnHours.HasValue) { details.Add("наработка " + disk.PowerOnHours.Value + " ч"); }
                if (disk.SmartPassed.HasValue) { details.Add(disk.SmartPassed.Value ? "SMART: без ошибок по доступным данным" : "SMART: есть предупреждение"); }
                if (disk.CriticalWarning.HasValue && disk.CriticalWarning.Value != 0) { details.Add("критическое предупреждение NVMe"); }
                item.Details = String.Join(" · ", details);
                BenchmarkProgress result = diskResults.LastOrDefault(value => value.DiskIndex == disk.Index);
                item.Test = DescribeDiskTest(result);
                draft.Disks.Add(item);
            }
            var gpuResults = (gpus ?? new GpuBenchmarkProgress[0]).Where(item => item != null).ToList();
            foreach (GpuSnapshot gpu in (snapshot.Gpus ?? new List<GpuSnapshot>()).Where(item => item != null).Take(16))
            {
                var parts = new List<string>(); Put(parts, Known(gpu.Name));
                if (gpu.IsIntegrated.HasValue) { parts.Add(gpu.IsIntegrated.Value ? "встроенная" : "дискретная"); }
                AddBytes(parts, "выделенная память ", gpu.MemoryBytes);
                AddBytes(parts, "доступная общая память до ", gpu.SharedMemoryBytes);
                GpuBenchmarkProgress result = gpu.AdapterIndex.HasValue ? gpuResults.LastOrDefault(value => value.AdapterIndex == gpu.AdapterIndex) : null;
                draft.Gpus.Add(new CrmGpuDraft { Caption = String.Join(" · ", parts), Test = DescribeGpuTest(result) });
            }
            foreach (ScreenSnapshot screen in (snapshot.Screens ?? new List<ScreenSnapshot>()).Where(item => item != null && item.IsInternal == true).Take(4))
            {
                var parts = new List<string>();
                if (screen.Width > 0 && screen.Height > 0) { parts.Add("текущий режим " + screen.Width + " × " + screen.Height); }
                if (Positive(screen.RefreshHz)) { parts.Add(Number(screen.RefreshHz.Value, " Гц")); }
                Put(parts, Known(screen.MatrixType));
                if (Positive(screen.BrightnessNits)) { parts.Add("паспортная яркость " + Number(screen.BrightnessNits.Value, " нит") + " (справочные данные, не измерение)"); }
                if (parts.Count > 0) { draft.Screens.Add(String.Join(" · ", parts)); }
            }
            draft.Battery = DescribeBattery(snapshot);
            if (ports != null && ports.Ports != null)
            {
                foreach (PortEntry port in ports.Ports.Where(item => item != null && item.Count > 0 && item.Count <= 64).Take(24))
                {
                    string name = Known(port.Name);
                    if (name.Length > 0) { draft.Ports.Add(name + " × " + port.Count + (port.Optional ? " — опция; наличие нужно проверить" : " — по спецификации модели")); }
                }
                if (draft.Ports.Count > 0 && PortLookup.IsAllowedSource(ports.SourceUrl ?? "", snapshot.ComputerManufacturer))
                {
                    Uri uri = new Uri(ports.SourceUrl);
                    draft.Ports.Add("источник: " + uri.GetLeftPart(UriPartial.Path));
                }
            }
            foreach (PeripheralTestResult check in (peripherals ?? new PeripheralTestResult[0]).Where(PeripheralTestResult.Valid).Take(4))
            {
                string name = check.Kind == "camera" ? "Камера" : check.Kind == "microphone" ? "Микрофон" : check.Kind == "keyboard" ? "Клавиатура" : "Динамики";
                DateTime date;
                string when = DateTime.TryParse(check.TestedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date) ? Date(date) : "дата не сохранена";
                draft.DeviceChecks.Add(name + " — " + (check.Outcome == "passed" ? "работает" : "проблема") + "; подтверждено пользователем " + when + ", не автоматический вывод");
            }
            if (stress != null && stress.Results != null)
            {
                foreach (StressProgress check in stress.Results.Where(item => item != null).Take(4))
                {
                    string name = ComponentName(check.Component);
                    if (name == null) { continue; }
                    string state = check.IsRunning ? "не завершена" : check.Cancelled || stress.Cancelled ? "остановлена" : !String.IsNullOrEmpty(check.Error) ? "ошибка" : check.Completed ? "завершена" : "не завершена";
                    string duration = Positive(check.ElapsedSeconds) ? " · " + Number(check.ElapsedSeconds, " с") : "";
                    draft.StressChecks.Add(name + " — " + state + duration + " · " + Date(stress.StartedAtUtc));
                }
            }
            return draft;
        }

        private static string DescribeDiskTest(BenchmarkProgress test)
        {
            if (test == null) { return "нет сохранённого результата"; }
            bool valid = !test.IsRunning && String.IsNullOrEmpty(test.Error) && test.Percent >= 100
                && Positive(test.ReadBytesPerSecond) && Positive(test.WriteBytesPerSecond);
            string date = test.TestedAtUtc.HasValue ? Date(test.TestedAtUtc.Value) : "дата не сохранена";
            if (!valid) { return (test.IsRunning ? "не завершён" : !String.IsNullOrEmpty(test.Error) ? "ошибка / остановлен" : "неполный результат") + " · " + date; }
            return "чтение " + Number(test.ReadBytesPerSecond.Value / 1000000, " MB/s")
                + " · запись " + Number(test.WriteBytesPerSecond.Value / 1000000, " MB/s")
                + " · " + date + (test.FromHistory ? " · сохранённый замер" : "")
                + (test.QueueDepth.HasValue && test.ThreadCount.HasValue ? " · Q" + test.QueueDepth + "T" + test.ThreadCount : "");
        }

        private static string DescribeGpuTest(GpuBenchmarkProgress test)
        {
            if (test == null) { return "нет сохранённого результата"; }
            string date = test.TestedAtUtc.HasValue ? Date(test.TestedAtUtc.Value) : "дата не сохранена";
            bool valid = test.Completed && !test.IsRunning && String.IsNullOrEmpty(test.Error) && Positive(test.FramesPerSecond);
            if (!valid) { return (test.IsRunning ? "не завершён" : !String.IsNullOrEmpty(test.Error) ? "ошибка / остановлен" : "неполный результат") + " · " + date; }
            return Number(test.FramesPerSecond.Value, " FPS") + " · " + Known(test.Preset) + " · " + date + (test.FromHistory ? " · сохранённый замер" : "");
        }

        private static string DescribeBattery(Snapshot snapshot)
        {
            if (snapshot.BatteryPresent == false) { return "не обнаружена"; }
            var parts = new List<string>();
            if (Positive(snapshot.BatteryDesignWh)) { parts.Add("проектная ёмкость " + Number(snapshot.BatteryDesignWh.Value, " Вт·ч")); }
            if (snapshot.BatteryFullChargeWh.HasValue && Nonnegative(snapshot.BatteryFullChargeWh.Value)) { parts.Add("полная ёмкость " + Number(snapshot.BatteryFullChargeWh.Value, " Вт·ч")); }
            if (Percentage(snapshot.BatteryHealthPercent)) { parts.Add("состояние " + Number(snapshot.BatteryHealthPercent.Value, "%")); }
            if (snapshot.BatteryCycleCount.HasValue && !snapshot.BatteryCyclesPerDevice) { parts.Add("циклов " + snapshot.BatteryCycleCount.Value); }
            return String.Join(" · ", parts);
        }

        internal static string Known(string value)
        {
            string text = Clean(value, 180);
            string lower = text.ToLowerInvariant();
            if (lower == "нет данных" || lower == "не подтверждён" || lower == "не подтверждена" || lower == "тип не определён"
                || lower.StartsWith("определение") || lower == "unknown" || lower == "default string" || lower == "to be filled by o.e.m." || lower == "system product name") { return ""; }
            return text;
        }
        internal static string Clean(string value, int maximum)
        {
            if (String.IsNullOrWhiteSpace(value)) { return ""; }
            string clean = new String(value.Where(character => !Char.IsControl(character)).ToArray()).Trim();
            return clean.Length > maximum ? clean.Substring(0, maximum) : clean;
        }
        private static string ComponentName(StressComponent component)
        { return component == StressComponent.Cpu ? "CPU" : component == StressComponent.Ram ? "RAM" : component == StressComponent.Ssd ? "SSD" : component == StressComponent.Gpu ? "GPU" : null; }
        private static string Date(DateTime date)
        { return date.Year < 2020 ? "дата не сохранена" : date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture); }
        private static bool Positive(double? value) { return value.HasValue && value.Value > 0 && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value); }
        private static bool Nonnegative(double value) { return value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool Percentage(double? value) { return value.HasValue && Nonnegative(value.Value) && value.Value <= 100; }
        private static string Number(double value, string suffix) { return value.ToString("0.#", Culture) + suffix; }
        private static string Join(string separator, params string[] values) { return String.Join(separator, values.Where(value => !String.IsNullOrWhiteSpace(value))); }
        private static void Put(List<string> parts, string value) { if (value.Length > 0) { parts.Add(value); } }
        private static string DiskSize(ulong? bytes) { return bytes.HasValue && bytes.Value > 0 ? Number(bytes.Value / 1000000000d, " GB") : ""; }
        private static void AddBytes(List<string> parts, string prefix, ulong? bytes)
        {
            if (!bytes.HasValue || bytes.Value == 0) { return; }
            parts.Add(prefix + (bytes.Value >= 1073741824UL ? Number(bytes.Value / 1073741824d, " GB")
                : bytes.Value >= 1048576UL ? Number(bytes.Value / 1048576d, " MB") : Number(bytes.Value / 1024d, " KB")));
        }
    }
}
