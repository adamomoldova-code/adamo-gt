using System;
using System.Runtime.InteropServices;

namespace PcPulse
{
    // Uses the installed display driver's documented WDDM telemetry, independently
    // of LibreHardwareMonitor and CPU/MSR driver access.
    internal static class WindowsGpuTemperatureReader
    {
        internal interface IApi
        {
            int Open(ref OpenAdapter request);
            int Query(ref QueryAdapter request);
            int Close(ref CloseAdapter request);
        }

        internal static void Apply(Snapshot snapshot)
        {
            foreach (GpuSnapshot gpu in snapshot.Gpus)
            {
                double? temperature = Read(gpu, new NativeApi());
                if (!temperature.HasValue) { continue; }
                gpu.Temperature = temperature;
                gpu.TemperatureStatus = "Температура GPU: Windows / драйвер видеокарты (WDDM).";
            }
        }

        internal static double? Read(GpuSnapshot gpu, IApi api)
        {
            if (gpu == null || !gpu.AdapterIndex.HasValue || (gpu.AdapterLuidLow == 0 && gpu.AdapterLuidHigh == 0)) { return null; }
            OpenAdapter adapter = new OpenAdapter { LuidLow = gpu.AdapterLuidLow, LuidHigh = gpu.AdapterLuidHigh };
            IntPtr buffer = IntPtr.Zero;
            bool opened = false;
            try
            {
                if (api.Open(ref adapter) < 0 || adapter.Handle == 0) { return null; }
                opened = true;
                buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AdapterPerformanceData)));
                Marshal.WriteInt32(buffer, 0);
                QueryAdapter count = new QueryAdapter { Handle = adapter.Handle, Type = 30, Data = buffer, Size = 4 };
                // A linked adapter can represent multiple physical GPUs. Do not report
                // physical GPU #0 as the temperature of the entire logical adapter.
                if (api.Query(ref count) < 0 || Marshal.ReadInt32(buffer) != 1) { return null; }
                AdapterPerformanceData data = new AdapterPerformanceData { PhysicalAdapterIndex = 0 };
                Marshal.StructureToPtr(data, buffer, false);
                QueryAdapter query = new QueryAdapter { Handle = adapter.Handle, Type = 62, Data = buffer, Size = (uint)Marshal.SizeOf(typeof(AdapterPerformanceData)) };
                if (api.Query(ref query) < 0) { return null; }
                data = (AdapterPerformanceData)Marshal.PtrToStructure(buffer, typeof(AdapterPerformanceData));
                // Temperature is an unsigned deci-Celsius reading. An untouched zero
                // buffer is unsupported, not a 0 degree GPU. Do not cache measurements.
                return DecodeTemperature(data.Temperature);
            }
            catch (Exception) { return null; }
            finally
            {
                if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
                if (opened)
                {
                    CloseAdapter close = new CloseAdapter { Handle = adapter.Handle };
                    try { api.Close(ref close); } catch { }
                }
            }
        }

        internal static double? DecodeTemperature(uint value) { return value > 0 && value <= 1500 ? (double?)(value / 10d) : null; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OpenAdapter { public uint LuidLow; public int LuidHigh; public uint Handle; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct CloseAdapter { public uint Handle; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct QueryAdapter { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
        // D3DKMT_ALIGN64 fixes the 64-bit quantities at 8-byte alignment even on x86.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct AdapterPerformanceData
        {
            [FieldOffset(0)] public uint PhysicalAdapterIndex;
            [FieldOffset(8)] public ulong MemoryFrequency;
            [FieldOffset(16)] public ulong MaxMemoryFrequency;
            [FieldOffset(24)] public ulong MaxMemoryFrequencyOC;
            [FieldOffset(32)] public ulong MemoryBandwidth;
            [FieldOffset(40)] public ulong PCIEBandwidth;
            [FieldOffset(48)] public uint FanRpm;
            [FieldOffset(52)] public uint Power;
            [FieldOffset(56)] public uint Temperature;
            [FieldOffset(60)] public byte PowerStateOverride;
        }

        private sealed class NativeApi : IApi
        {
            public int Open(ref OpenAdapter request) { return D3DKMTOpenAdapterFromLuid(ref request); }
            public int Query(ref QueryAdapter request) { return D3DKMTQueryAdapterInfo(ref request); }
            public int Close(ref CloseAdapter request) { return D3DKMTCloseAdapter(ref request); }
        }
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter request);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapter request);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTCloseAdapter(ref CloseAdapter request);
    }
}
