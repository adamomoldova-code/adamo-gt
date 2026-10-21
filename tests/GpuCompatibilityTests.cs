using System;
using PcPulse;

namespace PcPulseTests
{
    public static class GpuCompatibilityTests
    {
        public static int Main()
        {
            try { Run(); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void Run()
        {
            AdapterReorderingPreservesSelectedLuid();
            RemovedAdapterNeverFallsBackToOldIndexOrAnotherGpu();
            SoftwareAdapterCannotBecomeTheBenchmarkTarget();
            AmbiguousIdentityIsRejected();
            UnsupportedFeatureLevelDoesNotMaskUnrelatedFailures();
            Console.WriteLine("PASS: GPU compatibility (LUID selection, unavailable/software adapters, specific feature-level errors; no GPU workload)");
        }

        private static void AdapterReorderingPreservesSelectedLuid()
        {
            var selected = new GpuSnapshot { AdapterIndex = 1, AdapterLuidLow = 120, AdapterLuidHigh = -3 };
            uint index = GpuNative.SelectAdapterIndex(selected, new[]
            {
                Adapter(0, 120, -3), Adapter(1, 888, 0), Adapter(2, 120, 0)
            });
            Require(index == 0, "The current index must be selected by both LUID halves, ignoring the obsolete index.");
            selected.AdapterIndex = 17;
            Require(GpuNative.SelectAdapterIndex(selected, new[] { Adapter(2, 888, 0), Adapter(7, 120, -3) }) == 7,
                "Selection must follow LUID even when the old index no longer exists.");
        }

        private static void RemovedAdapterNeverFallsBackToOldIndexOrAnotherGpu()
        {
            var selected = new GpuSnapshot { AdapterIndex = 1, AdapterLuidLow = 120, AdapterLuidHigh = -3 };
            string message = Error(delegate { GpuNative.SelectAdapterIndex(selected, new[] { Adapter(1, 999, -3), Adapter(2, 120, 0) }); });
            Require(message.Contains("больше недоступна"), "A removed LUID cannot silently fall back to an index or similar LUID.");
            Require(Error(delegate { GpuNative.SelectAdapterIndex(selected, new GpuNative.AdapterIdentity[0]); }).Contains("больше недоступна"),
                "An empty enumeration must not select a default GPU.");
        }

        private static void SoftwareAdapterCannotBecomeTheBenchmarkTarget()
        {
            var selected = new GpuSnapshot { AdapterIndex = 0, AdapterLuidLow = 120, AdapterLuidHigh = -3 };
            Require(Error(delegate
            {
                GpuNative.SelectAdapterIndex(selected, new[] { new GpuNative.AdapterIdentity(4, 120, -3, 2, 0, 0) });
            }).Contains("Программный GPU"), "A matched software adapter must be rejected.");
            Require(Error(delegate
            {
                GpuNative.SelectAdapterIndex(selected, new[] { new GpuNative.AdapterIdentity(4, 120, -3, 0, 0x1414, 0x008c) });
            }).Contains("Программный GPU"), "The primary Microsoft Basic Render Driver must also be rejected when SOFTWARE is absent.");
            Require(GpuNative.SelectAdapterIndex(selected, new[]
            {
                new GpuNative.AdapterIdentity(0, 999, 0, 2, 0x1414, 0x008c), Adapter(1, 120, -3)
            }) == 1, "An unrelated software adapter must not prevent selection of the intended hardware.");
        }

        private static void AmbiguousIdentityIsRejected()
        {
            var selected = new GpuSnapshot { AdapterIndex = 0, AdapterLuidLow = 120, AdapterLuidHigh = -3 };
            Require(Error(delegate { GpuNative.SelectAdapterIndex(selected, new[] { Adapter(0, 120, -3), Adapter(1, 120, -3) }); }).Contains("однозначно"),
                "Contradictory duplicate identities must not select an arbitrary GPU.");
        }

        private static void UnsupportedFeatureLevelDoesNotMaskUnrelatedFailures()
        {
            GpuNative.CheckDeviceCreation(0);
            Require(Error(delegate { GpuNative.CheckDeviceCreation(unchecked((int)0x887A0004)); }).Contains("feature level 11_0"),
                "DXGI_ERROR_UNSUPPORTED explains the required hardware feature level.");
            foreach (uint result in new uint[] { 0x8007000e, 0x887A0005, 0x80070057 })
            {
                string message = Error(delegate { GpuNative.CheckDeviceCreation(unchecked((int)result)); });
                Require(message.Contains(result.ToString("X8")) && !message.Contains("feature level 11_0"),
                    "Memory, device removal and invalid arguments retain their diagnostic HRESULT and are not mislabeled as old graphics.");
            }
        }

        private static GpuNative.AdapterIdentity Adapter(uint index, uint luidLow, int luidHigh)
        {
            return new GpuNative.AdapterIdentity(index, luidLow, luidHigh, 0, 0x8086, 1);
        }

        private static string Error(Action action)
        {
            try { action(); }
            catch (InvalidOperationException exception) { return exception.Message; }
            throw new InvalidOperationException("Expected a specific adapter/device failure.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
