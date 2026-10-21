using System;
using System.Diagnostics;
using System.Threading;

namespace PcPulse
{
    public static class GpuBenchmark
    {
        public const string Preset = "ADAMO GT Raymarch v1 · D3D11 · 1280 × 720 · вне экрана";

        public static void Run(GpuSnapshot adapter, Action<GpuBenchmarkProgress> report, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            BenchmarkSettings settings = BenchmarkSettings.Current;
            RunCore(adapter, delegate(GpuBenchmarkProgress progress)
            {
                progress.WarmupSeconds = settings.GpuWarmupSeconds;
                progress.PlannedMeasurementSeconds = settings.GpuMeasurementSeconds;
                if (report != null) { report(progress); }
            }, token, delegate { return new GpuNative(adapter); },
                delegate { return clock.Elapsed.TotalSeconds; }, settings.GpuWarmupSeconds, settings.GpuMeasurementSeconds);
        }

        // A renderer and monotonic clock are injected only by deterministic tests.
        internal static void RunCore(GpuSnapshot adapter, Action<GpuBenchmarkProgress> report, CancellationToken token,
            Func<IGpuFrameRenderer> createRenderer, Func<double> seconds, double warmupSeconds, double measurementSeconds)
        {
            double percent = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                if (adapter == null || !adapter.AdapterIndex.HasValue)
                {
                    throw new InvalidOperationException("Аппаратный DXGI-адаптер не определён.");
                }
                Publish(report, adapter, "Подготовка GPU-теста", 0, true, false, null, null, "");
                token.ThrowIfCancellationRequested();
                GpuFrameMeasurements measurements = null;
                double duration = 0;
                using (IGpuFrameRenderer renderer = createRenderer())
                {
                    token.ThrowIfCancellationRequested();
                    double started = seconds();
                    double nextReport = started;
                    while (seconds() - started < warmupSeconds)
                    {
                        token.ThrowIfCancellationRequested();
                        renderer.Render((float)(seconds() - started), token);
                        token.ThrowIfCancellationRequested();
                        double now = seconds();
                        percent = Math.Min(100, 100 * (now - started) / (warmupSeconds + measurementSeconds));
                        if (now >= nextReport)
                        {
                            Publish(report, adapter, "Разогрев GPU · " + warmupSeconds + " с", percent, true, false, null, null, "");
                            nextReport = now + 0.25;
                        }
                    }

                    measurements = new GpuFrameMeasurements();
                    double measuringStarted = seconds();
                    nextReport = measuringStarted;
                    while (seconds() - measuringStarted < measurementSeconds)
                    {
                        token.ThrowIfCancellationRequested();
                        GpuFrameSample sample = renderer.Render((float)(seconds() - started), token);
                        token.ThrowIfCancellationRequested();
                        measurements.Add(sample);
                        double now = seconds();
                        percent = Math.Min(99.9, 100 * (warmupSeconds + now - measuringStarted) / (warmupSeconds + measurementSeconds));
                        if (now >= nextReport)
                        {
                            Publish(report, adapter, "Измерение времени GPU · " + measurementSeconds + " с", percent, true, false,
                                measurements, now - measuringStarted, "");
                            nextReport = now + 0.25;
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (measurements.FrameCount < 10)
                    {
                        throw new InvalidOperationException("Недостаточно достоверных GPU timestamps; скорость не определена.");
                    }
                    duration = seconds() - measuringStarted;
                }
                token.ThrowIfCancellationRequested();
                string finalStage = "GPU-тест завершён";
                if (measurements.RejectedFrameCount > 0)
                {
                    finalStage += " · пропущено измерений: " + measurements.RejectedFrameCount;
                }
                Publish(report, adapter, finalStage, 100, false, true, measurements, duration, "");
            }
            catch (OperationCanceledException)
            {
                Publish(report, adapter, "GPU-тест остановлен", percent, false, false, null, null, "");
            }
            catch (Exception exception)
            {
                Publish(report, adapter, "GPU-тест недоступен", percent, false, false, null, null, exception.Message);
            }
        }

        private static void Publish(Action<GpuBenchmarkProgress> report, GpuSnapshot adapter, string stage,
            double percent, bool running, bool completed, GpuFrameMeasurements measurements, double? duration, string error)
        {
            if (report == null) { return; }
            report(new GpuBenchmarkProgress
            {
                AdapterIndex = adapter == null ? null : adapter.AdapterIndex,
                AdapterName = adapter == null ? "" : adapter.Name,
                Stage = stage,
                Preset = Preset,
                Percent = Math.Max(0, Math.Min(100, percent)),
                IsRunning = running,
                Completed = completed,
                FrameTimeMs = measurements == null ? null : measurements.AverageMilliseconds,
                FramesPerSecond = measurements == null ? null : measurements.FramesPerSecond,
                DurationSeconds = duration,
                Error = error
            });
        }
    }

    internal interface IGpuFrameRenderer : IDisposable
    {
        GpuFrameSample Render(float elapsedSeconds, CancellationToken token);
    }

    internal sealed class GpuFrameSample
    {
        internal readonly ulong Started;
        internal readonly ulong Finished;
        internal readonly ulong Frequency;
        internal readonly bool Disjoint;

        internal GpuFrameSample(ulong started, ulong finished, ulong frequency, bool disjoint)
        {
            Started = started;
            Finished = finished;
            Frequency = frequency;
            Disjoint = disjoint;
        }
    }

    internal sealed class GpuFrameMeasurements
    {
        private double totalMilliseconds;
        internal int FrameCount { get; private set; }
        internal int RejectedFrameCount { get; private set; }
        internal double? AverageMilliseconds { get { return FrameCount > 0 ? (double?)(totalMilliseconds / FrameCount) : null; } }
        internal double? FramesPerSecond { get { return FrameCount > 0 ? (double?)(1000d * FrameCount / totalMilliseconds) : null; } }

        internal bool Add(GpuFrameSample sample)
        {
            if (sample == null || sample.Disjoint || sample.Frequency == 0 || sample.Finished <= sample.Started)
            {
                RejectedFrameCount++;
                return false;
            }
            double milliseconds = (sample.Finished - sample.Started) * 1000d / sample.Frequency;
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0)
            {
                RejectedFrameCount++;
                return false;
            }
            totalMilliseconds += milliseconds;
            FrameCount++;
            return true;
        }
    }
}
