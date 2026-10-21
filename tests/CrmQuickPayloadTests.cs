using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class CrmQuickPayloadTests
    {
        public static void Run()
        {
            VerifyHardwareProjection();
            VerifyUnknownsAndNumbers();
            VerifyPrivateFieldsAndDetachedCopy();
            VerifyPeripheralFilters();
            VerifyAbsentAndMultipleBatteries();
            VerifyBoundsAndAllowlist();
            Console.WriteLine("PASS CRM quick payload: hardware allowlist, unknowns, numeric validity, detached copy and external-device filtering");
        }

        private static void VerifyHardwareProjection()
        {
            Snapshot snapshot = Sample();
            var result = CrmQuickPayload.Build(snapshot, SamplePorts());
            Require((int)result["schema_version"] == 1 && (bool)result["needs_enrichment"], "The hardware schema is versioned and requires CRM enrichment");
            var computer = Object(result, "computer");
            Require((string)computer["manufacturer"] == "HP" && (string)computer["model"] == "EliteBook 840 G8"
                && (string)computer["sku"] == "3G2X0EA" && (string)computer["windows_name"] == "Windows 11 Pro", "Model, commercial SKU and OS edition survive projection");
            var cpu = Object(result, "cpu");
            Require((int)cpu["cores"] == 4 && (int)cpu["threads"] == 8 && (double)cpu["max_clock_mhz"] == 4200
                && (ulong)cpu["l1_cache_bytes"] == 320000 && (ulong)cpu["l2_cache_bytes"] == 5242880 && (ulong)cpu["l3_cache_bytes"] == 8388608,
                "CPU counts, maximum frequency and each cache level stay separate");
            var ram = Object(result, "ram");
            Require((ulong)ram["installed_bytes"] == 16UL * 1073741824 && (string)ram["type"] == "DDR4"
                && (string)ram["rate"] == "3200 MT/s" && (double)ram["clock_mhz"] == 1600, "RAM capacity, transfer rate and clock remain distinct");
            Require((int)ram["module_count"] == 1 && (int)ram["slot_count"] == 2 && (int)ram["free_slots"] == 1
                && (ulong)ram["maximum_bios_bytes"] == 64UL * 1073741824 && (ulong)ram["maximum_manufacturer_bytes"] == 32UL * 1073741824,
                "RAM counts and BIOS/manufacturer limits are not conflated");
            var disk = Items(result, "storage")[0];
            Require((ulong)disk["size_bytes"] == 512000000000 && (double)disk["health_percent"] == 97
                && (ulong)disk["power_on_hours"] == 1234 && (bool)disk["smart_passed"], "Disk size, health, hours and SMART result survive projection");
            var display = Items(result, "displays")[0];
            Require((int)display["width"] == 1920 && (int)display["height"] == 1080 && (double)display["refresh_hz"] == 60
                && (string)display["matrix_type"] == "IPS" && (double)display["brightness_nits"] == 250, "Reported screen values are copied without native-resolution inference");
            var battery = Object(result, "battery");
            Require((double)battery["charge_percent"] == 50 && (double)battery["design_wh"] == 53 && (double)battery["full_charge_wh"] == 45
                && (uint)battery["cycle_count"] == 120 && (bool)battery["runtime_is_estimate"]
                && (double)battery["runtime_min_hours"] == 3 && (double)battery["runtime_max_hours"] == 6, "Battery keeps capacities, cycle count and explicitly estimated runtime");
            var port = Items(result, "ports")[0];
            Require((string)port["name"] == "HDMI" && (int)port["count"] == 1 && (bool)port["optional"], "Optional manufacturer ports are not represented as confirmed standard ports");
        }

        private static void VerifyUnknownsAndNumbers()
        {
            var snapshot = new Snapshot
            {
                ComputerManufacturer = "To be filled by O.E.M.", ComputerModel = "System Product Name", WindowsName = null,
                RamTotalBytes = 8UL * 1073741824, RamType = "Не определён", RamRate = "Windows не сообщает частоту",
                CpuCores = -1, CpuThreads = 0, CpuMaxClockMhz = Double.NaN, CpuL1CacheBytes = 0,
                CpuL2CacheBytes = ulong.MaxValue, RamInstalledBytes = 0, RamClockMhz = Double.PositiveInfinity,
                RamModuleCount = -1, RamSlotCount = 0, RamFreeSlots = 0,
                BatteryPercent = 0, BatteryHealthPercent = 101, BatteryDesignWh = -5, BatteryFullChargeWh = 0,
                BatteryCycleCount = 0, BatteryRuntimeMinHours = 0, BatteryRuntimeMaxHours = Double.NegativeInfinity
            };
            snapshot.Disks.Add(new DiskSnapshot { MediaType = "SSD · NVMe", SizeBytes = 0, HealthPercent = 0, PowerOnHours = 0, SmartPassed = false });
            snapshot.Gpus.Add(new GpuSnapshot { MemoryBytes = 0, SharedMemoryBytes = null, IsIntegrated = null });
            snapshot.Screens.Add(new ScreenSnapshot { IsInternal = true, Width = 0, Height = -1, RefreshHz = Double.NaN, BrightnessNits = Double.PositiveInfinity });
            var result = CrmQuickPayload.Build(snapshot, null);
            var computer = Object(result, "computer"); var cpu = Object(result, "cpu"); var ram = Object(result, "ram"); var battery = Object(result, "battery");
            Require(computer.Values.All(value => value == null), "Placeholder computer names and unavailable OS names are null");
            Require(cpu.Values.All(value => value == null), "Zero, negative, nonfinite and impossible CPU values are unknown");
            Require(ram["installed_bytes"] == null && ram["type"] == null && ram["rate"] == null && ram["clock_mhz"] == null
                && ram["module_count"] == null && (int)ram["slot_count"] == 0 && (int)ram["free_slots"] == 0,
                "Unknown capacity is not replaced with OS-usable memory, while explicit zero slot counts are preserved");
            Require(battery["present"] == null && (double)battery["charge_percent"] == 0 && battery["health_percent"] == null
                && battery["design_wh"] == null && (double)battery["full_charge_wh"] == 0 && (uint)battery["cycle_count"] == 0
                && (double)battery["runtime_min_hours"] == 0 && battery["runtime_max_hours"] == null,
                "Valid zero battery readings survive; invalid readings do not become fabricated bounds");
            Require(Items(result, "storage")[0]["size_bytes"] == null && (double)Items(result, "storage")[0]["health_percent"] == 0
                && (ulong)Items(result, "storage")[0]["power_on_hours"] == 0 && !(bool)Items(result, "storage")[0]["smart_passed"], "A zero health/hour value and a failed SMART result are retained");
            Require((ulong)Items(result, "gpus")[0]["memory_bytes"] == 0 && Items(result, "gpus")[0]["shared_memory_bytes"] == null,
                "Known zero dedicated GPU memory differs from missing shared memory");
            Require(Items(result, "displays")[0].Where(pair => pair.Key != "is_internal").All(pair => pair.Value == null), "Unknown panel properties remain unknown");
            string json = Serialize(result);
            Require(!json.Contains("NaN") && !json.Contains("Infinity"), "Payload JSON never contains nonfinite numbers");
        }

        private static void VerifyPrivateFieldsAndDetachedCopy()
        {
            Snapshot snapshot = Sample(); PortSpecification ports = SamplePorts();
            snapshot.ComputerName = "PRIVATE_HOST"; snapshot.SensorStatus = "PRIVATE_SENSOR"; snapshot.Issues.Add("PRIVATE_LOG");
            snapshot.CpuClockSource = "PRIVATE_CLOCK_SOURCE"; snapshot.CpuCacheSource = "PRIVATE_CACHE_SOURCE";
            snapshot.CpuTemperatureStatus = "PRIVATE_CPU_STATUS"; snapshot.AdamoThermalStatus = "PRIVATE_THERMAL";
            snapshot.RamSlotsNote = "PRIVATE_SLOT_NOTE"; snapshot.RamMaximumSource = "PRIVATE_RAM_SOURCE";
            snapshot.RamManufacturerMaximumSource = "PRIVATE_MANUFACTURER_SOURCE"; snapshot.RamConfiguration = "PRIVATE_CONFIGURATION";
            snapshot.BatteryDetail = "PRIVATE_BATTERY_DETAIL"; snapshot.BatteryCycleDetail = "PRIVATE_BATTERY_SERIAL";
            snapshot.BatteryRuntimeStatus = "PRIVATE_RUNTIME"; snapshot.BatteryStatus = "PRIVATE_BATTERY_STATUS";
            snapshot.Disks[0].StorageIdentity = "PRIVATE_STORAGE_ID"; snapshot.Disks[0].PersistentHardwareId = "PRIVATE_DISK_SERIAL";
            snapshot.Disks[0].Volumes = "PRIVATE_VOLUME"; snapshot.Disks[0].TestVolumeRoot = "C:\\PRIVATE_PATH";
            snapshot.Disks[0].SmartSource = "PRIVATE_SMART_SOURCE"; snapshot.Disks[0].SmartStatus = "PRIVATE_SMART_STATUS";
            snapshot.Gpus[0].PersistentHardwareId = "PRIVATE_GPU_ID"; snapshot.Gpus[0].TemperatureStatus = "PRIVATE_GPU_STATUS";
            snapshot.Screens[0].InstanceName = "PRIVATE_SCREEN_ID"; snapshot.Screens[0].Source = "PRIVATE_SCREEN_SOURCE";
            ports.SourceUrl = "https://example.test/PRIVATE_QUERY"; ports.SourceTitle = "PRIVATE_PORT_TITLE"; ports.Note = "PRIVATE_PORT_NOTE";
            ports.Model = "PRIVATE_PORT_MODEL"; ports.Status = "PRIVATE_PORT_STATUS"; ports.Ports[0].Detail = "PRIVATE_PORT_DETAIL";
            var result = CrmQuickPayload.Build(snapshot, ports);
            string before = Serialize(result);
            Require(!before.Contains("PRIVATE_"), "No hostname, serial, identifier, local path, provenance URL, note or raw diagnostic leaves the explicit projection");
            snapshot.ComputerModel = "CHANGED"; snapshot.CpuName = "CHANGED"; snapshot.Disks[0].Name = "CHANGED";
            snapshot.Gpus[0].Name = "CHANGED"; snapshot.Screens[0].Width = 1; snapshot.BatteryPercent = 1; ports.Ports[0].Name = "CHANGED";
            snapshot.Disks.Clear(); snapshot.Gpus.Clear(); snapshot.Screens.Clear(); ports.Ports.Clear();
            Require(before == Serialize(result), "Changing any source object or list cannot alter a pending request");
        }

        private static void VerifyPeripheralFilters()
        {
            var snapshot = new Snapshot();
            foreach (string bus in new[] { "NVMe", "SATA", "ATA", "USB", "SCSI", "SAS", "RAID", "IEEE 1394", "iSCSI", "Fibre Channel", "SD", "MMC", "VHD", "Storage Spaces", "Виртуальный диск", "" })
            { snapshot.Disks.Add(new DiskSnapshot { Name = "Disk " + bus, MediaType = "SSD" + (bus.Length > 0 ? " · " + bus : "") }); }
            snapshot.Disks.Add(new DiskSnapshot { Name = "Ambiguous transport", MediaType = "SSD · NVMe · USB" });
            snapshot.Disks.Add(null);
            snapshot.Gpus.Add(new GpuSnapshot { Name = "Integrated", IsIntegrated = true });
            snapshot.Gpus.Add(new GpuSnapshot { Name = "Dedicated", IsIntegrated = false });
            snapshot.Gpus.Add(null);
            snapshot.Screens.Add(new ScreenSnapshot { Name = "Internal", IsInternal = true });
            snapshot.Screens.Add(new ScreenSnapshot { Name = "PRIVATE_OFFICE_MONITOR", IsInternal = false });
            snapshot.Screens.Add(new ScreenSnapshot { Name = "PRIVATE_UNKNOWN_MONITOR", IsInternal = null });
            snapshot.Screens.Add(null);
            var result = CrmQuickPayload.Build(snapshot, null);
            Require(Items(result, "storage").Select(item => (string)item["name"]).SequenceEqual(new[] { "Disk NVMe", "Disk SATA", "Disk ATA" }),
                "Only explicit supported internal bus labels enter the product; external/unknown/remote/virtual/ambiguous storage is omitted");
            Require(Items(result, "gpus").Count == 2 && Items(result, "displays").Count == 1 && !Serialize(result).Contains("PRIVATE_"),
                "Both GPUs are included while external or unclassified monitors cannot become product panels");
            snapshot.Disks = null; snapshot.Gpus = null; snapshot.Screens = null;
            result = CrmQuickPayload.Build(snapshot, new PortSpecification { Ports = null });
            Require(new[] { "storage", "gpus", "displays", "ports" }.All(key => Items(result, key).Count == 0), "Missing hardware collections become empty lists without invented devices");
        }

        private static void VerifyAbsentAndMultipleBatteries()
        {
            Snapshot snapshot = Sample(); snapshot.BatteryPresent = false;
            var battery = Object(CrmQuickPayload.Build(snapshot, null), "battery");
            Require(!(bool)battery["present"] && battery.Where(pair => pair.Key != "present" && pair.Key != "runtime_is_estimate").All(pair => pair.Value == null),
                "Confirmed battery absence clears stale capacity, charge, cycles and runtime readings");
            snapshot.BatteryPresent = true; snapshot.BatteryCyclesPerDevice = true;
            snapshot.BatteryRuntimeMinHours = 7; snapshot.BatteryRuntimeMaxHours = 3;
            battery = Object(CrmQuickPayload.Build(snapshot, null), "battery");
            Require(battery["cycle_count"] == null && battery["runtime_min_hours"] == null && battery["runtime_max_hours"] == null,
                "Multi-battery cycle counts and reversed runtime bounds are not published as single reliable values");
        }

        private static void VerifyBoundsAndAllowlist()
        {
            Snapshot snapshot = Sample(); var ports = SamplePorts();
            snapshot.ComputerModel = new String('X', 300) + "\r\n\0";
            for (int i = 0; i < 80; i++)
            {
                snapshot.Disks.Add(new DiskSnapshot { Name = "Disk", MediaType = "SSD · NVMe" });
                snapshot.Gpus.Add(new GpuSnapshot()); snapshot.Screens.Add(new ScreenSnapshot { IsInternal = true });
                ports.Ports.Add(new PortEntry { Name = "USB", Count = 1 });
            }
            var result = CrmQuickPayload.Build(snapshot, ports);
            Require(((string)Object(result, "computer")["model"]).Length == 180, "Untrusted firmware labels are length-bounded and contain no control characters");
            Require(Items(result, "storage").Count == 16 && Items(result, "gpus").Count == 16 && Items(result, "displays").Count == 8 && Items(result, "ports").Count == 64,
                "All device lists have bounded payload size");
            Keys(result, "schema_version,computer,cpu,ram,storage,gpus,displays,battery,ports,needs_enrichment");
            Keys(Object(result, "computer"), "manufacturer,model,sku,windows_name");
            Keys(Object(result, "cpu"), "name,cores,threads,max_clock_mhz,l1_cache_bytes,l2_cache_bytes,l3_cache_bytes");
            Keys(Object(result, "ram"), "installed_bytes,type,rate,clock_mhz,module_count,slot_count,free_slots,maximum_bios_bytes,maximum_manufacturer_bytes");
            Keys(Items(result, "storage")[0], "name,media_type,size_bytes,health_percent,power_on_hours,smart_passed");
            Keys(Items(result, "gpus")[0], "name,is_integrated,memory_bytes,shared_memory_bytes");
            Keys(Items(result, "displays")[0], "name,width,height,refresh_hz,is_internal,matrix_type,brightness_nits");
            Keys(Object(result, "battery"), "present,charge_percent,health_percent,design_wh,full_charge_wh,cycle_count,runtime_min_hours,runtime_max_hours,runtime_is_estimate");
            Keys(Items(result, "ports")[0], "name,count,optional");
        }

        private static Snapshot Sample()
        {
            var snapshot = new Snapshot
            {
                ComputerManufacturer = "HP", ComputerModel = "EliteBook 840 G8", ComputerSku = "3G2X0EA", WindowsName = "Windows 11 Pro",
                CpuName = "Intel Core i5-1135G7", CpuCores = 4, CpuThreads = 8, CpuMaxClockMhz = 4200,
                CpuL1CacheBytes = 320000, CpuL2CacheBytes = 5242880, CpuL3CacheBytes = 8388608,
                RamInstalledBytes = 16UL * 1073741824, RamType = "DDR4", RamRate = "3200 MT/s", RamClockMhz = 1600,
                RamModuleCount = 1, RamSlotCount = 2, RamFreeSlots = 1, RamMaximumBytes = 64UL * 1073741824, RamManufacturerMaximumBytes = 32UL * 1073741824,
                BatteryPresent = true, BatteryPercent = 50, BatteryDesignWh = 53, BatteryFullChargeWh = 45, BatteryHealthPercent = 85,
                BatteryCycleCount = 120, BatteryRuntimeMinHours = 3, BatteryRuntimeMaxHours = 6
            };
            snapshot.Disks.Add(new DiskSnapshot { Name = "Internal SSD", MediaType = "SSD · NVMe", SizeBytes = 512000000000, HealthPercent = 97, PowerOnHours = 1234, SmartPassed = true });
            snapshot.Gpus.Add(new GpuSnapshot { Name = "Intel Iris Xe", IsIntegrated = true, MemoryBytes = 128UL * 1048576, SharedMemoryBytes = 8UL * 1073741824 });
            snapshot.Screens.Add(new ScreenSnapshot { Name = "Panel model", IsInternal = true, Width = 1920, Height = 1080, RefreshHz = 60, MatrixType = "IPS", BrightnessNits = 250 });
            return snapshot;
        }
        private static PortSpecification SamplePorts()
        { return new PortSpecification { Ports = new List<PortEntry> { new PortEntry { Name = "HDMI", Count = 1, Optional = true } } }; }
        private static Dictionary<string, object> Object(Dictionary<string, object> root, string key) { return (Dictionary<string, object>)root[key]; }
        private static List<Dictionary<string, object>> Items(Dictionary<string, object> root, string key) { return (List<Dictionary<string, object>>)root[key]; }
        private static string Serialize(object value) { return new JavaScriptSerializer().Serialize(value); }
        private static void Keys(Dictionary<string, object> value, string expected)
        { Require(value.Keys.OrderBy(key => key).SequenceEqual(expected.Split(',').OrderBy(key => key)), "Only agreed explicit schema keys are present: " + expected); }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    }
}
