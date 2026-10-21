using System;
using System.Collections.Generic;
using System.Linq;

namespace PcPulse
{
    // A detached, explicit product-specification projection. Never serialize the
    // Snapshot itself: it also contains machine identities, paths and diagnostics.
    public static class CrmQuickPayload
    {
        private const int MaximumComponents = 16;
        private const int MaximumDisplays = 8;
        private const int MaximumPorts = 64;
        private const ulong MaximumExactJsonInteger = 9007199254740991UL;

        public static Dictionary<string, object> Build(Snapshot snapshot, PortSpecification ports)
        {
            if (snapshot == null) { throw new ArgumentNullException("snapshot"); }
            return new Dictionary<string, object>
            {
                { "schema_version", 1 },
                { "computer", new Dictionary<string, object>
                    {
                        { "manufacturer", Known(snapshot.ComputerManufacturer) },
                        { "model", Known(snapshot.ComputerModel) },
                        { "sku", Known(snapshot.ComputerSku) },
                        // OS edition only; ComputerName is deliberately excluded.
                        { "windows_name", Known(snapshot.WindowsName) }
                    }
                },
                { "cpu", new Dictionary<string, object>
                    {
                        { "name", Known(snapshot.CpuName) },
                        { "cores", PositiveInteger(snapshot.CpuCores) },
                        { "threads", PositiveInteger(snapshot.CpuThreads) },
                        { "max_clock_mhz", Positive(snapshot.CpuMaxClockMhz) },
                        { "l1_cache_bytes", Capacity(snapshot.CpuL1CacheBytes) },
                        { "l2_cache_bytes", Capacity(snapshot.CpuL2CacheBytes) },
                        { "l3_cache_bytes", Capacity(snapshot.CpuL3CacheBytes) }
                    }
                },
                { "ram", new Dictionary<string, object>
                    {
                        // Available OS memory is not a fallback for installed RAM.
                        { "installed_bytes", Capacity(snapshot.RamInstalledBytes) },
                        { "type", Known(snapshot.RamType) },
                        { "rate", Known(snapshot.RamRate) },
                        { "clock_mhz", Positive(snapshot.RamClockMhz) },
                        { "module_count", NonnegativeInteger(snapshot.RamModuleCount) },
                        { "slot_count", NonnegativeInteger(snapshot.RamSlotCount) },
                        { "free_slots", NonnegativeInteger(snapshot.RamFreeSlots) },
                        { "maximum_bios_bytes", Capacity(snapshot.RamMaximumBytes) },
                        { "maximum_manufacturer_bytes", Capacity(snapshot.RamManufacturerMaximumBytes) }
                    }
                },
                { "storage", Storage(snapshot.Disks) },
                { "gpus", Gpus(snapshot.Gpus) },
                { "displays", Displays(snapshot.Screens) },
                { "battery", Battery(snapshot) },
                { "ports", Ports(ports) },
                { "needs_enrichment", true }
            };
        }

