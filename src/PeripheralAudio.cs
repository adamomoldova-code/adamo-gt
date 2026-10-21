using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcPulse
{
    public sealed class AudioDevice
    {
        public int Id { get; private set; }
        public string Name { get; private set; }
        public AudioDevice(int id, string name) { Id = id; Name = name ?? ""; }
        public override string ToString() { return Name; }
    }

    public sealed class AudioLevel
    {
        public double Rms { get; internal set; }
        public double Peak { get; internal set; }
        public double ElapsedSeconds { get; internal set; }
    }

    // PCM is held only in managed memory. Dispose clears the retained recording.
    public sealed class AudioRecording : IDisposable
    {
        private readonly object gate = new object();
        private byte[] data;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get { return 16; } }
        public int ByteCount { get { lock (gate) { return data == null ? 0 : data.Length; } } }
        public double DurationSeconds { get { return (double)ByteCount / (SampleRate * Channels * 2); } }
        public byte[] Data { get { return CopyData(); } }
        public AudioRecording(byte[] pcmData, int sampleRate, int channels)
        {
            Validate(pcmData, sampleRate, channels);
            data = (byte[])pcmData.Clone(); SampleRate = sampleRate; Channels = channels;
        }
        internal static void Validate(byte[] bytes, int sampleRate, int channels)
        {
            if (sampleRate < 8000 || sampleRate > 48000 || (channels != 1 && channels != 2))
                throw new ArgumentException("Поддерживается PCM 16 бит, 8–48 кГц, один или два канала.");
            if (bytes == null || bytes.Length == 0 || bytes.Length % (channels * 2) != 0 || bytes.Length > sampleRate * channels * 2 * 5)
                throw new ArgumentException("Запись должна содержать целые PCM-кадры длительностью не более 5 секунд.");
        }
        internal byte[] CopyData()
        {
            lock (gate)
            {
                if (data == null) { throw new ObjectDisposedException("AudioRecording"); }
                return (byte[])data.Clone();
            }
        }
        public void Dispose() { lock (gate) { if (data != null) { Array.Clear(data, 0, data.Length); data = null; } } }
    }

    // Creating this object never opens a device. UI buttons explicitly start each operation.
    // Callbacks run on the worker thread; UI callers must dispatch without blocking it.
    public sealed class PeripheralAudio : IDisposable
    {
        private readonly object gate = new object();
        private readonly IPeripheralWaveApi api;
        private CancellationTokenSource activeCancellation;
        private Task activeTask;
        private bool disposed;
        public PeripheralAudio() : this(new PeripheralWaveApi()) { }
        internal PeripheralAudio(IPeripheralWaveApi waveApi) { api = waveApi ?? throwArgument(); }
        private static IPeripheralWaveApi throwArgument() { throw new ArgumentNullException("waveApi"); }
        public Task Completion { get { lock (gate) { return activeTask ?? Task.FromResult(0); } } }
        public static List<AudioDevice> GetInputDevices() { return Enumerate(new PeripheralWaveApi(), true); }
        public static List<AudioDevice> GetOutputDevices() { return Enumerate(new PeripheralWaveApi(), false); }
        internal static List<AudioDevice> Enumerate(IPeripheralWaveApi waveApi, bool input)
        {
            uint count = waveApi.DeviceCount(input);
            var result = new List<AudioDevice>();
            if (count == 0) { return result; }
            result.Add(new AudioDevice(-1, "По умолчанию Windows"));
            for (uint index = 0; index < count && index < 1024; index++)
            {
                string name;
                if (waveApi.DeviceName(input, index, out name) == 0)
                    result.Add(new AudioDevice((int)index, String.IsNullOrWhiteSpace(name) ? "Аудиоустройство " + (index + 1) : name));
            }
            return result;
        }

        public Task<AudioRecording> RecordMicrophoneAsync(int deviceId, Action<AudioLevel> callback, CancellationToken token)
        {
            ValidateDevice(deviceId);
            return Start(delegate(CancellationToken cancellation) { return Capture(deviceId, callback, cancellation); }, token);
        }
        public Task PlaybackAsync(AudioRecording recording, int outputDeviceId, CancellationToken token)
        {
            if (recording == null) { throw new ArgumentNullException("recording"); }
            ValidateDevice(outputDeviceId);
            // Copy before scheduling so clearing/replacing the UI recording cannot alter playback.
            byte[] bytes = recording.CopyData();
            try
            {
                return Start(delegate(CancellationToken cancellation)
                {
                    Play(outputDeviceId, PeripheralWaveFormat.Pcm(recording.SampleRate, recording.Channels), bytes, cancellation); return true;
                }, token, delegate { Array.Clear(bytes, 0, bytes.Length); });
            }
            catch { Array.Clear(bytes, 0, bytes.Length); throw; }
        }
        public Task TestSpeakersAsync(int outputDeviceId, CancellationToken token)
        {
            ValidateDevice(outputDeviceId);
            return Start(delegate(CancellationToken cancellation) { PlayTone(outputDeviceId, -1, cancellation); return true; }, token);
        }
        public Task TestChannelAsync(int outputDeviceId, int channel, CancellationToken token)
        {
            ValidateDevice(outputDeviceId);
            if (channel != 0 && channel != 1) { throw new ArgumentOutOfRangeException("channel"); }
            return Start(delegate(CancellationToken cancellation) { PlayTone(outputDeviceId, channel, cancellation); return true; }, token);
        }
        public void Stop()
        {
            CancellationTokenSource cancellation;
            lock (gate) { cancellation = activeCancellation; }
            Cancel(cancellation);
        }
        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (gate) { disposed = true; cancellation = activeCancellation; }
            Cancel(cancellation);
            // Cancellation is nonblocking, including from a level callback. Await Completion
            // to observe native cleanup; a window can bound that await if a driver hangs.
        }
        private static void Cancel(CancellationTokenSource cancellation)
        {
            if (cancellation != null) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        }
        private Task<T> Start<T>(Func<CancellationToken, T> operation, CancellationToken token, Action cleanup = null)
        {
            lock (gate)
            {
                if (disposed) { throw new ObjectDisposedException("PeripheralAudio"); }
                if (activeTask != null && !activeTask.IsCompleted) { throw new InvalidOperationException("Сначала остановите текущую проверку звука."); }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                activeCancellation = cancellation;
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                activeTask = completion.Task;
                try { Task.Factory.StartNew(delegate
                {
                    T result = default(T);
                    Exception failure = null;
                    bool cancelled = false;
                    try { cancellation.Token.ThrowIfCancellationRequested(); result = operation(cancellation.Token); cancellation.Token.ThrowIfCancellationRequested(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch (Exception error) { failure = error; }
                    finally
                    {
                        // Also runs if pre-cancellation skips operation entirely. Playback
                        // already owns a private PCM copy before the worker is scheduled.
                        if (cleanup != null) { cleanup(); }
                        if (cancelled || failure != null)
                        {
                            IDisposable retained = (object)result as IDisposable;
                            if (retained != null) { retained.Dispose(); }
                        }
                        lock (gate) { if (ReferenceEquals(activeCancellation, cancellation)) { activeCancellation = null; } }
                        cancellation.Dispose();
                    }
                    if (failure != null) { completion.TrySetException(failure); }
                    else if (cancelled) { completion.TrySetCanceled(); }
                    else { completion.TrySetResult(result); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
                catch { activeTask = null; activeCancellation = null; cancellation.Dispose(); throw; }
                return completion.Task;
            }
        }
        private static void ValidateDevice(int deviceId) { if (deviceId < -1) { throw new ArgumentOutOfRangeException("deviceId"); } }

        private AudioRecording Capture(int deviceId, Action<AudioLevel> callback, CancellationToken token)
        {
            using (PeripheralWaveSession session = Open(true, deviceId, CaptureFormats(), token))
            {
                int frameBytes = session.Format.BlockAlign;
                int maximum = checked((int)session.Format.AverageBytesPerSecond * 5);
                int chunk = checked((int)session.Format.SamplesPerSecond / 20 * frameBytes);
                byte[] captured = new byte[maximum];
                byte[] transfer = new byte[chunk];
                try
                {
                    for (int i = 0; i < 4; i++) { token.ThrowIfCancellationRequested(); session.AddBuffer(chunk, null); }
                    foreach (PeripheralWaveBuffer buffer in session.Buffers) { token.ThrowIfCancellationRequested(); session.Queue(buffer); }
                    token.ThrowIfCancellationRequested();
                    Check(api.StartInput(session.Handle), true, "запустить микрофон");
                    Stopwatch elapsed = Stopwatch.StartNew();
                    int written = 0, next = 0;
                    while (written < maximum)
                    {
                        token.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) { throw new TimeoutException("Микрофон не вернул 5 секунд звука. Проверьте подключение и разрешения Windows."); }
                        PeripheralWaveBuffer buffer = session.Buffers[next];
                        if (!buffer.IsDone) { Thread.Sleep(15); continue; }
                        int recorded = buffer.RecordedBytes;
                        if (recorded < 0 || recorded > buffer.Capacity || recorded % frameBytes != 0)
                            throw new InvalidOperationException("Драйвер микрофона вернул неверный размер звукового буфера.");
                        int count = Math.Min(recorded, maximum - written);
                        if (count > 0)
                        {
                            Marshal.Copy(buffer.Data, transfer, 0, count);
                            Buffer.BlockCopy(transfer, 0, captured, written, count);
                            written += count;
                            AudioLevel level = MeasureLevel(transfer, count);
                            level.ElapsedSeconds = (double)written / session.Format.AverageBytesPerSecond;
                            if (callback != null) { try { callback(level); } catch { /* A closed UI must not prevent native cleanup. */ } }
                        }
                        token.ThrowIfCancellationRequested();
                        if (written < maximum) { session.Queue(buffer); }
                        next = (next + 1) % session.Buffers.Count;
                    }
                    token.ThrowIfCancellationRequested();
                    return new AudioRecording(captured, (int)session.Format.SamplesPerSecond, session.Format.Channels);
                }
                finally { Array.Clear(captured, 0, captured.Length); Array.Clear(transfer, 0, transfer.Length); }
            }
        }

        private void Play(int deviceId, PeripheralWaveFormat format, byte[] bytes, CancellationToken token)
        {
            using (PeripheralWaveSession session = Open(false, deviceId, new[] { format }, token)) { PlayPrepared(session, bytes, token); }
        }
        private void PlayTone(int deviceId, int channel, CancellationToken token)
        {
            using (PeripheralWaveSession session = Open(false, deviceId, new[] { PeripheralWaveFormat.Pcm(44100, 2), PeripheralWaveFormat.Pcm(48000, 2), PeripheralWaveFormat.Pcm(22050, 2) }, token))
            {
                byte[] tone = GenerateTone((int)session.Format.SamplesPerSecond, channel);
                try { PlayPrepared(session, tone, token); }
                finally { Array.Clear(tone, 0, tone.Length); }
            }
        }
        private void PlayPrepared(PeripheralWaveSession session, byte[] bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PeripheralWaveBuffer buffer = session.AddBuffer(bytes.Length, bytes);
            token.ThrowIfCancellationRequested();
            session.Queue(buffer);
            Stopwatch elapsed = Stopwatch.StartNew();
            double timeout = (double)bytes.Length / session.Format.AverageBytesPerSecond + 5;
            while (!buffer.IsDone)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed.Elapsed.TotalSeconds > timeout) { throw new TimeoutException("Аудиодрайвер не завершил воспроизведение. Проверьте устройство вывода."); }
                Thread.Sleep(15);
            }
            token.ThrowIfCancellationRequested();
        }

        private PeripheralWaveSession Open(bool input, int deviceId, PeripheralWaveFormat[] formats, CancellationToken token)
        {
            uint last = 32;
            foreach (PeripheralWaveFormat candidate in formats)
            {
                token.ThrowIfCancellationRequested();
                IntPtr handle;
                PeripheralWaveFormat format = candidate;
                last = api.Open(input, unchecked((uint)deviceId), ref format, out handle);
                if (last == 0)
                {
                    if (handle == IntPtr.Zero) { throw new InvalidOperationException("Аудиодрайвер вернул пустой дескриптор устройства."); }
                    var session = new PeripheralWaveSession(api, input, handle, format);
                    if (token.IsCancellationRequested) { session.Dispose(); token.ThrowIfCancellationRequested(); }
                    return session;
                }
                if (handle != IntPtr.Zero) { new PeripheralWaveSession(api, input, handle, format).Dispose(); }
                // Only unsupported-format errors warrant negotiation; busy/access/driver errors do not.
                if (last != 32) { break; }
            }
            Check(last, input, input ? "открыть микрофон" : "открыть звуковой выход");
            throw new InvalidOperationException("Аудиоустройство не открылось.");
        }
        private static PeripheralWaveFormat[] CaptureFormats()
        {
            return new[] { PeripheralWaveFormat.Pcm(44100, 2), PeripheralWaveFormat.Pcm(48000, 2), PeripheralWaveFormat.Pcm(44100, 1), PeripheralWaveFormat.Pcm(48000, 1), PeripheralWaveFormat.Pcm(22050, 1), PeripheralWaveFormat.Pcm(16000, 1), PeripheralWaveFormat.Pcm(8000, 1) };
        }
        internal static AudioLevel MeasureLevel(byte[] data, int count)
        {
            if (data == null || count < 0 || count > data.Length || count % 2 != 0) { throw new ArgumentException("Неверный PCM-буфер."); }
            double squares = 0, peak = 0;
            for (int i = 0; i < count; i += 2)
            {
                double value = (short)(data[i] | data[i + 1] << 8) / 32768.0;
                squares += value * value; peak = Math.Max(peak, Math.Abs(value));
            }
            return new AudioLevel { Rms = count == 0 ? 0 : Math.Sqrt(squares / (count / 2)), Peak = peak };
        }
        internal static byte[] GenerateTone(int sampleRate, int channel)
        {
            if (sampleRate < 8000 || sampleRate > 48000 || channel < -1 || channel > 1) { throw new ArgumentOutOfRangeException(); }
            const double seconds = 0.6, gap = 0.15, amplitude = 0.10;
            int segments = channel == -1 ? 4 : 1;
            int segmentFrames = (int)(sampleRate * (seconds + gap));
            int audibleFrames = (int)(sampleRate * seconds);
            int fade = Math.Max(1, sampleRate / 40);
            byte[] bytes = new byte[segments * segmentFrames * 4];
            for (int segment = 0; segment < segments; segment++)
            {
                int selected = channel == -1 ? segment % 2 : channel;
                double frequency = selected == 0 ? 440 : 660;
                for (int frame = 0; frame < audibleFrames; frame++)
                {
                    double envelope = Math.Min(1, Math.Min((double)frame / fade, (double)(audibleFrames - 1 - frame) / fade));
                    short value = (short)Math.Round(32767 * amplitude * envelope * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
                    int offset = ((segment * segmentFrames + frame) * 2 + selected) * 2;
                    bytes[offset] = (byte)(value & 255); bytes[offset + 1] = (byte)((value >> 8) & 255);
                }
            }
            return bytes;
        }
        private void Check(uint code, bool input, string action) { if (code != 0) { throw AudioError(api, code, input, action); } }
        internal static Exception AudioError(IPeripheralWaveApi waveApi, uint code, bool input, string action)
        {
            string hint = code == 4 ? "Устройство занято другой программой." : code == 2 ? "Устройство отключено или изменилось; обновите список." : code == 6 ? "Нет доступного аудиодрайвера." : code == 32 ? "Устройство не поддерживает нужный PCM-формат." : input ? "Проверьте подключение и доступ к микрофону для классических приложений в настройках Windows." : "Проверьте подключение и выбранное устройство вывода.";
            return new InvalidOperationException("Не удалось " + action + ". " + hint + " (WinMM " + code + ": " + waveApi.ErrorText(input, code) + ")");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct PeripheralWaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
        public static PeripheralWaveFormat Pcm(int rate, int channels)
        {
            return new PeripheralWaveFormat { FormatTag = 1, Channels = (ushort)channels, SamplesPerSecond = (uint)rate, AverageBytesPerSecond = (uint)(rate * channels * 2), BlockAlign = (ushort)(channels * 2), BitsPerSample = 16 };
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PeripheralWaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    internal sealed class PeripheralWaveBuffer : IDisposable
    {
        internal static readonly int HeaderSize = Marshal.SizeOf(typeof(PeripheralWaveHeader));
        internal static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(PeripheralWaveHeader), "Flags");
        internal static readonly int RecordedOffset = (int)Marshal.OffsetOf(typeof(PeripheralWaveHeader), "BytesRecorded");
        internal IntPtr Header, Data;
        internal readonly int Capacity;
        internal bool Prepared;
        internal static int LiveAllocations;
        internal PeripheralWaveBuffer(int capacity, byte[] initial)
        {
            Capacity = capacity;
            try
            {
                Data = Marshal.AllocHGlobal(capacity);
                Header = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new PeripheralWaveHeader { Data = Data, BufferLength = (uint)capacity }, Header, false);
                if (initial != null) { Marshal.Copy(initial, 0, Data, initial.Length); }
                Interlocked.Increment(ref LiveAllocations);
            }
            catch { if (Header != IntPtr.Zero) { Marshal.FreeHGlobal(Header); } if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); } throw; }
        }
        internal bool IsDone { get { return (Marshal.ReadInt32(Header, FlagsOffset) & 1) != 0; } }
        internal int RecordedBytes { get { Thread.MemoryBarrier(); return Marshal.ReadInt32(Header, RecordedOffset); } }
        public void Dispose()
        {
            if (Header == IntPtr.Zero) { return; }
            Marshal.FreeHGlobal(Header); Header = IntPtr.Zero;
            Marshal.FreeHGlobal(Data); Data = IntPtr.Zero;
            Interlocked.Decrement(ref LiveAllocations);
        }
    }
    internal sealed class PeripheralWaveSession : IDisposable
    {
        private readonly IPeripheralWaveApi api;
        private readonly bool input;
        internal IntPtr Handle;
        internal readonly PeripheralWaveFormat Format;
        internal readonly List<PeripheralWaveBuffer> Buffers = new List<PeripheralWaveBuffer>();
        internal PeripheralWaveSession(IPeripheralWaveApi waveApi, bool isInput, IntPtr handle, PeripheralWaveFormat format) { api = waveApi; input = isInput; Handle = handle; Format = format; }
        internal PeripheralWaveBuffer AddBuffer(int capacity, byte[] bytes)
        {
            var buffer = new PeripheralWaveBuffer(capacity, bytes);
            Buffers.Add(buffer);
            uint code = api.Prepare(input, Handle, buffer.Header);
            if (code != 0) { throw PeripheralAudio.AudioError(api, code, input, "подготовить аудиобуфер"); }
            buffer.Prepared = true;
            return buffer;
        }
        internal void Queue(PeripheralWaveBuffer buffer)
        {
            uint code = api.Queue(input, Handle, buffer.Header);
            if (code != 0) { throw PeripheralAudio.AudioError(api, code, input, "передать аудиобуфер"); }
        }
        public void Dispose()
        {
            if (Handle == IntPtr.Zero) { return; }
            // Reset returns queued buffers. Unprepare and close precede releasing any native storage.
            // No managed callback, event handle or pinned GC object is handed to the driver.
            int attempt = 0;
            while (true)
            {
                if (TryClose()) { return; }
                // Keep the same owner and keep Completion pending. A cancellation or UI
                // deadline cannot make it safe to free buffers still owned by a driver.
                Thread.Sleep(attempt++ < 40 ? 25 : 250);
            }
        }
        private bool TryClose()
        {
            api.Reset(input, Handle);
            foreach (PeripheralWaveBuffer buffer in Buffers)
            {
                if (buffer.Prepared && api.Unprepare(input, Handle, buffer.Header) == 0) { buffer.Prepared = false; }
            }
            if (api.Close(input, Handle) != 0) { return false; }
            Handle = IntPtr.Zero;
            foreach (PeripheralWaveBuffer buffer in Buffers) { buffer.Dispose(); }
            Buffers.Clear();
            return true;
        }
    }

    internal interface IPeripheralWaveApi
    {
        uint DeviceCount(bool input);
        uint DeviceName(bool input, uint id, out string name);
        uint Open(bool input, uint id, ref PeripheralWaveFormat format, out IntPtr handle);
        uint Prepare(bool input, IntPtr handle, IntPtr header);
        uint Queue(bool input, IntPtr handle, IntPtr header);
        uint StartInput(IntPtr handle);
        uint Reset(bool input, IntPtr handle);
        uint Unprepare(bool input, IntPtr handle, IntPtr header);
        uint Close(bool input, IntPtr handle);
        string ErrorText(bool input, uint code);
    }
    internal sealed class PeripheralWaveApi : IPeripheralWaveApi
    {
        public uint DeviceCount(bool input) { return input ? waveInGetNumDevs() : waveOutGetNumDevs(); }
        public uint DeviceName(bool input, uint id, out string name)
        {
            if (input) { WaveInCaps caps; uint code = waveInGetDevCapsW(new UIntPtr(id), out caps, (uint)Marshal.SizeOf(typeof(WaveInCaps))); name = caps.Name; return code; }
            else { WaveOutCaps caps; uint code = waveOutGetDevCapsW(new UIntPtr(id), out caps, (uint)Marshal.SizeOf(typeof(WaveOutCaps))); name = caps.Name; return code; }
        }
        public uint Open(bool input, uint id, ref PeripheralWaveFormat format, out IntPtr handle)
        {
            return input ? waveInOpen(out handle, id, ref format, UIntPtr.Zero, UIntPtr.Zero, 0) : waveOutOpen(out handle, id, ref format, UIntPtr.Zero, UIntPtr.Zero, 0);
        }
        public uint Prepare(bool input, IntPtr handle, IntPtr header) { return input ? waveInPrepareHeader(handle, header, (uint)PeripheralWaveBuffer.HeaderSize) : waveOutPrepareHeader(handle, header, (uint)PeripheralWaveBuffer.HeaderSize); }
        public uint Queue(bool input, IntPtr handle, IntPtr header) { return input ? waveInAddBuffer(handle, header, (uint)PeripheralWaveBuffer.HeaderSize) : waveOutWrite(handle, header, (uint)PeripheralWaveBuffer.HeaderSize); }
        public uint StartInput(IntPtr handle) { return waveInStart(handle); }
        public uint Reset(bool input, IntPtr handle) { return input ? waveInReset(handle) : waveOutReset(handle); }
        public uint Unprepare(bool input, IntPtr handle, IntPtr header) { return input ? waveInUnprepareHeader(handle, header, (uint)PeripheralWaveBuffer.HeaderSize) : waveOutUnprepareHeader(handle, header, (uint)PeripheralWaveBuffer.HeaderSize); }
        public uint Close(bool input, IntPtr handle) { return input ? waveInClose(handle) : waveOutClose(handle); }
        public string ErrorText(bool input, uint code)
        {
            var text = new StringBuilder(256);
            uint result = input ? waveInGetErrorTextW(code, text, (uint)text.Capacity) : waveOutGetErrorTextW(code, text, (uint)text.Capacity);
            return result == 0 ? text.ToString() : "ошибка аудиодрайвера";
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveInCaps { public ushort Mid, Pid; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveOutCaps { public ushort Mid, Pid; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCapsW(UIntPtr id, out WaveInCaps caps, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetDevCapsW(UIntPtr id, out WaveOutCaps caps, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint id, ref PeripheralWaveFormat format, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint id, ref PeripheralWaveFormat format, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetErrorTextW(uint code, StringBuilder text, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetErrorTextW(uint code, StringBuilder text, uint size);
    }
}
