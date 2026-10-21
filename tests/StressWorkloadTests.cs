using System;
using System.Collections.Generic;
using System.Threading;
using PcPulse;

namespace PcPulseTests
{
    public static class StressWorkloadTests
    {
        public static void Run()
        {
            CpuKnownAnswersDetectCorruption();
            CpuWorkersAreBoundedAndCancellationJoinsThreads();
            CpuFailureAndCallbackFailureJoinThreads();
            RamBudgetPreservesWindowsReserve();
            RamCommitAndScaledReserveAreCheckedWithoutLargeAllocations();
            RamLargeAllocationTelemetryUses64BitBytes();
            RamPreparationReportsAndCancelsLargePlan();
            RamPatternsDetectCorruptionAndRespectCancellation();
            RamAllocationRefusesLostReserve();
            RamPoolSweepsCompleteOnlyAfterAllPatterns();
            RamLaterWritesCannotHideEarlierCorruption();
            RamChecksMemoryPressureDuringWork();
            RamCancellationInsideVerificationStopsTheSweep();
            RamIncompleteDeadlineCannotBecomeAFullCycle();
            GpuFramesAreValidatedAndDisposed();
            GpuPreconditionsAvoidDeviceCreation();
            GpuFailureAndCancellationDisposeResources();
            GpuInvalidClocksAndCleanupCannotPass();
        }

        private static StressOptions Options()
        {
            return new StressOptions
            {
                DurationSeconds = 1,
                Gpu = new GpuSnapshot { Name = "Fixture GPU", AdapterIndex = 1 }
            };
        }

        private static void CpuKnownAnswersDetectCorruption()
        {
            var checks = new HashSet<uint>();
            for (int pattern = 0; pattern < 8; pattern++)
            {
                uint result = StressWorkloads.ComputeCpuCheck(pattern, CancellationToken.None);
                StressWorkloads.ValidateCpuResult(pattern, result);
                True(checks.Add(result), "CPU patterns exercise distinct known answers.");
                int selected = pattern;
                Throws<InvalidOperationException>(delegate { StressWorkloads.ValidateCpuResult(selected, result ^ 1U); });
            }
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                Throws<OperationCanceledException>(delegate { StressWorkloads.ComputeCpuCheck(0, stop.Token); });
            }
            Throws<ArgumentOutOfRangeException>(delegate { StressWorkloads.ComputeCpuCheck(8, CancellationToken.None); });
        }

