using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    // Read-only metadata probe; no reference to LibreHardwareMonitor/PawnIO, no camera,
    // benchmark, installer, registry mutation or driver/service start/stop.
    public static class DriverlessTemperatureProbe
    {
        public static int Main(string[] args)
        {
            try
            {
                string manufacturer = "";
                using (var searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT Manufacturer FROM Win32_ComputerSystem"))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows) { using (row) { manufacturer = Convert.ToString(row["Manufacturer"]); break; } }
                }
                var reader = new DriverlessTemperatureReader();
                var results = new List<object>();
                for (int pass = 0; pass < 2; pass++)
                {
                    var snapshot = new Snapshot { ComputerManufacturer = manufacturer };
                    reader.ApplyCpu(snapshot, DateTime.UtcNow);
                    ReadAdapters(snapshot);
                    WindowsGpuTemperatureReader.Apply(snapshot);
                    var gpus = new List<object>();
                    foreach (GpuSnapshot gpu in snapshot.Gpus) { gpus.Add(new { gpu.Name, gpu.AdapterIndex, gpu.Temperature, gpu.TemperatureStatus }); }
                    results.Add(new { SampledAtUtc = DateTime.UtcNow, snapshot.ComputerManufacturer, snapshot.CpuTemperature, snapshot.CpuTemperatureStatus, Gpus = gpus });
                    if (pass == 0) { Thread.Sleep(300); }
                }
                var json = new JavaScriptSerializer().Serialize(new { Probe = "Windows/OEM/WDDM only; no PawnIO or companion monitor", Results = results });
                if (args.Length > 0) { File.WriteAllText(Path.GetFullPath(args[0]), json); }
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message); return 1; }
        }

        private static void ReadAdapters(Snapshot snapshot)
        {
            Guid factoryId = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            IntPtr factory;
            int status = CreateDXGIFactory(ref factoryId, out factory);
            if (status < 0 || factory == IntPtr.Zero) { return; }
            try
            {
                var enumerate = (EnumerateAdapters)Marshal.GetDelegateForFunctionPointer(Method(factory, 7), typeof(EnumerateAdapters));
                for (uint index = 0; index < 32; index++)
                {
                    IntPtr adapter;
                    status = enumerate(factory, index, out adapter);
                    if (status < 0) { break; }
                    if (adapter == IntPtr.Zero) { continue; }
                    try
                    {
                        var describe = (GetDescription)Marshal.GetDelegateForFunctionPointer(Method(adapter, 8), typeof(GetDescription));
                        AdapterDescription description;
                        if (describe(adapter, out description) < 0) { continue; }
                        if (description.VendorId == 0x1414 && description.DeviceId == 0x008c) { continue; }
                        snapshot.Gpus.Add(new GpuSnapshot { Name = description.Description.Trim(), AdapterIndex = index, AdapterLuidLow = description.LuidLow, AdapterLuidHigh = description.LuidHigh });
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }
        private static IntPtr Method(IntPtr instance, int slot) { return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size); }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubsystemId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumerateAdapters(IntPtr factory, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
        [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory(ref Guid id, out IntPtr factory);
    }
}
