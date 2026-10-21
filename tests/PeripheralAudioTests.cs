using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class PeripheralAudioTests
    {
        public static int Main()
        {
            try { Run(); Console.WriteLine("PASS: peripheral audio; fake WinMM only, no hardware opened"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        public static void Run()
        {
            StructuresAndSignals();
            EnumerationDoesNotOpen();
            CaptureAndFallback();
            PhysicalSignalsAndRouting();
            FailureCleanup();
            CancellationAndExclusion();
            NonblockingDisposeAndNativeOwnership();
            PlaybackAndClearing();
            Require(PeripheralWaveBuffer.LiveAllocations == 0, "No audio buffer allocation remains after the test groups");
        }
        private static void StructuresAndSignals()
        {
            Require(Marshal.SizeOf(typeof(PeripheralWaveFormat)) == 18, "WAVEFORMATEX PCM layout is 18 bytes");
            Require(PeripheralWaveBuffer.HeaderSize == (IntPtr.Size == 8 ? 48 : 32), "WAVEHDR follows native pointer alignment");
            AudioLevel level = PeripheralAudio.MeasureLevel(new byte[] { 0, 128, 255, 127, 0, 0, 0, 0 }, 8);
            Require(level.Peak == 1 && level.Rms > .707 && level.Rms < .708, "RMS/peak include negative full-scale without short overflow");
            Require(PeripheralAudio.MeasureLevel(new byte[0], 0).Rms == 0, "Empty buffer has a zero level");
            foreach (int channel in new[] { -1, 0, 1 })
            {
                byte[] tone = PeripheralAudio.GenerateTone(44100, channel);
                Require(tone.Length == 44100 * 4 * (channel == -1 ? 3 : .75), "Tone duration is bounded");
                Require(PeripheralAudio.MeasureLevel(tone, tone.Length).Peak <= .101, "Tone amplitude never exceeds conservative 10 percent");
                int left = 0, right = 0;
                for (int frame = 0; frame < tone.Length / 4; frame++)
                {
                    short l = (short)(tone[frame * 4] | tone[frame * 4 + 1] << 8);
                    short r = (short)(tone[frame * 4 + 2] | tone[frame * 4 + 3] << 8);
                    if (l != 0) { left++; }
                    if (r != 0) { right++; }
                    Require(l == 0 || r == 0, "Left and right tones never sound together");
                    if (channel == -1)
                    {
                        int segment = frame / (int)(44100 * .75);
                        Require(segment % 2 == 0 ? r == 0 : l == 0, "Alternation preserves left/right ordering");
                    }
                }
                Require(channel == 0 ? left > 0 && right == 0 : channel == 1 ? right > 0 && left == 0 : left > 0 && right > 0, "Selected channels contain the expected signal");
                Require(tone[0] == 0 && tone[1] == 0 && tone[tone.Length - 1] == 0, "Fade/silence boundaries avoid abrupt starts and ends");
            }
            Console.WriteLine("PASS: PCM layout, RMS/peak, bounded faded stereo channel tones");
        }
        private static void EnumerationDoesNotOpen()
        {
            var api = new FakeWaveApi();
            List<AudioDevice> devices = PeripheralAudio.Enumerate(api, true);
            Require(devices.Count == 3 && devices[0].Id == -1 && devices[1].Name == "Device 0" && devices[2].Id == 1, "Default mapper and device names are enumerated");
            using (var audio = new PeripheralAudio(api)) { Require(audio.Completion.IsCompleted, "Idle construction has no running worker"); }
            Require(api.Opens.Count == 0 && api.StartCalls == 0, "Construction and enumeration never activate recording/playback");
            api.Devices = 0;
            Require(PeripheralAudio.Enumerate(api, true).Count == 0 && PeripheralAudio.Enumerate(api, false).Count == 0,
                "An absent device does not leave a misleading default option");
            Console.WriteLine("PASS: enumeration and construction do not open an audio device");
        }
        private static void CaptureAndFallback()
        {
            foreach (bool mono in new[] { false, true })
            {
                var api = new FakeWaveApi { MonoInputOnly = mono };
                int callbacks = 0;
                bool validLevels = true;
                using (var audio = new PeripheralAudio(api))
                using (AudioRecording recording = audio.RecordMicrophoneAsync(-1, delegate(AudioLevel level)
                {
                    callbacks++;
                    validLevels &= level.ElapsedSeconds > 0 && level.ElapsedSeconds <= 5 && level.Peak > 0 && level.Peak <= 1;
                }, CancellationToken.None).GetAwaiter().GetResult())
                {
                    Require(recording.SampleRate == 44100 && recording.Channels == (mono ? 1 : 2), "Selected format metadata survives negotiation");
                    Require(recording.DurationSeconds == 5 && recording.ByteCount == 44100 * recording.Channels * 2 * 5, "Exactly five seconds of bounded PCM are retained");
                    Require(callbacks == 100, "Live levels arrive per 50 millisecond chunk");
                    Require(validLevels, "Live levels are bounded even though the engine isolates UI callback exceptions");
                    Require(api.Opens.Count == (mono ? 3 : 1), "Only unsupported formats trigger the bounded fallback list");
                }
                Clean(api);
            }
            foreach (int speechRate in new[] { 16000, 8000 })
            {
                var api = new FakeWaveApi { MonoInputOnly = true, InputSampleRate = speechRate };
                using (var audio = new PeripheralAudio(api))
                using (AudioRecording recording = audio.RecordMicrophoneAsync(-1, null, CancellationToken.None).GetAwaiter().GetResult())
                {
                    Require(recording.SampleRate == speechRate && recording.Channels == 1 && recording.DurationSeconds == 5,
                        "Speech-only input preserves the negotiated mono rate and five-second bound");
                    Require(api.Opens.Count == (speechRate == 16000 ? 6 : 7), "Speech rates are attempted only after higher-quality formats are unsupported");
                }
                Clean(api);
            }
            var callbackApi = new FakeWaveApi();
            using (var audio = new PeripheralAudio(callbackApi))
            using (AudioRecording recording = audio.RecordMicrophoneAsync(-1, delegate { throw new InvalidOperationException("UI closed"); }, CancellationToken.None).GetAwaiter().GetResult())
                Require(recording.DurationSeconds == 5, "A failed UI callback does not leak the microphone");
            Clean(callbackApi);
            Console.WriteLine("PASS: five-second capture, live levels, mono/16 kHz/8 kHz fallback and closed-UI callbacks");
        }
        private static void FailureCleanup()
        {
            foreach (string point in new[] { "open", "prepare", "queue", "start", "size", "partial-open", "zero-handle" })
            {
                var api = new FakeWaveApi
                {
                    OpenError = point == "open" || point == "partial-open" ? 4u : 0u,
                    FailedOpenReturnsHandle = point == "partial-open", SuccessReturnsZeroHandle = point == "zero-handle",
                    FailPrepareAt = point == "prepare" ? 2 : 0, FailQueueAt = point == "queue" ? 2 : 0,
                    StartError = point == "start" ? 1u : 0u, InvalidRecordedSize = point == "size"
                };
                using (var audio = new PeripheralAudio(api))
                {
                    Expect<InvalidOperationException>(delegate { audio.RecordMicrophoneAsync(-1, null, CancellationToken.None).GetAwaiter().GetResult(); });
                }
                Require(PeripheralWaveBuffer.LiveAllocations == 0, "Failure at " + point + " releases every native buffer");
                Require(!api.HandleOpen, "Failure at " + point + " closes any partially opened handle");
                if (point == "open") { Require(api.Opens.Count == 1, "Busy devices must not be retried as format negotiation"); }
            }
            var retryApi = new FakeWaveApi { UnprepareBusyCount = 2, CloseBusyCount = 2 };
            using (var audio = new PeripheralAudio(retryApi)) { audio.TestChannelAsync(-1, 0, CancellationToken.None).GetAwaiter().GetResult(); }
            Clean(retryApi);
            Require(retryApi.ResetCalls >= 3, "Transient still-playing cleanup errors retry reset before releasing native storage");
            Console.WriteLine("PASS: open/prepare/queue/start/corrupt-buffer faults and transient cleanup errors release native ownership");
        }
        private static void PhysicalSignalsAndRouting()
        {
            foreach (bool silent in new[] { true, false })
            {
                byte[] frame = silent ? new byte[] { 0, 0, 0, 0 } : new byte[] { 0, 0, 0, 64 };
                var api = new FakeWaveApi { InputPattern = frame };
                double peak = -1, rms = -1;
                using (var audio = new PeripheralAudio(api))
                using (AudioRecording recording = audio.RecordMicrophoneAsync(-1, value => { peak = value.Peak; rms = value.Rms; }, CancellationToken.None).GetAwaiter().GetResult())
                {
                    byte[] expected = recording.Data;
                    Require(expected.Where((value, index) => value != frame[index % frame.Length]).Count() == 0,
                        "Capture preserves signed little-endian interleaved PCM, including silent channels");
                    Require(silent ? peak == 0 && rms == 0 : peak == .5 && Math.Abs(rms - Math.Sqrt(.125)) < .0000001,
                        "Silence stays zero; a right-only signal has the correct peak and RMS across channels");
                    audio.PlaybackAsync(recording, 1, CancellationToken.None).GetAwaiter().GetResult();
                    Require(expected.SequenceEqual(api.WrittenOutput), "Microphone playback preserves every captured PCM byte");
                    Require(api.OpenIds.SequenceEqual(new uint[] { UInt32.MaxValue, 1 })
                        && api.InputDirections.SequenceEqual(new[] { true, false }),
                        "Default input uses WAVE_MAPPER while playback uses the explicitly selected output");
                    Require(api.Opens[0].SamplesPerSecond == api.Opens[1].SamplesPerSecond
                        && api.Opens[0].Channels == api.Opens[1].Channels, "Playback retains captured format metadata");
                }
                Clean(api);
            }
            foreach (int outputRate in new[] { 48000, 22050 })
            {
                var api = new FakeWaveApi { OutputSampleRate = outputRate };
                using (var audio = new PeripheralAudio(api)) { audio.TestChannelAsync(0, 1, CancellationToken.None).GetAwaiter().GetResult(); }
                Require(api.Opens.Count == (outputRate == 48000 ? 2 : 3) && api.Opens.Last().SamplesPerSecond == outputRate
                    && api.Opens.Last().Channels == 2 && api.OpenIds.All(id => id == 0),
                    "Speaker fallback keeps stereo channels and the explicitly selected output");
                for (int index = 0; index < api.WrittenOutput.Length; index += 4)
                    Require(api.WrittenOutput[index] == 0 && api.WrittenOutput[index + 1] == 0, "Right tone remains isolated after sample-rate negotiation");
                Clean(api);
            }
            Console.WriteLine("PASS: silent/right-only capture, byte-exact playback, default/explicit device mapping and stereo rate fallback");
        }
        private static void CancellationAndExclusion()
        {
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var api = new FakeWaveApi();
            using (var audio = new PeripheralAudio(api))
            {
                Task<AudioRecording> task = audio.RecordMicrophoneAsync(-1, null, cancelled.Token);
                Expect<OperationCanceledException>(delegate { task.GetAwaiter().GetResult(); });
                Require(task.IsCanceled && api.Opens.Count == 0, "Pre-cancellation opens no device and returns a cancelled task");
            }
            cancelled.Dispose();
            api = new FakeWaveApi { CompleteInput = false };
            using (var audio = new PeripheralAudio(api))
            using (var recording = new AudioRecording(new byte[400], 44100, 2))
            {
                Task<AudioRecording> task = audio.RecordMicrophoneAsync(-1, null, CancellationToken.None);
                Require(api.Started.WaitOne(2000), "Fake microphone worker started");
                Expect<InvalidOperationException>(delegate { audio.PlaybackAsync(recording, -1, CancellationToken.None); });
                Require(api.Opens.Count == 1, "No feedback path is opened concurrently with recording");
                audio.Stop(); audio.Stop();
                Expect<OperationCanceledException>(delegate { task.GetAwaiter().GetResult(); });
                Require(task.IsCanceled, "Stop cancels the task only after cleanup");
            }
            Clean(api);
            api = new FakeWaveApi();
            using (var audio = new PeripheralAudio(api))
            {
                Task<AudioRecording> selfDisposal = audio.RecordMicrophoneAsync(-1, delegate { audio.Dispose(); }, CancellationToken.None);
                Require(Task.WhenAny(selfDisposal, Task.Delay(2000)).GetAwaiter().GetResult() == selfDisposal, "Disposal from a level callback cannot deadlock its own worker");
                Expect<OperationCanceledException>(delegate { selfDisposal.GetAwaiter().GetResult(); });
                Expect<ObjectDisposedException>(delegate { audio.TestSpeakersAsync(-1, CancellationToken.None); });
            }
            Clean(api);
            Console.WriteLine("PASS: pre-cancel, Stop, repeated Stop, feedback exclusion and callback disposal");
        }
        private static void PlaybackAndClearing()
        {
            using (var cancelled = new CancellationTokenSource())
            using (var cancelledRecording = new AudioRecording(new byte[] {1,2,3,4}, 44100, 2))
            {
                cancelled.Cancel();
                var cancelledApi = new FakeWaveApi();
                using (var audio = new PeripheralAudio(cancelledApi))
                {
                    Task playback = audio.PlaybackAsync(cancelledRecording, -1, cancelled.Token);
                    Expect<OperationCanceledException>(delegate { playback.GetAwaiter().GetResult(); });
                    Require(playback.IsCanceled && cancelledApi.Opens.Count == 0, "Pre-cancelled playback never opens a device");
                    byte[] owned = { 9, 8, 7, 6 };
                    bool operationRan = false, cleanupRan = false;
                    var start = typeof(PeripheralAudio).GetMethod("Start", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).MakeGenericMethod(typeof(bool));
                    Task cleanup = (Task)start.Invoke(audio, new object[] {
                        new Func<CancellationToken, bool>(token => { operationRan = true; return true; }), cancelled.Token,
                        new Action(delegate { Array.Clear(owned, 0, owned.Length); cleanupRan = true; }) });
                    Expect<OperationCanceledException>(delegate { cleanup.GetAwaiter().GetResult(); });
                    Require(!operationRan && cleanupRan && owned.All(value => value == 0),
                        "Worker cleanup clears already-owned PCM even when pre-cancellation skips the playback delegate");
                }
            }
            var api = new FakeWaveApi { CompleteOutput = false };
            var recording = new AudioRecording(new byte[] {1,2,3,4,5,6,7,8}, 44100, 2);
            using (var audio = new PeripheralAudio(api))
            {
                Task playing = audio.PlaybackAsync(recording, -1, CancellationToken.None);
                recording.Dispose();
                Require(api.Queued.WaitOne(2000), "Fake speaker buffer was queued");
                Require(api.WrittenOutput.SequenceEqual(new byte[] {1,2,3,4,5,6,7,8}), "Playback owns a copy independent of recording disposal");
                Require(recording.ByteCount == 0, "Disposed recording clears retained PCM");
                Expect<ObjectDisposedException>(delegate { byte[] ignored = recording.Data; });
                audio.Dispose();
                Expect<OperationCanceledException>(delegate { playing.GetAwaiter().GetResult(); });
            }
            Clean(api);
            var toneApi = new FakeWaveApi();
            using (var audio = new PeripheralAudio(toneApi)) { audio.TestSpeakersAsync(-1, CancellationToken.None).GetAwaiter().GetResult(); }
            Require(toneApi.Opens.Count == 1 && toneApi.Opens[0].Channels == 2, "Speaker channel test opens a stereo format");
            Clean(toneApi);
            Expect<ArgumentException>(delegate { new AudioRecording(new byte[3], 44100, 2); });
            Expect<ArgumentException>(delegate { new AudioRecording(new byte[44100 * 4 * 6], 44100, 2); });
            Console.WriteLine("PASS: playback cancellation, defensive recording copies, RAM clearing and bounded input validation");
        }
        private static void NonblockingDisposeAndNativeOwnership()
        {
            var api = new FakeWaveApi { OpenGate = new ManualResetEvent(false) };
            var audio = new PeripheralAudio(api);
            Task<AudioRecording> capture = audio.RecordMicrophoneAsync(-1, null, CancellationToken.None);
            Require(api.OpenEntered.WaitOne(2000), "The simulated native open call is blocked");
            Stopwatch timer = Stopwatch.StartNew(); audio.Dispose(); audio.Dispose(); timer.Stop();
            Require(timer.ElapsedMilliseconds < 250 && !audio.Completion.IsCompleted,
                "Dispose cannot block the UI on a native call and must not fake completion");
            api.OpenGate.Set();
            Expect<OperationCanceledException>(delegate { capture.GetAwaiter().GetResult(); });
            Require(api.StartCalls == 0, "A delayed native open cannot start microphone capture after cancellation");
            Clean(api);

            api = new FakeWaveApi { BlockClose = true };
            audio = new PeripheralAudio(api);
            Task playing = audio.TestChannelAsync(-1, 0, CancellationToken.None);
            Require(api.CloseEntered.WaitOne(2000), "Cleanup reached the simulated busy driver");
            timer.Restart(); audio.Dispose(); timer.Stop();
            Require(timer.ElapsedMilliseconds < 250 && !playing.IsCompleted && PeripheralWaveBuffer.LiveAllocations > 0,
                "Native storage and completion are retained while the driver refuses close");
            api.BlockClose = false;
            Expect<OperationCanceledException>(delegate { playing.GetAwaiter().GetResult(); });
            Clean(api);
            Console.WriteLine("PASS: nonblocking Dispose, delayed native calls and driver-owned buffer lifetime");
        }
        private static void Clean(FakeWaveApi api)
        {
            Require(!api.HandleOpen && api.CloseCalls > 0 && api.ResetCalls > 0, "Native device is reset and closed");
            Require(PeripheralWaveBuffer.LiveAllocations == 0, "All native buffers are released after close");
            Require(!api.BufferFreedBeforeClose, "Native storage remains alive until the driver closes");
        }
        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

        private sealed class FakeWaveApi : IPeripheralWaveApi
        {
            internal readonly List<PeripheralWaveFormat> Opens = new List<PeripheralWaveFormat>();
            internal readonly List<uint> OpenIds = new List<uint>();
            internal readonly List<bool> InputDirections = new List<bool>();
            internal readonly ManualResetEvent Started = new ManualResetEvent(false), Queued = new ManualResetEvent(false), OpenEntered = new ManualResetEvent(false), CloseEntered = new ManualResetEvent(false);
            internal ManualResetEvent OpenGate;
            internal volatile bool BlockClose;
            internal uint Devices = 2;
            internal bool MonoInputOnly, FailedOpenReturnsHandle, SuccessReturnsZeroHandle, InvalidRecordedSize;
            internal bool CompleteInput = true, CompleteOutput = true, HandleOpen, BufferFreedBeforeClose;
            internal uint OpenError, StartError;
            internal int InputSampleRate, OutputSampleRate;
            internal int FailPrepareAt, FailQueueAt, UnprepareBusyCount, CloseBusyCount, PrepareCalls, QueueCalls, StartCalls, ResetCalls, CloseCalls;
            internal byte[] WrittenOutput;
            internal byte[] InputPattern;
            private readonly HashSet<IntPtr> headers = new HashSet<IntPtr>();
            public uint DeviceCount(bool input) { return Devices; }
            public uint DeviceName(bool input, uint id, out string name) { name = "Device " + id; return 0; }
            public uint Open(bool input, uint id, ref PeripheralWaveFormat format, out IntPtr handle)
            {
                Opens.Add(format);
                OpenIds.Add(id); InputDirections.Add(input);
                OpenEntered.Set();
                if (OpenGate != null) { OpenGate.WaitOne(); }
                uint error = OpenError != 0 ? OpenError : input && ((MonoInputOnly && format.Channels == 2)
                    || (InputSampleRate != 0 && format.SamplesPerSecond != InputSampleRate))
                    || !input && OutputSampleRate != 0 && format.SamplesPerSecond != OutputSampleRate ? 32u : 0u;
                handle = (error == 0 || FailedOpenReturnsHandle) && !SuccessReturnsZeroHandle ? new IntPtr(123) : IntPtr.Zero;
                HandleOpen = handle != IntPtr.Zero;
                return error;
            }
            public uint Prepare(bool input, IntPtr handle, IntPtr header)
            {
                PrepareCalls++;
                if (FailPrepareAt == PrepareCalls) { return 1; }
                headers.Add(header);
                PeripheralWaveHeader state = Read(header); state.Flags = 2; Write(header, state);
                return 0;
            }
            public uint Queue(bool input, IntPtr handle, IntPtr header)
            {
                QueueCalls++;
                if (FailQueueAt == QueueCalls) { return 1; }
                PeripheralWaveHeader state = Read(header); state.Flags = 2 | 16; state.BytesRecorded = 0;
                if (input && CompleteInput)
                {
                    byte[] bytes = new byte[state.BufferLength];
                    for (int i = 0; i < bytes.Length; i += 2) { bytes[i] = 0; bytes[i + 1] = 32; }
                    if (InputPattern != null) { for (int i = 0; i < bytes.Length; i++) { bytes[i] = InputPattern[i % InputPattern.Length]; } }
                    Marshal.Copy(bytes, 0, state.Data, bytes.Length);
                    state.BytesRecorded = InvalidRecordedSize ? state.BufferLength + 4 : state.BufferLength;
                    state.Flags = 2 | 1;
                }
                if (!input)
                {
                    WrittenOutput = new byte[state.BufferLength]; Marshal.Copy(state.Data, WrittenOutput, 0, WrittenOutput.Length);
                    if (CompleteOutput) { state.Flags = 2 | 1; }
                }
                Write(header, state); Queued.Set(); return 0;
            }
            public uint StartInput(IntPtr handle) { StartCalls++; Started.Set(); return StartError; }
            public uint Reset(bool input, IntPtr handle)
            {
                ResetCalls++;
                foreach (IntPtr header in headers) { PeripheralWaveHeader state = Read(header); state.Flags = 2 | 1; Write(header, state); }
                return 0;
            }
            public uint Unprepare(bool input, IntPtr handle, IntPtr header)
            {
                if (UnprepareBusyCount > 0) { UnprepareBusyCount--; return 33; }
                headers.Remove(header); PeripheralWaveHeader state = Read(header); state.Flags &= ~2u; Write(header, state); return 0;
            }
            public uint Close(bool input, IntPtr handle)
            {
                CloseCalls++;
                CloseEntered.Set();
                if (PrepareCalls > 0 && PeripheralWaveBuffer.LiveAllocations == 0) { BufferFreedBeforeClose = true; }
                if (BlockClose) { return 33; }
                if (CloseBusyCount > 0) { CloseBusyCount--; return 33; }
                HandleOpen = false; headers.Clear(); return 0;
            }
            public string ErrorText(bool input, uint code) { return "simulated error"; }
            private static PeripheralWaveHeader Read(IntPtr ptr) { return (PeripheralWaveHeader)Marshal.PtrToStructure(ptr, typeof(PeripheralWaveHeader)); }
            private static void Write(IntPtr ptr, PeripheralWaveHeader header) { Marshal.StructureToPtr(header, ptr, false); }
        }
    }
}
