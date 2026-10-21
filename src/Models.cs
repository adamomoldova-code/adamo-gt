using System;
using System.Collections.Generic;

namespace PcPulse
{
    public sealed class Snapshot
    {
        public DateTime Timestamp = DateTime.Now;
        public string ComputerName = Environment.MachineName;
        public string WindowsName = "Windows";
        public string ComputerManufacturer = "";
        public string ComputerModel = "";
        public string ComputerSku = "";
        public string CpuName = "Определение процессора…";
        public double? CpuLoad;
        public double? CpuClockMhz;
        public string CpuClockSource = "";
        public double? CpuMaxClockMhz;
        public double? CpuTemperature;
        public string CpuTemperatureStatus = "";
        public bool CpuTemperatureTrustedForStress;
        // Retain provenance when reading saved prototype reports. The normal
        // application has no experimental device transport or activation mode.
        public bool CpuTemperatureExperimental;
        public string AdamoThermalStatus = "";
        public int CpuCores;
        public int CpuThreads;
        public ulong? CpuL1CacheBytes;
        public ulong? CpuL2CacheBytes;
        public ulong? CpuL3CacheBytes;
        public string CpuCacheSource = "";
        public ulong? RamTotalBytes;
        public ulong? RamInstalledBytes;
        public ulong? RamMaximumBytes;
        public string RamMaximumSource = "";
        public ulong? RamManufacturerMaximumBytes;
        public string RamManufacturerMaximumSource = "";
        public ulong? RamUsedBytes;
        public string RamRate = "Нет данных";
        public double? RamClockMhz;
        public string RamConfiguration = "";
        public string RamType = "Нет данных";
        public int? RamModuleCount;
        public int? RamSlotCount;
        public int? RamFreeSlots;
        public bool RamHasModularFormFactor;
        public string RamSlotsNote = "BIOS не сообщает сведения о слотах памяти.";
        public List<DiskSnapshot> Disks = new List<DiskSnapshot>();
        public List<GpuSnapshot> Gpus = new List<GpuSnapshot>();
        public List<ScreenSnapshot> Screens = new List<ScreenSnapshot>();
        public bool? BatteryPresent;
        public double? BatteryPercent;
        public double? BatteryHealthPercent;
        public double? BatteryDesignWh;
        public double? BatteryFullChargeWh;
        public double? BatteryRuntimeMinHours;
        public double? BatteryRuntimeMaxHours;
        public string BatteryRuntimeStatus = "";
        public uint? BatteryCycleCount;
        public bool BatteryCyclesPerDevice;
        public string BatteryCycleDetail = "";
        public string BatteryStatus = "Определение батареи…";
        public string BatteryDetail = "";
        public string SensorStatus = "";
        public bool IsAdmin;
        public List<string> Issues = new List<string>();
    }

    public sealed class DiskSnapshot
    {
        public uint Index;
        public string StorageIdentity = "";
        public string PersistentHardwareId = "";
        public string Name = "Накопитель";
        public string MediaType = "Тип не определён";
        public ulong? SizeBytes;
        public double? ReadBytesPerSecond;
        public double? WriteBytesPerSecond;
        public double? Temperature;
        public DateTime? TemperatureSampledAtUtc;
        public string Volumes = "";
        public string TestVolumeRoot = "";
        public double? HealthPercent;
        public ulong? PowerOnHours;
        public byte? CriticalWarning;
        public bool? SmartPassed;
        public string SmartStatus = "SMART ещё не прочитан";
        public string SmartSource = "";
    }

    public sealed class GpuSnapshot
    {
        public string Name = "Видеокарта";
        public string PersistentHardwareId = "";
        public ulong? MemoryBytes;
        public string MemoryKind = "Выделенная видеопамять";
        public double? Temperature;
        public string TemperatureStatus = "";
        public double? Load;
        public uint? AdapterIndex;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public ulong? SharedMemoryBytes;
        public bool? IsIntegrated;
    }

    public sealed class ScreenSnapshot
    {
        public string Name = "Экран";
        public string InstanceName = "";
        public int? Width;
        public int? Height;
        public double? RefreshHz;
        public bool? IsInternal;
        public string MatrixType = "Не подтверждён";
        public double? BrightnessNits;
        public string Source = "Windows / EDID";
    }

    public sealed class GpuBenchmarkProgress
    {
        public DateTime? TestedAtUtc;
        public bool FromHistory;
        public int? WarmupSeconds;
        public int? PlannedMeasurementSeconds;
        public uint? AdapterIndex;
        public string AdapterName = "";
        public string Stage = "Ожидание GPU-теста";
        public string Preset = "ADAMO GT · Direct3D 11 · 1280 × 720";
        public double Percent;
        public double? FramesPerSecond;
        public double? FrameTimeMs;
        public double? DurationSeconds;
        public double? MaxTemperature;
        public bool IsRunning;
        public bool Completed;
        public string Error = "";
    }

    public sealed class BenchmarkProgress
    {
        public DateTime? TestedAtUtc;
        public bool FromHistory;
        public long? TestSizeBytes;
        public int? BlockSizeBytes;
        public string Engine = "";
        public string EngineVersion = "";
        public string Profile = "";
        public string CacheMode = "";
        public int? QueueDepth;
        public int? ThreadCount;
        public int? WarmupSeconds;
        public int? MeasurementSeconds;
        public long? ReadBytes;
        public long? WriteBytes;
        public double? ReadSeconds;
        public double? WriteSeconds;
        public string Method = "";
        public uint DiskIndex;
        public string Stage = "Ожидание теста";
        public double Percent;
        public double? ReadBytesPerSecond;
        public double? WriteBytesPerSecond;
        public bool IsRunning;
        public string Error = "";

        internal BenchmarkProgress Copy()
        {
            return (BenchmarkProgress)MemberwiseClone();
        }
    }

    public sealed class PortSpecification
    {
        public string Model = "";
        public string Status = "Ищем спецификацию производителя…";
        public string SourceUrl = "";
        public string SourceTitle = "";
        public DateTime? CheckedAt;
        public bool IsCached;
        public bool IsLoading;
        public string Note = "";
        public List<PortEntry> Ports = new List<PortEntry>();
    }

    public sealed class PortEntry
    {
        public string Name = "";
        public int Count;
        public bool Optional;
        public string Detail = "";
    }

    public static class Display
    {
        public const string CpuHeatNote = "Во время обновления Windows процессор может временно нагреваться сильнее — это обычно нормально. После завершения обновления и снижения нагрузки температура обычно снижается.";

        public static string Number(double? value, string suffix, int decimals)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
                ? value.Value.ToString("F" + decimals, System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + suffix
                : "Нет данных";
        }

        public static string Memory(ulong? bytes)
        {
            return bytes.HasValue ? Number(bytes.Value / 1073741824d, " GB", 1) : "Нет данных";
        }

        public static string DiskSize(ulong? bytes)
        {
            if (!bytes.HasValue) { return "Нет данных"; }
            return bytes.Value >= 1000000000000UL
                ? Number(bytes.Value / 1000000000000d, " TB", 2)
                : Number(bytes.Value / 1000000000d, " GB", 0);
        }

        public static string Speed(double? bytes)
        {
            return Number(bytes / 1000000d, " MB/s", 1);
        }

        public static double Percent(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? Math.Max(0, Math.Min(100, value.Value)) : 0;
        }
    }
}
