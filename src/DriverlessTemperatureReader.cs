using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace PcPulse
{
    // Only existing Windows/OEM interfaces are queried. This reader installs no driver,
    // starts no companion monitor and never relabels an ACPI thermal zone as the CPU.
    internal sealed class DriverlessTemperatureReader
    {
        private readonly Func<string, string, List<Dictionary<string, object>>> query;
        private readonly Dictionary<string, DateTime> retryAfter = new Dictionary<string, DateTime>();

        internal DriverlessTemperatureReader() : this(Query) { }
        internal DriverlessTemperatureReader(Func<string, string, List<Dictionary<string, object>>> query) { this.query = query; }

        internal void ApplyCpu(Snapshot snapshot, DateTime nowUtc)
        {
            if (snapshot.CpuTemperature.HasValue) { return; }
            string manufacturer = snapshot.ComputerManufacturer ?? "";
            if (Regex.IsMatch(manufacturer, "^(HP|Hewlett[ -]Packard)(\\b|$)", RegexOptions.IgnoreCase))
            {
                ApplyProvider(snapshot, nowUtc, "HP CMI", "root\\HP\\InstrumentedBIOS", "HP_BIOSNumericSensor", false);
            }
            else if (manufacturer.IndexOf("Dell", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ApplyProvider(snapshot, nowUtc, "Dell Command Monitor", "root\\dcim\\sysman", "DCIM_NumericSensor", false);
            }
            if (!snapshot.CpuTemperature.HasValue)
            {
                // Current Windows implementations normally leave CurrentReading empty.
                // Accept it only when a provider explicitly supplies a CPU probe reading.
                ApplyProvider(snapshot, nowUtc, "Win32_TemperatureProbe", "root\\cimv2", "Win32_TemperatureProbe", true);
            }
        }

        private void ApplyProvider(Snapshot snapshot, DateTime nowUtc, string provider, string scope, string className, bool win32Probe)
        {
            DateTime next;
            if (retryAfter.TryGetValue(className, out next) && nowUtc < next) { return; }
            try
            {
                KeyValuePair<string, double?> reading = SelectCpuProbe(query(scope, "SELECT * FROM " + className), win32Probe);
                if (reading.Value.HasValue)
                {
                    snapshot.CpuTemperature = reading.Value;
                    snapshot.CpuTemperatureTrustedForStress = true;
                    snapshot.CpuTemperatureExperimental = false;
                    snapshot.CpuTemperatureStatus = "Датчик CPU производителя (Windows WMI): " + provider + " / " + reading.Key
                        + ". Показание датчика производителя может отличаться от температуры ядра.";
                    retryAfter.Remove(className);
                }
                else { retryAfter[className] = nowUtc.AddSeconds(60); }
            }
            catch (Exception)
            {
                // Missing optional namespaces, permission denial or an unavailable provider
                // must not suppress other sensors or replay the last measurement.
                retryAfter[className] = nowUtc.AddSeconds(60);
            }
        }

        internal static KeyValuePair<string, double?> SelectCpuProbe(IEnumerable<Dictionary<string, object>> rows, bool win32Probe)
        {
            KeyValuePair<string, double?> selected = new KeyValuePair<string, double?>("", null);
            if (rows == null) { return selected; }
            foreach (Dictionary<string, object> row in rows)
            {
                if (row == null || !IsAvailable(row)) { continue; }
                string name = Text(row, "ElementName");
                if (name.Length == 0) { name = Text(row, "Name"); }
                string description = Text(row, "Description");
                if (!IsExplicitCpuProbe(name, description)) { continue; }
                double? raw = Number(row, "CurrentReading");
                double? temperature;
                if (win32Probe)
                {
                    // Microsoft documents tenths of degrees Celsius for this class.
                    temperature = raw.HasValue ? (double?)(raw.Value / 10d) : null;
                }
                else
                {
                    double? sensorType = Number(row, "SensorType");
                    double? rate = Number(row, "RateUnits");
                    if (sensorType != 2 || (rate.HasValue && rate.Value != 0)) { continue; }
                    temperature = ConvertNumericTemperature(raw, Number(row, "BaseUnits"), Number(row, "UnitModifier"));
                }
                // OEM zero readings commonly mean unsupported. Never fill the dashboard
                // with these or with integer sentinels / thresholds / nominal values.
                if (!IsPlausible(temperature)) { continue; }
                if (!selected.Value.HasValue || temperature.Value > selected.Value.Value)
                {
                    selected = new KeyValuePair<string, double?>(name.Length > 0 ? name : description, temperature);
                }
            }
            return selected;
        }

        internal static double? ConvertNumericTemperature(double? raw, double? units, double? modifier)
        {
            if (!raw.HasValue || !units.HasValue || !modifier.HasValue || Math.Truncate(modifier.Value) != modifier.Value
                || modifier < -6 || modifier > 3 || !IsFinite(raw.Value) || raw == uint.MaxValue || raw == int.MaxValue || raw == int.MinValue) { return null; }
            double value = raw.Value * Math.Pow(10d, modifier.Value);
            if (units == 3) { value = (value - 32d) * 5d / 9d; }
            else if (units == 4) { value -= 273.15d; }
            else if (units != 2) { return null; }
            return IsPlausible(value) ? (double?)value : null;
        }

        internal static bool IsExplicitCpuProbe(string name, string description)
        {
            string text = (name ?? "") + " " + (description ?? "");
            // An OEM must identify the measured component. Core/thermal/zone alone is not
            // proof that a sensor measures the CPU; proximity/ambient probes are excluded.
            bool cpu = Regex.IsMatch(text, "(^|[^a-z])(cpu|processor|processors)([0-9]|[^a-z]|$)", RegexOptions.IgnoreCase);
            bool other = Regex.IsMatch(text, "gpu|graphics|ambient|proximity|skin|chassis|motherboard|board|socket|peci.*margin|margin|distance|threshold|limit|critical|trip|tjmax|thermal[ _-]*index|fan|acpi|thermal[ _-]*zone|\\bTZ[0-9]", RegexOptions.IgnoreCase);
            return cpu && !other;
        }

        private static bool IsAvailable(Dictionary<string, object> row)
        {
            double? availability = Number(row, "Availability");
            if (availability.HasValue && availability != 3 && availability != 4 && availability != 5) { return false; }
            double? enabled = Number(row, "EnabledState");
            if (enabled.HasValue && enabled != 2 && enabled != 5) { return false; }
            object status;
            if (row.TryGetValue("OperationalStatus", out status) && status != null)
            {
                IEnumerable entries = status is Array ? (IEnumerable)status : new[] { status };
                foreach (object item in entries)
                {
                    double number;
                    try { number = Convert.ToDouble(item, CultureInfo.InvariantCulture); } catch { return false; }
                    // OK, degraded, stressed and predictive failure can still have current
                    // readings. Unknown, dormant, disconnected or errored sensors cannot.
                    if (number != 2 && number != 3 && number != 4 && number != 5) { return false; }
                }
            }
            string state = Text(row, "Status");
            return state.Length == 0 || state.Equals("OK", StringComparison.OrdinalIgnoreCase)
                || state.Equals("Degraded", StringComparison.OrdinalIgnoreCase) || state.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlausible(double? value) { return value.HasValue && IsFinite(value.Value) && value > 0 && value <= 150; }
        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Text(Dictionary<string, object> row, string name)
        {
            object value;
            return row.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : "";
        }
        private static double? Number(Dictionary<string, object> row, string name)
        {
            object value;
            if (!row.TryGetValue(name, out value) || value == null) { return null; }
            try { double number = Convert.ToDouble(value, CultureInfo.InvariantCulture); return IsFinite(number) ? (double?)number : null; }
            catch { return null; }
        }

        private static List<Dictionary<string, object>> Query(string scopePath, string query)
        {
            ConnectionOptions connection = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(1) };
            ManagementScope scope = new ManagementScope(scopePath, connection);
            EnumerationOptions options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(1), ReturnImmediately = true, Rewindable = false, BlockSize = 16 };
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
                    if (rows.Count >= 128) { break; }
                }
            }
            return rows;
        }
    }
}
