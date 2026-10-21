using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class PortLookupTests
    {
        public static void Run()
        {
            SharedConnectorFunctionsAreNotExtraSockets();
            AmbiguousAliasAndComparisonCountsAreRejected();
            OptionalSlotsStayOptional();
            OtherSectionsAndAccessoriesCannotInventPorts();
            OppositeSidesAreDistinctPhysicalSockets();
            RepeatedIdenticalTablesAreNotCountedTwice();
            CompleteOverviewDoesNotAddSideBreakdownAgain();
            DefinitionsAndBoundedHeadingsAreSupported();
            QuantityMustBeExplicitAndPlausible();
            ModelSizeAndGenerationMustAgree();
            ProcessorVariantsMustAgreeWithLocalHardware();
            PreCancelledLookupDoesNotStartNetworkWork();
            CommunityPostsAreNotManufacturerSpecifications();
        }

        private static void SharedConnectorFunctionsAreNotExtraSockets()
        {
            string html = Table("Front ports",
                "<p>(2) Thunderbolt 4 with USB4 Type-C 40 Gbps signaling rate ports (USB Power Delivery, DisplayPort 1.4)</p>" +
                "<p>(2) Super Speed USB Type-A 5Gbps signaling rate ports (1 charging)</p>" +
                "<p>(1) HDMI 2.0</p><p>(1) Headphone/microphone combo jack</p>");
            List<PortEntry> ports = PortLookup.ExtractPorts(html);
            Equal(4, ports.Count, "Four physical connector types");
            Equal(2, Port(ports, "USB-C / Thunderbolt").Count, "Thunderbolt and USB4 share two USB-C sockets");
            Equal(2, Port(ports, "USB-A").Count, "Charging is a USB-A feature, not a third socket");
            Equal(1, Port(ports, "HDMI").Count, "HDMI socket");
            Equal(1, Port(ports, "Аудио · наушники/микрофон").Count, "Combo audio is one socket");
            True(!ports.Any(p => p.Name == "DisplayPort" || p.Name == "Питание"), "USB-C alternate functions are not separate sockets");
        }

        private static void OptionalSlotsStayOptional()
        {
            List<PortEntry> ports = PortLookup.ExtractPorts(Table("Slots",
                "<p>(1) Smart card reader (optional)</p><p>(1) Nano SIM card slot (optional)</p><p>(1) Nano security lock slot</p>"));
            True(Port(ports, "Smart Card").Optional, "Optional smart card reader");
            True(Port(ports, "SIM").Optional, "Optional SIM slot");
            True(!Port(ports, "Слот замка безопасности").Optional, "Security slot remains standard");
        }

        private static void AmbiguousAliasAndComparisonCountsAreRejected()
        {
            string duplicateAliases = Table("Ports", "<p>2 x USB-C Thunderbolt 4</p><p>2 x Thunderbolt 4</p><p>1 x HDMI</p>");
            Equal(0, PortLookup.ExtractPorts(duplicateAliases).Count, "Ambiguous duplicate connector/function entries require confirmation");
            string comparedConfigurations = "<table><tr><td>Ports</td><td>2 x USB-A</td><td>4 x USB-A</td></tr></table>";
            Equal(0, PortLookup.ExtractPorts(comparedConfigurations).Count, "Different configuration columns cannot be added");
        }

        private static void OtherSectionsAndAccessoriesCannotInventPorts()
        {
            string html = "<script><table><tr><td>Ports</td><td>9 x USB-A</td></tr></table></script>" +
                Table("Accessories", "12 x USB-A") +
                Table("Ports", "<p>1 x HDMI</p><p>4 x USB-A docking station</p><p>2 x USB-C adapter</p><p>8 x USB internal header</p>");
            List<PortEntry> ports = PortLookup.ExtractPorts(html);
            Equal(1, ports.Count, "Only recognized external socket remains");
            Equal(1, Port(ports, "HDMI").Count, "Accessory and hidden script counts ignored");
            Equal(0, PortLookup.ExtractPorts("<p>2 x USB-A</p><p>1 x HDMI</p>").Count, "Loose document text has no specification context");
        }

        private static void OppositeSidesAreDistinctPhysicalSockets()
        {
            List<PortEntry> ports = PortLookup.ExtractPorts(Table("Left ports", "1 x USB-A") + Table("Right ports", "1 x USB-A"));
            Equal(2, Port(ports, "USB-A").Count, "Identical USB-A sockets on opposite sides must sum");
            List<PortEntry> inlineSides = PortLookup.ExtractPorts(Table("Ports", "<p>Left: 1 x USB-A</p><p>Right: 1 x USB-A</p>"));
            Equal(2, Port(inlineSides, "USB-A").Count, "Side prefixes within one port row identify distinct sockets");
        }

        private static void RepeatedIdenticalTablesAreNotCountedTwice()
        {
            string table = Table("Ports", "<p>2 x USB-C</p><p>2 x USB-A</p><p>1 x HDMI</p>");
            List<PortEntry> ports = PortLookup.ExtractPorts(table + table);
            Equal(2, Port(ports, "USB-C").Count, "Duplicated desktop/mobile table does not double sockets");
            Equal(2, Port(ports, "USB-A").Count, "Repeated identical specification deduplicated");
        }

        private static void CompleteOverviewDoesNotAddSideBreakdownAgain()
        {
            string html = Table("Ports", "2 x USB-A") + Table("Left ports", "1 x USB-A") + Table("Right ports", "1 x USB-A");
            List<PortEntry> ports = PortLookup.ExtractPorts(html);
            Equal(2, Port(ports, "USB-A").Count, "Complete port overview takes precedence over its side breakdown");
        }

        private static void DefinitionsAndBoundedHeadingsAreSupported()
        {
            List<PortEntry> definitions = PortLookup.ExtractPorts("<dl><dt>External I/O Ports</dt><dd>2 x USB-C; 1 x HDMI</dd></dl>");
            Equal(2, Port(definitions, "USB-C").Count, "Definition-list port counts");
            List<PortEntry> headings = PortLookup.ExtractPorts("<h2>Ports and connectors</h2><ul><li>2 x USB-A</li><li>1 x HDMI</li></ul><h2>Accessories</h2><p>9 x USB-A</p>");
            Equal(2, Port(headings, "USB-A").Count, "Port heading stops at next section");
            Equal(1, Port(headings, "HDMI").Count, "List item port quantity");
        }

        private static void QuantityMustBeExplicitAndPlausible()
        {
            List<PortEntry> ports = PortLookup.ExtractPorts(Table("Ports", "<p>USB-A 5Gbps</p><p>HDMI 2.0</p><p>0 x USB-C</p><p>999 x USB-A</p>"));
            Equal(0, ports.Count, "Versions, zero and implausible quantities cannot become socket counts");
        }

        private static void ModelSizeAndGenerationMustAgree()
        {
            const string model = "HP ZBook Firefly 14 inch G9 Mobile Workstation PC";
            True(PortLookup.ModelMatches(model, model + " specifications | HP Support"), "Exact model title accepted");
            True(!PortLookup.ModelMatches(model, "HP ZBook Firefly 16 inch G9 Mobile Workstation PC specifications"), "Wrong screen size rejected");
            True(!PortLookup.ModelMatches(model, "HP ZBook Firefly 14 inch G10 Mobile Workstation PC specifications"), "Wrong generation rejected");
            True(!PortLookup.ModelMatches(model, "HP ZBook Power 14 G9 specifications"), "Different model family rejected");
            True(!PortLookup.ModelMatches(model, "HP ZBook Firefly 14 G9 and 16 G10 specifications"), "Multiple model sizes and generations rejected");
            True(!PortLookup.ModelMatches(model, "HP ZBook Firefly 14 G9 vs G10 specifications"), "Comparison title rejected");
            True(!PortLookup.ModelMatches(model, "HP ZBook Firefly 14 G9 docking station specifications"), "Accessory model mention is not computer specification");
            True(!PortLookup.ModelMatches("System Product Name", "System Product Name specifications"), "Firmware placeholder cannot match a product");
            True(!PortLookup.ModelMatches("Laptop", "Laptop specifications"), "Generic family does not identify a model");
        }

        private static void ProcessorVariantsMustAgreeWithLocalHardware()
        {
            MethodInfo method = typeof(PortLookup).GetMethod("VariantMatches", BindingFlags.NonPublic | BindingFlags.Static);
            True(method != null, "Variant check exists");
            var intel = new Snapshot { ComputerModel = "ThinkPad T14 Gen 3", CpuName = "Intel Core i7-1260P" };
            var amd = new Snapshot { ComputerModel = "ThinkPad T14 Gen 3", CpuName = "AMD Ryzen 7 PRO 6850U" };
            True(!(bool)method.Invoke(null, new object[] { intel, "ThinkPad T14 Gen 3 AMD specifications" }), "AMD variant rejected on Intel computer");
            True(!(bool)method.Invoke(null, new object[] { amd, "ThinkPad T14 Gen 3 Intel specifications" }), "Intel variant rejected on AMD computer");
            True((bool)method.Invoke(null, new object[] { amd, "ThinkPad T14 Gen 3 AMD specifications" }), "Matching processor variant accepted");
            True(!(bool)method.Invoke(null, new object[] { intel, "ThinkPad T14 Gen 3 Intel and AMD specifications" }), "Combined variants are ambiguous");
            True(!(bool)method.Invoke(null, new object[] { new Snapshot(), "ThinkPad T14 Gen 3 AMD specifications" }), "Missing CPU cannot verify a variant");
            MethodInfo titleMethod = typeof(PortLookup).GetMethod("PageModelTitle", BindingFlags.NonPublic | BindingFlags.Static);
            string pageTitle = (string)titleMethod.Invoke(null, new object[] { "<title>ThinkPad T14 Gen 3 specifications</title><h1>ThinkPad T14 Gen 3 AMD</h1>", "ThinkPad T14 Gen 3" });
            Equal("ThinkPad T14 Gen 3 AMD", pageTitle, "Concrete variant heading takes precedence over generic browser title");
            string incompatibleHeading = (string)titleMethod.Invoke(null, new object[] { "<title>ThinkPad T14 Gen 3 specifications</title><h1>ThinkPad T14 Gen 4 AMD specifications</h1>", "ThinkPad T14 Gen 3" });
            Equal("", incompatibleHeading, "Conflicting product heading cannot fall back to an older browser title");
        }

        private static void PreCancelledLookupDoesNotStartNetworkWork()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool cancelled = false;
                try { new PortLookup().Lookup(new Snapshot(), cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                True(cancelled, "Pre-cancelled lookup exits before cache or internet work");
            }
        }

        private static void CommunityPostsAreNotManufacturerSpecifications()
        {
            True(!PortLookup.IsAllowedSource("https://forums.lenovo.com/t5/ThinkPad/specifications", "LENOVO"), "Lenovo forum excluded");
            True(!PortLookup.IsAllowedSource("https://www.dell.com/community/en/conversations/latitude/specifications", "Dell Inc."), "Dell community excluded");
            True(!PortLookup.IsAllowedSource("https://h30434.www3.hp.com/t5/Notebook-Hardware/specifications", "HP"), "HP community excluded");
        }

        private static string Table(string label, string value)
        {
            return "<table><tr><td>" + label + "</td><td>" + value + "</td></tr></table>";
        }

        private static PortEntry Port(IEnumerable<PortEntry> ports, string name)
        {
            PortEntry entry = ports.SingleOrDefault(p => p.Name == name);
            if (entry == null) { throw new InvalidOperationException("Port missing: " + name); }
            return entry;
        }

        private static void Equal<T>(T expected, T actual, string context)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(context + ": expected " + expected + ", received " + actual);
            }
        }

        private static void True(bool value, string context)
        {
            if (!value) { throw new InvalidOperationException(context); }
        }
    }
}
