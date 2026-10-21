using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using PcPulse;

namespace PcPulseTests
{
    public static class HardwareCatalogTests
    {
        public static void Run()
        {
            ExactIdentityAndSkuPreventCrossModelSpecifications();
            AmbiguousRecordsDoNotPickAnArbitraryConfiguration();
            ManufacturerLimitsPreserveFirmwareEvidence();
            PanelDetailsRequireTheActualPanelId();
            CorruptUntrustedAndOversizedCatalogsAreUnavailable();
            EmbeddedCatalogSurvivesMissingOrCorruptExternalFiles();
        }

        private static void ExactIdentityAndSkuPreventCrossModelSpecifications()
        {
            WithFiles(delegate(string directory)
            {
                HardwareCatalog.ModelRecord record = Model();
                record.ModelAliases.Add("HP Verified Alias G1");
                record.Skus.Add("SKU-123#ABA");
                WriteModels(directory, record);
                HardwareCatalog catalog = HardwareCatalog.Load(directory);
                Snapshot correct = Computer();
                correct.ComputerModel = "  hp   example 14 g1  ";
                correct.ComputerSku = "sku-123#aba";
                Require(catalog.FindPorts(correct) != null, "case/whitespace normalization keeps exact verified identity");
                correct.ComputerModel = "HP Verified Alias G1";
                Require(catalog.FindPorts(correct) != null, "explicitly catalogued alias is accepted");
                foreach (Snapshot wrong in new[]
                {
                    new Snapshot { ComputerManufacturer = "Dell", ComputerModel = "HP Example 14 G1", ComputerSku = "SKU-123#ABA" },
                    new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP Example 16 G1", ComputerSku = "SKU-123#ABA" },
                    new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP Example 14 G2", ComputerSku = "SKU-123#ABA" },
                    new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP Example 14 G1", ComputerSku = "SKU-123#ABB" },
                    new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP Example 14 G1", ComputerSku = "" },
                    new Snapshot { ComputerManufacturer = "HP", ComputerModel = "Example 14 G1", ComputerSku = "SKU-123#ABA" },
                    new Snapshot { ComputerManufacturer = "HP", ComputerModel = "System Product Name", ComputerSku = "SKU-123#ABA" }
                })
                {
                    Require(catalog.FindPorts(wrong) == null, "different manufacturer, generation, size, unverified alias or SKU is not a match");
                }
                PortSpecification first = catalog.FindPorts(correct);
                first.Ports[0].Count = 99;
                Require(catalog.FindPorts(correct).Ports[0].Count == 2, "a caller cannot mutate the catalog's future results");
            });
        }

        private static void AmbiguousRecordsDoNotPickAnArbitraryConfiguration()
        {
            WithFiles(delegate(string directory)
            {
                HardwareCatalog.ModelRecord other = Model();
                other.Id = "other-configuration";
                other.MaximumRamBytes = 32UL << 30;
                other.Ports[0].Count = 1;
                WriteModels(directory, Model(), other);
                HardwareCatalog catalog = HardwareCatalog.Load(directory);
                Snapshot computer = Computer();
                catalog.Apply(computer);
                Require(catalog.FindPorts(computer) == null && !computer.RamManufacturerMaximumBytes.HasValue, "overlapping model records remain unconfirmed");
                other.Skus.Add("SKU-123#ABA");
                WriteModels(directory, Model(), other);
                computer.ComputerSku = "SKU-123#ABA";
                Require(HardwareCatalog.Load(directory).FindPorts(computer) == null, "a generic and SKU-specific overlap also requires review");
            });
        }

