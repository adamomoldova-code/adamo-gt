using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class GpuBenchmarkTests
    {
        public static int Main()
        {
            try { Run(); Console.WriteLine("GPU benchmark deterministic tests: PASS (no GPU workload)."); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        public static void Run()
        {
            AggregatesGpuTimeAndRejectsInvalidTimestamps();
            WarmupExcludedAndProgressSnapshotsIndependent();
            MissingAdapterAndPreCancellationAvoidNativeInitialization();
            CancellationDuringFrameDisposesAndClearsIncompleteSpeed();
            FailureDisposesAndDoesNotReportSuccess();
            DisposalFailureNeverPublishesSuccess();
            DisjointOnlyCannotComplete();
            RejectedMeasurementCountRemainsVisible();
            NativeLayoutsMatchWindowsSdk();
            GpuNative.ValidateShaders();
        }

        private static void AggregatesGpuTimeAndRejectsInvalidTimestamps()
        {
            var samples = new GpuFrameMeasurements();
            True(!samples.FramesPerSecond.HasValue, "An empty measurement is unavailable, not zero.");
            True(samples.Add(new GpuFrameSample(100, 110, 1000, false)), "First stable GPU timestamp accepted.");
            True(samples.Add(new GpuFrameSample(1000, 1060, 2000, false)), "Changed timestamp frequency is converted per sample.");
            Near(20, samples.AverageMilliseconds.Value, "Average GPU time is 20 ms.");
            Near(50, samples.FramesPerSecond.Value, "FPS is reciprocal mean frame time, not mean instantaneous FPS.");
            foreach (GpuFrameSample invalid in new[] {
                new GpuFrameSample(0, 10, 1000, true), new GpuFrameSample(0, 10, 0, false),
                new GpuFrameSample(10, 10, 1000, false), new GpuFrameSample(11, 10, 1000, false), null })
            {
                True(!samples.Add(invalid), "Disjoint, zero frequency and invalid timestamp intervals are rejected.");
            }
            True(samples.FrameCount == 2 && samples.RejectedFrameCount == 5, "Only valid completed frames contribute.");
            Near(50, samples.FramesPerSecond.Value, "Rejected samples do not distort the result.");
        }

        private static void WarmupExcludedAndProgressSnapshotsIndependent()
        {
            var renderer = new FakeRenderer();
            var reports = new List<GpuBenchmarkProgress>();
            GpuBenchmark.RunCore(Adapter(), reports.Add, CancellationToken.None, delegate { return renderer; },
                delegate { return renderer.Clock; }, 0.03, 0.16);
            GpuBenchmarkProgress final = reports[reports.Count - 1];
            True(final.Completed && !final.IsRunning && final.Error == "", "Valid completed rendering reports success.");
            Near(10, final.FrameTimeMs.Value, "Slow warmup frames are excluded from measurement.");
            Near(100, final.FramesPerSecond.Value, "Result measures timestamp frame time.");
            True(final.DurationSeconds >= 0.16 && final.Percent == 100, "Duration and completion are reported after measurement.");
            True(renderer.Disposed, "Native resources are disposed on completion.");
            True(reports[0].IsRunning && reports[0].Percent == 0 && !reports[0].FramesPerSecond.HasValue,
                "Publishing later results does not mutate the original progress object.");
            for (int i = 1; i < reports.Count; i++)
            {
                True(!object.ReferenceEquals(reports[i - 1], reports[i]), "Each callback gets an independent snapshot.");
            }
            True(final.AdapterIndex == 2 && final.AdapterName == "Fixture GPU", "Result preserves the tested adapter identity.");
            True(!final.MaxTemperature.HasValue, "Unavailable temperature is never fabricated.");
        }

        private static void MissingAdapterAndPreCancellationAvoidNativeInitialization()
        {
            int initialized = 0;
            var reports = new List<GpuBenchmarkProgress>();
            Func<IGpuFrameRenderer> create = delegate { initialized++; return new FakeRenderer(); };
            GpuBenchmark.RunCore(new GpuSnapshot(), reports.Add, CancellationToken.None, create, delegate { return 0; }, 3, 15);
            True(initialized == 0 && reports.Count == 1 && reports[0].Error != "", "A WMI-only adapter cannot silently test a default GPU.");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                reports.Clear();
                GpuBenchmark.RunCore(Adapter(), reports.Add, cancellation.Token, create, delegate { return 0; }, 3, 15);
                True(initialized == 0 && reports.Count == 1 && !reports[0].IsRunning && !reports[0].Completed,
                    "A cancelled operation neither creates a device nor reports a result.");
            }
        }

        private static void CancellationDuringFrameDisposesAndClearsIncompleteSpeed()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var renderer = new FakeRenderer { OnFrame = delegate(int frame) { if (frame == 6) { cancellation.Cancel(); } } };
                var reports = new List<GpuBenchmarkProgress>();
                GpuBenchmark.RunCore(Adapter(), reports.Add, cancellation.Token, delegate { return renderer; },
                    delegate { return renderer.Clock; }, 0.03, 0.16);
                GpuBenchmarkProgress final = reports[reports.Count - 1];
                True(renderer.Disposed && renderer.Frames == 6, "Cancellation stops submitting frames promptly and releases resources.");
                True(!final.Completed && !final.IsRunning && !final.FramesPerSecond.HasValue && final.Error == "",
                    "Cancellation does not present a partial measurement as a completed score or an error.");
            }
        }

        private static void FailureDisposesAndDoesNotReportSuccess()
        {
            var renderer = new FakeRenderer { OnFrame = delegate(int frame) { if (frame == 7) { throw new InvalidOperationException("Device removed fixture"); } } };
            var reports = new List<GpuBenchmarkProgress>();
            GpuBenchmark.RunCore(Adapter(), reports.Add, CancellationToken.None, delegate { return renderer; },
                delegate { return renderer.Clock; }, 0.03, 0.16);
            GpuBenchmarkProgress final = reports[reports.Count - 1];
            True(renderer.Disposed && !final.Completed && !final.IsRunning && final.Error.Contains("Device removed"),
                "Device errors dispose resources and remain visible.");
            True(!final.FrameTimeMs.HasValue, "A driver failure invalidates a partial measurement.");
        }

        private static void DisjointOnlyCannotComplete()
        {
            var renderer = new FakeRenderer { Disjoint = true };
            var reports = new List<GpuBenchmarkProgress>();
            GpuBenchmark.RunCore(Adapter(), reports.Add, CancellationToken.None, delegate { return renderer; },
                delegate { return renderer.Clock; }, 0.03, 0.16);
            GpuBenchmarkProgress final = reports[reports.Count - 1];
            True(renderer.Disposed && !final.Completed && !final.FramesPerSecond.HasValue && final.Error != "",
                "Unreliable GPU clocks cannot produce a successful benchmark.");
        }

        private static void DisposalFailureNeverPublishesSuccess()
        {
            var renderer = new FakeRenderer { DisposalFailure = true };
            var reports = new List<GpuBenchmarkProgress>();
            GpuBenchmark.RunCore(Adapter(), reports.Add, CancellationToken.None, delegate { return renderer; },
                delegate { return renderer.Clock; }, 0.03, 0.16);
            GpuBenchmarkProgress final = reports[reports.Count - 1];
            True(renderer.Disposed && !final.Completed && !final.IsRunning && final.Error.Contains("Cleanup fixture"),
                "A cleanup error is reported before any success result.");
            True(!reports.Exists(report => report.Completed), "Cleanup failure cannot publish a transient completed result.");
        }

        private static void RejectedMeasurementCountRemainsVisible()
        {
            var renderer = new FakeRenderer();
            renderer.OnFrame = delegate(int frame) { renderer.Disjoint = frame == 6 || frame == 9; };
            var reports = new List<GpuBenchmarkProgress>();
            GpuBenchmark.RunCore(Adapter(), reports.Add, CancellationToken.None, delegate { return renderer; },
                delegate { return renderer.Clock; }, 0.03, 0.16);
            GpuBenchmarkProgress final = reports[reports.Count - 1];
            True(final.Completed && final.Stage.Contains("пропущено измерений: 2"), "The result discloses discarded timestamp samples.");
            Near(100, final.FramesPerSecond.Value, "Rejected samples do not affect the remaining valid GPU measurement.");
        }

        private static void NativeLayoutsMatchWindowsSdk()
        {
            var expected = new Dictionary<string, int>
            {
                { "AdapterDescription", 312 }, { "TextureDescription", 44 }, { "BufferDescription", 24 },
                { "RasterizerDescription", 40 }, { "QueryDescription", 8 }, { "Viewport", 24 }
            };
            foreach (var layout in expected)
            {
                Type type = typeof(GpuNative).GetNestedType(layout.Key, System.Reflection.BindingFlags.NonPublic);
                True(Marshal.SizeOf(type) == layout.Value, "Native x64 layout matches the Windows SDK: " + layout.Key);
            }
        }

        private static GpuSnapshot Adapter()
        {
            return new GpuSnapshot { Name = "Fixture GPU", AdapterIndex = 2, AdapterLuidLow = 123, AdapterLuidHigh = 1 };
        }

        private sealed class FakeRenderer : IGpuFrameRenderer
        {
            public double Clock;
            public int Frames;
            public bool Disposed;
            public bool Disjoint;
            public bool DisposalFailure;
            public Action<int> OnFrame;

            public GpuFrameSample Render(float elapsed, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Frames++;
                Clock += 0.01;
                if (OnFrame != null) { OnFrame(Frames); }
                return new GpuFrameSample(100, Frames <= 3 ? 1100UL : 110UL, 1000, Disjoint);
            }

            public void Dispose()
            {
                Disposed = true;
                if (DisposalFailure) { throw new InvalidOperationException("Cleanup fixture"); }
            }
        }

        private static void Near(double expected, double actual, string message)
        {
            True(Math.Abs(expected - actual) < 0.00001, message + " Expected " + expected + ", got " + actual + ".");
        }

        private static void True(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
