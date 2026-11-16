using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PcPulse
{
    internal static class MemoryModuleInventory
    {
        private static string Text(Dictionary<string, object> row, string key)
        { object value; return row != null && row.TryGetValue(key,out value) && value != null ? Convert.ToString(value) : ""; }
        private static string Tag(string path, string className)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 2048) { return ""; }
            Match match=Regex.Match(path, @"(?:^|:)"+className+@"\.Tag=""((?:[^""\\]|\\.)*)""$",RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Replace("\\\"","\"").Replace("\\\\","\\") : "";
        }
        internal static List<Dictionary<string, object>> SystemModules(IEnumerable<Dictionary<string, object>> modules,
            IEnumerable<Dictionary<string, object>> arrays, IEnumerable<Dictionary<string, object>> locations)
        {
            var memory=(modules ?? new Dictionary<string, object>[0]).ToList();
            var groups=(arrays ?? new Dictionary<string, object>[0]).ToList();
            var links=(locations ?? new Dictionary<string, object>[0]).ToList();
            return memory.Where(module =>
            {
                // CIM MemoryType and raw SMBIOSMemoryType use different enumerations.
                ulong? smbios=HardwareDetailsInventory.Unsigned(module,"SMBIOSMemoryType");
                ulong? cim=HardwareDetailsInventory.Unsigned(module,"MemoryType");
                if ((smbios >= 9 && smbios <= 13) || (cim >= 10 && cim <= 14 && (!smbios.HasValue || smbios <= 2))) { return false; } // ROM/Flash/EEPROM, not RAM.
                string tag=Text(module,"Tag");
                if (tag.Length==0 || memory.Count(m=>String.Equals(Text(m,"Tag"),tag,StringComparison.OrdinalIgnoreCase))!=1) { return true; }
                var matched=links.Where(l=>String.Equals(Tag(Text(l,"PartComponent"),"Win32_PhysicalMemory"),tag,StringComparison.OrdinalIgnoreCase)).ToList();
                if (matched.Count!=1) { return true; }
                string group=Tag(Text(matched[0],"GroupComponent"),"Win32_PhysicalMemoryArray");
                var owners=groups.Where(g=>group.Length>0 && String.Equals(Text(g,"Tag"),group,StringComparison.OrdinalIgnoreCase)).ToList();
                if (owners.Count!=1) { return true; }
                ulong? use=HardwareDetailsInventory.Unsigned(owners[0],"Use");
                // Microsoft: 3 system, 4 video, 5 flash, 6 nonvolatile, 7 cache.
                return !(use >= 4 && use <= 7);
            }).ToList();
        }
    }
}
