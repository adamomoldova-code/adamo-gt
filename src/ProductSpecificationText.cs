using System;
using System.Linq;
using System.Text.RegularExpressions;
namespace PcPulse
{
    internal static class ProductSpecificationText
    {
        private static string Clean(string value)
        { return Regex.Replace(value ?? "", @"\(R\)|\(TM\)|[®™]", "", RegexOptions.IgnoreCase).Trim(); }
        internal static string Manufacturer(string value)
        {
            string text=Clean(value);
            return Regex.Replace(text, @"\s+(?:Corporation|Incorporated|Inc\.?|Corp\.?)$", "",RegexOptions.IgnoreCase).Trim();
        }
        internal static string Model(Snapshot snapshot)
        {
            string model=Clean(snapshot.ComputerModel);
            // Panasonic firmware includes the configuration SKU in the model. Keep
            // that full SKU separately; use only the confirmed CF/FZ family on labels.
            if (Manufacturer(snapshot.ComputerManufacturer).Equals("Panasonic",StringComparison.OrdinalIgnoreCase)
                && String.Equals(model,snapshot.ComputerSku,StringComparison.OrdinalIgnoreCase))
            {
                Match family=Regex.Match(model,@"^((?:CF|FZ)-[A-Z][0-9])[A-Z0-9]+$",RegexOptions.IgnoreCase);
                if (family.Success) { return family.Groups[1].Value.ToUpperInvariant(); }
            }
            return model;
        }
        internal static string Cpu(string value)
        {
            string text=Clean(value);
            text=Regex.Replace(text,@"\s+@\s*[\d.,]+\s*GHz$","",RegexOptions.IgnoreCase);
            return Regex.Replace(Regex.Replace(text,@"\bCPU\b","",RegexOptions.IgnoreCase),@"\s+"," ").Trim();
        }
        internal static string Gpu(string value)
        { return Regex.Replace(Clean(value),@"\s+Family$","",RegexOptions.IgnoreCase).Trim(); }
        internal static string RamType(string value)
        {
            var types=(value ?? "").Split('/').Select(s=>s.Trim())
                .Where(s=>Regex.IsMatch(s,@"^(?:LP)?DDR[2-5]?(?:X| FB-DIMM)?$",RegexOptions.IgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return types.Length==0 ? null : String.Join(" / ",types);
        }
    }
}
