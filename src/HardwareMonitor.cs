using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace PcPulse
{
    public sealed class HardwareMonitor : IDisposable
    {
        private readonly object sync = new object();
        private Snapshot inventory = new Snapshot();
        private SmartReader smartReader = new SmartReader();
        private List<Dictionary<string, object>> memoryModules;
        private Computer computer;
        private bool initialized;
        private bool sensorsInitialized;
        private bool disposed;
        private bool pawnInstalled;
        private string sensorFailure = "";
        private string cpuSensorFailure = "";
        private readonly List<string> sensorInitializationIssues = new List<string>();
        private DriverlessTemperatureReader driverlessTemperatures = new DriverlessTemperatureReader();
        private ulong previousIdle;
        private ulong previousTotal;
        private bool hasPreviousCpu;
        private DateTime nextBatteryInventory = DateTime.MinValue;
        private double? cachedBatteryHealth;
        private double? cachedBatteryDesignWh;
        private double? cachedBatteryFullChargeWh;
        private uint? cachedBatteryCycleCount;
        private string cachedBatteryCycleDetail = "";
        private bool cachedBatteryCyclesPerDevice;
        private int detectedBatterySensorCount;
        private int cachedBatteryDeviceCount;
        private string cachedBatteryDetail = "";
        private string cachedBatteryIssue = "";

        public Snapshot Read()
        {
            return ReadSnapshot(true);
        }

        public Snapshot ReadInitialSnapshot()
        {
            return ReadSnapshot(false);
        }

        public void RefreshInventory()
        {
            lock (sync)
            {
                if (disposed) { throw new ObjectDisposedException("HardwareMonitor"); }
                if (computer != null) { try { computer.Close(); } catch { } computer = null; }
                inventory = new Snapshot();
                smartReader = new SmartReader();
                memoryModules = null;
                initialized = sensorsInitialized = false;
                sensorFailure = "";
                cpuSensorFailure = "";
                sensorInitializationIssues.Clear();
                driverlessTemperatures = new DriverlessTemperatureReader();
                nextBatteryInventory = DateTime.MinValue;
                detectedBatterySensorCount = 0;
            }
        }

        private Snapshot ReadSnapshot(bool includeHardwareSensors)
        {
            lock (sync)
            {
                if (disposed) { throw new ObjectDisposedException("HardwareMonitor"); }
                if (!initialized)
                {
                    InitializeInventory();
                    initialized = true;
                }
                if (includeHardwareSensors) { EnsureHardwareSensors(); }

                Snapshot snapshot = CopyInventory();
                ReadSystemUsage(snapshot);
                ReadDiskActivity(snapshot);
                foreach (DiskSnapshot disk in snapshot.Disks) { smartReader.Apply(disk, DateTime.UtcNow, includeHardwareSensors); }
                ReadPower(snapshot);
                if (includeHardwareSensors)
                {
                    // Windows display-driver telemetry is independent of CPU access.
                    // Prefer precise CPU package/core readings when they are available;
                    // otherwise consult only explicitly identified Windows/OEM probes.
                    WindowsGpuTemperatureReader.Apply(snapshot);
                    ReadHardwareSensors(snapshot);
                    driverlessTemperatures.ApplyCpu(snapshot, DateTime.UtcNow);
                }
                if (snapshot.BatteryPresent == true)
                {
                    ReadBatteryHealth(snapshot);
                }
                ApplyBatteryRuntime(snapshot);
                if (includeHardwareSensors)
                {
                    SetSensorStatus(snapshot);
                }
                else
                {
                    snapshot.SensorStatus = "Характеристики определены; подключение датчиков начнётся после открытия окна.";
                    snapshot.CpuTemperatureStatus = "Датчики подключаются…";
                    foreach (GpuSnapshot gpu in snapshot.Gpus) { gpu.TemperatureStatus = "Датчики подключаются…"; }
                }
                snapshot.Timestamp = DateTime.Now;
                return snapshot;
            }
        }

        private void InitializeInventory()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    inventory.IsAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { inventory.IsAdmin = false; }

            ReadComputerIdentity();
            Attempt(inventory, "Название Windows", delegate
            {
                foreach (Dictionary<string, object> row in Query("root\\cimv2", "SELECT Caption FROM Win32_OperatingSystem"))
                {
                    inventory.WindowsName = Text(row, "Caption", "Windows");
                }
            });
            Attempt(inventory, "Информация о процессоре", ReadCpuInventory);
            Attempt(inventory, "Кэш процессора", delegate { HardwareDetailsInventory.ReadCpuCaches(inventory); });
            Attempt(inventory, "Информация об оперативной памяти", ReadMemoryInventory);
            Attempt(inventory, "Слоты оперативной памяти", delegate
            {
                List<Dictionary<string, object>> arrays = Query("root\\cimv2", "SELECT Use, MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
                ApplyMemorySlots(inventory, memoryModules, arrays);
                HardwareDetailsInventory.ApplyMemoryMaximum(inventory, arrays);
            });
            Attempt(inventory, "Информация о накопителях", ReadDiskInventory);
            Attempt(inventory, "Типы накопителей", ReadDiskTypes);
            Attempt(inventory, "Разделы накопителей", ReadDiskVolumes);
            Attempt(inventory, "Видеокарты DXGI", ReadGpuInventory);
            Attempt(inventory, "Информация об экранах", ReadScreenInventory);
            Attempt(inventory, "Справочник оборудования", delegate { HardwareCatalog.Default.Apply(inventory); });
            if (inventory.Gpus.Count == 0)
            {
                Attempt(inventory, "Названия видеокарт", delegate
                {
                    foreach (Dictionary<string, object> row in Query("root\\cimv2", "SELECT Name FROM Win32_VideoController"))
                    {
                        inventory.Gpus.Add(new GpuSnapshot { Name = Text(row, "Name", "Видеокарта"), MemoryKind = "Объём видеопамяти недоступен" });
                    }
                });
            }

        }

        private void EnsureHardwareSensors()
        {
            if (sensorsInitialized) { return; }
            sensorsInitialized = true;
            try
            {
                pawnInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
                // Open the base object before enabling groups: a failing CPU provider
                // must not abort GPU and battery initialization. LHM creates Intel GPU
                // telemetry with its CPU group, including IGCL when PawnIO is absent.
                computer = new Computer();
                computer.Open();
            }
            catch (Exception exception)
            {
                sensorFailure = "Датчики оборудования недоступны (" + exception.GetType().Name + ").";
                if (computer != null)
                {
                    try { computer.Close(); } catch { }
                    computer = null;
                }
            }
            if (computer == null) { return; }
            try { computer.IsCpuEnabled = true; }
            catch (Exception exception)
            {
                cpuSensorFailure = "Датчики CPU недоступны (" + exception.GetType().Name + ").";
                sensorInitializationIssues.Add(cpuSensorFailure);
            }
            try { computer.IsGpuEnabled = true; }
            catch (Exception exception) { sensorInitializationIssues.Add("Дополнительные датчики GPU недоступны (" + exception.GetType().Name + ")."); }
            try { computer.IsMemoryEnabled = true; }
            catch (Exception exception) { sensorInitializationIssues.Add("Дополнительные датчики RAM недоступны (" + exception.GetType().Name + ")."); }
            try { computer.IsBatteryEnabled = true; }
            catch (Exception exception) { sensorInitializationIssues.Add("Дополнительные датчики батареи недоступны (" + exception.GetType().Name + ")."); }
        }

        private void ReadComputerIdentity()
        {
            Attempt(inventory, "Производитель и модель компьютера", delegate
            {
                Dictionary<string, object> system = Query("root\\cimv2", "SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem").FirstOrDefault();
                if (system != null)
                {
                    ApplyComputerIdentity(inventory, Text(system, "Manufacturer", ""), Text(system, "Model", ""), Text(system, "SystemSKUNumber", ""), "", "", "");
                }
            });
            if (inventory.ComputerManufacturer.Length == 0 || inventory.ComputerModel.Length == 0 || (IsLenovo(inventory.ComputerManufacturer) && IsMachineTypeCode(inventory.ComputerModel)))
            {
                Attempt(inventory, "Уточнение модели компьютера", delegate
                {
                    Dictionary<string, object> product = Query("root\\cimv2", "SELECT Vendor, Name, Version FROM Win32_ComputerSystemProduct").FirstOrDefault();
                    if (product != null)
                    {
                        ApplyComputerIdentity(inventory, inventory.ComputerManufacturer, inventory.ComputerModel, inventory.ComputerSku, Text(product, "Vendor", ""), Text(product, "Name", ""), Text(product, "Version", ""));
                    }
                });
            }
        }

        internal static string SanitizeModelValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return ""; }
            string cleaned = Regex.Replace(Regex.Replace(value, "[\\x00-\\x1F\\x7F]", " "), "\\s+", " ").Trim();
            string key = Regex.Replace(cleaned.ToLowerInvariant(), "[^a-z0-9]", "");
            switch (key)
            {
                case "tobefilledbyoem":
                case "systemproductname":
                case "systemmanufacturer":
                case "systemmodel":
                case "systemversion":
                case "systemsku":
                case "productname":
                case "productversion":
                case "defaultstring":
                case "default":
                case "unknown":
                case "unspecified":
                case "notspecified":
                case "notapplicable":
                case "notavailable":
                case "notprovided":
                case "none":
                case "null":
                case "na":
                case "invalid":
                case "oem":
                case "type1productconfigid":
                case "0123456789":
                case "1234567890":
                    return "";
            }
            if (!Regex.IsMatch(cleaned, "[\\p{L}\\p{N}]") || Regex.IsMatch(cleaned, "^[0\\s._-]+$")) { return ""; }
            return cleaned;
        }

        internal static void ApplyComputerIdentity(Snapshot snapshot, string manufacturer, string model, string sku, string vendor, string productName, string productVersion)
        {
            string resolvedManufacturer = SanitizeModelValue(manufacturer);
            if (resolvedManufacturer.Length == 0) { resolvedManufacturer = SanitizeModelValue(vendor); }
            string resolvedModel = SanitizeModelValue(model);
            string resolvedSku = SanitizeModelValue(sku);
            string alternateName = SanitizeModelValue(productName);
            string alternateVersion = SanitizeModelValue(productVersion);
            if (resolvedModel.Length == 0) { resolvedModel = alternateName; }
            if (IsLenovo(resolvedManufacturer) && (resolvedModel.Length == 0 || IsMachineTypeCode(resolvedModel)))
            {
                string descriptiveModel = IsDescriptiveLenovoModel(alternateVersion) ? alternateVersion : IsDescriptiveLenovoModel(alternateName) ? alternateName : "";
                if (descriptiveModel.Length > 0)
                {
                    if (resolvedSku.Length == 0 && IsMachineTypeCode(resolvedModel)) { resolvedSku = resolvedModel; }
                    resolvedModel = descriptiveModel;
                }
            }
            snapshot.ComputerManufacturer = resolvedManufacturer;
            snapshot.ComputerModel = resolvedModel;
            snapshot.ComputerSku = resolvedSku;
        }

        private static bool IsLenovo(string manufacturer)
        {
            return manufacturer.IndexOf("Lenovo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMachineTypeCode(string model)
        {
            return Regex.IsMatch(model, "^(?=.{4,24}$)(?=.*[0-9])[A-Za-z0-9-]+$");
        }

        private static bool IsDescriptiveLenovoModel(string model)
        {
            return Regex.IsMatch(model, "^(?:Lenovo\\s+)?(?:ThinkPad|ThinkBook|ThinkCentre|ThinkStation|IdeaPad|IdeaCentre|Yoga|Legion|LOQ)\\b.+", RegexOptions.IgnoreCase);
        }

        private void ReadCpuInventory()
        {
            List<Dictionary<string, object>> processors = Query("root\\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
            if (processors.Count == 0) { inventory.CpuName = "Процессор: нет данных Windows"; return; }
            inventory.CpuName = string.Join(" / ", processors.Select(row => Text(row, "Name", "Процессор")).ToArray());
            foreach (Dictionary<string, object> processor in processors)
            {
                inventory.CpuCores += (int)(Unsigned(processor, "NumberOfCores") ?? 0);
                inventory.CpuThreads += (int)(Unsigned(processor, "NumberOfLogicalProcessors") ?? 0);
            }
            inventory.CpuClockMhz = Positive(Unsigned(processors[0], "CurrentClockSpeed"));
            inventory.CpuMaxClockMhz = Positive(Unsigned(processors[0], "MaxClockSpeed"));
            inventory.CpuClockSource = "Данные BIOS/SMBIOS, не текущая частота";
        }

        private void ReadMemoryInventory()
        {
            List<Dictionary<string, object>> modules = Query("root\\cimv2", "SELECT Capacity, ConfiguredClockSpeed, DeviceLocator, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory");
            memoryModules = modules;
            HardwareDetailsInventory.ApplyInstalledMemory(inventory, modules);
            ApplyMemorySlots(inventory, modules, null);
            List<string> rates = new List<string>();
            List<string> configurations = new List<string>();
            List<string> memoryTypes = new List<string>();
            foreach (Dictionary<string, object> module in modules)
            {
                ulong capacity = Unsigned(module, "Capacity") ?? 0;
                ulong rate = Unsigned(module, "ConfiguredClockSpeed") ?? 0;
                if (rate > 0) { rates.Add(rate.ToString(CultureInfo.InvariantCulture)); }
                string memoryType = MemoryType((int)(Unsigned(module, "SMBIOSMemoryType") ?? 0));
                memoryTypes.Add(memoryType.Length > 0 ? memoryType : "Не определён");
                configurations.Add((capacity / 1073741824d).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " GB" + (memoryType.Length > 0 ? " " + memoryType : ""));
            }
            inventory.RamTotalBytes = inventory.RamInstalledBytes;
            inventory.RamRate = rates.Count == 0 ? "Windows не сообщает частоту" : string.Join(" / ", rates.Distinct().ToArray()) + " МТ/с (настроено в BIOS)";
            if (rates.Count > 0 && rates.Count < modules.Count) { inventory.RamRate += "; часть модулей без данных"; }
            inventory.RamClockMhz = MemoryClockMhz(modules);
            inventory.RamConfiguration = string.Join(" + ", configurations.ToArray());
            inventory.RamType = memoryTypes.Count == 0 ? "Нет данных" : string.Join(" / ", memoryTypes.Distinct().ToArray());
        }

        internal static void ApplyMemorySlots(Snapshot snapshot, List<Dictionary<string, object>> modules, List<Dictionary<string, object>> arrays)
        {
            snapshot.RamModuleCount = null;
            snapshot.RamSlotCount = null;
            snapshot.RamFreeSlots = null;
            snapshot.RamHasModularFormFactor = false;
            snapshot.RamSlotsNote = "BIOS не сообщает полные сведения о слотах памяти.";
            if (modules == null || modules.Count == 0 || modules.Any(module => !Unsigned(module, "Capacity").HasValue)) { return; }
            var installed = modules.Where(module => Unsigned(module, "Capacity") > 0).ToList();
            if (installed.Count == 0) { return; }
            snapshot.RamModuleCount = installed.Count;
            snapshot.RamHasModularFormFactor = installed.All(module =>
            {
                ulong form = Unsigned(module, "FormFactor") ?? 0;
                return form == 7 || form == 8 || form == 11 || form == 12 || form == 13;
            });
            if (!snapshot.RamHasModularFormFactor)
            {
                snapshot.RamSlotsNote = "Обнаружено устройств памяти: " + installed.Count + ". Возможна распаянная память или неизвестный форм-фактор; число свободных разъёмов не подтверждено BIOS.";
                return;
            }
            if (arrays == null || arrays.Any(array => !Unsigned(array, "Use").HasValue || Unsigned(array, "Use") <= 2)) { return; }
            var systemArrays = arrays.Where(array => Unsigned(array, "Use") == 3).ToList();
            if (systemArrays.Count == 0 || systemArrays.Any(array => !(Unsigned(array, "MemoryDevices") > 0))) { return; }
            ulong total = 0;
            foreach (var array in systemArrays)
            {
                ulong slots = Unsigned(array, "MemoryDevices").Value;
                if (slots > int.MaxValue || total > (ulong)int.MaxValue - slots) { return; }
                total += slots;
            }
            if (total < (ulong)installed.Count)
            {
                snapshot.RamSlotsNote = "BIOS сообщает меньше слотов, чем установленных модулей. Число свободных слотов не определено.";
                return;
            }
            snapshot.RamSlotCount = (int)total;
            snapshot.RamFreeSlots = (int)total - installed.Count;
            snapshot.RamSlotsNote = "По данным BIOS: установлено планок — " + installed.Count + ", всего слотов — " + total + ", свободно — " + snapshot.RamFreeSlots + ".";
        }

        private static string MemoryType(int type)
        {
            switch (type)
            {
                case 18: return "DDR";
                case 19: return "DDR2";
                case 20: return "DDR2 FB-DIMM";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                default: return "";
            }
        }

        internal static double? MemoryClockMhz(List<Dictionary<string, object>> modules)
        {
            if (modules == null || modules.Count == 0) { return null; }
            ulong? commonRate = null;
            foreach (var module in modules)
            {
                ulong type = Unsigned(module, "SMBIOSMemoryType") ?? 0;
                if (type != 18 && type != 19 && type != 20 && type != 24 && type != 26 && type != 34) { return null; }
                ulong? rate = Unsigned(module, "ConfiguredClockSpeed");
                if (!rate.HasValue || rate.Value == 0 || (commonRate.HasValue && commonRate != rate)) { return null; }
                commonRate = rate;
            }
            return commonRate.Value / 2d;
        }

        private void ReadDiskInventory()
        {
            foreach (Dictionary<string, object> row in Query("root\\cimv2", "SELECT Index, Model, Size, InterfaceType, PNPDeviceID, SerialNumber FROM Win32_DiskDrive"))
            {
                ulong? index = Unsigned(row, "Index");
                if (!index.HasValue) { continue; }
                inventory.Disks.Add(new DiskSnapshot
                {
                    Index = (uint)index.Value,
                    StorageIdentity = StorageTopology.CreateIdentity(Text(row, "PNPDeviceID", "")),
                    PersistentHardwareId = PersistentDeviceIdentity.Disk(Text(row, "SerialNumber", ""), Text(row, "Model", ""), Unsigned(row, "Size")),
                    Name = Text(row, "Model", "Накопитель " + index.Value),
                    SizeBytes = Unsigned(row, "Size"),
                    MediaType = "Тип не определён" + (Text(row, "InterfaceType", "") == "USB" ? " · USB" : "")
                });
            }
            inventory.Disks.Sort((first, second) => first.Index.CompareTo(second.Index));
        }

        private void ReadDiskTypes()
        {
            var windows = Query("root\\Microsoft\\Windows\\Storage", "SELECT Number, BusType, UniqueId, UniqueIdFormat FROM MSFT_Disk");
            List<Dictionary<string, object>> physical = null;
            try { physical = Query("root\\Microsoft\\Windows\\Storage", "SELECT UniqueId, UniqueIdFormat, MediaType FROM MSFT_PhysicalDisk"); } catch { }
            StorageTopology.ApplyDiskTypes(inventory.Disks, windows, physical);
        }

        private void ReadDiskVolumes()
        {
            StorageTopology.ApplyVolumes(inventory.Disks,
                Query("root\\cimv2", "SELECT DeviceID, DriveType, FileSystem, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"),
                Query("root\\cimv2", "SELECT DeviceID, DiskIndex FROM Win32_DiskPartition"),
                Query("root\\cimv2", "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"),
                Path.GetPathRoot(Path.GetTempPath()), Path.GetPathRoot(Environment.SystemDirectory),
                BenchmarkSettings.Current.DiskMiB * 1024L * 1024L);
        }
        private static string ReferenceDeviceId(string reference)
        {
            Match match = Regex.Match(reference, "DeviceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\") : "";
        }

        private void ReadGpuInventory()
        {
            Guid factoryId = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            IntPtr factory = IntPtr.Zero;
            int result = CreateDXGIFactory(ref factoryId, out factory);
            if (result < 0 || factory == IntPtr.Zero) { Marshal.ThrowExceptionForHR(result); return; }
            try
            {
                EnumerateAdapters enumerate = (EnumerateAdapters)Marshal.GetDelegateForFunctionPointer(Method(factory, 7), typeof(EnumerateAdapters));
                for (uint index = 0; index < 32; index++)
                {
                    IntPtr adapter;
                    result = enumerate(factory, index, out adapter);
                    if (result == unchecked((int)0x887A0002)) { break; }
                    if (result < 0) { Marshal.ThrowExceptionForHR(result); }
                    if (adapter == IntPtr.Zero) { continue; }
                    try
                    {
                        GetAdapterDescription getDescription = (GetAdapterDescription)Marshal.GetDelegateForFunctionPointer(Method(adapter, 8), typeof(GetAdapterDescription));
                        AdapterDescription description;
                        result = getDescription(adapter, out description);
                        if (result < 0) { Marshal.ThrowExceptionForHR(result); }
                        if (description.VendorId == 0x1414 && description.DeviceId == 0x008c) { continue; }
                        ulong dedicated = description.DedicatedVideoMemory.ToUInt64();
                        ulong shared = description.SharedSystemMemory.ToUInt64();
                        GpuSnapshot gpu = new GpuSnapshot
                        {
                            Name = description.Description.Trim(),
                            PersistentHardwareId = PersistentDeviceIdentity.Gpu(description.VendorId, description.DeviceId, description.SubSysId, description.Revision),
                            AdapterIndex = index,
                            AdapterLuidLow = description.LuidLow,
                            AdapterLuidHigh = description.LuidHigh,
                            SharedMemoryBytes = shared,
                            IsIntegrated = HardwareDetailsInventory.ReadUnifiedGpuArchitecture(adapter)
                        };
                        if (dedicated > 0)
                        {
                            gpu.MemoryBytes = dedicated;
                            gpu.MemoryKind = gpu.IsIntegrated == true ? "Зарезервированная RAM (DXGI)" : "Выделенная / зарезервированная (DXGI)";
                            if (shared > 0)
                            {
                                gpu.MemoryKind += "; общая до " + (shared / 1073741824d).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " GB";
                            }
                        }
                        else if (shared > 0)
                        {
                            gpu.MemoryBytes = 0;
                            gpu.MemoryKind = "Общая RAM: доступный предел, не отдельная VRAM";
                        }
                        else { gpu.MemoryKind = "Объём видеопамяти недоступен"; }
                        inventory.Gpus.Add(gpu);
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }

        private static IntPtr Method(IntPtr instance, int slot)
        {
            return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
        }

        private void ReadScreenInventory()
        {
            List<Dictionary<string, object>> identities = null;
            List<Dictionary<string, object>> connections = null;
            try { identities = Query("root\\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID WHERE Active = TRUE"); } catch { }
            try { connections = Query("root\\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams WHERE Active = TRUE"); } catch { }
            inventory.Screens = ScreenInventory.Read(identities, connections);
        }

        private Snapshot CopyInventory()
        {
            Snapshot snapshot = new Snapshot
            {
                ComputerManufacturer = inventory.ComputerManufacturer,
                ComputerModel = inventory.ComputerModel,
                ComputerSku = inventory.ComputerSku,
                WindowsName = inventory.WindowsName,
                CpuName = inventory.CpuName,
                CpuCores = inventory.CpuCores,
                CpuThreads = inventory.CpuThreads,
                CpuClockMhz = inventory.CpuClockMhz,
                CpuMaxClockMhz = inventory.CpuMaxClockMhz,
                CpuClockSource = inventory.CpuClockSource,
                CpuL1CacheBytes = inventory.CpuL1CacheBytes,
                CpuL2CacheBytes = inventory.CpuL2CacheBytes,
                CpuL3CacheBytes = inventory.CpuL3CacheBytes,
                CpuCacheSource = inventory.CpuCacheSource,
                RamTotalBytes = inventory.RamTotalBytes,
                RamInstalledBytes = inventory.RamInstalledBytes,
                RamMaximumBytes = inventory.RamMaximumBytes,
                RamMaximumSource = inventory.RamMaximumSource,
                RamManufacturerMaximumBytes = inventory.RamManufacturerMaximumBytes,
                RamManufacturerMaximumSource = inventory.RamManufacturerMaximumSource,
                RamRate = inventory.RamRate,
                RamClockMhz = inventory.RamClockMhz,
                RamConfiguration = inventory.RamConfiguration,
                RamType = inventory.RamType,
                RamModuleCount = inventory.RamModuleCount,
                RamSlotCount = inventory.RamSlotCount,
                RamFreeSlots = inventory.RamFreeSlots,
                RamHasModularFormFactor = inventory.RamHasModularFormFactor,
                RamSlotsNote = inventory.RamSlotsNote,
                IsAdmin = inventory.IsAdmin,
                Issues = new List<string>(inventory.Issues)
            };
            snapshot.Disks = inventory.Disks.Select(disk => new DiskSnapshot { Index = disk.Index, StorageIdentity = disk.StorageIdentity, PersistentHardwareId = disk.PersistentHardwareId, Name = disk.Name, SizeBytes = disk.SizeBytes, MediaType = disk.MediaType, Volumes = disk.Volumes, TestVolumeRoot = disk.TestVolumeRoot, HealthPercent = disk.HealthPercent, PowerOnHours = disk.PowerOnHours, CriticalWarning = disk.CriticalWarning, SmartPassed = disk.SmartPassed, SmartStatus = disk.SmartStatus, SmartSource = disk.SmartSource }).ToList();
            snapshot.Gpus = inventory.Gpus.Select(gpu => new GpuSnapshot { Name = gpu.Name, PersistentHardwareId = gpu.PersistentHardwareId, MemoryBytes = gpu.MemoryBytes, MemoryKind = gpu.MemoryKind, AdapterIndex = gpu.AdapterIndex, AdapterLuidLow = gpu.AdapterLuidLow, AdapterLuidHigh = gpu.AdapterLuidHigh, SharedMemoryBytes = gpu.SharedMemoryBytes, IsIntegrated = gpu.IsIntegrated }).ToList();
            snapshot.Screens = inventory.Screens.Select(screen => new ScreenSnapshot { Name = screen.Name, InstanceName = screen.InstanceName, Width = screen.Width, Height = screen.Height, RefreshHz = screen.RefreshHz, IsInternal = screen.IsInternal, MatrixType = screen.MatrixType, BrightnessNits = screen.BrightnessNits, Source = screen.Source }).ToList();
            return snapshot;
        }

        private void ReadSystemUsage(Snapshot snapshot)
        {
            MemoryStatus memory = new MemoryStatus();
            memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (GlobalMemoryStatusEx(ref memory))
            {
                snapshot.RamTotalBytes = memory.TotalPhysical;
                snapshot.RamUsedBytes = memory.TotalPhysical - Math.Min(memory.TotalPhysical, memory.AvailablePhysical);
            }
            else { snapshot.Issues.Add("Windows не предоставила использование памяти."); }

            FileTime idle;
            FileTime kernel;
            FileTime user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                ulong idleTicks = idle.Value;
                ulong totalTicks = kernel.Value + user.Value;
                if (hasPreviousCpu && totalTicks > previousTotal && idleTicks >= previousIdle)
                {
                    ulong elapsed = totalTicks - previousTotal;
                    ulong idleElapsed = idleTicks - previousIdle;
                    snapshot.CpuLoad = Math.Max(0, Math.Min(100, 100d * (elapsed - Math.Min(elapsed, idleElapsed)) / elapsed));
                }
                previousIdle = idleTicks;
                previousTotal = totalTicks;
                hasPreviousCpu = true;
            }
            else { snapshot.Issues.Add("Windows не предоставила загрузку процессора."); }
        }

        private void ReadDiskActivity(Snapshot snapshot)
        {
            Attempt(snapshot, "Скорость чтения и записи", delegate
            {
                foreach (Dictionary<string, object> row in Query("root\\cimv2", "SELECT Name, DiskReadBytesPerSec, DiskWriteBytesPerSec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
                {
                    Match match = Regex.Match(Text(row, "Name", ""), "^(\\d+)(?:\\s|$)");
                    uint index;
                    if (!match.Success || !uint.TryParse(match.Groups[1].Value, out index)) { continue; }
                    DiskSnapshot disk = snapshot.Disks.FirstOrDefault(item => item.Index == index);
                    if (disk == null) { continue; }
                    disk.ReadBytesPerSecond = Number(row, "DiskReadBytesPerSec");
                    disk.WriteBytesPerSecond = Number(row, "DiskWriteBytesPerSec");
                }
            });
        }

        private static void ReadPower(Snapshot snapshot)
        {
            PowerStatus power;
            if (!GetSystemPowerStatus(out power))
            {
                snapshot.BatteryStatus = "Windows не сообщает состояние батареи";
                return;
            }
            if (power.BatteryFlag == 255)
            {
                snapshot.BatteryPresent = null;
                snapshot.BatteryStatus = "Состояние батареи неизвестно";
            }
            else if ((power.BatteryFlag & 128) != 0)
            {
                snapshot.BatteryPresent = false;
                snapshot.BatteryStatus = "Батарея не установлена";
                snapshot.BatteryDetail = "Питание от сети";
                return;
            }
            else
            {
                snapshot.BatteryPresent = true;
                snapshot.BatteryStatus = (power.BatteryFlag & 8) != 0 ? "Заряжается" : power.AcLineStatus == 1 ? "Подключено питание" : power.AcLineStatus == 0 ? "Работа от батареи" : "Источник питания неизвестен";
            }
            if (power.BatteryLifePercent <= 100)
            {
                snapshot.BatteryPresent = true;
                snapshot.BatteryPercent = power.BatteryLifePercent;
            }
            if (power.AcLineStatus == 0 && power.BatteryLifeTime != uint.MaxValue)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(power.BatteryLifeTime);
                snapshot.BatteryDetail = "Осталось примерно " + ((int)remaining.TotalHours).ToString() + " ч " + remaining.Minutes.ToString() + " мин";
            }
        }

        private void ReadHardwareSensors(Snapshot snapshot)
        {
            detectedBatterySensorCount = 0;
            snapshot.Issues.AddRange(sensorInitializationIssues);
            if (computer == null) { return; }
            List<IHardware> batteries = new List<IHardware>();
            List<IHardware> devices = new List<IHardware>();
            try { devices.AddRange(computer.Hardware); }
            catch (Exception exception) { snapshot.Issues.Add("Список датчиков недоступен (" + exception.GetType().Name + ")."); return; }
            List<double> cpuClocks = new List<double>();
            List<double> cpuTemperatures = new List<double>();
            HashSet<GpuSnapshot> matchedGpus = new HashSet<GpuSnapshot>();
            foreach (IHardware hardware in devices)
            {
                try
                {
                    UpdateHardware(hardware);
                    List<ISensor> allSensors = AllSensors(hardware).ToList();
                    List<ISensor> sensors = allSensors.Where(sensor => Valid(sensor.Value)).ToList();
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        KeyValuePair<string, double?> temperature = SelectCpuTemperature(TemperatureReadings(allSensors));
                        if (temperature.Value.HasValue)
                        {
                            if (cpuTemperatures.Count == 0 || temperature.Value.Value > cpuTemperatures.Max())
                            {
                                snapshot.CpuTemperatureStatus = "Датчик CPU: " + temperature.Key + ".";
                            }
                            cpuTemperatures.Add(temperature.Value.Value);
                        }
                        cpuClocks.AddRange(sensors.Where(sensor => sensor.SensorType == SensorType.Clock && sensor.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 && sensor.Value > 0).Select(sensor => (double)sensor.Value.Value));
                    }
                    else if (hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel)
                    {
                        ReadGpuSensors(snapshot, hardware, allSensors, matchedGpus);
                    }
                    else if (hardware.HardwareType == HardwareType.Battery) { batteries.Add(hardware); }
                }
                catch (Exception exception)
                {
                    snapshot.Issues.Add("Датчики " + hardware.Name + " недоступны (" + exception.GetType().Name + ").");
                }
            }
            if (cpuClocks.Count > 0 && pawnInstalled)
            {
                snapshot.CpuClockMhz = cpuClocks.Average();
                snapshot.CpuClockSource = "Текущая: среднее по датчикам ядер";
            }
            if (cpuTemperatures.Count > 0)
            {
                snapshot.CpuTemperature = cpuTemperatures.Max();
                snapshot.CpuTemperatureTrustedForStress = true;
                snapshot.CpuTemperatureExperimental = false;
            }
            detectedBatterySensorCount = batteries.Count;
            if (batteries.Count > 0) { ReadBatterySensors(snapshot, batteries); }
        }

        private static void UpdateHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware child in hardware.SubHardware) { UpdateHardware(child); }
        }

        private static IEnumerable<ISensor> AllSensors(IHardware hardware)
        {
            foreach (ISensor sensor in hardware.Sensors) { yield return sensor; }
            foreach (IHardware child in hardware.SubHardware)
            {
                foreach (ISensor sensor in AllSensors(child)) { yield return sensor; }
            }
        }

        private static void ReadGpuSensors(Snapshot snapshot, IHardware hardware, List<ISensor> allSensors, HashSet<GpuSnapshot> matchedGpus)
        {
            GpuSnapshot gpu = ResolveGpuSensorTarget(snapshot, hardware.Name, matchedGpus);
            if (gpu == null) { return; }
            if (!gpu.Temperature.HasValue)
            {
                ApplyGpuTemperature(gpu, TemperatureReadings(allSensors), hardware.HardwareType == HardwareType.GpuIntel);
            }
            List<ISensor> sensors = allSensors.Where(sensor => Valid(sensor.Value)).ToList();
            ISensor load = sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Load && string.Equals(sensor.Name, "GPU Core", StringComparison.OrdinalIgnoreCase));
            if (load != null) { gpu.Load = load.Value; }
            ISensor memory = sensors.FirstOrDefault(sensor => (sensor.SensorType == SensorType.SmallData || sensor.SensorType == SensorType.Data) && string.Equals(sensor.Name, "GPU Memory Total", StringComparison.OrdinalIgnoreCase) && sensor.Value > 0);
            if (memory != null)
            {
                double bytes = memory.Value.Value * (memory.SensorType == SensorType.SmallData ? 1048576d : 1073741824d);
                if (bytes > 0 && bytes < ulong.MaxValue)
                {
                    gpu.MemoryBytes = (ulong)bytes;
                    gpu.MemoryKind = hardware.HardwareType == HardwareType.GpuIntel ? "Память GPU по данным драйвера" : "Видеопамять по данным драйвера";
                }
            }
        }

        private static IEnumerable<KeyValuePair<string, double?>> TemperatureReadings(IEnumerable<ISensor> sensors)
        {
            return sensors.Where(sensor => sensor.SensorType == SensorType.Temperature)
                .Select(sensor => new KeyValuePair<string, double?>(sensor.Name, sensor.Value.HasValue ? (double?)sensor.Value.Value : null));
        }

        internal static KeyValuePair<string, double?> SelectCpuTemperature(IEnumerable<KeyValuePair<string, double?>> readings)
        {
            List<KeyValuePair<string, double?>> valid = readings.Where(reading => IsFiniteTemperature(reading.Value)
                && !string.IsNullOrWhiteSpace(reading.Key)
                && reading.Key.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) < 0
                && reading.Key.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            KeyValuePair<string, double?> selected = valid.FirstOrDefault(reading => string.Equals(reading.Key, "CPU Package", StringComparison.OrdinalIgnoreCase));
            // Prefer AMD's physical die reading when an offset-adjusted Tctl also exists.
            if (!selected.Value.HasValue) { selected = valid.FirstOrDefault(reading => reading.Key.IndexOf("Core (Tdie)", StringComparison.OrdinalIgnoreCase) >= 0); }
            if (!selected.Value.HasValue) { selected = valid.FirstOrDefault(reading => reading.Key.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0); }
            if (!selected.Value.HasValue)
            {
                selected = valid.Where(reading => reading.Key.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0
                    || reading.Key.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0
                    || reading.Key.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(reading => reading.Value.Value).FirstOrDefault();
            }
            return selected;
        }

        internal static void ApplyGpuTemperature(GpuSnapshot gpu, IEnumerable<KeyValuePair<string, double?>> readings, bool intel)
        {
            List<KeyValuePair<string, double?>> coreReadings = readings.Where(reading => string.Equals(reading.Key, "GPU Core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reading.Key, "GPU", StringComparison.OrdinalIgnoreCase)).ToList();
            KeyValuePair<string, double?> selected = coreReadings.FirstOrDefault(reading => IsFiniteTemperature(reading.Value));
            gpu.Temperature = selected.Value;
            if (gpu.Temperature.HasValue) { gpu.TemperatureStatus = "Датчик GPU: " + selected.Key + "."; }
            else if (coreReadings.Count > 0) { gpu.TemperatureStatus = "Датчик GPU временно не вернул температуру."; }
            else if (intel && gpu.IsIntegrated == true) { gpu.TemperatureStatus = "Драйвер Intel не предоставляет отдельную температуру GPU."; }
            else { gpu.TemperatureStatus = "Драйвер/видеокарта не предоставили температуру GPU."; }
        }

        private static bool IsFiniteTemperature(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }

        internal static GpuSnapshot ResolveGpuSensorTarget(Snapshot snapshot, string hardwareName, HashSet<GpuSnapshot> matchedGpus)
        {
            string name = NormalizeName(hardwareName);
            List<GpuSnapshot> candidates = snapshot.Gpus.Where(item => NormalizeName(item.Name) == name).ToList();
            if (candidates.Count > 1)
            {
                string issue = "Датчики " + hardwareName + " не сопоставлены: обнаружено несколько видеокарт с одинаковым названием.";
                if (!snapshot.Issues.Contains(issue)) { snapshot.Issues.Add(issue); }
                return null;
            }
            GpuSnapshot gpu = candidates.FirstOrDefault();
            if (gpu == null)
            {
                if (snapshot.Gpus.Any(item => item.AdapterIndex.HasValue))
                {
                    snapshot.Issues.Add("Датчики " + hardwareName + " не сопоставлены с видеокартами DXGI.");
                    return null;
                }
                gpu = new GpuSnapshot { Name = hardwareName, MemoryKind = "Объём видеопамяти недоступен" };
                snapshot.Gpus.Add(gpu);
            }
            // A prior match does not make a second same-name hardware sensor trustworthy.
            if (!matchedGpus.Add(gpu)) { return null; }
            return gpu;
        }

        private static string NormalizeName(string name)
        {
            return Regex.Replace(name.ToLowerInvariant().Replace("(r)", "").Replace("(tm)", ""), "[^a-z0-9]", "");
        }

        private static void ReadBatterySensors(Snapshot snapshot, List<IHardware> batteries)
        {
            double designed = 0;
            double full = 0;
            double remaining = 0;
            int complete = 0;
            int charged = 0;
            foreach (IHardware battery in batteries)
            {
                List<ISensor> sensors = AllSensors(battery).Where(sensor => Valid(sensor.Value)).ToList();
                double? designedValue = SensorNumber(sensors, "Designed Capacity");
                double? fullValue = SensorNumber(sensors, "Fully-Charged Capacity");
                double? remainingValue = SensorNumber(sensors, "Remaining Capacity");
                if (designedValue > 0 && fullValue > 0)
                {
                    designed += designedValue.Value;
                    full += fullValue.Value;
                    complete++;
                }
                if (remainingValue >= 0 && fullValue > 0)
                {
                    remaining += remainingValue.Value;
                    charged++;
                }
            }
            snapshot.BatteryPresent = true;
            if (snapshot.BatteryStatus == "Батарея не установлена" || snapshot.BatteryStatus == "Windows не сообщает состояние батареи")
            {
                snapshot.BatteryStatus = "Батарея обнаружена; режим питания недоступен";
            }
            if (batteries.Count == 1 && complete == 1 && designed > 0)
            {
                snapshot.BatteryHealthPercent = Math.Min(100, full / designed * 100);
                if (!snapshot.BatteryPercent.HasValue && charged == batteries.Count) { snapshot.BatteryPercent = Math.Min(100, remaining / full * 100); }
            }
            if (batteries.Count > 1) { AppendBatteryDetail(snapshot, batteries.Count + " батареи; заряд — общий показатель Windows"); }
        }

        private static double? SensorNumber(IEnumerable<ISensor> sensors, string name)
        {
            ISensor sensor = sensors.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            return sensor == null ? (double?)null : sensor.Value;
        }

        private void ReadBatteryHealth(Snapshot snapshot)
        {
            if (DateTime.UtcNow >= nextBatteryInventory || detectedBatterySensorCount > cachedBatteryDeviceCount)
            {
                nextBatteryInventory = DateTime.UtcNow.AddMinutes(1);
                cachedBatteryHealth = null;
                cachedBatteryDesignWh = null;
                cachedBatteryFullChargeWh = null;
                cachedBatteryCycleCount = null;
                cachedBatteryCycleDetail = "";
                cachedBatteryCyclesPerDevice = false;
                cachedBatteryDetail = "";
                cachedBatteryIssue = "";
                int detectedBatteryCount = detectedBatterySensorCount;
                try
                {
                    List<Dictionary<string, object>> designed = Query("root\\wmi", "SELECT InstanceName, DesignedCapacity, Capabilities FROM BatteryStaticData WHERE Active = TRUE");
                    detectedBatteryCount = Math.Max(detectedBatteryCount, designed.Count);
                    List<Dictionary<string, object>> charged = Query("root\\wmi", "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity WHERE Active = TRUE");
                    detectedBatteryCount = Math.Max(detectedBatteryCount, charged.Count);
                    Snapshot batteryInventory = new Snapshot();
                    ApplyBatteryCapacityInventory(batteryInventory, designed, charged, detectedBatteryCount);
                    cachedBatteryHealth = batteryInventory.BatteryHealthPercent;
                    cachedBatteryDesignWh = batteryInventory.BatteryDesignWh;
                    cachedBatteryFullChargeWh = batteryInventory.BatteryFullChargeWh;
                    if (cachedBatteryDesignWh.HasValue && cachedBatteryFullChargeWh.HasValue)
                    {
                        cachedBatteryDetail = designed.Count > 1 ? "Ёмкость: сумма " + designed.Count + " батарей" : "";
                    }
                    else { cachedBatteryDetail = "Ёмкость в Вт·ч недоступна: нет полных данных или единицы относительные"; }
                }
                catch (Exception exception)
                {
                    cachedBatteryDetail = "Ёмкость батареи в Вт·ч недоступна";
                    cachedBatteryIssue = "Ёмкость батареи: " + exception.GetType().Name + ".";
                }
                try
                {
                    List<Dictionary<string, object>> cycles = Query("root\\wmi", "SELECT InstanceName, CycleCount FROM BatteryCycleCount WHERE Active = TRUE");
                    Snapshot cycleInventory = new Snapshot();
                    ApplyBatteryCycleInventory(cycleInventory, cycles, detectedBatteryCount);
                    cachedBatteryCycleCount = cycleInventory.BatteryCycleCount;
                    cachedBatteryCycleDetail = cycleInventory.BatteryCycleDetail;
                    cachedBatteryCyclesPerDevice = cycleInventory.BatteryCyclesPerDevice;
                }
                catch (Exception)
                {
                    cachedBatteryCyclesPerDevice = detectedBatteryCount > 1;
                    cachedBatteryCycleDetail = "Windows не предоставляет счётчик циклов батареи.";
                }
                cachedBatteryDeviceCount = detectedBatteryCount;
            }
            if (cachedBatteryHealth.HasValue || Math.Max(cachedBatteryDeviceCount, detectedBatterySensorCount) > 1) { snapshot.BatteryHealthPercent = cachedBatteryHealth; }
            if (!snapshot.BatteryDesignWh.HasValue) { snapshot.BatteryDesignWh = cachedBatteryDesignWh; }
            if (!snapshot.BatteryFullChargeWh.HasValue) { snapshot.BatteryFullChargeWh = cachedBatteryFullChargeWh; }
            snapshot.BatteryCycleCount = cachedBatteryCycleCount;
            snapshot.BatteryCycleDetail = cachedBatteryCycleDetail;
            snapshot.BatteryCyclesPerDevice = cachedBatteryCyclesPerDevice;
            AppendBatteryDetail(snapshot, cachedBatteryDetail);
            if (cachedBatteryIssue.Length > 0) { snapshot.Issues.Add(cachedBatteryIssue); }
        }

        internal static uint? BatteryCycleCountValue(double? rawCount)
        {
            // Windows uses zero for an unsupported counter; ACPI also reserves 0xFFFFFFFF for unknown.
            if (!rawCount.HasValue || double.IsNaN(rawCount.Value) || double.IsInfinity(rawCount.Value) || rawCount.Value <= 0 || rawCount.Value >= uint.MaxValue || Math.Truncate(rawCount.Value) != rawCount.Value)
            {
                return null;
            }
            return (uint)rawCount.Value;
        }

        internal static void ApplyBatteryCycleInventory(Snapshot snapshot, List<Dictionary<string, object>> rows, int detectedBatteryCount)
        {
            snapshot.BatteryCycleCount = null;
            snapshot.BatteryCyclesPerDevice = detectedBatteryCount > 1;
            snapshot.BatteryCycleDetail = "Windows не предоставляет счётчик циклов батареи.";
            if (rows == null || rows.Count == 0) { return; }

            Dictionary<string, List<Dictionary<string, object>>> devices = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in rows)
            {
                string name = row == null ? "" : Text(row, "InstanceName", "");
                if (name.Length == 0)
                {
                    snapshot.BatteryCycleDetail = "Счётчик циклов недоступен: Windows не указала, к какой батарее относятся данные.";
                    return;
                }
                List<Dictionary<string, object>> entries;
                if (!devices.TryGetValue(name, out entries))
                {
                    entries = new List<Dictionary<string, object>>();
                    devices.Add(name, entries);
                }
                entries.Add(row);
            }

            int batteryCount = Math.Max(detectedBatteryCount, devices.Count);
            snapshot.BatteryCyclesPerDevice = batteryCount > 1;
            List<string> details = new List<string>();
            foreach (KeyValuePair<string, List<Dictionary<string, object>>> device in devices.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> entries = device.Value;
                uint? count = entries.Count == 1 ? BatteryCycleCountValue(Number(entries[0], "CycleCount")) : null;
                if (batteryCount == 1 && entries.Count == 1)
                {
                    snapshot.BatteryCycleCount = count;
                    snapshot.BatteryCycleDetail = count.HasValue
                        ? "Число циклов зарядки и разрядки по данным счётчика Windows. Обновляется раз в минуту."
                        : "Windows не сообщает достоверное число циклов; ноль может означать, что счётчик не поддерживается.";
                    return;
                }
                details.Add("Батарея " + (details.Count + 1).ToString(CultureInfo.InvariantCulture) + ": " + (count.HasValue ? count.Value.ToString(CultureInfo.InvariantCulture) : "нет данных"));
            }
            if (batteryCount > devices.Count)
            {
                details.Add("Для остальных батарей (" + (batteryCount - devices.Count).ToString(CultureInfo.InvariantCulture) + ") счётчик недоступен");
            }
            snapshot.BatteryCycleDetail = string.Join("; ", details) + (batteryCount > 1 ? ". Циклы разных батарей не суммируются." : ". Windows вернула неоднозначные сведения о счётчике.");
        }

        internal static double? BatteryCapacityWh(double? rawCapacity, uint? capabilities)
        {
            if (!rawCapacity.HasValue || !capabilities.HasValue || double.IsNaN(rawCapacity.Value) || double.IsInfinity(rawCapacity.Value) || rawCapacity.Value <= 0 || rawCapacity.Value >= uint.MaxValue)
            {
                return null;
            }
            if ((capabilities.Value & 0x40000000U) != 0 || (capabilities.Value & 0x80000000U) == 0) { return null; }
            return rawCapacity.Value / 1000d;
        }

        internal static void ApplyBatteryCapacityInventory(Snapshot snapshot, List<Dictionary<string, object>> designed, List<Dictionary<string, object>> charged, int expectedCount = 0)
        {
            snapshot.BatteryDesignWh = snapshot.BatteryFullChargeWh = snapshot.BatteryHealthPercent = null;
            if (designed == null || charged == null || designed.Count == 0 || designed.Count != charged.Count || designed.Count < expectedCount) { return; }
            HashSet<string> matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double designWh = 0;
            double fullWh = 0;
            bool absolute = true;
            double totalRawDesign = 0;
            double totalRawFull = 0;
            foreach (Dictionary<string, object> row in designed)
            {
                string name = Text(row, "InstanceName", "");
                if (name.Length == 0 || !matchedNames.Add(name)) { return; }
                List<Dictionary<string, object>> matches = charged.Where(item => string.Equals(Text(item, "InstanceName", ""), name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) { return; }
                double? rawDesign = Number(row, "DesignedCapacity");
                double? rawFull = Number(matches[0], "FullChargedCapacity");
                if (!rawDesign.HasValue || !rawFull.HasValue || rawDesign <= 0 || rawFull <= 0 || rawDesign >= uint.MaxValue || rawFull >= uint.MaxValue) { return; }
                ulong? rawCapabilities = Unsigned(row, "Capabilities");
                uint? capabilities = rawCapabilities.HasValue && rawCapabilities.Value <= uint.MaxValue ? (uint?)rawCapabilities.Value : null;
                double? batteryDesignWh = BatteryCapacityWh(rawDesign, capabilities);
                double? batteryFullWh = BatteryCapacityWh(rawFull, capabilities);
                if (batteryDesignWh.HasValue && batteryFullWh.HasValue)
                {
                    designWh += batteryDesignWh.Value;
                    fullWh += batteryFullWh.Value;
                }
                else { absolute = false; }
                totalRawDesign += rawDesign.Value;
                totalRawFull += rawFull.Value;
            }
            if (absolute)
            {
                if (!snapshot.BatteryDesignWh.HasValue) { snapshot.BatteryDesignWh = designWh; }
                if (!snapshot.BatteryFullChargeWh.HasValue) { snapshot.BatteryFullChargeWh = fullWh; }
            }
            if (!snapshot.BatteryHealthPercent.HasValue && (absolute || designed.Count == 1))
            {
                snapshot.BatteryHealthPercent = Math.Min(100, 100d * totalRawFull / totalRawDesign);
            }
        }

        private static void AppendBatteryDetail(Snapshot snapshot, string value)
        {
            if (value.Length == 0) { return; }
            snapshot.BatteryDetail += (snapshot.BatteryDetail.Length > 0 ? " · " : "") + value;
        }

        internal static void ApplyBatteryRuntime(Snapshot snapshot)
        {
            var estimate = BatteryRuntimeEstimator.Estimate(snapshot.BatteryFullChargeWh, snapshot.BatteryPresent);
            snapshot.BatteryRuntimeMinHours = estimate.MinimumHours;
            snapshot.BatteryRuntimeMaxHours = estimate.MaximumHours;
            snapshot.BatteryRuntimeStatus = estimate.Status;
        }

        private void SetSensorStatus(Snapshot snapshot)
        {
            ApplyTemperatureStatus(snapshot, pawnInstalled, sensorFailure, cpuSensorFailure);
        }

        internal static void ApplyTemperatureStatus(Snapshot snapshot, bool pawnInstalled, string sensorFailure)
        {
            ApplyTemperatureStatus(snapshot, pawnInstalled, sensorFailure, "");
        }

        internal static void ApplyTemperatureStatus(Snapshot snapshot, bool pawnInstalled, string sensorFailure, string cpuFailure)
        {
            bool cpuAvailable = snapshot.CpuTemperature.HasValue;
            bool gpuMissing = snapshot.Gpus.Any(gpu => !gpu.Temperature.HasValue);
            if (!cpuAvailable)
            {
                if (!string.IsNullOrEmpty(sensorFailure)) { snapshot.CpuTemperatureStatus = sensorFailure; }
                else if (!string.IsNullOrEmpty(cpuFailure)) { snapshot.CpuTemperatureStatus = cpuFailure; }
                else if (!pawnInstalled) { snapshot.CpuTemperatureStatus = "Нет доступного датчика CPU. Windows/производитель не предоставляют температуру; дополнительный драйвер PawnIO может расширить доступ."; }
                else if (!snapshot.IsAdmin) { snapshot.CpuTemperatureStatus = "Нужны права администратора; откройте «Датчики»."; }
                else { snapshot.CpuTemperatureStatus = "Датчик CPU не предоставил температуру."; }
            }
            else if (string.IsNullOrEmpty(snapshot.CpuTemperatureStatus)) { snapshot.CpuTemperatureStatus = "Температура по датчику CPU."; }
            foreach (GpuSnapshot gpu in snapshot.Gpus)
            {
                if (!string.IsNullOrEmpty(gpu.TemperatureStatus)) { continue; }
                if (gpu.Temperature.HasValue) { gpu.TemperatureStatus = "Температура по датчику GPU."; }
                else if (!string.IsNullOrEmpty(sensorFailure)) { gpu.TemperatureStatus = sensorFailure; }
                else { gpu.TemperatureStatus = "Датчик GPU не найден или не сопоставлен с адаптером."; }
            }
            if (!cpuAvailable) { snapshot.SensorStatus = snapshot.CpuTemperatureStatus; }
            else if (gpuMissing) { snapshot.SensorStatus = "Температура CPU доступна; часть видеокарт не предоставляет температуру."; }
            else { snapshot.SensorStatus = snapshot.Gpus.Count > 0 ? "Датчики CPU и GPU доступны." : "Датчик CPU доступен."; }
            if (gpuMissing && !cpuAvailable) { snapshot.SensorStatus += " Температура части GPU также недоступна."; }
            if (cpuAvailable && snapshot.CpuTemperatureExperimental)
            { snapshot.SensorStatus = "CPU: AdamoThermal · экспериментальное измерение ядер. " + snapshot.SensorStatus; }
        }

        private static List<Dictionary<string, object>> Query(string scopePath, string query)
        {
            ConnectionOptions connection = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(5) };
            ManagementScope scope = new ManagementScope(scopePath, connection);
            EnumerationOptions options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false, BlockSize = 32 };
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options))
            using (ManagementObjectCollection objects = searcher.Get())
            {
                foreach (ManagementObject instance in objects)
                {
                    using (instance)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (PropertyData property in instance.Properties) { row[property.Name] = property.Value; }
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        private static void Attempt(Snapshot snapshot, string name, Action action)
        {
            try { action(); }
            catch (Exception exception) { snapshot.Issues.Add(name + ": нет данных (" + exception.GetType().Name + ")."); }
        }

        private static string Text(Dictionary<string, object> row, string key, string fallback)
        {
            object value;
            return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : fallback;
        }

        private static ulong? Unsigned(Dictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) { return null; }
            try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); } catch { return null; }
        }

        private static double? Number(Dictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) { return null; }
            try
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
            }
            catch { return null; }
        }

        private static double? Positive(ulong? number)
        {
            return number.HasValue && number.Value > 0 ? (double?)number.Value : null;
        }

        private static bool Valid(float? number)
        {
            return number.HasValue && !float.IsNaN(number.Value) && !float.IsInfinity(number.Value);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) { return; }
                disposed = true;
                if (computer != null)
                {
                    try { computer.Close(); } catch { }
                    computer = null;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerStatus
        {
            public byte AcLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumerateAdapters(IntPtr factory, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapterDescription(IntPtr adapter, out AdapterDescription description);

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory(ref Guid interfaceId, out IntPtr factory);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out PowerStatus power);
    }
}
