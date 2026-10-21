using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests
{
    public static class CameraPreviewTests
    {
        private static readonly CameraDevice Device = new CameraDevice { Id = "test-only-camera", Name = "Test camera" };
        public static void Run()
        {
            Task tests = Task.Run(async delegate
            {
                await ExplicitStartOnly();
                await StartStopAndResize();
                await FailureReleasesAndAllowsRetry();
                await StopDuringOpening();
                await StopBeforeStaIsReady();
                await TimeoutDoesNotBlockAndReleasesLater();
                await DisposeDuringOpening();
                await DeviceLoss();
                await DeviceLossCannotCompleteNextAttempt();
                await OldTimeoutCannotStopNextAttempt();
                ImageBounds();
                ErrorMessages();
                EmptyGraphInterfaces();
                ReleasingOneComOwnerPreservesOtherOwner();
                FrameFormatBounds();
                FrameDeliveryAndCleanup();
                FrameCallbackRaces();
                ConservativeFormatRanking();
                EmptyGrabberInterfaces();
            });
            if (Task.WhenAny(tests, Task.Delay(15000)).GetAwaiter().GetResult() != tests) { throw new TimeoutException("Camera tests exceeded their deadline."); }
            tests.GetAwaiter().GetResult();
            Console.WriteLine("PASS camera preview: 19 groups, no real capture filter opened");
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Run();
                if (Array.IndexOf(args, "--metadata") >= 0)
                {
                    IList<CameraDevice> devices = CameraPreview.EnumerateDevicesAsync().GetAwaiter().GetResult();
                    Console.WriteLine("Metadata-only devices: " + devices.Count);
                    foreach (CameraDevice device in devices) { Console.WriteLine(device.Name + " (identity present: " + !String.IsNullOrWhiteSpace(device.Id) + ")"); }
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static async Task ExplicitStartOnly()
        {
            int created = 0;
            var preview = new CameraPreview(delegate { created++; return new FakeSession(); }, 2000);
            Require(!preview.IsRunning && created == 0, "Construction must not create a capture session.");
            preview.Resize(640, 480);
            preview.Resize(0, 0);
            await preview.StopAsync();
            await Expect<ArgumentException>(preview.StartAsync(null, new IntPtr(1), 640, 480));
            await Expect<ArgumentException>(preview.StartAsync(Device, IntPtr.Zero, 640, 480));
            await Expect<ArgumentOutOfRangeException>(preview.StartAsync(Device, new IntPtr(1), 0, 480));
            preview.Dispose(); preview.Dispose();
            await Expect<ObjectDisposedException>(preview.StartAsync(Device, new IntPtr(1), 640, 480));
            Require(created == 0, "Listing or invalid actions must not start camera capture.");
        }

        private static async Task StartStopAndResize()
        {
            var fake = new FakeSession();
            using (var preview = new CameraPreview(delegate { return fake; }, 2000))
            {
                await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(preview.IsRunning && !preview.Completion.IsCompleted && fake.Started && fake.StartApartment == ApartmentState.STA, "Explicit start runs once on STA and keeps its resource lifetime open.");
                Require(preview.FrameCount == 2 && preview.PaintedFrameCount == 1 && preview.Status == "Synthetic preview",
                    "UI can read immutable frame/status metadata without accessing the native session from its thread.");
                await Expect<InvalidOperationException>(preview.StartAsync(Device, new IntPtr(1), 640, 480));
                fake.Resized.Reset();
                preview.Resize(1280, 720);
                Require(fake.Resized.Wait(2000), "Live resize is processed without blocking the calling thread.");
                await preview.StopAsync();
                Require(!preview.IsRunning && preview.Completion.IsCompleted && fake.Disposals == 1 && fake.LastWidth == 1280 && fake.LastHeight == 720, "Resize is processed and stop releases once.");
                Require(fake.OwnerThread == fake.DisposeThread && fake.OwnerThread == fake.ResizeThread, "All session operations stay on the same thread.");
                await preview.StopAsync();
                Require(fake.Disposals == 1, "Repeated stop cannot double-dispose COM ownership.");
            }
        }

        private static async Task FailureReleasesAndAllowsRetry()
        {
            var failed = new FakeSession { OpenFailure = new COMException("access denied", unchecked((int)0x80070005)) };
            var replacement = new FakeSession(); int created = 0;
            using (var preview = new CameraPreview(delegate { return ++created == 1 ? failed : replacement; }, 2000))
            {
                Exception result = await Expect<InvalidOperationException>(preview.StartAsync(Device, new IntPtr(1), 640, 480));
                Require(result.Message.Contains("Доступ") && !preview.IsRunning && failed.Disposals == 1, "Failed COM open is visible, localized and cleaned up.");
                await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(preview.IsRunning && replacement.Started, "A failed open permits an explicit retry.");
                await preview.StopAsync();
            }
        }

        private static async Task StopDuringOpening()
        {
            var fake = new FakeSession { BlockOpening = true };
            using (var preview = new CameraPreview(delegate { return fake; }, 2000))
            {
                Task opening = preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(fake.Entered.Wait(2000), "Fake driver entered opening.");
                Task stopping = preview.StopAsync();
                Require(!stopping.IsCompleted, "Stop waits for pending COM work before reporting resource release.");
                fake.Release.Set();
                await Expect<OperationCanceledException>(opening);
                await stopping;
                Require(!fake.Started && !preview.IsRunning && fake.Disposals == 1, "Late opening after cancellation cannot enter running state.");
            }
        }

        private static async Task StopBeforeStaIsReady()
        {
            var ready = new ManualResetEventSlim(false);
            var fake = new FakeSession(); int created = 0;
            using (var preview = new CameraPreview(delegate { created++; return fake; }, 2000, delegate { ready.Wait(); }))
            {
                Task opening = preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Task stopping = preview.StopAsync();
                ready.Set();
                await Expect<OperationCanceledException>(opening);
                await stopping;
                Require(created == 0, "Cancelled work queued before STA readiness never binds a source.");
                await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(preview.IsRunning && created == 1 && fake.Disposals == 0, "Old queued startup cannot dispose a newer session.");
                await preview.StopAsync();
            }
            ready.Dispose();
        }

        private static async Task TimeoutDoesNotBlockAndReleasesLater()
        {
            var fake = new FakeSession { BlockOpening = true };
            using (var preview = new CameraPreview(delegate { return fake; }, 80))
            {
                var elapsed = Stopwatch.StartNew();
                Task opening = preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(fake.Entered.Wait(2000), "Fake blocked driver entered.");
                await Expect<TimeoutException>(opening);
                Require(elapsed.ElapsedMilliseconds < 2000 && !preview.IsRunning && !preview.Completion.IsCompleted && fake.Disposals == 0, "Timeout returns without pretending a hung driver has released resources.");
                await Expect<InvalidOperationException>(preview.StartAsync(Device, new IntPtr(1), 640, 480));
                await Expect<TimeoutException>(preview.StopAsync());
                fake.Release.Set();
                await preview.StopAsync();
                Require(preview.Completion.IsCompleted && fake.Disposals == 1 && !fake.Started, "Delayed driver release completes on its owning STA after timeout.");
            }
        }

        private static async Task DisposeDuringOpening()
        {
            var fake = new FakeSession { BlockOpening = true };
            var preview = new CameraPreview(delegate { return fake; }, 2000);
            Task opening = preview.StartAsync(Device, new IntPtr(1), 640, 480);
            Require(fake.Entered.Wait(2000), "Pending startup entered before close.");
            var elapsed = Stopwatch.StartNew(); preview.Dispose(); elapsed.Stop();
            Require(elapsed.ElapsedMilliseconds < 500, "Dispose must not freeze the UI in a driver call.");
            fake.Release.Set();
            await Expect<OperationCanceledException>(opening);
            Require(fake.Disposed.Wait(2000) && fake.Disposals == 1 && !fake.Started, "Disposed preview cleans its late source without running it.");
        }

        private static async Task DeviceLoss()
        {
            var fake = new FakeSession { PendingError = "Камера отключена." };
            var reported = new TaskCompletionSource<string>();
            using (var preview = new CameraPreview(delegate { return fake; }, 2000))
            {
                preview.Error += delegate(string message) { reported.TrySetResult(message); };
                await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(await Task.WhenAny(reported.Task, Task.Delay(2000)) == reported.Task, "Device removal must be visible to UI.");
                Require((await reported.Task).Contains("отключена") && !preview.IsRunning && fake.Disposals == 1, "Asynchronous device loss closes the source before reporting.");
            }
        }

        private static void ImageBounds()
        {
            int left, top, width, height;
            DirectShowCameraSession.FitImage(960, 540, 640, 480, out left, out top, out width, out height);
            Require(left == 120 && top == 0 && width == 720 && height == 540, "4:3 camera letterboxes in wide preview without distortion.");
            DirectShowCameraSession.FitImage(640, 480, 480, 640, out left, out top, out width, out height);
            Require(left == 140 && top == 0 && width == 360 && height == 480, "Portrait camera fits within the child HWND.");
            DirectShowCameraSession.FitImage(1, 1, 0, 0, out left, out top, out width, out height);
            Require(width == 1 && height == 1 && left == 0 && top == 0, "Unknown camera dimensions retain a valid minimal viewport.");
        }

        private static async Task DeviceLossCannotCompleteNextAttempt()
        {
            var failed = new FakeSession { PendingError = "Lost", BlockDisposal = true };
            var next = new FakeSession(); int created = 0;
            var reported = new TaskCompletionSource<bool>();
            using (var preview = new CameraPreview(delegate { return ++created == 1 ? failed : next; }, 2000))
            {
                preview.Error += delegate { reported.TrySetResult(true); };
                await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(failed.DisposeEntered.Wait(2000), "Device loss entered slow native cleanup.");
                try
                {
                    await Expect<InvalidOperationException>(preview.StartAsync(Device, new IntPtr(1), 640, 480));
                    Require(!preview.Completion.IsCompleted, "A driver still disposing cannot claim release or accept a new attempt.");
                }
                finally { failed.ReleaseDisposal.Set(); }
                Require(await Task.WhenAny(reported.Task, Task.Delay(2000)) == reported.Task, "Completed old cleanup reports its error.");
                await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(preview.IsRunning && !preview.Completion.IsCompleted && next.Disposals == 0, "Old cleanup cannot complete the next attempt's resource lifetime.");
                await preview.StopAsync();
            }
        }

        private static async Task OldTimeoutCannotStopNextAttempt()
        {
            var old = new FakeSession { BlockOpening = true };
            var next = new FakeSession(); int created = 0;
            var timeoutEntered = new ManualResetEventSlim(false);
            var releaseTimeout = new ManualResetEventSlim(false);
            using (var preview = new CameraPreview(delegate { return ++created == 1 ? old : next; }, 120, null,
                delegate { timeoutEntered.Set(); releaseTimeout.Wait(); }))
            {
                Task oldOpening = preview.StartAsync(Device, new IntPtr(1), 640, 480);
                Require(timeoutEntered.Wait(2000), "Old timeout continuation is deliberately delayed.");
                old.Release.Set();
                try
                {
                    await preview.StopAsync();
                    await preview.StartAsync(Device, new IntPtr(1), 640, 480);
                }
                finally { releaseTimeout.Set(); }
                await Expect<TimeoutException>(oldOpening);
                Require(preview.IsRunning && !preview.Completion.IsCompleted && next.Disposals == 0, "Stale timeout cannot stop or release a newer explicitly started session.");
                await preview.StopAsync();
            }
            timeoutEntered.Dispose(); releaseTimeout.Dispose();
        }

        private static void ErrorMessages()
        {
            Require(DirectShowCameraSession.DescribeError(unchecked((int)0x80070005), "Start").Contains("классических"), "Permission errors explain desktop app camera setting.");
            Require(DirectShowCameraSession.DescribeError(unchecked((int)0x800700AA), "Start").Contains("занята"), "Busy camera is distinguished from no devices.");
            Require(DirectShowCameraSession.DescribeError(unchecked((int)0x87654321), "Start").Contains("0x87654321"), "Unknown vendor errors preserve their HRESULT.");
        }

        private static void EmptyGraphInterfaces()
        {
            // Empty graph only: validates COM ABI without enumerating/binding a capture filter or Run().
            object graph = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770")));
            try
            {
                int state; int code; IntPtr first, second;
                Require(((ICameraMediaControl)graph).GetState(0, out state) >= 0 && state == 0, "Dual IMediaControl vtable returns stopped state for an empty graph.");
                Require(((ICameraMediaEvent)graph).GetEvent(out code, out first, out second, 0) == unchecked((int)0x80004004), "Empty event queue returns E_ABORT with correct x64 ABI.");
                int windowStyle;
                int result = ((ICameraVideoWindow)graph).get_WindowStyle(out windowStyle);
                Require(result < 0, "IVideoWindow reports that the empty graph has no renderer; HRESULT=0x" + unchecked((uint)result).ToString("X8"));
            }
            finally { Marshal.FinalReleaseComObject(graph); }
        }

        private static void ReleasingOneComOwnerPreservesOtherOwner()
        {
            // A second incoming interface can map to the same RCW, as device
            // monikers/property bags can. No device, source or graph Run is used.
            object original = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770")));
            object incoming = null;
            IntPtr pointer = IntPtr.Zero;
            try
            {
                pointer = Marshal.GetIUnknownForObject(original);
                incoming = Marshal.GetObjectForIUnknown(pointer);
                Require(Object.ReferenceEquals(original, incoming), "Two incoming references exercise the same native COM wrapper.");
                DirectShowCameraSession.Release(ref incoming);
                Require(incoming == null, "Release clears only the relinquished owner.");
                int state;
                Require(((ICameraMediaControl)original).GetState(0, out state) >= 0 && state == 0,
                    "Releasing a property-bag-like incoming reference must not disconnect the still-owned COM wrapper.");
            }
            finally
            {
                if (pointer != IntPtr.Zero) { Marshal.Release(pointer); }
                if (incoming != null) { Marshal.ReleaseComObject(incoming); }
                if (original != null && Marshal.IsComObject(original)) { Marshal.ReleaseComObject(original); }
            }
        }

        private static async Task<Exception> Expect<T>(Task task) where T : Exception
        {
            try { await task; }
            catch (T error) { return error; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }
        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

        private static void FrameFormatBounds()
        {
            var format = new CameraBitmapInfo { Size = 40, Width = 3, Height = 2, Planes = 1, BitCount = 24 };
            Require(CameraFrameReceiver.RequiredBytes(format) == 24, "RGB24 rows must include four-byte stride padding.");
            format.Height = -2;
            Require(CameraFrameReceiver.RequiredBytes(format) == 24, "Top-down DIB keeps a signed height but positive allocation.");
            format.BitCount = 32;
            Require(CameraFrameReceiver.RequiredBytes(format) == 24, "RGB32 has no extra alignment beyond each pixel.");
            foreach (int badHeight in new[] { 0, Int32.MinValue, 2161, -2161 })
            {
                format.Height = badHeight;
                try { CameraFrameReceiver.RequiredBytes(format); throw new Exception("Invalid frame height accepted."); }
                catch (InvalidOperationException) { }
            }
            format.Height = 480; format.Width = 1000000;
            try { CameraFrameReceiver.RequiredBytes(format); throw new Exception("Unbounded allocation accepted."); }
            catch (InvalidOperationException) { }
            format.Width = 640; format.Compression = 1;
            try { CameraFrameReceiver.RequiredBytes(format); throw new Exception("Compressed pixels accepted as RGB."); }
            catch (InvalidOperationException) { }
            Require(Marshal.SizeOf(typeof(CameraBitmapInfo)) == 40 && Marshal.SizeOf(typeof(CameraVideoInfo)) == 88,
                "Bitmap and VideoInfo ABI must match the Windows SDK, including the 64-bit frame interval.");
            Require(Marshal.SizeOf(typeof(CameraMediaType)) == (IntPtr.Size == 8 ? 88 : 72), "AM_MEDIA_TYPE must preserve native pointer padding.");
        }

        private static void FrameDeliveryAndCleanup()
        {
            var format = new CameraBitmapInfo { Size = 40, Width = 3, Height = -2, Planes = 1, BitCount = 24 };
            var receiver = new CameraFrameReceiver(format);
            var bytes = new byte[24];
            // Padding is bright while actual pixels are black. Padding must not count.
            bytes[9] = bytes[10] = bytes[11] = bytes[21] = bytes[22] = bytes[23] = 255;
            IntPtr native = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, native, bytes.Length);
                receiver.BufferCB(0, IntPtr.Zero, bytes.Length);
                receiver.BufferCB(0, native, bytes.Length - 1);
                Require(!receiver.HasFrame && receiver.Count == 0 && receiver.CopyFrame() == null,
                    "Null/truncated samples cannot report a successful camera start.");
                receiver.BufferCB(0, native, bytes.Length);
                Require(receiver.HasFrame && receiver.Count == 1 && receiver.IsDarkFrame, "A valid dark frame is real delivery, with a separate darkness warning.");
                Require(!receiver.Stalled(5), "New valid sample is not marked stalled.");
                byte[] displayed = receiver.CopyFrame();
                Require(displayed.Length == bytes.Length && displayed[9] == 255, "Copy retains the exact padded DIB bytes without writing a file.");
                bytes[0] = 160; Marshal.Copy(bytes, 0, native, bytes.Length);
                receiver.BufferCB(1, native, bytes.Length);
                Require(receiver.Count == 2 && !receiver.IsDarkFrame && receiver.CopyFrame()[0] == 160, "Successive bright samples update preview and dark guidance.");
                Thread.Sleep(2);
                Require(receiver.Stalled(0), "No newer sample can be detected independently of graph Run state.");
                receiver.Stop();
                receiver.BufferCB(2, native, bytes.Length);
                Require(!receiver.HasFrame && receiver.Count == 2 && receiver.CopyFrame() == null && displayed[0] == 0,
                    "Stop wipes preview RAM, rejects late callbacks and cannot revive a stopped camera.");
            }
            finally { receiver.Stop(); Marshal.FreeHGlobal(native); }
            var bgra = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 };
            Require(CameraFrameReceiver.IsDark(bgra, 2, 1, 32), "Opaque alpha must not make a black RGB32 frame appear bright.");
        }

        private static void FrameCallbackRaces()
        {
            var receiver = new CameraFrameReceiver(new CameraBitmapInfo { Width = 4, Height = 2, Planes = 1, BitCount = 24 });
            IntPtr pixels = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(new byte[24], 0, pixels, 24);
                Task stream = Task.Run(delegate { for (int i = 0; i < 500; i++) { receiver.BufferCB(i, pixels, 24); } });
                for (int i = 0; i < 50; i++) { receiver.CopyFrame(); }
                receiver.Stop();
                stream.GetAwaiter().GetResult();
                long stoppedCount = receiver.Count;
                receiver.BufferCB(1000, pixels, 24);
                Require(receiver.Count == stoppedCount && !receiver.HasFrame && receiver.Failure == null, "Callback/paint/stop race remains bounded and ignores late driver delivery.");
            }
            finally { receiver.Stop(); Marshal.FreeHGlobal(pixels); }
        }

        private static void ConservativeFormatRanking()
        {
            Require(DirectShowCameraSession.FormatScore(640, 480, 333333) < DirectShowCameraSession.FormatScore(1920, 1080, 333333),
                "Preview prefers conservative bandwidth to full HD when both are advertised.");
            Require(DirectShowCameraSession.FormatScore(640, 480, 333333) < DirectShowCameraSession.FormatScore(640, 480, 166666),
                "Preview avoids unnecessary 60fps bandwidth.");
            Require(DirectShowCameraSession.FormatScore(640, -480, 333333) == DirectShowCameraSession.FormatScore(640, 480, 333333),
                "Orientation does not alter format preference.");
            Require(DirectShowCameraSession.FormatScore(640, Int32.MinValue, 333333) == Int64.MaxValue,
                "Malformed advertised dimensions are rejected without integer overflow.");
        }

        private static void EmptyGrabberInterfaces()
        {
            object grabber = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C1F400A0-3F08-11D3-9F0B-006008039E37")));
            var callback = new CameraFrameReceiver(new CameraBitmapInfo { Width = 4, Height = 2, Planes = 1, BitCount = 24 });
            try
            {
                var api = (ICameraSampleGrabber)grabber;
                var type = new CameraMediaType { MajorType = new Guid("73646976-0000-0010-8000-00AA00389B71"), SubType = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770"), FormatType = new Guid("05589F80-C356-11CE-BF01-00AA0055595A") };
                Require(api.SetMediaType(ref type) >= 0 && api.SetOneShot(false) >= 0 && api.SetBufferSamples(false) >= 0,
                    "Sample Grabber COM ABI accepts RGB24 callback mode without any camera.");
                Require(api.SetCallback(callback, 1) >= 0 && api.SetCallback(null, 1) >= 0, "Managed callback COM interface attaches and detaches safely.");
                CameraMediaType connected;
                int result = api.GetConnectedMediaType(out connected);
                try { Require(result < 0, "Unconnected grabber cannot claim a negotiated camera format."); }
                finally { connected.Free(); }
            }
            finally { callback.Stop(); Marshal.ReleaseComObject(grabber); }
        }

        private sealed class FakeSession : ICameraPreviewSession, ICameraPreviewStatus
        {
            public string Status { get { CheckMetadataThread(); return "Synthetic preview"; } }
            public long FrameCount { get { CheckMetadataThread(); return 2; } }
            public long PaintedFrameCount { get { CheckMetadataThread(); return 1; } }
            private void CheckMetadataThread() { Require(Thread.CurrentThread.ManagedThreadId == OwnerThread, "Session metadata access belongs on camera STA."); }
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim Disposed = new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim Resized = new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim DisposeEntered = new ManualResetEventSlim(false);
            internal readonly ManualResetEventSlim ReleaseDisposal = new ManualResetEventSlim(false);
            internal bool BlockOpening, BlockDisposal, Started;
            internal Exception OpenFailure;
            internal string PendingError;
            internal int Disposals, OwnerThread, DisposeThread, ResizeThread, LastWidth, LastHeight;
            internal ApartmentState StartApartment;
            public void Start(CameraDevice device, IntPtr childWindow, int width, int height, Func<bool> current)
            {
                OwnerThread = Thread.CurrentThread.ManagedThreadId;
                StartApartment = Thread.CurrentThread.GetApartmentState();
                Entered.Set();
                if (BlockOpening) { Release.Wait(); }
                if (!current()) { throw new OperationCanceledException(); }
                if (OpenFailure != null) { throw OpenFailure; }
                Started = true;
            }
            public void Resize(int width, int height) { ResizeThread = Thread.CurrentThread.ManagedThreadId; LastWidth = width; LastHeight = height; Resized.Set(); }
            public string ReadError() { string error = PendingError; PendingError = null; return error; }
            public void Dispose()
            {
                DisposeThread = Thread.CurrentThread.ManagedThreadId; DisposeEntered.Set();
                if (BlockDisposal) { ReleaseDisposal.Wait(); }
                Interlocked.Increment(ref Disposals); Disposed.Set();
            }
        }
    }
}
