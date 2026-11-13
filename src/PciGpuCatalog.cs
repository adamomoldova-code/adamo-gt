using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PcPulse
{
    // Offline factual PCI database, distributed under BSD-3-Clause; no network lookup.
    internal static class PciGpuCatalog
    {
        private static readonly Lazy<Dictionary<string, string>> Names = new Lazy<Dictionary<string, string>>(Load);
        internal static string Lookup(string key)
        {
            string name;
            return Names.Value.TryGetValue(key, out name) ? name : "";
        }
        private static Dictionary<string, string> Load()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "pci.ids");
                if (!File.Exists(path) || new FileInfo(path).Length > 5000000) { return new Dictionary<string, string>(); }
                byte[] bytes = File.ReadAllBytes(path);
                using (var hash = SHA256.Create())
                {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "bd44f4bc57defbc52743cedbb60a5c60aa6dad7e88d5a26d506a7e04dcf30d9f") { return new Dictionary<string, string>(); }
                }
                return Parse(new UTF8Encoding(false, true).GetString(bytes));
            }
            catch { return new Dictionary<string, string>(); }
        }
        internal static Dictionary<string, string> Parse(string content)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string vendor = "", prefix = "";
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("C ")) { break; }
                    Match v = Regex.Match(line, @"^([0-9a-fA-F]{4})  (.+)$");
                    if (v.Success)
                    {
                        vendor = v.Groups[1].Value.ToUpperInvariant();
                        prefix = vendor == "8086" ? "Intel" : vendor == "10DE" ? "NVIDIA" : vendor == "1002" ? "AMD" : v.Groups[2].Value;
                        continue;
                    }
                    Match d = Regex.Match(line, @"^	([0-9a-fA-F]{4})  (.+)$");
                    if (!d.Success || vendor.Length == 0) { continue; }
                    string name = d.Groups[2].Value.Trim();
                    Match marketing = Regex.Match(name, @"\[([^\[\]]+)\]");
                    // A shared PCI ID can cover several variants: retain the full family.
                    if (marketing.Success && name.IndexOf('/') < 0 && name.IndexOf('|') < 0
                        && Regex.Matches(name, @"\[").Count == 1) { name = marketing.Groups[1].Value; }
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = prefix + " " + name; }
                    if (name.Length <= 220 && !Regex.IsMatch(name, @"[\x00-\x1f]")) { result[vendor + ":" + d.Groups[1].Value.ToUpperInvariant()] = name; }
                }
            }
            return result;
        }
    }
}