        private static void CpuWorkersAreBoundedAndCancellationJoinsThreads()
        {
            True(StressWorkloads.CpuWorkerCount(-1) == 1, "At least one CPU worker is selected.");
            True(StressWorkloads.CpuWorkerCount(16) == 16, "Every normal logical core is exercised.");
            True(StressWorkloads.CpuWorkerCount(256) == 64, "Very large CPUs cannot create unbounded workers.");
            int calls = 0;
            int active = 0;
            using (var stop = new CancellationTokenSource())
            {
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunCpuCore(Options(), null, stop.Token, 2, delegate(int pattern, CancellationToken token)
                    {
                        Interlocked.Increment(ref active);
                        try
                        {
                            Interlocked.Increment(ref calls);
                            stop.Cancel();
                            token.ThrowIfCancellationRequested();
                        }
                        finally { Interlocked.Decrement(ref active); }
                    });
                });
                True(calls > 0 && active == 0, "Cancellation joins every worker before returning.");
                calls = 0;
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunCpuCore(Options(), null, stop.Token, 2,
                        delegate(int pattern, CancellationToken token) { Interlocked.Increment(ref calls); });
                });
                True(calls == 0, "A pre-cancelled CPU stress does not create work.");
            }
        }

        private static void CpuFailureAndCallbackFailureJoinThreads()
        {
            int active = 0;
            Exception failure = Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunCpuCore(Options(), null, CancellationToken.None, 2,
                    delegate(int pattern, CancellationToken token)
                    {
                        Interlocked.Increment(ref active);
                        try { throw new InvalidOperationException("CPU corruption fixture"); }
                        finally { Interlocked.Decrement(ref active); }
                    });
            });
            True(active == 0 && failure.Message.Contains("CPU corruption fixture"), "CPU failure joins workers and preserves its cause.");
            Throws<ApplicationException>(delegate
            {
                StressWorkloads.RunCpuCore(Options(), delegate { throw new ApplicationException("Callback fixture"); },
                    CancellationToken.None, 2, delegate(int pattern, CancellationToken token)
                    {
                        Interlocked.Increment(ref active);
                        try { token.WaitHandle.WaitOne(5); token.ThrowIfCancellationRequested(); }
                        finally { Interlocked.Decrement(ref active); }
                    });
            });
            True(active == 0, "A failed progress callback cannot leave CPU load running.");
        }

        private static void RamBudgetPreservesWindowsReserve()
        {
            const ulong mib = 1024UL * 1024UL;
            True(StressWorkloads.CalculateRamMiB(0, 0) == 0, "Unavailable physical memory cannot become a test allocation.");
            True(StressWorkloads.CalculateRamMiB(1024 * mib, 0) == 0, "One GB remains available for Windows.");
            True(StressWorkloads.CalculateRamMiB(1025 * mib, 0) == 1, "The minimum reserve has a precise MB boundary.");
            True(StressWorkloads.CalculateRamMiB(1300 * mib, 0) == 276, "Near the reserve, allocation cannot cross one GB free.");
            True(StressWorkloads.CalculateRamMiB(2048 * mib, 0) == 1024, "A small machine keeps the one GB reserve.");
            int[] availableMiB = { 4096, 8192, 16384, 32768, 65536 };
            int[] expectedMiB = { 3072, 6553, 13107, 26214, 52428 };
            for (int index = 0; index < availableMiB.Length; index++)
            {
                ulong available = (ulong)availableMiB[index] * mib;
                int budget = StressWorkloads.CalculateRamMiB(available, 0);
                ulong reserve = StressWorkloads.CalculateRamReserveBytes(available);
                True(budget == expectedMiB[index], "Automatic memory scales beyond two GB for " + availableMiB[index] + " MB available.");
                True(reserve >= 1024 * mib && (ulong)budget * mib + reserve <= available,
                    "The budget and reserve stay within available physical memory.");
                True((reserve / mib) * 5 >= (ulong)availableMiB[index], "The reserve rounds up twenty percent of available MB.");
            }
            True(StressWorkloads.CalculateRamMiB(16384 * mib, 8192) == 8192, "An explicit eight GB choice is no longer limited to two GB.");
            True(StressWorkloads.CalculateRamMiB(8192 * mib, 8192) == 6553, "Explicit requests are clamped to the actual safe budget.");
            True(StressWorkloads.CalculateRamMiB(65536 * mib, 65536) == 52428, "A large request still leaves the scaled Windows reserve.");
            True(StressWorkloads.CalculateRamMiB(2048 * mib, 64) == 64, "A smaller explicit amount remains bounded.");
            True(StressWorkloads.CalculateRamMiB(ulong.MaxValue, 0) == int.MaxValue, "An extreme physical size is limited only by the MiB option type.");
            True(StressWorkloads.CalculateRamMiB(ulong.MaxValue, int.MaxValue) == int.MaxValue, "Large memory fields do not overflow explicit requests.");
            True(StressWorkloads.CalculateRamMiB(ulong.MaxValue, 8192) == 8192, "Extreme inventory values cannot enlarge an explicit request.");
            True(StressWorkloads.CalculateRamReserveBytes(ulong.MaxValue) <= ulong.MaxValue / 4,
                "The rounded scaled reserve remains overflow safe at the maximum byte count.");
            Throws<ArgumentOutOfRangeException>(delegate { StressWorkloads.CalculateRamMiB(4096 * mib, -1); });
        }

        private static void RamCommitAndScaledReserveAreCheckedWithoutLargeAllocations()
        {
            const ulong mib = 1024UL * 1024UL;
            True(StressWorkloads.CalculateRamCommitMiB(4096 * mib, 65536 * mib) == 3840,
                "Commit headroom can permit more than two GB while preserving 256 MB.");
            True(StressWorkloads.CalculateRamCommitMiB(65536 * mib, 4096 * mib) == 3840,
                "Available virtual address space independently limits the commit budget.");
            True(StressWorkloads.CalculateRamCommitMiB(256 * mib, ulong.MaxValue) == 0,
                "The entire commit reserve cannot be allocated.");
            True(StressWorkloads.CalculateRamCommitMiB(257 * mib, ulong.MaxValue) == 1,
                "The commit boundary is rounded down to full MB.");
            True(StressWorkloads.CalculateRamCommitMiB(ulong.MaxValue, ulong.MaxValue) == int.MaxValue,
                "Extreme commit and address values retain the type limit without overflow.");
            StressWorkloads.EnsureRamCommitHeadroom(272 * mib, ulong.MaxValue, 16 * mib);
            Throws<InvalidOperationException>(delegate { StressWorkloads.EnsureRamCommitHeadroom(271 * mib, ulong.MaxValue, 16 * mib); });
            Throws<InvalidOperationException>(delegate { StressWorkloads.EnsureRamCommitHeadroom(ulong.MaxValue, 271 * mib, 16 * mib); });
            Throws<InvalidOperationException>(delegate { StressWorkloads.EnsureRamCommitHeadroom(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue); });

            int guardCalls = 0, reports = 0;
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamCore(8192, 1, delegate { reports++; }, CancellationToken.None,
                    delegate { return ulong.MaxValue; }, 1024 * mib,
                    delegate(ulong bytes)
                    {
                        guardCalls++;
                        True(bytes == 16 * mib, "Even a multi-GB plan allocates only sixteen MB at a time.");
                        StressWorkloads.EnsureRamCommitHeadroom(256 * mib, ulong.MaxValue, bytes);
                    });
            });
            True(guardCalls == 1 && reports == 0, "The live commit guard runs before allocating the first block of a large plan.");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunRamCore(int.MaxValue, 1, null, stop.Token, delegate { return ulong.MaxValue; });
                });
            }
            int operations = 0;
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamBuffers(new[] { new uint[4] }, 1, null, CancellationToken.None,
                    delegate { return 6144 * mib; }, delegate { return 0; },
                    delegate { operations++; }, delegate { operations++; }, 8192 * mib);
            });
            True(operations == 0, "A scaled reserve is enforced during work, not replaced by the one GB minimum.");
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamCore(1, 1, null, CancellationToken.None,
                    delegate { return ulong.MaxValue; }, ulong.MaxValue);
            });
        }

        private static void RamLargeAllocationTelemetryUses64BitBytes()
        {
            const long mib = 1024L * 1024L;
            StressProgress allocating = StressWorkloads.CreateRamAllocationProgress(65536, 8192, 13108UL * (ulong)mib);
            True(allocating.RamTargetBytes == 65536 * mib && allocating.RamAllocatedBytes == 8192 * mib
                && allocating.RamCoveredBytes == 0 && allocating.Iterations == 0 && allocating.BytesProcessed == 0,
                "A 64 GB target with eight GB allocated retains exact long counts without claiming verification.");
            True(allocating.Detail.Contains("8192/65536 MB"), "Allocation progress distinguishes actual allocated memory from its target.");
            StressProgress largest = StressWorkloads.CreateRamAllocationProgress(int.MaxValue, int.MaxValue, 1024UL * (ulong)mib);
            True(largest.RamTargetBytes == (long)int.MaxValue * mib && largest.RamAllocatedBytes > int.MaxValue,
                "Even the largest representable MB plan does not overflow byte fields.");
            StressProgress partial = StressWorkloads.CreateRamProgress(65536 * mib, 32768 * mib, 0, 4096, 300000 * mib);
            True(partial.RamTargetBytes == 65536 * mib && partial.RamAllocatedBytes == 65536 * mib
                && partial.RamCoveredBytes == 32768 * mib && partial.Iterations == 0,
                "Partial coverage of a multi-GB pool is not confused with a completed cycle.");
            StressProgress complete = StressWorkloads.CreateRamProgress(65536 * mib, 65536 * mib, 1, 12288, 65536 * mib * 6);
            True(complete.Iterations == 1 && complete.RamCoveredBytes == complete.RamAllocatedBytes
                && complete.BytesProcessed == 412316860416L, "A complete 64 GB cycle reports all six memory passes without integer overflow.");
            Throws<ArgumentOutOfRangeException>(delegate { StressWorkloads.CreateRamProgress(4 * mib, 8 * mib, 0, 1, 0); });
        }

        private static void RamPreparationReportsAndCancelsLargePlan()
        {
            const long mib = 1024L * 1024L;
            var reports = new List<StressProgress>();
            int allocationGuards = 0;
            using (var stop = new CancellationTokenSource())
            {
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunRamCore(8192, 1, delegate(StressProgress progress)
                    {
                        reports.Add(progress);
                        stop.Cancel();
                    }, stop.Token, delegate { return ulong.MaxValue; }, 1024UL * (ulong)mib,
                    delegate(ulong bytes)
                    {
                        allocationGuards++;
                        True(bytes == 16UL * (ulong)mib, "A large request prepares one bounded array at a time.");
                    });
                });
            }
            True(allocationGuards == 1 && reports.Count == 1, "Cancellation from the first allocation report prevents the next allocation and all pattern work.");
            StressProgress first = reports[0];
            True(first.RamTargetBytes == 8192 * mib && first.RamAllocatedBytes == 16 * mib && first.RamCoveredBytes == 0,
                "The actual first preparation report distinguishes the eight GB plan from the sixteen MB allocated.");
            True(first.Iterations == 0 && first.BytesProcessed == 0 && first.Detail.Contains("подготовка RAM"),
                "Allocation is visible before verification starts and does not invent completed work.");
        }

        private static void RamPatternsDetectCorruptionAndRespectCancellation()
        {
            var buffer = new uint[8193];
            for (int pattern = 0; pattern < 3; pattern++)
            {
                StressWorkloads.FillRamPattern(buffer, pattern, 123, CancellationToken.None);
                StressWorkloads.VerifyRamPattern(buffer, pattern, 123, CancellationToken.None);
                buffer[4097] ^= 1U;
                int selected = pattern;
                Throws<InvalidOperationException>(delegate
                {
                    StressWorkloads.VerifyRamPattern(buffer, selected, 123, CancellationToken.None);
                });
            }
            StressWorkloads.FillRamPattern(buffer, 2, 123, CancellationToken.None);
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.VerifyRamPattern(buffer, 2, 124, CancellationToken.None);
            });
            True(buffer[0] != buffer[1] && buffer[4096] != buffer[4097], "Pseudo-random data varies by address.");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                uint before = buffer[0];
                Throws<OperationCanceledException>(delegate { StressWorkloads.FillRamPattern(buffer, 0, 0, stop.Token); });
                True(buffer[0] == before, "Pre-cancellation does not alter RAM.");
                Throws<OperationCanceledException>(delegate { StressWorkloads.VerifyRamPattern(buffer, 2, 123, stop.Token); });
            }
            Throws<ArgumentException>(delegate { StressWorkloads.VerifyRamPattern(new uint[0], 0, 0, CancellationToken.None); });
        }

        private static void RamAllocationRefusesLostReserve()
        {
            int reports = 0;
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamCore(16, 1, delegate { reports++; }, CancellationToken.None, delegate { return 0UL; });
            });
            True(reports == 0, "An unsafe allocation cannot report work or success.");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                int reads = 0;
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunRamCore(16, 1, null, stop.Token, delegate { reads++; return ulong.MaxValue; });
                });
                True(reads == 0, "Pre-cancellation avoids allocation and memory probing.");
            }
        }

        private static void RamPoolSweepsCompleteOnlyAfterAllPatterns()
        {
            var buffers = new[] { new uint[1024], new uint[1024], new uint[1024], new uint[1024] };
            var operations = new List<string>();
            var reports = new List<StressProgress>();
            double clock = 0;
            int reserveChecks = 0;
            StressWorkloads.RunRamBuffers(buffers, 3, reports.Add, CancellationToken.None,
                delegate { reserveChecks++; return ulong.MaxValue; }, delegate { return clock; },
                delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                {
                    operations.Add("fill" + pattern + ":" + Array.IndexOf(buffers, buffer));
                    StressWorkloads.FillRamPattern(buffer, pattern, seed, token);
                    clock += 0.125;
                },
                delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                {
                    operations.Add("verify" + pattern + ":" + Array.IndexOf(buffers, buffer));
                    StressWorkloads.VerifyRamPattern(buffer, pattern, seed, token);
                    clock += 0.125;
                });
            True(operations.Count == 24 && reserveChecks == 24, "Every fill and verification block checks the live memory reserve.");
            for (int pattern = 0; pattern < 3; pattern++)
            {
                for (int block = 0; block < buffers.Length; block++)
                {
                    True(operations[pattern * 8 + block] == "fill" + pattern + ":" + block,
                        "All blocks are written before verification for each pattern.");
                    True(operations[pattern * 8 + 4 + block] == "verify" + pattern + ":" + block,
                        "Every filled block is checked before changing the pattern.");
                }
            }
            StressProgress final = reports[reports.Count - 1];
            True(final.Iterations == 1, "Iterations counts one full three-pattern cycle, not twelve checked blocks.");
            True(final.RamTargetBytes == 16384 && final.RamAllocatedBytes == 16384 && final.RamCoveredBytes == 16384,
                "The real pool sweep fills the structured allocation and coverage fields.");
            True(final.BytesProcessed == 4L * 1024 * sizeof(uint) * 6 && final.WrittenBytes == 0,
                "RAM telemetry counts actual writes plus reads without inventing disk writes.");
            string allocationMiB = (16d / 1024).ToString("0.###");
            True(final.Detail.Contains(allocationMiB + "/" + allocationMiB + " MB") && final.Detail.Contains("блоков: 12")
                && final.Detail.Contains("часть RAM"), "Allocation, three-pattern coverage and verified block count are distinct.");
            True(reports[0].Iterations == 0 && reports[0].BytesProcessed < final.BytesProcessed,
                "Early partial reports are not mutated into full-cycle results.");
        }

        private static void RamLaterWritesCannotHideEarlierCorruption()
        {
            var buffers = new[] { new uint[32], new uint[32] };
            double clock = 0;
            int fills = 0, verifies = 0;
            Exception error = Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamBuffers(buffers, 1, null, CancellationToken.None,
                    delegate { return ulong.MaxValue; }, delegate { return clock; },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        fills++;
                        StressWorkloads.FillRamPattern(buffer, pattern, seed, token);
                        if (object.ReferenceEquals(buffer, buffers[1])) { buffers[0][7] ^= 1U; }
                        clock += 0.125;
                    },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        verifies++;
                        StressWorkloads.VerifyRamPattern(buffer, pattern, seed, token);
                        clock += 0.125;
                    });
            });
            True(fills == 2 && verifies == 1 && error.Message.Contains("Ошибка RAM") && error.Message.Contains("28 байт"),
                "Corruption of the first block while writing a later block is detected before another fill can hide it.");
        }

        private static void RamChecksMemoryPressureDuringWork()
        {
            var buffers = new[] { new uint[32], new uint[32] };
            double clock = 0;
            int fills = 0, verifies = 0, reserveChecks = 0;
            var reports = new List<StressProgress>();
            Exception error = Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamBuffers(buffers, 1, reports.Add, CancellationToken.None,
                    delegate { return ++reserveChecks <= 2 ? ulong.MaxValue : 1024UL * 1024 * 1024 - 1; },
                    delegate { return clock; },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        fills++;
                        StressWorkloads.FillRamPattern(buffer, pattern, seed, token);
                        clock += 0.125;
                    },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        verifies++;
                        StressWorkloads.VerifyRamPattern(buffer, pattern, seed, token);
                        clock += 0.125;
                    });
            });
            True(fills == 2 && verifies == 0 && reserveChecks == 3 && error.Message.Contains("резерв 1 GB"),
                "Memory pressure arising after writes stops verification before consuming the Windows reserve.");
            True(reports.TrueForAll(value => value.Iterations == 0), "A memory-pressure stop cannot publish a complete cycle.");
        }

        private static void RamCancellationInsideVerificationStopsTheSweep()
        {
            var buffers = new[] { new uint[8193], new uint[8193] };
            double clock = 0;
            int fills = 0, verifies = 0, lastCheckpoint = -1;
            using (var stop = new CancellationTokenSource())
            {
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunRamBuffers(buffers, 1, null, stop.Token,
                        delegate { return ulong.MaxValue; }, delegate { return clock; },
                        delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                        {
                            fills++;
                            StressWorkloads.FillRamPattern(buffer, pattern, seed, token);
                            clock += 0.125;
                        },
                        delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                        {
                            verifies++;
                            // This future mismatch must never be read after cancellation at word 4096.
                            buffer[5000] ^= 1U;
                            StressWorkloads.VerifyRamPattern(buffer, pattern, seed, token, delegate(int index)
                            {
                                lastCheckpoint = index;
                                if (index == 4096) { stop.Cancel(); }
                            });
                            clock += 0.125;
                        });
                });
            }
            True(fills == 2 && verifies == 1 && lastCheckpoint == 4096,
                "Cancellation inside the actual verification loop stops at the checkpoint before another block or mismatch is read.");
        }

        private static void RamIncompleteDeadlineCannotBecomeAFullCycle()
        {
            var buffers = new[] { new uint[1024], new uint[1024] };
            double clock = 0;
            int fills = 0, verifies = 0;
            var reports = new List<StressProgress>();
            Exception error = Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamBuffers(buffers, 1, reports.Add, CancellationToken.None,
                    delegate { return ulong.MaxValue; }, delegate { return clock; },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        fills++;
                        StressWorkloads.FillRamPattern(buffer, pattern, seed, token);
                        clock += 0.125;
                    },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        verifies++;
                        StressWorkloads.VerifyRamPattern(buffer, pattern, seed, token);
                        clock += 0.125;
                    });
            });
            StressProgress last = reports[reports.Count - 1];
            True(fills == 4 && verifies == 4 && clock == 1 && last.BytesProcessed > 0 && last.Iterations == 0,
                "Two verified patterns are useful work but cannot be counted as a full three-pattern cycle.");
            True(last.RamTargetBytes == 8192 && last.RamAllocatedBytes == 8192 && last.RamCoveredBytes == 0,
                "An incomplete sweep keeps its allocated bytes while leaving three-pattern coverage at zero.");
            True(error.Message.Contains("не завершил полный цикл") && last.Detail.Contains("3 шаблона: 0/"),
                "The incomplete deadline explains missing coverage instead of fabricating completion.");

            // A deadline reached during whole-pool fill must not extend the pass to verify it.
            clock = 0; fills = verifies = 0;
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunRamBuffers(buffers, 1, null, CancellationToken.None,
                    delegate { return ulong.MaxValue; }, delegate { return clock; },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token)
                    {
                        fills++; StressWorkloads.FillRamPattern(buffer, pattern, seed, token); clock = 1;
                    },
                    delegate(uint[] buffer, int pattern, uint seed, CancellationToken token) { verifies++; });
            });
            True(fills == 1 && verifies == 0, "Expiry between blocks does not extend a partially filled pool beyond the deadline.");
        }

        private static void GpuFramesAreValidatedAndDisposed()
        {
            var renderer = new StressFakeRenderer();
            var reports = new List<StressProgress>();
            StressWorkloads.RunGpuCore(Options(), reports.Add, CancellationToken.None,
                delegate { return renderer; }, delegate { return renderer.Clock; });
            StressProgress final = reports[reports.Count - 1];
            True(renderer.Disposed && final.Component == StressComponent.Gpu && final.Iterations >= 10,
                "GPU reports completed validated frames and disposes the renderer.");
            True(final.Detail.Contains("FPS") && final.BytesProcessed == 0 && final.WrittenBytes == 0,
                "GPU throughput is timestamp based and does not invent VRAM coverage or disk writes.");
            True(reports[0].Iterations < final.Iterations, "Progress reports are independent snapshots.");
            renderer = new StressFakeRenderer();
            renderer.OnFrame = delegate(int frame) { renderer.Disjoint = frame == 3; };
            reports.Clear();
            StressWorkloads.RunGpuCore(Options(), reports.Add, CancellationToken.None,
                delegate { return renderer; }, delegate { return renderer.Clock; });
            True(reports[reports.Count - 1].Detail.Contains("пропущено: 1"), "Rejected GPU timestamps remain visible.");
        }

        private static void GpuPreconditionsAvoidDeviceCreation()
        {
            int creates = 0;
            Func<IGpuFrameRenderer> factory = delegate { creates++; return new StressFakeRenderer(); };
            var options = Options();
            options.Gpu = new GpuSnapshot();
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunGpuCore(options, null, CancellationToken.None, factory, delegate { return 0; });
            });
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunGpuCore(Options(), null, stop.Token, factory, delegate { return 0; });
                });
            }
            options = Options();
            options.DurationSeconds = 901;
            Throws<ArgumentOutOfRangeException>(delegate
            {
                StressWorkloads.RunGpuCore(options, null, CancellationToken.None, factory, delegate { return 0; });
            });
            True(creates == 0, "Missing adapter, cancellation and invalid duration do not initialize hardware.");
        }

        private static void GpuFailureAndCancellationDisposeResources()
        {
            var renderer = new StressFakeRenderer();
            renderer.OnFrame = delegate(int frame) { if (frame == 3) { throw new InvalidOperationException("Device removed fixture"); } };
            Exception failure = Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunGpuCore(Options(), null, CancellationToken.None,
                    delegate { return renderer; }, delegate { return renderer.Clock; });
            });
            True(renderer.Disposed && failure.Message.Contains("Device removed"), "Device loss remains a failure after disposing the renderer.");
            using (var stop = new CancellationTokenSource())
            {
                renderer = new StressFakeRenderer();
                renderer.OnFrame = delegate(int frame) { if (frame == 3) { stop.Cancel(); } };
                Throws<OperationCanceledException>(delegate
                {
                    StressWorkloads.RunGpuCore(Options(), null, stop.Token,
                        delegate { return renderer; }, delegate { return renderer.Clock; });
                });
                True(renderer.Disposed && renderer.Frames == 3, "GPU cancellation stops submitting frames immediately.");
            }
            renderer = new StressFakeRenderer();
            Throws<ApplicationException>(delegate
            {
                StressWorkloads.RunGpuCore(Options(), delegate { throw new ApplicationException("Progress fixture"); },
                    CancellationToken.None, delegate { return renderer; }, delegate { return renderer.Clock; });
            });
            True(renderer.Disposed, "Progress callback failure also releases GPU resources.");
        }

        private static void GpuInvalidClocksAndCleanupCannotPass()
        {
            var renderer = new StressFakeRenderer { Disjoint = true, FrameSeconds = 0.001 };
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunGpuCore(Options(), null, CancellationToken.None,
                    delegate { return renderer; }, delegate { return renderer.Clock; });
            });
            True(renderer.Disposed && renderer.Frames == 32, "A stream of invalid timestamps fails early, without a fabricated result.");
            renderer = new StressFakeRenderer { FrameSeconds = 2 };
            Throws<InvalidOperationException>(delegate
            {
                StressWorkloads.RunGpuCore(Options(), null, CancellationToken.None,
                    delegate { return renderer; }, delegate { return renderer.Clock; });
            });
            True(renderer.Disposed && renderer.Frames == 1, "Insufficient validated work cannot pass.");
            renderer = new StressFakeRenderer { FailDispose = true };
            Exception failure = Throws<ApplicationException>(delegate
            {
                StressWorkloads.RunGpuCore(Options(), null, CancellationToken.None,
                    delegate { return renderer; }, delegate { return renderer.Clock; });
            });
            True(renderer.Disposed && failure.Message.Contains("Dispose fixture"), "GPU cleanup failure reaches the session before success.");
        }

        private sealed class StressFakeRenderer : IGpuFrameRenderer
        {
            public double Clock;
            public double FrameSeconds = 0.05;
            public int Frames;
            public bool Disposed;
            public bool Disjoint;
            public bool FailDispose;
            public Action<int> OnFrame;

            public GpuFrameSample Render(float elapsedSeconds, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Frames++;
                Clock += FrameSeconds;
                if (OnFrame != null) { OnFrame(Frames); }
                return new GpuFrameSample(100, 110, 1000, Disjoint);
            }

            public void Dispose()
            {
                Disposed = true;
                if (FailDispose) { throw new ApplicationException("Dispose fixture"); }
            }
        }

        private static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T exception) { return exception; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private static void True(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
        }
    }
}