        private static List<Dictionary<string, object>> Storage(IEnumerable<DiskSnapshot> disks)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (DiskSnapshot disk in (disks ?? new DiskSnapshot[0]).Where(item => item != null && KnownInternalBus(item.MediaType)).Take(MaximumComponents))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", Known(disk.Name) },
                    { "media_type", Known(disk.MediaType) },
                    { "size_bytes", Capacity(disk.SizeBytes) },
                    { "health_percent", Percent(disk.HealthPercent) },
                    { "power_on_hours", ExactUnsigned(disk.PowerOnHours) },
                    { "smart_passed", disk.SmartPassed }
                });
            }
            return result;
        }

        private static bool KnownInternalBus(string mediaType)
        {
            // StorageTopology emits "media · bus". USB/1394, remote, array,
            // virtual and unknown transports are not sold as internal hardware.
            // SCSI/SAS alone does not distinguish internal from external storage.
            string[] parts = (mediaType ?? "").Split('\u00b7');
            if (parts.Length != 2) { return false; }
            string bus = parts[1].Trim();
            return String.Equals(bus, "NVMe", StringComparison.OrdinalIgnoreCase)
                || String.Equals(bus, "SATA", StringComparison.OrdinalIgnoreCase)
                || String.Equals(bus, "ATA", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> Gpus(IEnumerable<GpuSnapshot> gpus)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (GpuSnapshot gpu in (gpus ?? new GpuSnapshot[0]).Where(item => item != null).Take(MaximumComponents))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", Known(gpu.Name) },
                    { "is_integrated", gpu.IsIntegrated },
                    { "memory_bytes", ExactUnsigned(gpu.MemoryBytes) },
                    { "shared_memory_bytes", ExactUnsigned(gpu.SharedMemoryBytes) }
                });
            }
            return result;
        }

        private static List<Dictionary<string, object>> Displays(IEnumerable<ScreenSnapshot> screens)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (ScreenSnapshot screen in (screens ?? new ScreenSnapshot[0]).Where(item => item != null && item.IsInternal == true).Take(MaximumDisplays))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", Known(screen.Name) },
                    // Windows' current mode, not a claim about native panel mode.
                    { "width", PositiveInteger(screen.Width) },
                    { "height", PositiveInteger(screen.Height) },
                    { "refresh_hz", Positive(screen.RefreshHz) },
                    { "is_internal", true },
                    { "matrix_type", Known(screen.MatrixType) },
                    // Rated panel specification, not measured light output.
                    { "brightness_nits", Positive(screen.BrightnessNits) }
                });
            }
            return result;
        }

        private static Dictionary<string, object> Battery(Snapshot snapshot)
        {
            bool absent = snapshot.BatteryPresent == false;
            double? minimum = absent ? null : Nonnegative(snapshot.BatteryRuntimeMinHours);
            double? maximum = absent ? null : Nonnegative(snapshot.BatteryRuntimeMaxHours);
            if (minimum.HasValue && maximum.HasValue && minimum > maximum) { minimum = maximum = null; }
            return new Dictionary<string, object>
            {
                { "present", snapshot.BatteryPresent },
                { "charge_percent", absent ? null : Percent(snapshot.BatteryPercent) },
                { "health_percent", absent ? null : Percent(snapshot.BatteryHealthPercent) },
                { "design_wh", absent ? null : Positive(snapshot.BatteryDesignWh) },
                { "full_charge_wh", absent ? null : Nonnegative(snapshot.BatteryFullChargeWh) },
                // A multi-battery device has no single meaningful cycle count.
                { "cycle_count", absent || snapshot.BatteryCyclesPerDevice ? null : snapshot.BatteryCycleCount },
                { "runtime_min_hours", minimum },
                { "runtime_max_hours", maximum },
                { "runtime_is_estimate", true }
            };
        }

        private static List<Dictionary<string, object>> Ports(PortSpecification ports)
        {
            var result = new List<Dictionary<string, object>>();
            if (ports == null || ports.Ports == null) { return result; }
            foreach (PortEntry port in ports.Ports.Where(item => item != null && Known(item.Name) != null).Take(MaximumPorts))
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", Known(port.Name) },
                    { "count", NonnegativeInteger(port.Count) },
                    { "optional", port.Optional }
                });
            }
            return result;
        }

        private static string Known(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) { return null; }
            string text = new String(value.Where(character => !Char.IsControl(character)).ToArray()).Trim();
            string lower = text.ToLowerInvariant();
            if (lower.Length == 0 || lower == "нет данных" || lower == "не подтверждён" || lower == "не подтверждена"
                || lower == "не определён" || lower == "тип не определён" || lower.StartsWith("определение", StringComparison.Ordinal)
                || lower.StartsWith("windows не сообщает", StringComparison.Ordinal) || lower == "unknown" || lower == "n/a"
                || lower == "not available" || lower == "default string" || lower == "to be filled by o.e.m."
                || lower == "system product name" || lower == "system manufacturer" || lower == "none"
                || lower == "экран" || lower == "видеокарта" || lower == "накопитель") { return null; }
            return text.Length > 180 ? text.Substring(0, 180) : text;
        }

        private static ulong? Capacity(ulong? value) { return value > 0 ? ExactUnsigned(value) : null; }
        private static ulong? ExactUnsigned(ulong? value) { return value.HasValue && value <= MaximumExactJsonInteger ? value : null; }
        private static int? PositiveInteger(int? value) { return value > 0 ? value : null; }
        private static int? NonnegativeInteger(int? value) { return value >= 0 ? value : null; }
        private static double? Positive(double? value) { return value > 0 ? Nonnegative(value) : null; }
        private static double? Nonnegative(double? value)
        { return value.HasValue && value >= 0 && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) ? value : null; }
        private static double? Percent(double? value) { return value <= 100 ? Nonnegative(value) : null; }
    }
}
