using System;
using System.Collections.Generic;
using System.Linq;
using PcPulse;
namespace PcPulseTests
{
    public static class MemoryLabelRegressionTests
    {
        private static void Require(bool value,string message) { if(!value) { throw new Exception(message); } }
        private static Dictionary<string,object> Module(string tag,ulong size,int type)
        { return new Dictionary<string,object>{{"Tag",tag},{"Capacity",size},{"SMBIOSMemoryType",type},{"FormFactor",12},{"ConfiguredClockSpeed",1600}}; }
        private static Dictionary<string,object> Array(string tag,int use)
        { return new Dictionary<string,object>{{"Tag",tag},{"Use",use},{"MemoryDevices",2}}; }
        private static Dictionary<string,object> Link(string module,string array)
        { return new Dictionary<string,object>{{"PartComponent","Win32_PhysicalMemory.Tag=\""+module+"\""},{"GroupComponent","\\\\PC\\root\\cimv2:Win32_PhysicalMemoryArray.Tag=\""+array+"\""}}; }
        public static void Run()
        {
            var modules=new[]{Module("RAM0",8UL<<30,24),Module("RAM1",8UL<<30,24),Module("FLASH",16UL<<20,10)};
            var s=new Snapshot();
            var filtered=MemoryModuleInventory.SystemModules(modules,null,null);
            HardwareDetailsInventory.ApplyInstalledMemory(s,filtered);
            HardwareMonitor.ApplyMemorySlots(s,filtered,new List<Dictionary<string,object>>{Array("SYSTEM",3)});
            Require(s.RamInstalledBytes==(16UL<<30) && s.RamModuleCount==2 && s.RamFreeSlots==0,"ROM/Flash is neither RAM capacity nor a third installed module");
            modules[2]["SMBIOSMemoryType"]=0;
            var arrays=new[]{Array("SYS",3),Array("FIRMWARE",5)};
            var links=new[]{Link("RAM0","SYS"),Link("RAM1","SYS"),Link("FLASH","FIRMWARE")};
            filtered=MemoryModuleInventory.SystemModules(modules,arrays,links);
            Require(filtered.Count==2,"Association excludes flash even if module type is unknown");
            foreach(int use in new[]{4,5,6,7})
            { arrays[1]["Use"]=use;Require(MemoryModuleInventory.SystemModules(modules,arrays,links).Count==2,"Video/cache/nonvolatile memory is separate from system RAM"); }
            arrays[1]["Use"]=0;
            Require(MemoryModuleInventory.SystemModules(modules,arrays,links).Count==3,"Unknown ownership cannot silently remove a device");
            arrays[1]["Use"]=5;
            Require(MemoryModuleInventory.SystemModules(modules,arrays,links.Concat(new[]{Link("FLASH","SYS")})).Count==3,"Contradictory associations cannot select an arbitrary owner");
            Require(MemoryModuleInventory.SystemModules(modules,null,null).Count==3,"Size alone must not remove an unknown small RAM module");
            HardwareDetailsInventory.ApplyInstalledMemory(s,MemoryModuleInventory.SystemModules(new[]{Module("OLD",32UL<<20,17)},null,null));
            Require(s.RamInstalledBytes==(32UL<<20),"Real low-capacity legacy memory is retained, never rounded to GB");
            var biosFlash=Module("ROM",16UL<<20,0);biosFlash["MemoryType"]=11;
            Require(MemoryModuleInventory.SystemModules(new[]{biosFlash},null,null).Count==0,"CIM Flash enum differs from raw SMBIOS enum");
            var conflicting=Module("DDR",8UL<<30,24); conflicting["MemoryType"]=11;
            Require(MemoryModuleInventory.SystemModules(new[]{conflicting},null,null).Count==1,"Raw SMBIOS DDR type takes precedence over conflicting legacy CIM classification");
            s.ComputerManufacturer="Panasonic Corporation";s.ComputerModel=s.ComputerSku="CF-C2CCEZFCM";
            s.CpuName="Intel(R) Core(TM) i5-4300U CPU @ 1.90GHz";
            s.RamInstalledBytes=16UL<<30;s.RamType="DDR3 / Не определён";
            s.Gpus.Add(new GpuSnapshot{Name="Intel(R) HD Graphics Family",IsIntegrated=true});
            s.Disks.Add(new DiskSnapshot{Name="SSD",SizeBytes=500000000000,MediaType="SSD · SATA"});
            s.Screens.Add(new ScreenSnapshot{IsInternal=true,Width=1366,Height=768,IsTouch=true});
            var payload=CrmQuickPayload.Build(s,null);
            var computer=(Dictionary<string,object>)payload["computer"];
            var cpu=(Dictionary<string,object>)payload["cpu"];var ram=(Dictionary<string,object>)payload["ram"];
            Require((string)computer["manufacturer"]=="Panasonic" && (string)computer["model"]=="CF-C2" && (string)computer["sku"]=="CF-C2CCEZFCM","Compact commercial model retains the full configuration SKU separately");
            Require((string)cpu["name"]=="Intel Core i5-4300U" && (string)ram["type"]=="DDR3" && (ulong)ram["installed_bytes"]==(16UL<<30),"No CPU suffix, phantom MB or unknown-memory fragment on labels");
            Require(((List<Dictionary<string,object>>)payload["gpus"])[0]["name"].Equals("Intel HD Graphics"),"Generic GPU family is shortened without inventing a chip model");
            Require(s.ComputerModel=="CF-C2CCEZFCM" && s.CpuName.Contains("@"),"Payload presentation does not mutate the original inventory");
            s.ComputerSku="OTHER";Require(ProductSpecificationText.Model(s)=="CF-C2CCEZFCM","Do not collapse an unconfirmed configuration code");
            Require(ProductSpecificationText.RamType("DDR3 / DDR4 / unknown")=="DDR3 / DDR4" && ProductSpecificationText.RamType("Не определён")==null,"Mixed valid generations survive; missing values stay unknown");
            Require(ProductSpecificationText.Cpu("AMD Ryzen 7 5800H")=="AMD Ryzen 7 5800H" && ProductSpecificationText.Gpu("NVIDIA GeForce RTX 3060 Laptop GPU")=="NVIDIA GeForce RTX 3060 Laptop GPU","Meaningful vendor/model suffixes remain intact");
        }
    }
}