        private static void ManufacturerLimitsPreserveFirmwareEvidence()
        {
            WithFiles(delegate(string directory)
            {
                WriteModels(directory, Model());
                Snapshot computer = Computer();
                computer.RamMaximumBytes = 128UL << 30;
                computer.RamMaximumSource = "BIOS / SMBIOS";
                HardwareCatalog catalog = HardwareCatalog.Load(directory);
                catalog.Apply(computer);
                Require(computer.RamMaximumBytes == 128UL << 30 && computer.RamMaximumSource == "BIOS / SMBIOS", "manufacturer data never overwrites firmware evidence");
                Require(computer.RamManufacturerMaximumBytes == 64UL << 30, "manufacturer maximum has its own value");
                Require(computer.RamManufacturerMaximumSource.Contains("https://support.hp.com/") && computer.RamManufacturerMaximumSource.Contains("2026-01-01"), "source and verification date remain attached to specification");
                computer.ComputerModel = "Unknown";
                catalog.Apply(computer);
                Require(!computer.RamManufacturerMaximumBytes.HasValue && computer.RamMaximumBytes == 128UL << 30, "unmatched identity cannot retain an earlier model's manufacturer limit");
            });
        }

        private static void PanelDetailsRequireTheActualPanelId()
        {
            WithFiles(delegate(string directory)
            {
                WritePanels(directory, Panel());
                Snapshot computer = Computer();
                computer.Screens.Add(new ScreenSnapshot { InstanceName = "DISPLAY\\TST1234\\INSTANCE_0", Width = 1920, Height = 1200 });
                computer.Screens.Add(new ScreenSnapshot { InstanceName = "DISPLAY\\TST5678\\INSTANCE_0", Name = "TST1234", Width = 1920, Height = 1200 });
                computer.Screens.Add(new ScreenSnapshot { InstanceName = "\\\\.\\DISPLAY1", Name = "TST1234" });
                HardwareCatalog.Load(directory).Apply(computer);
                Require(computer.Screens[0].MatrixType == "IPS" && computer.Screens[0].BrightnessNits == 400, "matching EDID identifier supplies catalog fields");
                Require(computer.Screens[0].Source.Contains("не измеренная") && computer.Screens[0].Width == 1920, "typical brightness is labelled and current Windows mode preserved");
                Require(computer.Screens.Skip(1).All(screen => !screen.BrightnessNits.HasValue && screen.MatrixType == "Не подтверждён"), "same resolution/name without exact panel ID proves nothing");
                WritePanels(directory, Panel(), Panel());
                Snapshot ambiguous = Computer();
                ambiguous.Screens.Add(new ScreenSnapshot { InstanceName = "DISPLAY\\TST1234\\INSTANCE" });
                HardwareCatalog.Load(directory).Apply(ambiguous);
                Require(!ambiguous.Screens[0].BrightnessNits.HasValue, "duplicate panel mappings are ambiguous even when values agree");
            });
        }

        private static void CorruptUntrustedAndOversizedCatalogsAreUnavailable()
        {
            WithFiles(delegate(string directory)
            {
                foreach (string content in new[] { "{broken", "null", "{\"SchemaVersion\":2,\"Revision\":\"future\",\"Models\":[]}", new string(' ', 2 * 1024 * 1024 + 1) })
                {
                    File.WriteAllText(Path.Combine(directory, "models.json"), content, Encoding.UTF8);
                    Require(HardwareCatalog.Load(directory).FindPorts(Computer()) == null, "malformed, unknown-schema or oversized input cannot produce a specification");
                }
                foreach (string source in new[] { "http://support.hp.com/specifications", "https://support.hp.com.evil.example/specifications", "file:///C:/catalog.json", "https://h30434.www3.hp.com/t5/specifications" })
                {
                    HardwareCatalog.ModelRecord record = Model();
                    record.Source.Url = source;
                    WriteModels(directory, record);
                    Require(HardwareCatalog.Load(directory).FindPorts(Computer()) == null, "source must be an official HTTPS document, not a forum or spoofed host");
                }
                HardwareCatalog.ModelRecord future = Model();
                future.Source.CheckedOn = "2999-01-01";
                WriteModels(directory, future);
                Require(HardwareCatalog.Load(directory).FindPorts(Computer()) == null, "future verification dates are invalid");
                HardwareCatalog.ModelRecord invalidCount = Model();
                invalidCount.Ports[0].Count = 999;
                WriteModels(directory, invalidCount);
                Require(HardwareCatalog.Load(directory).FindPorts(Computer()) == null, "implausible port counts are invalid");
                HardwareCatalog.ModelRecord missingSource = Model();
                missingSource.Source = null;
                WriteModels(directory, missingSource);
                Require(HardwareCatalog.Load(directory).FindPorts(Computer()) == null, "provenance is mandatory");
            });
        }

