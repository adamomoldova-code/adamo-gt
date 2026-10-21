using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse
{
    [Flags]
    public enum StressComponent { Cpu = 1, Ram = 2, Ssd = 4, Gpu = 8 }

    public sealed class StressOptions
    {
        public StressComponent Components;
        public int DurationSeconds = 60;
        public bool Sequential;
        public int RamMiB;
        public DiskSnapshot Disk;
        public GpuSnapshot Gpu;

        internal StressOptions Copy()
        {
            return new StressOptions { Components = Components, DurationSeconds = DurationSeconds,
                Sequential = Sequential, RamMiB = RamMiB, Disk = Disk, Gpu = Gpu };
        }
        internal void Validate()
        {
            if (Components == 0 || ((int)Components & ~15) != 0) { throw new ArgumentException("Выберите хотя бы один тест."); }
            if (DurationSeconds < 1 || DurationSeconds > 900) { throw new ArgumentException("Длительность: от 1 до 900 секунд."); }
            if (RamMiB < 0) { throw new ArgumentException("Недопустимый объём памяти."); }
            if ((Components & StressComponent.Ssd) != 0 && Disk == null) { throw new ArgumentException("Выберите накопитель."); }
            if ((Components & StressComponent.Gpu) != 0 && (Gpu == null || !Gpu.AdapterIndex.HasValue)) { throw new ArgumentException("Аппаратный GPU не определён."); }
        }
    }

    public sealed class StressProgress
    {
        public StressComponent Component;
        public string Detail = "Ожидание";
        public long Iterations, BytesProcessed, WrittenBytes;
        public long RamTargetBytes, RamAllocatedBytes, RamCoveredBytes;
        public double ElapsedSeconds, Percent;
        public bool IsRunning, Completed, Cancelled;
        public string Error = "";
        public double? MaxTemperature;
        internal StressProgress Copy() { return (StressProgress)MemberwiseClone(); }
    }

    public sealed class StressRunResult
    {
        public DateTime StartedAtUtc, FinishedAtUtc;
        public StressOptions Options;
        public List<StressProgress> Results = new List<StressProgress>();
        public string SafetyStopReason = "";
        public bool Cancelled;
    }

    public static class StressTestRunner
    {
        internal static readonly StressComponent[] Order = { StressComponent.Cpu, StressComponent.Ram, StressComponent.Ssd, StressComponent.Gpu };
        public static Task<StressRunResult> RunAsync(StressOptions options, Action<StressProgress> report, Func<Snapshot> sensors, CancellationToken token)
        { return RunAsync(options, report, sensors, token, StressWorkloads.Run); }

        internal static async Task<StressRunResult> RunAsync(StressOptions requested, Action<StressProgress> report,
            Func<Snapshot> sensors, CancellationToken token, Action<StressComponent, StressOptions, Action<StressProgress>, CancellationToken> workload)
        {
            if (requested == null || workload == null) { throw new ArgumentNullException(); }
            StressOptions options = requested.Copy(); options.Validate();
            var result = new StressRunResult { StartedAtUtc = DateTime.UtcNow, Options = options };
            var states = Order.Where(component => (options.Components & component) != 0).ToDictionary(component => component, component => new StressProgress { Component = component });
            object sync = new object();
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Action<StressProgress> publish = delegate(StressProgress value)
                {
                    StressProgress copy = value.Copy();
                    lock (sync)
                    {
                        StressProgress previous = states[copy.Component];
                        copy.MaxTemperature = previous.MaxTemperature;
                        states[copy.Component] = copy;
                    }
                    if (report != null) { report(copy.Copy()); }
                };
                Action<string> stop = delegate(string reason)
                {
                    lock (sync) { if (result.SafetyStopReason.Length == 0) { result.SafetyStopReason = reason; } }
                    cancellation.Cancel();
                };
                Action<Snapshot> inspect = delegate(Snapshot sample)
                {
                    if (sample == null || Math.Abs((DateTime.UtcNow - sample.Timestamp.ToUniversalTime()).TotalSeconds) > 10) { return; }
                    string reason = "";
                    lock (sync)
                    {
                        foreach (StressComponent component in states.Keys.ToArray())
                        {
                            double? temperature = Temperature(sample, options, component);
                            if (!temperature.HasValue || Double.IsNaN(temperature.Value) || Double.IsInfinity(temperature.Value)) { continue; }
                            StressProgress state = states[component];
                            state.MaxTemperature = state.MaxTemperature.HasValue ? Math.Max(state.MaxTemperature.Value, temperature.Value) : temperature;
                            double limit = component == StressComponent.Cpu ? 95 : component == StressComponent.Gpu ? 90 : 70;
                            if (temperature.Value >= limit) { reason = Label(component) + ": достигнут предел " + limit + " °C"; }
                        }
                        // RAM also heats the processor even when CPU stress is unchecked.
                        double? cpuTemperature = CpuTemperatureForStress(sample);
                        if ((options.Components & StressComponent.Ram) != 0 && cpuTemperature.HasValue && cpuTemperature.Value >= 95)
                        { reason = "CPU: достигнут предел 95 °C при проверке RAM"; }
                    }
                    if (reason.Length > 0) { stop(reason); }
                };
                if (sensors != null) { try { inspect(sensors()); } catch { } }
                Action<StressComponent> execute = delegate(StressComponent component)
                {
                    var state = new StressProgress { Component = component, IsRunning = true, Detail = "Подготовка" };
                    Stopwatch elapsed = Stopwatch.StartNew();
                    try
                    {
                        cancellation.Token.ThrowIfCancellationRequested(); publish(state);
                        using (IDisposable temperaturePolling = component == StressComponent.Ssd
                            ? SmartReader.BeginStressTemperaturePolling(options.Disk) : null)
                        {
                            workload(component, options, delegate(StressProgress update)
                            {
                                if (update == null) { return; }
                                state = update.Copy(); state.Component = component; state.IsRunning = true;
                                state.Completed = state.Cancelled = false; state.Error = "";
                                state.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                                state.Percent = Math.Min(99.9, state.ElapsedSeconds * 100 / options.DurationSeconds);
                                publish(state);
                            }, cancellation.Token);
                        }
                        cancellation.Token.ThrowIfCancellationRequested();
                        state.Completed = true; state.Percent = 100;
                        state.Detail = "Завершён · ошибок нагрузки не обнаружено"
                            + (String.IsNullOrWhiteSpace(state.Detail) || state.Detail == "Подготовка" ? "" : " · " + state.Detail);
                    }
                    catch (OperationCanceledException)
                    {
                        state.Cancelled = true;
                        state.Detail = "Остановлен · проверка не завершена" + RamWorkDetail(state);
                    }
                    catch (Exception error)
                    {
                        state.Error = error.Message; state.Detail = "Ошибка · " + error.Message + RamWorkDetail(state);
                        stop(Label(component) + ": " + error.Message);
                    }
                    finally
                    {
                        state.IsRunning = false; state.ElapsedSeconds = elapsed.Elapsed.TotalSeconds; publish(state);
                    }
                };
                Task workers;
                if (options.Sequential)
                {
                    workers = Task.Factory.StartNew(delegate { foreach (StressComponent component in Order.Where(states.ContainsKey)) { execute(component); } },
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                else
                {
                    workers = Task.WhenAll(Order.Where(states.ContainsKey).Select(component => Task.Factory.StartNew(delegate { execute(component); },
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
                }
                while (!workers.IsCompleted)
                {
                    await Task.WhenAny(workers, Task.Delay(300)).ConfigureAwait(false);
                    if (sensors != null && !workers.IsCompleted) { try { inspect(sensors()); } catch { } }
                }
                await workers.ConfigureAwait(false);
                lock (sync) { result.Results = Order.Where(states.ContainsKey).Select(component => states[component].Copy()).ToList(); }
                result.Cancelled = token.IsCancellationRequested || result.Results.Any(value => value.Cancelled);
                result.FinishedAtUtc = DateTime.UtcNow;
                return result;
            }
        }

        private static string RamWorkDetail(StressProgress state)
        {
            return state.Component == StressComponent.Ram && state.RamAllocatedBytes > 0
                && !String.IsNullOrWhiteSpace(state.Detail) ? " · " + state.Detail : "";
        }

        internal static double? Temperature(Snapshot sample, StressOptions options, StressComponent component)
        {
            if (component == StressComponent.Cpu) { return CpuTemperatureForStress(sample); }
            if (component == StressComponent.Gpu && options.Gpu != null)
            {
                GpuSnapshot gpu = sample.Gpus.FirstOrDefault(value => value.AdapterIndex == options.Gpu.AdapterIndex
                    && value.AdapterLuidLow == options.Gpu.AdapterLuidLow && value.AdapterLuidHigh == options.Gpu.AdapterLuidHigh);
                return gpu == null ? null : gpu.Temperature;
            }
            if (component == StressComponent.Ssd && options.Disk != null)
            {
                DiskSnapshot disk = sample.Disks.FirstOrDefault(value => value.Index == options.Disk.Index && value.StorageIdentity == options.Disk.StorageIdentity);
                if (disk == null || !disk.TemperatureSampledAtUtc.HasValue
                    || Math.Abs((DateTime.UtcNow - disk.TemperatureSampledAtUtc.Value.ToUniversalTime()).TotalSeconds) > 10) { return null; }
                return disk.Temperature;
            }
            return null;
        }

        internal static double? CpuTemperatureForStress(Snapshot sample)
        {
            if (sample == null || !sample.CpuTemperatureTrustedForStress || sample.CpuTemperatureExperimental
                || !sample.CpuTemperature.HasValue || Double.IsNaN(sample.CpuTemperature.Value)
                || Double.IsInfinity(sample.CpuTemperature.Value)) { return null; }
            return sample.CpuTemperature;
        }
        internal static string Label(StressComponent component) { return component == StressComponent.Cpu ? "CPU" : component == StressComponent.Ram ? "RAM" : component == StressComponent.Gpu ? "GPU" : "SSD"; }
    }

    internal static partial class StressWorkloads
    {
        internal static void Run(StressComponent component, StressOptions options, Action<StressProgress> report, CancellationToken token)
        {
            switch (component)
            {
                case StressComponent.Cpu: RunCpu(options, report, token); break;
                case StressComponent.Ram: RunRam(options, report, token); break;
                case StressComponent.Gpu: RunGpu(options, report, token); break;
                case StressComponent.Ssd: RunSsd(options, report, token); break;
                default: throw new ArgumentException("Неизвестный стресс-тест.");
            }
        }
    }
}
