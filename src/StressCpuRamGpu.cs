using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PcPulse
{
    internal static partial class StressWorkloads
    {
        private const long StressMiB = 1024L * 1024L;
        private const int RamChunkMiB = 16;
        private static readonly uint[] CpuAnswers = new uint[]
        {
            0xa4e1accfU, 0x771e9c9aU, 0x2149f10eU, 0xfd244313U,
            0xe61baa8aU, 0xbecafcd3U, 0xe1b7d8cfU, 0x4578285aU
        };

        internal static void RunCpu(StressOptions options, Action<StressProgress> report, CancellationToken token)
        {
            RunCpuCore(options, report, token, CpuWorkerCount(Environment.ProcessorCount),
                delegate(int pattern, CancellationToken workerToken)
                {
                    ValidateCpuResult(pattern, ComputeCpuCheck(pattern, workerToken));
                });
        }

        // Dedicated workers avoid occupying the thread pool used by the UI and sensor monitor.
        // BelowNormal priority keeps Windows and the stop button responsive under load.
        internal static void RunCpuCore(StressOptions options, Action<StressProgress> report, CancellationToken token,
            int workerCount, Action<int, CancellationToken> compute)
        {
            ValidateStressDuration(options);
            token.ThrowIfCancellationRequested();
            if (workerCount < 1 || workerCount > 64) { throw new ArgumentOutOfRangeException("workerCount"); }
            if (compute == null) { throw new ArgumentNullException("compute"); }
            var workers = new List<Thread>(workerCount);
            var clock = new Stopwatch();
            long blocks = 0;
            Exception workerFailure = null;
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var start = new ManualResetEvent(false))
            {
                try
                {
                    for (int index = 0; index < workerCount; index++)
                    {
                        int workerIndex = index;
                        var thread = new Thread(delegate()
                        {
                            try
                            {
                                start.WaitOne();
                                int pattern = workerIndex % CpuAnswers.Length;
                                while (clock.Elapsed.TotalSeconds < options.DurationSeconds)
                                {
                                    stop.Token.ThrowIfCancellationRequested();
                                    compute(pattern, stop.Token);
                                    stop.Token.ThrowIfCancellationRequested();
                                    Interlocked.Increment(ref blocks);
                                    pattern = (pattern + 1) % CpuAnswers.Length;
                                }
                            }
                            catch (OperationCanceledException exception)
                            {
                                // An unrelated cancellation thrown by the kernel is still a failure.
                                if (!stop.IsCancellationRequested)
                                {
                                    Interlocked.CompareExchange(ref workerFailure, exception, null);
                                    stop.Cancel();
                                }
                            }
                            catch (Exception exception)
                            {
                                Interlocked.CompareExchange(ref workerFailure, exception, null);
                                stop.Cancel();
                            }
                        });
                        thread.IsBackground = true;
                        thread.Priority = ThreadPriority.BelowNormal;
                        thread.Name = "ADAMO GT CPU stress " + (index + 1);
                        thread.Start();
                        workers.Add(thread);
                    }
                    clock.Start();
                    start.Set();
                    while (HasLiveStressThreads(workers))
                    {
                        token.ThrowIfCancellationRequested();
                        if (workerFailure != null) { break; }
                        ReportCpu(report, Interlocked.Read(ref blocks), workerCount);
                        if (stop.Token.WaitHandle.WaitOne(250)) { break; }
                    }
                }
                finally
                {
                    stop.Cancel();
                    start.Set();
                    // ComputeCpuCheck checks cancellation every 256 arithmetic rounds.
                    // No CPU worker or callback remains active after this method returns.
                    foreach (Thread worker in workers) { worker.Join(); }
                }
            }
            token.ThrowIfCancellationRequested();
            if (workerFailure != null)
            {
                throw new InvalidOperationException("Ошибка проверки вычислений CPU: " + workerFailure.Message, workerFailure);
            }
            if (blocks == 0) { throw new InvalidOperationException("CPU не выполнил ни одного проверенного блока вычислений."); }
            ReportCpu(report, blocks, workerCount);
        }

        internal static int CpuWorkerCount(int logicalProcessors)
        {
            return Math.Max(1, Math.Min(64, logicalProcessors));
        }

        private static bool HasLiveStressThreads(List<Thread> workers)
        {
            foreach (Thread worker in workers) { if (worker.IsAlive) { return true; } }
            return false;
        }

        private static void ReportCpu(Action<StressProgress> report, long blocks, int workers)
        {
            if (report == null) { return; }
            report(new StressProgress
            {
                Component = StressComponent.Cpu,
                Iterations = blocks,
                Detail = "Потоков: " + workers + " · проверено блоков: " + blocks.ToString("N0", CultureInfo.CurrentCulture)
            });
        }

        internal static uint ComputeCpuCheck(int pattern, CancellationToken token)
        {
            if (pattern < 0 || pattern >= CpuAnswers.Length) { throw new ArgumentOutOfRangeException("pattern"); }
            unchecked
            {
                uint a = (uint)(pattern + 1) * 0x01020304U;
                uint b = 0x9e3779b9U, c = 0x243f6a88U, d = 0xb7e15162U;
                for (uint round = 0; round < 4096; round++)
                {
                    if ((round & 255) == 0) { token.ThrowIfCancellationRequested(); }
                    a = (a ^ (a >> 13)) * 1664525U + b + round;
                    b = ((b << 7) | (b >> 25)) ^ a;
                    c += (a ^ d) * 2246822519U;
                    d = ((d << 11) | (d >> 21)) ^ c ^ (round * 2654435761U);
                }
                return a ^ b ^ c ^ d;
            }
        }

        internal static void ValidateCpuResult(int pattern, uint actual)
        {
            if (pattern < 0 || pattern >= CpuAnswers.Length) { throw new ArgumentOutOfRangeException("pattern"); }
            if (actual != CpuAnswers[pattern])
            {
                throw new InvalidOperationException("Контрольная сумма CPU не совпала, шаблон " + (pattern + 1) + ".");
            }
        }

        internal static void RunRam(StressOptions options, Action<StressProgress> report, CancellationToken token)
        {
            ValidateStressDuration(options);
            token.ThrowIfCancellationRequested();
            StressMemoryStatus status = ReadStressMemoryStatus();
            int targetMiB = CalculateRamMiB(status.AvailablePhysical, options.RamMiB);
            ulong reserveBytes = CalculateRamReserveBytes(status.AvailablePhysical);
            // Commit and address-space limits also matter on busy or constrained Windows systems.
            targetMiB = Math.Min(targetMiB, CalculateRamCommitMiB(status.AvailablePageFile, status.AvailableVirtual));
            if (targetMiB < 1)
            {
                throw new InvalidOperationException("Недостаточно свободной памяти для RAM-теста с резервом для Windows.");
            }
            RunRamCore(targetMiB, options.DurationSeconds, report, token, delegate
            {
                // Recheck before each allocation: other applications can consume the initial budget.
                StressMemoryStatus current = ReadStressMemoryStatus();
                EnsureRamCommitHeadroom(current.AvailablePageFile, current.AvailableVirtual, 0);
                return current.AvailablePhysical;
            }, reserveBytes, delegate(ulong allocationBytes)
            {
                StressMemoryStatus current = ReadStressMemoryStatus();
                EnsureRamCommitHeadroom(current.AvailablePageFile, current.AvailableVirtual, allocationBytes);
            });
        }

        internal static int CalculateRamMiB(ulong availablePhysical, int requestedMiB)
        {
            if (requestedMiB < 0) { throw new ArgumentOutOfRangeException("requestedMiB"); }
            ulong availableMiB = availablePhysical / (ulong)StressMiB;
            ulong reserveMiB = CalculateRamReserveBytes(availablePhysical) / (ulong)StressMiB;
            if (availableMiB <= reserveMiB) { return 0; }
            ulong budget = Math.Min((ulong)int.MaxValue, availableMiB - reserveMiB);
            if (requestedMiB > 0) { budget = Math.Min(budget, (ulong)requestedMiB); }
            return (int)budget;
        }

        internal static ulong CalculateRamReserveBytes(ulong availablePhysical)
        {
            ulong availableMiB = availablePhysical / (ulong)StressMiB;
            // ceil(availableMiB / 5), written without addition that could overflow.
            ulong fifthMiB = availableMiB / 5 + (availableMiB % 5 == 0 ? 0UL : 1UL);
            return checked(Math.Max(1024UL, fifthMiB) * (ulong)StressMiB);
        }

        internal static int CalculateRamCommitMiB(ulong availableCommit, ulong availableVirtual)
        {
            ulong reserveBytes = 256UL * (ulong)StressMiB;
            ulong available = Math.Min(availableCommit, availableVirtual);
            return available <= reserveBytes ? 0 :
                (int)Math.Min((ulong)int.MaxValue, (available - reserveBytes) / (ulong)StressMiB);
        }

        internal static void EnsureRamCommitHeadroom(ulong availableCommit, ulong availableVirtual, ulong allocationBytes)
        {
            ulong available = Math.Min(availableCommit, availableVirtual);
            ulong reserve = 256UL * (ulong)StressMiB;
            if (available < reserve || available - reserve < allocationBytes)
            {
                throw new InvalidOperationException("Windows не хватает доступного лимита памяти или адресного пространства: RAM-тест остановлен с резервом 256 MB.");
            }
        }

        internal static void RunRamCore(int targetMiB, int durationSeconds, Action<StressProgress> report,
            CancellationToken token, Func<ulong> availablePhysical)
        {
            RunRamCore(targetMiB, durationSeconds, report, token, availablePhysical, 1024UL * (ulong)StressMiB);
        }

        internal static void RunRamCore(int targetMiB, int durationSeconds, Action<StressProgress> report,
            CancellationToken token, Func<ulong> availablePhysical, ulong reserveBytes)
        {
            RunRamCore(targetMiB, durationSeconds, report, token, availablePhysical, reserveBytes, null);
        }

        internal static void RunRamCore(int targetMiB, int durationSeconds, Action<StressProgress> report,
            CancellationToken token, Func<ulong> availablePhysical, ulong reserveBytes, Action<ulong> beforeAllocation)
        {
            if (targetMiB < 1) { throw new ArgumentOutOfRangeException("targetMiB"); }
            if (durationSeconds < 1 || durationSeconds > 900) { throw new ArgumentOutOfRangeException("durationSeconds"); }
            if (availablePhysical == null) { throw new ArgumentNullException("availablePhysical"); }
            if (reserveBytes < 1024UL * (ulong)StressMiB) { throw new ArgumentOutOfRangeException("reserveBytes"); }
            token.ThrowIfCancellationRequested();
            var buffers = new List<uint[]>();
            int allocatedMiB = 0;
            var clock = Stopwatch.StartNew();
            double nextAllocationReport = 0;
            try
            {
                while (allocatedMiB < targetMiB)
                {
                    token.ThrowIfCancellationRequested();
                    if (clock.Elapsed.TotalSeconds >= durationSeconds)
                    {
                        throw IncompleteRamCycle((long)allocatedMiB * StressMiB, 0);
                    }
                    int chunkMiB = Math.Min(RamChunkMiB, targetMiB - allocatedMiB);
                    CheckRamReserve(availablePhysical, (ulong)chunkMiB * (ulong)StressMiB, reserveBytes, token);
                    if (beforeAllocation != null) { beforeAllocation((ulong)chunkMiB * (ulong)StressMiB); }
                    token.ThrowIfCancellationRequested();
                    if (clock.Elapsed.TotalSeconds >= durationSeconds)
                    {
                        throw IncompleteRamCycle((long)allocatedMiB * StressMiB, 0);
                    }
                    buffers.Add(new uint[chunkMiB * (int)(StressMiB / sizeof(uint))]);
                    allocatedMiB += chunkMiB;
                    if (report != null && (clock.Elapsed.TotalSeconds >= nextAllocationReport || allocatedMiB == targetMiB))
                    {
                        report(CreateRamAllocationProgress(targetMiB, allocatedMiB, reserveBytes));
                        nextAllocationReport = clock.Elapsed.TotalSeconds + 0.25;
                        token.ThrowIfCancellationRequested();
                    }
                }
                RunRamBuffers(buffers, durationSeconds, report, token, availablePhysical,
                    delegate { return clock.Elapsed.TotalSeconds; }, FillRamPattern, VerifyRamPattern, reserveBytes);
            }
            finally
            {
                // Large pools are reclaimed before return/retry, including cancellation and errors.
                // Forced Gen2 collection can briefly pause the UI; no LOH compaction is requested.
                for (int index = 0; index < buffers.Count; index++) { buffers[index] = null; }
                buffers.Clear();
                if (allocatedMiB >= 256) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true); }
            }
        }

        internal static StressProgress CreateRamAllocationProgress(int targetMiB, int allocatedMiB, ulong reserveBytes)
        {
            if (targetMiB < 1 || allocatedMiB < 0 || allocatedMiB > targetMiB) { throw new ArgumentOutOfRangeException("allocatedMiB"); }
            return new StressProgress
            {
                Component = StressComponent.Ram,
                RamTargetBytes = checked((long)targetMiB * StressMiB),
                RamAllocatedBytes = checked((long)allocatedMiB * StressMiB),
                RamCoveredBytes = 0,
                Detail = "подготовка RAM: выделено " + allocatedMiB + "/" + targetMiB + " MB · резерв Windows: " +
                    (reserveBytes / (ulong)StressMiB).ToString(CultureInfo.CurrentCulture) + " MB"
            };
        }

        // The test seam supplies tiny buffers and a controlled clock; the production path above
        // always uses the real pattern routines and a Stopwatch including allocation time.
        internal static void RunRamBuffers(IList<uint[]> buffers, int durationSeconds, Action<StressProgress> report,
            CancellationToken token, Func<ulong> availablePhysical, Func<double> elapsedSeconds,
            Action<uint[], int, uint, CancellationToken> fill, Action<uint[], int, uint, CancellationToken> verify)
        {
            RunRamBuffers(buffers, durationSeconds, report, token, availablePhysical, elapsedSeconds,
                fill, verify, 1024UL * (ulong)StressMiB);
        }

        internal static void RunRamBuffers(IList<uint[]> buffers, int durationSeconds, Action<StressProgress> report,
            CancellationToken token, Func<ulong> availablePhysical, Func<double> elapsedSeconds,
            Action<uint[], int, uint, CancellationToken> fill, Action<uint[], int, uint, CancellationToken> verify, ulong reserveBytes)
        {
            if (buffers == null || buffers.Count == 0) { throw new ArgumentException("Нужен хотя бы один блок RAM.", "buffers"); }
            if (durationSeconds < 1 || durationSeconds > 900) { throw new ArgumentOutOfRangeException("durationSeconds"); }
            if (availablePhysical == null || elapsedSeconds == null || fill == null || verify == null) { throw new ArgumentNullException(); }
            if (reserveBytes < 1024UL * (ulong)StressMiB) { throw new ArgumentOutOfRangeException("reserveBytes"); }
            token.ThrowIfCancellationRequested();
            long allocatedBytes = 0;
            foreach (uint[] buffer in buffers)
            {
                ValidateRamBuffer(buffer, 0);
                allocatedBytes = checked(allocatedBytes + (long)buffer.Length * sizeof(uint));
            }
            var coverage = new byte[buffers.Count];
            long coveredBytes = 0, cycles = 0, verifiedBlocks = 0, transferredBytes = 0;
            double nextReport = 0;
            Action<bool> publish = delegate(bool force)
            {
                double now = ReadRamElapsed(elapsedSeconds);
                if (force || now >= nextReport)
                {
                    ReportRam(report, allocatedBytes, coveredBytes, cycles, verifiedBlocks, transferredBytes);
                    nextReport = now + 0.25;
                }
            };
            while (ReadRamElapsed(elapsedSeconds) < durationSeconds)
            {
                for (int pattern = 0; pattern < 3; pattern++)
                {
                    // Finish writing the whole pool before reading it. A later block can no
                    // longer hide corruption in an earlier block by overwriting it first.
                    for (int block = 0; block < buffers.Count; block++)
                    {
                        if (!CanRunRamBlock(durationSeconds, token, availablePhysical, elapsedSeconds, reserveBytes)) { goto Finished; }
                        uint seed = RamCycleSeed(block, cycles);
                        fill(buffers[block], pattern, seed, token);
                        token.ThrowIfCancellationRequested();
                        transferredBytes = checked(transferredBytes + (long)buffers[block].Length * sizeof(uint));
                        publish(false);
                    }
                    for (int block = 0; block < buffers.Count; block++)
                    {
                        if (!CanRunRamBlock(durationSeconds, token, availablePhysical, elapsedSeconds, reserveBytes)) { goto Finished; }
                        uint seed = RamCycleSeed(block, cycles);
                        verify(buffers[block], pattern, seed, token);
                        token.ThrowIfCancellationRequested();
                        transferredBytes = checked(transferredBytes + (long)buffers[block].Length * sizeof(uint));
                        verifiedBlocks = checked(verifiedBlocks + 1);
                        byte previousCoverage = coverage[block];
                        coverage[block] |= (byte)(1 << pattern);
                        if (previousCoverage != 7 && coverage[block] == 7)
                        { coveredBytes = checked(coveredBytes + (long)buffers[block].Length * sizeof(uint)); }
                        publish(false);
                    }
                }
                // One iteration means all three patterns verified across every allocated block.
                cycles = checked(cycles + 1);
                publish(false);
            }
        Finished:
            token.ThrowIfCancellationRequested();
            publish(true);
            token.ThrowIfCancellationRequested();
            if (cycles == 0) { throw IncompleteRamCycle(allocatedBytes, coveredBytes); }
        }

        private static uint RamCycleSeed(int block, long cycle)
        {
            return unchecked(0x6d2b79f5U + (uint)block * 0x9e3779b9U + (uint)cycle * 0x85ebca6bU);
        }

        private static bool CanRunRamBlock(int durationSeconds, CancellationToken token,
            Func<ulong> availablePhysical, Func<double> elapsedSeconds, ulong reserveBytes)
        {
            token.ThrowIfCancellationRequested();
            if (ReadRamElapsed(elapsedSeconds) >= durationSeconds) { return false; }
            CheckRamReserve(availablePhysical, 0, reserveBytes, token);
            return ReadRamElapsed(elapsedSeconds) < durationSeconds;
        }

        private static void CheckRamReserve(Func<ulong> availablePhysical, ulong allocationBytes, ulong reserveBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ulong available = availablePhysical();
            if (available < reserveBytes || available - reserveBytes < allocationBytes)
            {
                string reserve = reserveBytes == 1024UL * (ulong)StressMiB ? "1 GB"
                    : (reserveBytes / (ulong)StressMiB).ToString(CultureInfo.CurrentCulture) + " MB";
                throw new InvalidOperationException("Свободная память уменьшилась. RAM-тест остановлен, чтобы сохранить резерв " + reserve + " для Windows.");
            }
            token.ThrowIfCancellationRequested();
        }

        private static double ReadRamElapsed(Func<double> elapsedSeconds)
        {
            double value = elapsedSeconds();
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0)
            { throw new InvalidOperationException("Не удалось определить время RAM-теста."); }
            return value;
        }

        private static InvalidOperationException IncompleteRamCycle(long allocatedBytes, long coveredBytes)
        {
            return new InvalidOperationException("RAM-тест не завершил полный цикл из 3 шаблонов. Увеличьте длительность. " +
                "Проверено всеми шаблонами: " + RamMegabytes(coveredBytes) + " из " + RamMegabytes(allocatedBytes) + " MB выделенной памяти.");
        }

        private static string RamMegabytes(long bytes)
        {
            return ((double)bytes / StressMiB).ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static void ReportRam(Action<StressProgress> report, long allocatedBytes, long coveredBytes,
            long cycles, long verifiedBlocks, long transferredBytes)
        {
            if (report == null) { return; }
            report(CreateRamProgress(allocatedBytes, coveredBytes, cycles, verifiedBlocks, transferredBytes));
        }

        internal static StressProgress CreateRamProgress(long allocatedBytes, long coveredBytes,
            long cycles, long verifiedBlocks, long transferredBytes)
        {
            if (allocatedBytes < 0 || coveredBytes < 0 || coveredBytes > allocatedBytes || cycles < 0 || verifiedBlocks < 0 || transferredBytes < 0)
            { throw new ArgumentOutOfRangeException("allocatedBytes"); }
            return new StressProgress
            {
                Component = StressComponent.Ram,
                Iterations = cycles,
                BytesProcessed = transferredBytes,
                RamTargetBytes = allocatedBytes,
                RamAllocatedBytes = allocatedBytes,
                RamCoveredBytes = coveredBytes,
                Detail = "выделено " + RamMegabytes(allocatedBytes) + " MB · 3 шаблона: " + RamMegabytes(coveredBytes) + "/" +
                    RamMegabytes(allocatedBytes) + " MB · проверено блоков: " + verifiedBlocks.ToString("N0", CultureInfo.CurrentCulture) +
                    " · часть RAM"
            };
        }

        internal static void FillRamPattern(uint[] buffer, int pattern, uint seed, CancellationToken token)
        {
            ValidateRamBuffer(buffer, pattern);
            for (int index = 0; index < buffer.Length; index++)
            {
                if ((index & 4095) == 0) { token.ThrowIfCancellationRequested(); }
                buffer[index] = RamPattern((uint)index, pattern, seed);
            }
            token.ThrowIfCancellationRequested();
        }

        internal static void VerifyRamPattern(uint[] buffer, int pattern, uint seed, CancellationToken token)
        {
            VerifyRamPattern(buffer, pattern, seed, token, null);
        }

        // A checkpoint observer is used only to exercise cancellation inside the real checker.
        internal static void VerifyRamPattern(uint[] buffer, int pattern, uint seed, CancellationToken token, Action<int> checkpoint)
        {
            ValidateRamBuffer(buffer, pattern);
            for (int index = 0; index < buffer.Length; index++)
            {
                if ((index & 4095) == 0)
                {
                    if (checkpoint != null) { checkpoint(index); }
                    token.ThrowIfCancellationRequested();
                }
                if (buffer[index] != RamPattern((uint)index, pattern, seed))
                {
                    throw new InvalidOperationException("Ошибка RAM: данные не совпали в блоке, смещение " +
                        ((long)index * sizeof(uint)).ToString(CultureInfo.InvariantCulture) + " байт, шаблон " + (pattern + 1) + ".");
                }
            }
            token.ThrowIfCancellationRequested();
        }

        private static void ValidateRamBuffer(uint[] buffer, int pattern)
        {
            if (buffer == null) { throw new ArgumentNullException("buffer"); }
            if (buffer.Length == 0) { throw new ArgumentException("Пустой блок RAM нельзя проверить.", "buffer"); }
            if (pattern < 0 || pattern > 2) { throw new ArgumentOutOfRangeException("pattern"); }
        }

        private static uint RamPattern(uint index, int pattern, uint seed)
        {
            if (pattern == 0) { return 0x55555555U; }
            if (pattern == 1) { return 0xaaaaaaaaU; }
            unchecked
            {
                uint value = index + seed;
                value ^= value >> 16;
                value *= 0x7feb352dU;
                value ^= value >> 15;
                value *= 0x846ca68bU;
                return value ^ (value >> 16);
            }
        }

        internal static void RunGpu(StressOptions options, Action<StressProgress> report, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            RunGpuCore(options, report, token, delegate { return new GpuNative(options.Gpu); },
                delegate { return clock.Elapsed.TotalSeconds; });
        }

        internal static void RunGpuCore(StressOptions options, Action<StressProgress> report, CancellationToken token,
            Func<IGpuFrameRenderer> createRenderer, Func<double> seconds)
        {
            ValidateStressDuration(options);
            token.ThrowIfCancellationRequested();
            if (options.Gpu == null || !options.Gpu.AdapterIndex.HasValue)
            {
                throw new InvalidOperationException("Аппаратный DXGI-адаптер не определён.");
            }
            if (createRenderer == null) { throw new ArgumentNullException("createRenderer"); }
            if (seconds == null) { throw new ArgumentNullException("seconds"); }
            var measurements = new GpuFrameMeasurements();
            using (IGpuFrameRenderer renderer = createRenderer())
            {
                if (renderer == null) { throw new InvalidOperationException("Не удалось создать GPU-рендерер."); }
                token.ThrowIfCancellationRequested();
                double started = seconds();
                double nextReport = started;
                int consecutiveRejected = 0;
                while (seconds() - started < options.DurationSeconds)
                {
                    token.ThrowIfCancellationRequested();
                    GpuFrameSample sample = renderer.Render((float)(seconds() - started), token);
                    token.ThrowIfCancellationRequested();
                    if (measurements.Add(sample)) { consecutiveRejected = 0; }
                    else { consecutiveRejected++; }
                    if (consecutiveRejected >= 32)
                    {
                        throw new InvalidOperationException("GPU не возвращает достоверные timestamps: 32 измерения подряд отклонены.");
                    }
                    double now = seconds();
                    if (now >= nextReport)
                    {
                        ReportStressGpu(report, measurements);
                        nextReport = now + 0.25;
                    }
                }
                token.ThrowIfCancellationRequested();
                if (measurements.FrameCount < 10)
                {
                    throw new InvalidOperationException("Недостаточно достоверных GPU timestamps: проверено менее 10 кадров.");
                }
            }
            token.ThrowIfCancellationRequested();
            // Disposal/device-loss failures propagate before the final telemetry and session result.
            ReportStressGpu(report, measurements);
        }

        private static void ReportStressGpu(Action<StressProgress> report, GpuFrameMeasurements measurements)
        {
            if (report == null) { return; }
            string detail = "D3D11 · кадров: " + measurements.FrameCount.ToString("N0", CultureInfo.CurrentCulture);
            if (measurements.FramesPerSecond.HasValue)
            {
                detail += " · " + measurements.FramesPerSecond.Value.ToString("0.0", CultureInfo.CurrentCulture) + " FPS";
            }
            if (measurements.RejectedFrameCount > 0) { detail += " · пропущено: " + measurements.RejectedFrameCount; }
            report(new StressProgress { Component = StressComponent.Gpu, Iterations = measurements.FrameCount, Detail = detail });
        }

        private static void ValidateStressDuration(StressOptions options)
        {
            if (options == null) { throw new ArgumentNullException("options"); }
            if (options.DurationSeconds < 1 || options.DurationSeconds > 900)
            {
                throw new ArgumentOutOfRangeException("DurationSeconds", "Длительность должна быть от 1 до 900 секунд.");
            }
        }

        private static StressMemoryStatus ReadStressMemoryStatus()
        {
            var memory = new StressMemoryStatus();
            memory.Length = (uint)Marshal.SizeOf(typeof(StressMemoryStatus));
            if (!GetStressMemoryStatus(ref memory))
            {
                throw new InvalidOperationException("Windows не сообщил доступный объём RAM; безопасный размер теста не определён.");
            }
            return memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StressMemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetStressMemoryStatus(ref StressMemoryStatus memory);
    }
}
