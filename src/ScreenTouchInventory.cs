using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PcPulse
{
    internal static class ScreenTouchInventory
    {
        // Use Windows' display mapping, not the presence of a touchpad or pen.
        internal static HashSet<string> ReadTouchDisplays()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GetSystemMetrics(0x1000) != 0) { return result; } // Remote session.
                uint count = 0;
                if (!GetPointerDevices(ref count, null) || count == 0 || count > 256) { return result; }
                var devices = new PointerDeviceInfo[count];
                if (!GetPointerDevices(ref count, devices) || count > devices.Length) { return result; }
                for (int i = 0; i < count; i++)
                {
                    PointerDeviceInfo device = devices[i];
                    if (!IsTouchPointer(device.Type, device.Contacts, device.Orientation, device.Monitor)) { continue; }
                    var monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    if (GetMonitorInfo(device.Monitor, ref monitor) && !String.IsNullOrWhiteSpace(monitor.DeviceName))
                    { result.Add(monitor.DeviceName); }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (ExternalException) { }
            return result;
        }

        internal static bool IsTouchPointer(uint type, ushort contacts, uint orientation, IntPtr monitor)
        {
            // POINTER_DEVICE_TYPE_TOUCH=3; orientation=0 denotes injected input.
            return type == 3 && contacts > 0 && orientation >= 1 && orientation <= 4 && monitor != IntPtr.Zero;
        }

        internal static bool HasIntegratedTouch()
        {
            try
            {
                return GetSystemMetrics(0x1000) == 0 && IntegratedTouch(GetSystemMetrics(94), GetSystemMetrics(95));
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        internal static bool IntegratedTouch(int digitizer, int contacts)
        {
            return (digitizer & 0x81) == 0x81 && contacts > 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PointerDeviceInfo
        {
            public uint Orientation;
            public IntPtr Device;
            public uint Type;
            public IntPtr Monitor;
            public uint StartingCursorId;
            public ushort Contacts;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string Product;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPointerDevices(ref uint count, [In, Out] PointerDeviceInfo[] devices);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}
