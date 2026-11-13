using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class InventoryRegressionTests
    {
        private static void Require(bool condition, string message) { if (!condition) { throw new Exception(message); } }
        private static Dictionary<string, object> Row(string name, string id, string driver)
        { return new Dictionary<string, object> { {"Name", name}, {"PNPDeviceID", id}, {"InstalledDisplayDrivers", driver} }; }
        public static void Gpus()
        {
            const string id = @"PCI\VEN_8086&DEV_1916&SUBSYS_00000001&REV_07\3";
            foreach (string label in new[] { "Microsoft Basic Display Adapter", "Базовый видеоадаптер (Майкрософт)", "Localized unknown name" })
            {
                var s = new Snapshot();
                var entities = new[] { new Dictionary<string, object> { {"DeviceID", id}, {"Service", "BasicDisplay"} } };
                GpuIdentityInventory.Apply(s, new[] { Row(label, id, "") }, entities);
                Require(s.Gpus.Count == 1 && s.Gpus[0].Name == "Intel HD Graphics 520", "Resolve physical PCI model regardless of display language: " + label);
                GpuSnapshot gpu = s.Gpus[0];
                Require(gpu.UsesBasicDisplayDriver && gpu.PciNameResolved && !gpu.MemoryBytes.HasValue && !gpu.AdapterIndex.HasValue, "Do not invent VRAM or DXGI targets");
                var payload = (List<Dictionary<string, object>>)CrmQuickPayload.Build(s, null)["gpus"];
                Require((string)payload[0]["name"] == gpu.Name && payload[0]["memory_bytes"] == null, "CRM carries actual model without fake memory");
                bool native = false;
                GpuBenchmarkProgress last = null;
                gpu.AdapterIndex = 0;
                GpuBenchmark.RunCore(gpu, p => last = p, CancellationToken.None,
                    delegate { native = true; throw new Exception("Must not initialize renderer"); }, () => 0, 0, 1);
                Require(!native && last != null && !last.IsRunning && last.Error.Contains("драйвер"), "Basic driver cannot produce a benchmark");
            }
            var healthy = new GpuSnapshot { Name = "Vendor verified GPU", PersistentHardwareId = PersistentDeviceIdentity.Gpu(0x8086,0x1916,1,7), MemoryBytes = 1234, AdapterIndex = 2 };
            var mixed = new Snapshot(); mixed.Gpus.Add(healthy);
            GpuIdentityInventory.Apply(mixed, new[] { Row("Intel HD Graphics 520",id,"igdkmd64.sys"), Row("Microsoft Basic Display Adapter", @"PCI\VEN_10DE&DEV_1C82&SUBSYS_00000002&REV_A1\4", "basicdisplay.sys") }, null);
            Require(mixed.Gpus.Count == 2 && Object.ReferenceEquals(healthy, mixed.Gpus[0]) && healthy.Name == "Vendor verified GPU" && healthy.MemoryBytes == 1234 && !healthy.UsesBasicDisplayDriver, "Healthy and fallback GPUs remain separate");
            GpuIdentityInventory.Apply(mixed, new[] { Row("Intel HD Graphics 520",id,"igdkmd64.sys") }, null);
            Require(mixed.Gpus.Count == 2, "No duplicate physical device");
            var unknown = new Snapshot();
            GpuIdentityInventory.Apply(unknown, new[] { Row("Microsoft Basic Display Adapter", @"PCI\VEN_FFFE&DEV_FFFE\1", "basicdisplay.sys"), Row("Microsoft Basic Render Driver", @"ROOT\BasicRender", "") }, null);
            Require(unknown.Gpus.Count == 1 && !unknown.Gpus[0].PciNameResolved && !unknown.Gpus[0].Name.Contains("Microsoft"), "Unknown model is explicit; software devices excluded");
            Require(((List<Dictionary<string, object>>)CrmQuickPayload.Build(unknown,null)["gpus"])[0]["name"] == null, "Unresolved generic driver not sent as model");
            var parsed = PciGpuCatalog.Parse("8086  Intel Corporation\n\t1234  Family [A / B]\n\t\t0001 0002  Laptop name\nC 03 Display\n\t1916  Fake\n");
            Require(parsed.Count == 1 && parsed["8086:1234"].Contains("A / B") && !parsed["8086:1234"].Contains("Laptop"), "Ambiguous variants retained; subsystem/class names excluded");
            var dxgiOnly = new Snapshot();
            dxgiOnly.Gpus.Add(new GpuSnapshot { Name="Microsoft Basic Display Adapter", PersistentHardwareId=PersistentDeviceIdentity.Gpu(0x8086,0x1916,1,7), MemoryBytes=128000000, AdapterIndex=0 });
            GpuIdentityInventory.NormalizeBasicAdapters(dxgiOnly);
            Require(dxgiOnly.Gpus[0].Name=="Intel HD Graphics 520" && !dxgiOnly.Gpus[0].MemoryBytes.HasValue, "WMI failure still resolves the DXGI PCI identity and discards simulated memory");
            var monitor=new HardwareMonitor();
            typeof(HardwareMonitor).GetField("inventory",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(monitor,dxgiOnly);
            Snapshot copied=(Snapshot)typeof(HardwareMonitor).GetMethod("CopyInventory",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(monitor,null);
            Require(copied.Gpus[0].UsesBasicDisplayDriver && copied.Gpus[0].PciNameResolved && copied.Gpus[0].NameSource==dxgiOnly.Gpus[0].NameSource, "Live inventory copies retain confirmed identity and generic-driver restriction");
            monitor.Dispose();
            var malformed = new Snapshot();
            GpuIdentityInventory.Apply(malformed, new[] { Row("Microsoft Basic Display Adapter", @"PCI\VEN_8086&DEV_1916&VEN_10DE&DEV_1C82", "basicdisplay.sys") }, null);
            Require(malformed.Gpus.Count == 0, "Contradictory multiple PCI IDs rejected");
        }
        public static void Storage()
        {
            DateTime now = DateTime.UtcNow;
            var disk = new DiskSnapshot { Index=0, Name="NVMe WD Blue SN570 25", SizeBytes=250000000000, MediaType="Тип не определён · RAID" };
            var reader = new SmartReader(i => new SmartReading { VerifiedSolidStateProtocol="NVMe", HealthPercent=100 }, d => new SmartReading());
            reader.Apply(disk, now, false);
            var s = new Snapshot(); s.Disks.Add(disk);
            var storage = (List<Dictionary<string,object>>)CrmQuickPayload.Build(s,null)["storage"];
            Require(storage.Count==1 && (string)storage[0]["media_type"]=="SSD · NVMe" && (ulong)storage[0]["size_bytes"]==250000000000, "Latitude RAID NVMe retains real SSD capacity for CRM formatter");
            foreach(string bus in new[] {"USB", "SCSI", "SAS", "Storage Spaces", "Virtual", "iSCSI"})
            {
                disk.MediaType="SSD · " + bus;
                Require(StorageTopology.VerifiedCrmMedia(disk)=="", "Do not widen internal-device filtering: " + bus);
            }
            disk.MediaType="Тип не определён · RAID";
            disk.Name="Replacement device";
            new SmartReader(i => new SmartReading(), d => new SmartReading()).Apply(disk,now,false);
            Require(StorageTopology.VerifiedCrmMedia(disk)=="", "No stale protocol evidence after failure or replacement");
            foreach(int rotation in new[] {-1,0,7200})
            {
                var root = new Dictionary<string,object> {
                    {"smartctl", new Dictionary<string,object>{{"exit_status",0}}},
                    {"device", new Dictionary<string,object>{{"name","/dev/pd0"},{"protocol","ATA"}}},
                    {"smart_status",new Dictionary<string,object>{{"passed",true}}}
                };
                if(rotation>=0) { root["rotation_rate"]=rotation; }
                SmartReading reading=SmartctlSmartReader.Parse(0,new SmartctlProcessResult { ExitCode=0, Output=new JavaScriptSerializer().Serialize(root) });
                Require((reading.VerifiedSolidStateProtocol=="ATA")== (rotation==0), "Only reported ATA nonrotating media can confirm SSD");
                disk.VerifiedSolidStateProtocol=reading.VerifiedSolidStateProtocol;
                Require((StorageTopology.VerifiedCrmMedia(disk)=="SSD · ATA")== (rotation==0), "Confirmed SATA SSD behind RAID reaches CRM; HDD and unknown medium do not");
                root["device"]=new Dictionary<string,object>{{"name","/dev/pd1"},{"protocol","ATA"}};
                Require(SmartctlSmartReader.Parse(0,new SmartctlProcessResult { ExitCode=0,Output=new JavaScriptSerializer().Serialize(root) }).VerifiedSolidStateProtocol=="", "Wrong physical device cannot grant protocol evidence");
            }
        }
    }
}
