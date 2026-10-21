using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PcPulse
{
    internal static class PersistentDeviceIdentity
    {
        internal static string Disk(string serial, string model, ulong? capacity)
        {
            string normalized = new string((serial ?? "").Where(character => !Char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();
            if (normalized.Length < 3 || normalized.Length > 128 || !capacity.HasValue || capacity == 0
                || String.IsNullOrWhiteSpace(model) || normalized.All(character => character == '0' || character == 'F')
                || new[] { "UNKNOWN", "NONE", "N/A", "NA", "NOTAVAILABLE", "DEFAULTSTRING", "TOBEFILLEDBYO.E.M." }.Contains(normalized)) { return ""; }
            string input = normalized + "|" + model.Trim().ToUpperInvariant() + "|" + capacity.Value.ToString(CultureInfo.InvariantCulture);
            using (var hash = SHA256.Create()) { return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", ""); }
        }

        internal static string Gpu(uint vendor, uint device, uint subsystem, uint revision)
        {
            return vendor == 0 || device == 0 ? "" : String.Format(CultureInfo.InvariantCulture,
                "PCI:VEN_{0:X4}:DEV_{1:X4}:SUBSYS_{2:X8}:REV_{3:X2}", vendor, device, subsystem, revision);
        }
    }
}
