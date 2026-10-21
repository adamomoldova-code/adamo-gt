using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

namespace PcPulse
{
    public sealed class DiagnosticReport
    {
        public int SchemaVersion = 1;
        public string AppName = "ADAMO GT";
        public string AppVersion;
        public string CapturedAtUtc;
        public bool ChecksRunning;
        public string Notice = "Короткая диагностика. Незавершённые и недоступные тесты не подтверждают исправность. Яркость из справочника является паспортной, а не измеренной. Автономность — сценарная оценка от полного заряда при условном потреблении 7–15 Вт для лёгкой работы, не результат разряда батареи.";
        public BenchmarkSettings Settings;
        public Snapshot Hardware;
        public List<BenchmarkProgress> DiskTests;
        public List<GpuBenchmarkProgress> GpuTests;
        public PortSpecification Ports;
        public List<PeripheralTestResult> PeripheralTests;
        public StressRunResult StressTests;
        public string StressTestsNotice = "Стресс-нагрузка текущего запуска; не заменяет сохранённые замеры скорости. RAM проверяет только выделенную часть памяти: RamTargetBytes — цель после ограничений Windows, RamAllocatedBytes — выделено, RamCoveredBytes — проверено всеми тремя шаблонами; BytesProcessed — повторный трафик чтения/записи. GPU не проверяет всю VRAM. Прерванный тест не подтверждает исправность.";
        public string PeripheralTestsNotice = "Ручная проверка, подтверждённая пользователем в указанную дату. Старый результат не подтверждает состояние текущего подключённого устройства.";

        internal static string Serialize(Snapshot hardware, IEnumerable<BenchmarkProgress> disks,
            IEnumerable<GpuBenchmarkProgress> gpus, PortSpecification ports, bool running, IEnumerable<PeripheralTestResult> peripherals = null, StressRunResult stress = null)
        {
            if (hardware == null) { throw new InvalidOperationException("Дождитесь определения характеристик компьютера."); }
            var report = new DiagnosticReport
            {
                AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                CapturedAtUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ChecksRunning = running,
                Settings = BenchmarkSettings.Current,
                Hardware = hardware,
                DiskTests = new List<BenchmarkProgress>(disks),
                GpuTests = new List<GpuBenchmarkProgress>(gpus),
                Ports = ports,
                StressTests = stress,
                PeripheralTests = peripherals == null ? new List<PeripheralTestResult>() : new List<PeripheralTestResult>(peripherals)
            };
            return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(report);
        }
    }
}