        private static void EmbeddedCatalogSurvivesMissingOrCorruptExternalFiles()
        {
            WithFiles(delegate(string directory)
            {
                Snapshot computer = new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP ZBook Firefly 14 inch G9 Mobile Workstation PC", ComputerSku = "6T6Y0US#ABA" };
                foreach (bool corrupt in new[] { false, true })
                {
                    if (corrupt) { File.WriteAllText(Path.Combine(directory, "models.json"), "{broken"); }
                    HardwareCatalog catalog = HardwareCatalog.Load(directory, true);
                    PortSpecification ports = catalog.FindPorts(computer);
                    catalog.Apply(computer);
                    Require(ports != null && ports.Ports.Single(port => port.Name == "USB-C / Thunderbolt 4").Count == 2, "embedded verified Firefly specification is available without usable external data");
                    Require(ports.Ports.Single(port => port.Name == "Smart Card").Optional, "optional ports remain optional in the seed");
                    Require(computer.RamManufacturerMaximumBytes == 64UL << 30, "embedded model memory specification is available");
                    computer.Screens.Clear();
                    computer.Screens.Add(new ScreenSnapshot { InstanceName = "DISPLAY\\AUO7A99\\INSTANCE", Width = 1920, Height = 1200 });
                    catalog.Apply(computer);
                    Require(!computer.Screens[0].BrightnessNits.HasValue, "unverified AUO7A99 is not guessed from laptop display options");
                }
            });
        }

        private static Snapshot Computer()
        {
            return new Snapshot { ComputerManufacturer = "HP", ComputerModel = "HP Example 14 G1", ComputerSku = "SKU-123#ABA" };
        }

        private static HardwareCatalog.ModelRecord Model()
        {
            return new HardwareCatalog.ModelRecord
            {
                Id = "test-model", Manufacturer = "HP", Model = "HP Example 14 G1", MaximumRamBytes = 64UL << 30,
                Source = Source(), Ports = new List<PortEntry> { new PortEntry { Name = "USB-A", Count = 2, Detail = "Test fixture" } }
            };
        }

        private static HardwareCatalog.PanelRecord Panel()
        {
            return new HardwareCatalog.PanelRecord { PanelId = "TST1234", MatrixType = "IPS", BrightnessNits = 400, Source = Source() };
        }

        private static HardwareCatalog.CatalogSource Source()
        {
            return new HardwareCatalog.CatalogSource { Url = "https://support.hp.com/test-fixture", Title = "Test specification fixture", CheckedOn = "2026-01-01" };
        }

        private static void WriteModels(string directory, params HardwareCatalog.ModelRecord[] records)
        {
            File.WriteAllText(Path.Combine(directory, "models.json"), new JavaScriptSerializer().Serialize(
                new HardwareCatalog.ModelDocument { SchemaVersion = 1, Revision = "test-1", Models = records.ToList() }), Encoding.UTF8);
        }

        private static void WritePanels(string directory, params HardwareCatalog.PanelRecord[] records)
        {
            File.WriteAllText(Path.Combine(directory, "panels.json"), new JavaScriptSerializer().Serialize(
                new HardwareCatalog.PanelDocument { SchemaVersion = 1, Revision = "test-1", Panels = records.ToList() }), Encoding.UTF8);
        }

        private static void WithFiles(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "PcPulse-CatalogTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); }
            finally
            {
                foreach (string filename in new[] { "models.json", "panels.json" })
                {
                    string path = Path.Combine(directory, filename);
                    if (File.Exists(path)) { File.Delete(path); }
                }
                Directory.Delete(directory);
            }
        }

        private static void Require(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
