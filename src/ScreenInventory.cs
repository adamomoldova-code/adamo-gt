using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PcPulse
{
    internal static class ScreenInventory
    {
        internal static List<ScreenSnapshot> Read(IList<Dictionary<string, object>> identities, IList<Dictionary<string, object>> connections)
        {
            List<ScreenSnapshot> result = new List<ScreenSnapshot>();
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < 32; index++)
            {
                DisplayDevice adapter = NewDevice();
                if (!EnumDisplayDevices(null, index, ref adapter, 0)) { break; }
                if ((adapter.StateFlags & 1) == 0 || (adapter.StateFlags & 8) != 0) { continue; }
                int? width = null, height = null;
                double? refresh = null;
                ReadCurrentMode(adapter.DeviceName, out width, out height, out refresh);
                bool found = false;
                for (uint monitorIndex = 0; monitorIndex < 32; monitorIndex++)
                {
                    DisplayDevice monitor = NewDevice();
                    if (!EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 1)) { break; }
                    if ((monitor.StateFlags & 1) == 0) { continue; }
                    string key = NormalizeInstance(monitor.DeviceId);
                    if (key.Length == 0 || !known.Add(key)) { continue; }
                    ScreenSnapshot screen = new ScreenSnapshot { Name = PanelId(key), InstanceName = key, Width = width, Height = height, RefreshHz = refresh, Source = "Текущий режим Windows; идентификатор EDID" };
                    Dictionary<string, object> identity = UniqueMatch(identities, key);
                    if (identity != null)
                    {
                        object chars;
                        if (identity.TryGetValue("UserFriendlyName", out chars))
                        {
                            string name = DecodeName(chars as ushort[]);
                            if (name.Length > 0) { screen.Name = name + " · " + PanelId(key); }
                        }
                    }
                    Dictionary<string, object> connection = UniqueMatch(connections, key);
                    if (connection != null) { screen.IsInternal = InternalConnection(HardwareDetailsInventory.Unsigned(connection, "VideoOutputTechnology")); }
                    result.Add(screen);
                    found = true;
                }
                if (!found)
                {
                    result.Add(new ScreenSnapshot { Name = "Экран " + (result.Count + 1), InstanceName = adapter.DeviceName, Width = width, Height = height, RefreshHz = refresh, Source = "Текущий режим Windows; EDID недоступен" });
                }
            }
            return result.OrderByDescending(screen => screen.IsInternal == true).ToList();
        }

        internal static string NormalizeInstance(string value)
        {
            string key = (value ?? "").Trim().Replace('#', '\\');
            if (key.StartsWith("\\\\?\\", StringComparison.Ordinal)) { key = key.Substring(4); }
            int guid = key.IndexOf("\\{", StringComparison.Ordinal);
            if (guid >= 0) { key = key.Substring(0, guid); }
            return Regex.Replace(key, "_[0-9]+$", "").ToUpperInvariant();
        }

        internal static bool? InternalConnection(ulong? technology)
        {
            if (!technology.HasValue || technology == uint.MaxValue) { return null; }
            // LVDS, embedded DisplayPort, embedded UDI and INTERNAL are explicit panel connections.
            if (technology == 6 || technology == 11 || technology == 13 || technology == 0x80000000UL) { return true; }
            if (technology <= 5 || technology == 8 || technology == 9 || technology == 10 || technology == 12 || technology == 14 || technology == 15 || technology == 16 || technology == 17) { return false; }
            return null;
        }

        private static Dictionary<string, object> UniqueMatch(IList<Dictionary<string, object>> rows, string key)
        {
            if (rows == null) { return null; }
            List<Dictionary<string, object>> matches = rows.Where(row =>
            {
                object value;
                return row != null && row.TryGetValue("InstanceName", out value) && string.Equals(NormalizeInstance(Convert.ToString(value)), key, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string PanelId(string key)
        {
            string[] parts = key.Split('\\');
            return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "Экран";
        }

        private static string DecodeName(ushort[] chars)
        {
            if (chars == null) { return ""; }
            StringBuilder name = new StringBuilder();
            foreach (ushort value in chars) { if (value >= 32 && value != 127) { name.Append((char)value); } }
            return name.ToString().Trim();
        }

        private static void ReadCurrentMode(string deviceName, out int? width, out int? height, out double? refresh)
        {
            width = height = null;
            refresh = null;
            byte[] mode = new byte[220];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)220), 0, mode, 68, 2);
            if (!EnumDisplaySettingsEx(deviceName, -1, mode, 0)) { return; }
            uint fields = BitConverter.ToUInt32(mode, 72);
            uint x = BitConverter.ToUInt32(mode, 172), y = BitConverter.ToUInt32(mode, 176), hz = BitConverter.ToUInt32(mode, 184);
            if ((fields & 0x80000) != 0 && x > 0 && x <= int.MaxValue) { width = (int)x; }
            if ((fields & 0x100000) != 0 && y > 0 && y <= int.MaxValue) { height = (int)y; }
            if ((fields & 0x400000) != 0 && hz > 1) { refresh = hz; }
        }

        private static DisplayDevice NewDevice() { return new DisplayDevice { Size = (uint)Marshal.SizeOf(typeof(DisplayDevice)) }; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);
        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, [In, Out] byte[] mode, uint flags);
    }
}
