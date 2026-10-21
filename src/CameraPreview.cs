using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PcPulse
{
    public sealed class CameraDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name ?? "Камера"; }
    }

    // StartAsync is the only path that binds a capture filter. Enumeration reads metadata only.
    // All DirectShow calls and releases stay on one background STA with a message pump.
    public sealed class CameraPreview : IDisposable
    {
        private static readonly object EnumerationGate = new object();
        private static Task<IList<CameraDevice>> enumeration;
        private readonly object gate = new object();
        private readonly Func<ICameraPreviewSession> createSession;
        private readonly int timeoutMilliseconds;
        private readonly Action beforeWorkerReady;
        private readonly Action beforeTimeoutCancellation;
        private CameraStaWorker worker;
        private ICameraPreviewSession session;
        private DispatcherTimer eventTimer;
        private Task stopTask;
        private TaskCompletionSource<bool> released = NewCompletion<bool>();
        private TaskCompletionSource<bool> sessionReleased;
        private int generation;
        private bool starting, running, stopping, disposed;
        private int previewWidth, previewHeight;
        private bool resizeQueued;
        private string status;
        private long frameCount;
        private long paintedFrameCount;

        // Raised on a pool thread for asynchronous device loss/renderer failures.
        // The UI must marshal this notification to its Dispatcher.
        public event Action<string> Error;

        public CameraPreview() : this(delegate { return new DirectShowCameraSession(); }, 15000) { }

        internal CameraPreview(Func<ICameraPreviewSession> factory, int timeout, Action beforeReady = null, Action beforeTimeout = null)
        {
            if (factory == null) { throw new ArgumentNullException("factory"); }
            if (timeout < 1) { throw new ArgumentOutOfRangeException("timeout"); }
            createSession = factory;
            timeoutMilliseconds = timeout;
            beforeWorkerReady = beforeReady;
            beforeTimeoutCancellation = beforeTimeout;
            released.TrySetResult(true);
        }

        public bool IsRunning { get { lock (gate) { return running; } } }
        // Cached managed text only: safe for the UI thread, without driver/COM calls.
        public string Status { get { lock (gate) { return status; } } }
        public long FrameCount { get { lock (gate) { return frameCount; } } }
        public long PaintedFrameCount { get { lock (gate) { return paintedFrameCount; } } }
        // Completes only after the current attempt's native cleanup returns, including after a timeout.
        public Task Completion { get { lock (gate) { return released.Task; } } }

        public static async Task<IList<CameraDevice>> EnumerateDevicesAsync()
        {
            Task<IList<CameraDevice>> current;
            lock (EnumerationGate)
            {
                if (enumeration == null || enumeration.IsCompleted)
                {
                    var completion = NewCompletion<IList<CameraDevice>>();
                    var thread = new Thread(delegate()
                    {
                        try { completion.TrySetResult(EnumerateDevices()); }
                        catch (Exception error) { completion.TrySetException(error); }
                    }) { IsBackground = true, Name = "ADAMO camera devices" };
                    thread.SetApartmentState(ApartmentState.STA);
                    enumeration = completion.Task;
                    Observe(enumeration);
                    thread.Start();
                }
                current = enumeration;
            }
            if (await Task.WhenAny(current, Task.Delay(5000)).ConfigureAwait(false) != current)
            { throw new TimeoutException("Список камер не ответил за 5 секунд. Попробуйте обновить список позднее."); }
            return await current.ConfigureAwait(false);
        }

        // Synchronous metadata enumeration is intended for an existing background thread.
        public static IList<CameraDevice> EnumerateDevices() { return DirectShowCameraSession.EnumerateDevices(); }

        public async Task StartAsync(CameraDevice device, IntPtr childWindow, int widthPixels, int heightPixels)
        {
            if (device == null || String.IsNullOrWhiteSpace(device.Id)) { throw new ArgumentException("Выберите камеру.", "device"); }
            if (childWindow == IntPtr.Zero) { throw new ArgumentException("Область предпросмотра ещё не создана.", "childWindow"); }
            if (widthPixels < 1 || heightPixels < 1) { throw new ArgumentOutOfRangeException("widthPixels", "Размер области предпросмотра должен быть положительным."); }
            var selected = new CameraDevice { Id = device.Id, Name = device.Name };
            int operation;
            Task opening;
            lock (gate)
            {
                if (disposed) { throw new ObjectDisposedException("CameraPreview"); }
                if (starting || running || stopping) { throw new InvalidOperationException("Дождитесь остановки предыдущего предпросмотра камеры."); }
                operation = ++generation;
                starting = true;
                status = null;
                frameCount = 0;
                paintedFrameCount = 0;
                released = NewCompletion<bool>();
                TaskCompletionSource<bool> attemptReleased = released;
                previewWidth = widthPixels; previewHeight = heightPixels;
                if (worker == null) { worker = new CameraStaWorker(beforeWorkerReady); }
                opening = Queue(delegate
                {
                    try
                    {
                        sessionReleased = attemptReleased;
                        EnsureCurrent(operation);
                        session = createSession();
                        if (session == null) { throw new InvalidOperationException("Не удалось создать предпросмотр камеры."); }
                        session.Start(selected, childWindow, widthPixels, heightPixels, delegate { return Current(operation); });
                        EnsureCurrent(operation);
                        lock (gate)
                        {
                            if (disposed || generation != operation) { throw new OperationCanceledException("Запуск камеры отменён."); }
                            running = true;
                            var details = session as ICameraPreviewStatus;
                            status = details == null ? null : details.Status;
                            frameCount = details == null ? 0 : details.FrameCount;
                            paintedFrameCount = details == null ? 0 : details.PaintedFrameCount;
                        }
                        session.Resize(previewWidth, previewHeight);
                        eventTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
                        eventTimer.Tick += delegate { PollEvents(operation); };
                        eventTimer.Start();
                    }
                    catch (COMException error)
                    {
                        try { Cleanup(); } catch { }
                        throw new InvalidOperationException(DirectShowCameraSession.DescribeError(error.ErrorCode, "Запуск камеры"), error);
                    }
                    catch
                    {
                        try { Cleanup(); } catch { }
                        throw;
                    }
                    finally { lock (gate) { starting = false; } }
                });
            }
            Observe(opening);
            if (await Task.WhenAny(opening, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != opening)
            {
                if (beforeTimeoutCancellation != null) { beforeTimeoutCancellation(); }
                lock (gate)
                {
                    if (!disposed && generation == operation)
                    {
                        generation++; running = false;
                        Observe(QueueStopLocked());
                    }
                }
                throw new TimeoutException("Камера не ответила вовремя. Запуск отменён; драйвер будет освобождён, когда завершит текущий вызов.");
            }
            await opening.ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            Task closing;
            lock (gate)
            {
                generation++;
                running = false;
                if (worker == null) { return; }
                closing = QueueStopLocked();
            }
            if (await Task.WhenAny(closing, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != closing)
            { throw new TimeoutException("Драйвер камеры задерживает остановку. Окно не заблокировано; освобождение камеры продолжится в фоне."); }
            await closing.ConfigureAwait(false);
        }

        private Task QueueStopLocked()
        {
            if (stopTask == null || stopTask.IsCompleted)
            {
                stopping = true;
                stopTask = Queue(delegate
                {
                    try { Cleanup(); }
                    finally { lock (gate) { starting = false; stopping = false; } }
                });
                Observe(stopTask);
            }
            return stopTask;
        }

        // Pixel dimensions, not WPF DIPs. Coalesces resize bursts while the driver is busy.
        public void Resize(int widthPixels, int heightPixels)
        {
            if (widthPixels < 1 || heightPixels < 1) { return; }
            int operation;
            lock (gate)
            {
                previewWidth = widthPixels; previewHeight = heightPixels;
                if (disposed || worker == null || !running || resizeQueued) { return; }
                resizeQueued = true;
                operation = generation;
            }
            Observe(Queue(delegate
            {
                int width, height;
                lock (gate) { resizeQueued = false; width = previewWidth; height = previewHeight; }
                if (!Current(operation) || session == null) { return; }
                try { session.Resize(width, height); }
                catch (Exception error) { FailSession(error.Message); }
            }));
        }

        public void Dispose()
        {
            CameraStaWorker owned;
            lock (gate)
            {
                if (disposed) { return; }
                disposed = true; generation++; running = false;
                owned = worker;
            }
            if (owned == null) { return; }
            Observe(Queue(delegate
            {
                try { Cleanup(); }
                finally { owned.Shutdown(); }
            }));
        }

        private void PollEvents(int operation)
        {
            if (!Current(operation) || session == null) { return; }
            try
            {
                string error = session.ReadError();
                var details = session as ICameraPreviewStatus;
                if (details != null) { lock (gate) { status = details.Status; frameCount = details.FrameCount; paintedFrameCount = details.PaintedFrameCount; } }
                if (!String.IsNullOrWhiteSpace(error)) { FailSession(error); }
            }
            catch (Exception error) { FailSession(error.Message); }
        }

        private void FailSession(string message)
        {
            int failedGeneration;
            lock (gate) { failedGeneration = ++generation; running = false; stopping = true; }
            try { Cleanup(); } catch { }
            finally { lock (gate) { stopping = stopTask != null && !stopTask.IsCompleted; } }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Action<string> handler;
                lock (gate) { handler = disposed || generation != failedGeneration ? null : Error; }
                if (handler != null) { try { handler(message); } catch { } }
            });
        }

        private void Cleanup()
        {
            if (eventTimer != null) { eventTimer.Stop(); eventTimer = null; }
            ICameraPreviewSession previous = session; session = null;
            TaskCompletionSource<bool> completion = sessionReleased; sessionReleased = null;
            lock (gate) { running = false; resizeQueued = false; }
            try { if (previous != null) { previous.Dispose(); } }
            finally { if (completion != null) { completion.TrySetResult(true); } }
        }

        private bool Current(int operation) { lock (gate) { return !disposed && generation == operation; } }
        private void EnsureCurrent(int operation) { if (!Current(operation)) { throw new OperationCanceledException("Запуск камеры отменён."); } }

        private Task Queue(Action action)
        {
            CameraStaWorker current;
            lock (gate) { current = worker; }
            return current.Post(action);
        }

        private static TaskCompletionSource<T> NewCompletion<T>() { return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var observed = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class CameraStaWorker
        {
            private readonly object queueGate = new object();
            private readonly Queue<Work> queue = new Queue<Work>();
            private Dispatcher dispatcher;
            private bool scheduled, closed;
            private Exception failure;
            private sealed class Work
            {
                internal Action Action;
                internal TaskCompletionSource<bool> Completion = NewCompletion<bool>();
            }

            internal CameraStaWorker(Action beforeReady)
            {
                var thread = new Thread(delegate()
                {
                    try
                    {
                        if (beforeReady != null) { beforeReady(); }
                        lock (queueGate) { dispatcher = Dispatcher.CurrentDispatcher; Schedule(); }
                        Dispatcher.Run();
                    }
                    catch (Exception error)
                    {
                        lock (queueGate)
                        {
                            failure = error; closed = true;
                            while (queue.Count > 0) { queue.Dequeue().Completion.TrySetException(error); }
                        }
                    }
                }) { IsBackground = true, Name = "ADAMO camera preview" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            internal Task Post(Action action)
            {
                var work = new Work { Action = action };
                lock (queueGate)
                {
                    if (closed) { work.Completion.TrySetException(failure ?? new ObjectDisposedException("CameraPreview")); }
                    else { queue.Enqueue(work); Schedule(); }
                }
                return work.Completion.Task;
            }

            private void Schedule()
            {
                if (dispatcher == null || scheduled || queue.Count == 0) { return; }
                scheduled = true;
                dispatcher.BeginInvoke(new Action(Drain), DispatcherPriority.Normal);
            }

            private void Drain()
            {
                while (true)
                {
                    Work work;
                    lock (queueGate)
                    {
                        if (queue.Count == 0) { scheduled = false; return; }
                        work = queue.Dequeue();
                    }
                    try { work.Action(); work.Completion.TrySetResult(true); }
                    catch (Exception error) { work.Completion.TrySetException(error); }
                }
            }

            internal void Shutdown()
            {
                lock (queueGate)
                {
                    closed = true;
                    while (queue.Count > 0) { queue.Dequeue().Completion.TrySetException(new ObjectDisposedException("CameraPreview")); }
                }
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    internal interface ICameraPreviewSession : IDisposable
    {
        void Start(CameraDevice device, IntPtr childWindow, int width, int height, Func<bool> current);
        void Resize(int width, int height);
        string ReadError();
    }

    internal interface ICameraPreviewStatus { string Status { get; } long FrameCount { get; } long PaintedFrameCount { get; } }

    internal sealed class DirectShowCameraSession : ICameraPreviewSession, ICameraPreviewStatus
    {
        private static readonly Guid DeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
        private static readonly Guid PropertyBagId = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
        private static readonly Guid VideoType = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid VideoInfoType = new Guid("05589F80-C356-11CE-BF01-00AA0055595A");
        private static readonly Guid CaptureCategory = new Guid("FB6C4281-0353-11D1-905F-0000C0CC16BA");
        private static readonly Guid PreviewCategory = new Guid("FB6C4282-0353-11D1-905F-0000C0CC16BA");
        private object graphObject, builderObject, sourceObject, grabberObject, sinkObject;
        private ICameraMediaControl control;
        private ICameraSampleGrabber grabber;
        private CameraFrameReceiver frames;
        private ICameraMediaEvent mediaEvent;
        private IntPtr owner;
        private int videoWidth, videoHeight, viewportWidth, viewportHeight;
        private string mode;
        public string Status { get; private set; }
        public long FrameCount { get { return frames == null ? 0 : frames.Count; } }
        public long PaintedFrameCount { get { return frames == null ? 0 : frames.PaintedCount; } }

        internal static IList<CameraDevice> EnumerateDevices()
        {
            var devices = new List<CameraDevice>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VisitDevices(delegate(IMoniker moniker, CameraDevice device)
            {
                if (identities.Add(device.Id)) { devices.Add(device); }
                return false;
            });
            return devices;
        }

        public void Start(CameraDevice device, IntPtr childWindow, int width, int height, Func<bool> current)
        {
            ValidateOwner(childWindow);
            Exception lastFailure = null;
            // Fresh graph/source for each attempt: failed negotiation must never leave
            // half-connected pins in a graph reused by the next format or pin attempt.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                CheckCurrent(current);
                try
                {
                    StartAttempt(device, childWindow, width, height, current, attempt);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    lastFailure = error;
                    // Do not keep reopening a camera whose access is denied or which is busy.
                    var comError = error as COMException;
                    bool cannotRetry = comError != null && IsAccessFailure(comError.ErrorCode);
                    // Cleanup errors prevent retry: there is no assurance the source is free.
                    Dispose();
                    CheckCurrent(current);
                    if (cannotRetry) { throw; }
                }
            }
            throw new InvalidOperationException("Камера не передала изображение в совместимых режимах. "
                + "Проверьте шторку, разрешение камеры в Windows и драйвер. На iMac/Mac установите актуальное ПО поддержки Windows (Boot Camp) от Apple. "
                + (lastFailure == null ? "" : lastFailure.Message), lastFailure);
        }

        private void StartAttempt(CameraDevice device, IntPtr childWindow, int width, int height, Func<bool> current, int attempt)
        {
            owner = childWindow; viewportWidth = width; viewportHeight = height;
            VisitDevices(delegate(IMoniker moniker, CameraDevice candidate)
            {
                if (!String.Equals(candidate.Id, device.Id, StringComparison.OrdinalIgnoreCase)) { return false; }
                CheckCurrent(current);
                Guid filterId = typeof(ICameraBaseFilter).GUID;
                moniker.BindToObject(null, null, ref filterId, out sourceObject);
                return true;
            });
            if (sourceObject == null) { throw new InvalidOperationException("Камера отключена или больше не найдена. Обновите список устройств."); }
            CheckCurrent(current);
            graphObject = Create("E436EBB3-524F-11CE-9F53-0020AF0BA770");
            builderObject = Create("BF87B6E1-8C27-11D0-B3F0-00AA003761C5");
            // Windows' Sample Grabber is a legacy DirectShow component. It is only
            // used for this existing DirectShow compatibility path, never recording.
            grabberObject = Create("C1F400A0-3F08-11D3-9F0B-006008039E37");
            sinkObject = Create("C1F400A4-3F08-11D3-9F0B-006008039E37");
            var graph = (ICameraGraphBuilder)graphObject;
            var builder = (ICameraCaptureGraphBuilder)builderObject;
            control = (ICameraMediaControl)graphObject;
            mediaEvent = (ICameraMediaEvent)graphObject;
            grabber = (ICameraSampleGrabber)grabberObject;
            Check(builder.SetFiltergraph(graph), "Подготовка камеры");
            Check(graph.AddFilter((ICameraBaseFilter)sourceObject, "ADAMO camera preview"), "Подключение камеры");
            Check(graph.AddFilter((ICameraBaseFilter)grabberObject, "ADAMO live frames"), "Подготовка изображения");
            Check(graph.AddFilter((ICameraBaseFilter)sinkObject, "ADAMO no recording"), "Подготовка изображения");
            Guid category = attempt == 2 ? PreviewCategory : CaptureCategory;
            Guid type = VideoType;
            bool selectedFormat = attempt == 0 && TryConservativeFormat(builder, category, current);
            CheckCurrent(current);
            var requested = new CameraMediaType
            {
                MajorType = VideoType, SubType = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770"), FormatType = VideoInfoType
            }; // RGB24. Intelligent Connect inserts Windows' decoder/color converter if needed.
            Check(grabber.SetMediaType(ref requested), "Формат изображения");
            Check(grabber.SetOneShot(false), "Непрерывный предпросмотр");
            Check(grabber.SetBufferSamples(false), "Подготовка кадров");
            Check(builder.RenderStream(ref category, ref type, sourceObject, (ICameraBaseFilter)grabberObject, (ICameraBaseFilter)sinkObject), "Подготовка изображения");
            CheckCurrent(current);
            CameraMediaType connected = new CameraMediaType();
            try
            {
                Check(grabber.GetConnectedMediaType(out connected), "Чтение формата камеры");
                if (connected.MajorType != VideoType || connected.FormatType != VideoInfoType
                    || connected.Format == IntPtr.Zero || connected.FormatSize < Marshal.SizeOf(typeof(CameraVideoInfo)))
                { throw new InvalidOperationException("Драйвер не согласовал RGB-изображение камеры."); }
                var info = (CameraVideoInfo)Marshal.PtrToStructure(connected.Format, typeof(CameraVideoInfo));
                frames = new CameraFrameReceiver(info.Bitmap);
                videoWidth = info.Bitmap.Width; videoHeight = Math.Abs(info.Bitmap.Height);
            }
            finally { connected.Free(); }
            Check(grabber.SetCallback(frames, 1), "Получение кадров камеры");
            Check(control.Run(), "Запуск камеры");
            // Run() can succeed while no frames arrive. Never mark that state as a
            // working preview; keep the UI cancellable while the worker waits.
            var waiting = Stopwatch.StartNew();
            while (!frames.HasFrame && waiting.ElapsedMilliseconds < 2500)
            {
                CheckCurrent(current);
                if (frames.Failure != null) { throw new InvalidOperationException(frames.Failure); }
                string error = ReadGraphError();
                if (error != null) { throw new InvalidOperationException(error); }
                Thread.Sleep(25);
            }
            CheckCurrent(current);
            if (!frames.HasFrame) { throw new TimeoutException("Драйвер запустился, но кадры камеры не поступили."); }
            mode = selectedFormat ? "совместимый формат" : attempt == 2 ? "поток предпросмотра" : "формат драйвера";
            UpdateStatus();
            Resize(width, height);
        }

        private bool TryConservativeFormat(ICameraCaptureGraphBuilder builder, Guid category, Func<bool> current)
        {
            Guid type = VideoType, id = typeof(ICameraStreamConfig).GUID;
            IntPtr pointer = IntPtr.Zero, capabilities = IntPtr.Zero;
            object configObject = null;
            var formats = new List<CameraMediaType>();
            var scores = new List<long>();
            try
            {
                if (builder.FindInterface(ref category, ref type, (ICameraBaseFilter)sourceObject, ref id, out pointer) < 0 || pointer == IntPtr.Zero) { return false; }
                configObject = Marshal.GetObjectForIUnknown(pointer);
                var config = (ICameraStreamConfig)configObject;
                int count, size;
                if (config.GetNumberOfCapabilities(out count, out size) < 0 || count < 1 || count > 256 || size < 1 || size > 4096) { return false; }
                capabilities = Marshal.AllocCoTaskMem(size);
                for (int index = 0; index < count; index++)
                {
                    CheckCurrent(current);
                    IntPtr mediaPointer = IntPtr.Zero;
                    CameraMediaType format = new CameraMediaType();
                    try
                    {
                        if (config.GetStreamCaps(index, out mediaPointer, capabilities) < 0 || mediaPointer == IntPtr.Zero) { continue; }
                        format = (CameraMediaType)Marshal.PtrToStructure(mediaPointer, typeof(CameraMediaType));
                        if (format.MajorType != VideoType || format.FormatType != VideoInfoType
                            || format.Format == IntPtr.Zero || format.FormatSize < Marshal.SizeOf(typeof(CameraVideoInfo))) { continue; }
                        var videoInfo = (CameraVideoInfo)Marshal.PtrToStructure(format.Format, typeof(CameraVideoInfo));
                        long score = FormatScore(videoInfo.Bitmap.Width, videoInfo.Bitmap.Height, videoInfo.AverageTimePerFrame);
                        if (score == Int64.MaxValue) { continue; }
                        formats.Add(format); scores.Add(score); format = new CameraMediaType();
                    }
                    finally { format.Free(); if (mediaPointer != IntPtr.Zero) { Marshal.FreeCoTaskMem(mediaPointer); } }
                }
                // Choose only formats advertised by the driver; never invent a size/fps.
                for (int attempt = 0; attempt < formats.Count; attempt++)
                {
                    CheckCurrent(current);
                    int best = -1;
                    for (int index = 0; index < scores.Count; index++)
                    { if (scores[index] < Int64.MaxValue && (best < 0 || scores[index] < scores[best])) { best = index; } }
                    if (best < 0) { break; }
                    scores[best] = Int64.MaxValue;
                    CameraMediaType chosen = formats[best];
                    if (config.SetFormat(ref chosen) >= 0) { return true; }
                }
                return false;
            }
            finally
            {
                foreach (CameraMediaType format in formats) { CameraMediaType owned = format; owned.Free(); }
                if (capabilities != IntPtr.Zero) { Marshal.FreeCoTaskMem(capabilities); }
                Release(ref configObject);
                if (pointer != IntPtr.Zero) { Marshal.Release(pointer); }
            }
        }

        internal static long FormatScore(int width, int height, long timePerFrame)
        {
            if (width < 160 || width > 1920 || height == 0 || height < -1080 || height > 1080 || timePerFrame < 0) { return Int64.MaxValue; }
            long pixels = (long)width * Math.Abs(height);
            // 640x480, 15-30 fps is a low-bandwidth, useful inspection preview.
            long sizePenalty = Math.Abs(pixels - 640L * 480L);
            long ratePenalty = timePerFrame > 0 && (timePerFrame < 333333 || timePerFrame > 1000000) ? 2000000 : 0;
            return sizePenalty + ratePenalty;
        }

        private static bool IsAccessFailure(int error)
        {
            return error == unchecked((int)0x80070005) || error == unchecked((int)0x80070020) || error == unchecked((int)0x800700AA);
        }

        private void UpdateStatus()
        {
            Status = "Получены кадры " + videoWidth + " × " + videoHeight + "; " + mode + ". "
                + (frames != null && frames.IsDarkFrame ? "Изображение очень тёмное: проверьте шторку и освещение; если темнота сохраняется — драйвер камеры." : "Оцените изображение и укажите результат.");
        }

        public void Resize(int width, int height)
        {
            if (frames == null || width < 1 || height < 1) { return; }
            ValidateOwner(owner);
            viewportWidth = width; viewportHeight = height;
            frames.Paint(owner, width, height);
        }

        internal static void FitImage(int width, int height, int sourceWidth, int sourceHeight, out int left, out int top, out int outputWidth, out int outputHeight)
        {
            outputWidth = Math.Max(1, width); outputHeight = Math.Max(1, height);
            if (sourceWidth > 0 && sourceHeight > 0)
            {
                double scale = Math.Min((double)outputWidth / sourceWidth, (double)outputHeight / sourceHeight);
                outputWidth = Math.Max(1, Math.Min(outputWidth, (int)Math.Floor(sourceWidth * scale)));
                outputHeight = Math.Max(1, Math.Min(outputHeight, (int)Math.Floor(sourceHeight * scale)));
            }
            left = (Math.Max(1, width) - outputWidth) / 2; top = (Math.Max(1, height) - outputHeight) / 2;
        }

        public string ReadError()
        {
            string error = ReadGraphError();
            if (error != null) { return error; }
            if (frames == null) { return null; }
            if (frames.Failure != null) { return frames.Failure; }
            if (frames.Stalled(5)) { return "Камера перестала передавать кадры. Проверьте подключение и драйвер, затем включите её снова."; }
            ValidateOwner(owner);
            frames.Paint(owner, viewportWidth, viewportHeight);
            UpdateStatus();
            return null;
        }

        private string ReadGraphError()
        {
            if (mediaEvent == null) { return null; }
            for (int count = 0; count < 32; count++)
            {
                int code; IntPtr first, second;
                int result = mediaEvent.GetEvent(out code, out first, out second, 0);
                if (result == unchecked((int)0x80004004)) { return null; } // Empty event queue: E_ABORT.
                Check(result, "События камеры");
                try
                {
                    if (code == 3) { return DescribeError(unchecked((int)first.ToInt64()), "Камера остановила предпросмотр"); }
                    if (code == 0x1F && second == IntPtr.Zero) { return "Камера отключена во время предпросмотра."; }
                    if (code == 1) { return "Камера завершила передачу изображения."; }
                }
                finally { mediaEvent.FreeEventParams(code, first, second); }
            }
            return null;
        }

        public void Dispose()
        {
            Exception failure = null;
            try { if (control != null) { Check(control.Stop(), "Остановка камеры"); } }
            catch (Exception error) { failure = error; }
            try { if (grabber != null) { Check(grabber.SetCallback(null, 1), "Отключение кадров камеры"); } }
            catch (Exception error) { if (failure == null) { failure = error; } }
            if (frames != null) { frames.Stop(); frames = null; }
            // Interface casts share their owner's RCW. Release each incoming owner once.
            control = null; grabber = null; mediaEvent = null; owner = IntPtr.Zero;
            try { Release(ref builderObject); } catch (Exception error) { if (failure == null) { failure = error; } }
            try { Release(ref sourceObject); } catch (Exception error) { if (failure == null) { failure = error; } }
            try { Release(ref grabberObject); } catch (Exception error) { if (failure == null) { failure = error; } }
            try { Release(ref sinkObject); } catch (Exception error) { if (failure == null) { failure = error; } }
            try { Release(ref graphObject); } catch (Exception error) { if (failure == null) { failure = error; } }
            if (failure != null) { throw failure; }
        }

        internal static string DescribeError(int result, string stage)
        {
            string detail;
            switch (unchecked((uint)result))
            {
                case 0x80070005: detail = "Доступ к камере запрещён. Проверьте разрешение камеры для классических приложений в параметрах Windows."; break;
                case 0x80070020: case 0x800700AA: detail = "Камера занята или не готова. Закройте другие приложения, использующие её, и повторите запуск."; break;
                case 0x80040275: case 0x8007048F: case 0x80070490: detail = "Камера не подключена или недоступна."; break;
                case 0x80040217: case 0x80040218: detail = "Не удалось согласовать формат изображения с драйвером камеры."; break;
                default: detail = "Не удалось открыть или обслужить камеру. Проверьте разрешения Windows, подключение и другие приложения камеры."; break;
            }
            return stage + ": " + detail + " (0x" + unchecked((uint)result).ToString("X8") + ")";
        }

        private static void Check(int result, string stage) { if (result < 0) { throw new COMException(DescribeError(result, stage), result); } }
        private static void CheckCurrent(Func<bool> current) { if (!current()) { throw new OperationCanceledException("Запуск камеры отменён."); } }
        private static object Create(string classId) { return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(classId), true)); }
        internal static void Release(ref object instance)
        {
            object owned = instance; instance = null;
            // Release only this incoming COM reference. A property bag and its
            // device moniker may share one RCW, still owned by the enumeration.
            if (owned != null && Marshal.IsComObject(owned)) { Marshal.ReleaseComObject(owned); }
        }

        private static void VisitDevices(Func<IMoniker, CameraDevice, bool> visitor)
        {
            object enumeratorObject = null; IEnumMoniker monikers = null;
            try
            {
                enumeratorObject = Create("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
                Guid category = DeviceCategory;
                int result = ((ICameraCreateDevEnum)enumeratorObject).CreateClassEnumerator(ref category, out monikers, 0);
                Check(result, "Поиск камер");
                if (result == 1 || monikers == null) { return; }
                var item = new IMoniker[1];
                while (true)
                {
                    result = monikers.Next(1, item, IntPtr.Zero);
                    Check(result, "Поиск камер");
                    if (result != 0 || item[0] == null) { break; }
                    IMoniker moniker = item[0]; item[0] = null;
                    try
                    {
                        CameraDevice device = ReadDevice(moniker);
                        if (device != null && visitor(moniker, device)) { return; }
                    }
                    finally { Marshal.ReleaseComObject(moniker); }
                }
            }
            finally
            {
                if (monikers != null) { Marshal.ReleaseComObject(monikers); }
                Release(ref enumeratorObject);
            }
        }

        private static CameraDevice ReadDevice(IMoniker moniker)
        {
            object bagObject = null;
            try
            {
                Guid propertyBag = PropertyBagId;
                moniker.BindToStorage(null, null, ref propertyBag, out bagObject);
                var bag = (ICameraPropertyBag)bagObject;
                string identity = ReadString(bag, "DevicePath");
                if (String.IsNullOrWhiteSpace(identity)) { moniker.GetDisplayName(null, null, out identity); }
                if (String.IsNullOrWhiteSpace(identity)) { return null; }
                string name = ReadString(bag, "FriendlyName");
                if (String.IsNullOrWhiteSpace(name)) { name = ReadString(bag, "Description"); }
                return new CameraDevice { Id = identity, Name = String.IsNullOrWhiteSpace(name) ? "Камера" : name };
            }
            catch (COMException) { return null; }
            finally { Release(ref bagObject); }
        }

        private static string ReadString(ICameraPropertyBag bag, string name)
        {
            object value = null;
            return bag.Read(name, out value, IntPtr.Zero) >= 0 ? value as string : null;
        }

        private static void ValidateOwner(IntPtr window)
        {
            uint process;
            if (!IsWindow(window) || GetWindowThreadProcessId(window, out process) == 0 || process != GetCurrentProcessId())
            { throw new InvalidOperationException("Окно предпросмотра закрыто или недоступно."); }
        }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    }

    // Vtable order and x64-sized handles follow the Windows SDK strmif.h/control.h declarations.
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraCreateDevEnum { [PreserveSig] int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker enumerator, int flags); }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string property, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string property, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraBaseFilter { }

    [ComImport, Guid("56A868A9-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraGraphBuilder
    {
        [PreserveSig] int AddFilter([MarshalAs(UnmanagedType.Interface)] ICameraBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);
    }

    [ComImport, Guid("93E5A4E0-2D50-11D2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICameraCaptureGraphBuilder
    {
        [PreserveSig] int SetFiltergraph([MarshalAs(UnmanagedType.Interface)] ICameraGraphBuilder graph);
        [PreserveSig] int GetFiltergraph([MarshalAs(UnmanagedType.Interface)] out ICameraGraphBuilder graph);
        [PreserveSig] int SetOutputFileName([In] ref Guid type, [MarshalAs(UnmanagedType.LPWStr)] string path, out IntPtr filter, out IntPtr sink);
        [PreserveSig] int FindInterface([In] ref Guid category, [In] ref Guid type, [MarshalAs(UnmanagedType.Interface)] ICameraBaseFilter filter, [In] ref Guid id, out IntPtr value);
        [PreserveSig] int RenderStream([In] ref Guid category, [In] ref Guid type, [MarshalAs(UnmanagedType.IUnknown)] object source, [MarshalAs(UnmanagedType.Interface)] ICameraBaseFilter compressor, [MarshalAs(UnmanagedType.Interface)] ICameraBaseFilter renderer);
    }

    [ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface ICameraMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int GetState(int milliseconds, out int state);
    }

    [ComImport, Guid("56A868B6-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface ICameraMediaEvent
    {
        [PreserveSig] int GetEventHandle(out IntPtr handle);
        [PreserveSig] int GetEvent(out int code, out IntPtr first, out IntPtr second, int timeout);
        [PreserveSig] int WaitForCompletion(int timeout, out int code);
        [PreserveSig] int CancelDefaultHandling(int code);
        [PreserveSig] int RestoreDefaultHandling(int code);
        [PreserveSig] int FreeEventParams(int code, IntPtr first, IntPtr second);
    }

    [ComImport, Guid("56A868B5-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface ICameraBasicVideo
    {
        [PreserveSig] int get_AvgTimePerFrame(out double value);
        [PreserveSig] int get_BitRate(out int value);
        [PreserveSig] int get_BitErrorRate(out int value);
        [PreserveSig] int get_VideoWidth(out int value);
        [PreserveSig] int get_VideoHeight(out int value);
    }

    [ComImport, Guid("56A868B4-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface ICameraVideoWindow
    {
        [PreserveSig] int put_Caption([MarshalAs(UnmanagedType.BStr)] string caption);
        [PreserveSig] int get_Caption([MarshalAs(UnmanagedType.BStr)] out string caption);
        [PreserveSig] int put_WindowStyle(int value);
        [PreserveSig] int get_WindowStyle(out int value);
        [PreserveSig] int put_WindowStyleEx(int value);
        [PreserveSig] int get_WindowStyleEx(out int value);
        [PreserveSig] int put_AutoShow(int value);
        [PreserveSig] int get_AutoShow(out int value);
        [PreserveSig] int put_WindowState(int value);
        [PreserveSig] int get_WindowState(out int value);
        [PreserveSig] int put_BackgroundPalette(int value);
        [PreserveSig] int get_BackgroundPalette(out int value);
        [PreserveSig] int put_Visible(int value);
        [PreserveSig] int get_Visible(out int value);
        [PreserveSig] int put_Left(int value);
        [PreserveSig] int get_Left(out int value);
        [PreserveSig] int put_Width(int value);
        [PreserveSig] int get_Width(out int value);
        [PreserveSig] int put_Top(int value);
        [PreserveSig] int get_Top(out int value);
        [PreserveSig] int put_Height(int value);
        [PreserveSig] int get_Height(out int value);
        [PreserveSig] int put_Owner(IntPtr value);
        [PreserveSig] int get_Owner(out IntPtr value);
        [PreserveSig] int put_MessageDrain(IntPtr value);
        [PreserveSig] int get_MessageDrain(out IntPtr value);
        [PreserveSig] int get_BorderColor(out int value);
        [PreserveSig] int put_BorderColor(int value);
        [PreserveSig] int get_FullScreenMode(out int value);
        [PreserveSig] int put_FullScreenMode(int value);
        [PreserveSig] int SetWindowForeground(int focus);
        [PreserveSig] int NotifyOwnerMessage(IntPtr owner, int message, IntPtr first, IntPtr second);
        [PreserveSig] int SetWindowPosition(int left, int top, int width, int height);
    }
}
