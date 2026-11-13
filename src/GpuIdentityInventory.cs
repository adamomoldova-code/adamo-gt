using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PcPulse
{
    internal static class GpuIdentityInventory
    {
        private sealed class Identity
        {
            internal string Key, Persistent;
        }
        private static Identity ReadIdentity(string text)
        {
            if (String.IsNullOrEmpty(text) || text.Length > 512 || !text.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) { return null; }
            MatchCollection matches = Regex.Matches(text, @"VEN_([0-9A-F]{4})[&:]DEV_([0-9A-F]{4})(?=[&:\\]|$)", RegexOptions.IgnoreCase);
            if (matches.Count != 1) { return null; }
            Match match = matches[0];
            uint vendor = Convert.ToUInt32(match.Groups[1].Value, 16), device = Convert.ToUInt32(match.Groups[2].Value, 16);
            if (vendor == 0 || device == 0 || vendor == 0x1414) { return null; }
            Match sub = Regex.Match(text, @"SUBSYS_([0-9A-F]{8})(?=[&:\\]|$)", RegexOptions.IgnoreCase);
            Match rev = Regex.Match(text, @"REV_([0-9A-F]{2})(?=[&:\\]|$)", RegexOptions.IgnoreCase);
            return new Identity { Key = vendor.ToString("X4") + ":" + device.ToString("X4"),
                Persistent = PersistentDeviceIdentity.Gpu(vendor, device, sub.Success ? Convert.ToUInt32(sub.Groups[1].Value, 16) : 0,
                    rev.Success ? Convert.ToUInt32(rev.Groups[1].Value, 16) : 0) };
        }
        private static string Text(Dictionary<string, object> row, string key)
        {
            object value; return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value).Trim() : "";
        }
        internal static bool IsBasic(string name, string drivers, string service)
        {
            return String.Equals(service, "BasicDisplay", StringComparison.OrdinalIgnoreCase)
                || (drivers ?? "").IndexOf("basicdisplay", StringComparison.OrdinalIgnoreCase) >= 0
                || Regex.IsMatch(name ?? "", @"Microsoft Basic Display|Базов.*видеоадаптер|Adaptor.*afi.*Microsoft", RegexOptions.IgnoreCase);
        }
        internal static void Apply(Snapshot snapshot, IEnumerable<Dictionary<string, object>> controllers,
            IEnumerable<Dictionary<string, object>> entities)
        {
            var rows = (controllers ?? new Dictionary<string, object>[0]).ToList();
            var devices = (entities ?? new Dictionary<string, object>[0]).ToList();
            foreach (var row in rows)
            {
                string name = Text(row, "Name"), pnp = Text(row, "PNPDeviceID");
                Identity id = ReadIdentity(pnp);
                if (id == null) { continue; } // Software / remote adapters are not physical PCI GPUs.
                if (rows.Count(other => String.Equals(Text(other, "PNPDeviceID"), pnp, StringComparison.OrdinalIgnoreCase)) != 1) { continue; }
                var services = devices.Where(d => String.Equals(Text(d, "DeviceID"), pnp, StringComparison.OrdinalIgnoreCase)).ToList();
                bool basic = IsBasic(name, Text(row, "InstalledDisplayDrivers"), services.Count == 1 ? Text(services[0], "Service") : "");
                var matches = snapshot.Gpus.Where(g => String.Equals(g.PersistentHardwareId, id.Persistent, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                {
                    var family = snapshot.Gpus.Where(g => { Identity other = ReadIdentity(g.PersistentHardwareId); return other != null && other.Key == id.Key; }).ToList();
                    int count = rows.Count(r => { Identity other = ReadIdentity(Text(r, "PNPDeviceID")); return other != null && other.Key == id.Key; });
                    if (family.Count == 1 && count == 1) { matches = family; }
                    else if (family.Count > 0) { continue; }
                }
                if (matches.Count > 1) { continue; }
                GpuSnapshot gpu;
                if (matches.Count == 1) { gpu = matches[0]; }
                else
                {
                    gpu = new GpuSnapshot { Name = name, PersistentHardwareId = id.Persistent, MemoryKind = "Объём видеопамяти недоступен" };
                    snapshot.Gpus.Add(gpu);
                }
                if (!basic) { continue; } // Preserve authoritative vendor-driver descriptions and memory.
                ResolveBasic(gpu, id);

            }
        }
        // DXGI may succeed while the WMI provider is unavailable. Never retain a
        // generic driver name or its simulated memory as physical GPU facts.
        internal static void NormalizeBasicAdapters(Snapshot snapshot)
        {
            foreach (GpuSnapshot gpu in snapshot.Gpus)
            {
                if (gpu.UsesBasicDisplayDriver || IsBasic(gpu.Name, "", ""))
                { ResolveBasic(gpu, ReadIdentity(gpu.PersistentHardwareId)); }
            }
        }
        private static void ResolveBasic(GpuSnapshot gpu, Identity id)
        {
            gpu.UsesBasicDisplayDriver = true;
            string resolved = id == null ? "" : PciGpuCatalog.Lookup(id.Key);
            gpu.PciNameResolved = resolved.Length > 0;
            gpu.Name = gpu.PciNameResolved ? resolved : "Видеокарта: модель не подтверждена";
            gpu.NameSource = "PCI IDs 2026.11.13 · " + (id == null ? "ID недоступен" : id.Key) + "; установлен базовый драйвер Microsoft";
            gpu.MemoryBytes = null; gpu.SharedMemoryBytes = null; gpu.IsIntegrated = null;
            gpu.MemoryKind = "Установите драйвер производителя: объём VRAM недоступен";
            gpu.Temperature = null;
            gpu.TemperatureStatus = "Нужен драйвер производителя видеокарты.";
        }

    }
}
