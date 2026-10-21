using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PcPulse;

namespace PcPulseTests.Live
{
    public sealed class CameraRendererEvidence
    {
        public int FilterIndex { get; set; }
        public string ClassId { get; set; }
        public int? FramesDrawn { get; set; }
        public int? DroppedFrames { get; set; }
        public double? AverageFramesPerSecond { get; set; }
    }

    public sealed class CameraEvidenceSnapshot
    {
        public DateTime SampledAtUtc { get; set; }
        public long MonotonicTimestamp { get; set; }
        public int SessionGeneration { get; set; }
        public bool IsRunning { get; set; }
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }
        public int WorkerThreadId { get; set; }
        public string Apartment { get; set; }
        public List<string> FilterClassIds { get; set; }
        public List<CameraRendererEvidence> Renderers { get; set; }
        public List<string> Errors { get; set; }
        public long? CapturedFrames { get; set; }
        public long? GdiPaints { get; set; }
        public bool FrameCounterAvailable
        {
            get { return (CapturedFrames.HasValue && GdiPaints.HasValue) || Renderers.Exists(delegate(CameraRendererEvidence item) { return item.FramesDrawn.HasValue; }); }
        }
        public long? FramesDrawn
        {
            get
            {
                if (GdiPaints.HasValue) { return GdiPaints; }
                long total = 0;
                bool available = false;
                foreach (CameraRendererEvidence item in Renderers)
                { if (item.FramesDrawn.HasValue) { total += item.FramesDrawn.Value; available = true; } }
                return available ? (long?)total : null;
            }
        }
        public CameraEvidenceSnapshot()
        {
            FilterClassIds = new List<string>();
            Renderers = new List<CameraRendererEvidence>();
            Errors = new List<string>();
        }
    }

    // Test-only, read-only observer of an already explicitly started preview.
    // No image/sample buffer is read, and no graph/filter is created or started.
    public static class CameraEvidence
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Guid FilterGraphId = new Guid("56A8689F-0AD4-11CE-B03A-0020AF0BA770");
        private static readonly Guid QualityId = new Guid("1BD0ECB0-F8E2-11CE-AAC6-0020AF0B99A3");
        private static readonly Guid PersistId = new Guid("0000010C-0000-0000-C000-000000000046");

        public static async Task<CameraEvidenceSnapshot> ReadAsync(CameraPreview camera, int timeoutMilliseconds = 3000)
        {
            if (camera == null) { throw new ArgumentNullException("camera"); }
            if (timeoutMilliseconds < 1) { throw new ArgumentOutOfRangeException("timeoutMilliseconds"); }
            CameraEvidenceSnapshot result = null;
            int requestedGeneration;
            object gate = Field(camera, "gate");
            Task operation;
            lock (gate)
            {
                requestedGeneration = (int)Field(camera, "generation");
                if (!camera.IsRunning) { return Unavailable(requestedGeneration, "Preview is not running."); }
                Action inspect = delegate { result = ReadOnCameraSta(camera, requestedGeneration); };
                try
                {
                    operation = (Task)typeof(CameraPreview).GetMethod("Queue", PrivateInstance).Invoke(camera, new object[] { inspect });
                }
                catch (Exception error) { return Unavailable(requestedGeneration, Describe(error)); }
            }
            Observe(operation);
            if (await Task.WhenAny(operation, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != operation)
            { return Unavailable(requestedGeneration, "Frame-counter query timed out; it did not stop or restart the camera."); }
            try { await operation.ConfigureAwait(false); }
            catch (Exception error) { return Unavailable(requestedGeneration, Describe(error)); }
            return result ?? Unavailable(requestedGeneration, "Camera query produced no result.");
        }

        public static bool FramesAdvanced(CameraEvidenceSnapshot before, CameraEvidenceSnapshot after)
        {
            if (before == null || after == null || !before.IsRunning || !after.IsRunning
                || before.SessionGeneration != after.SessionGeneration || after.MonotonicTimestamp <= before.MonotonicTimestamp)
            { return false; }
            if (before.CapturedFrames.HasValue && after.CapturedFrames.HasValue && before.GdiPaints.HasValue && after.GdiPaints.HasValue)
            { return after.CapturedFrames > before.CapturedFrames && after.GdiPaints > before.GdiPaints; }
            foreach (CameraRendererEvidence first in before.Renderers)
            {
                foreach (CameraRendererEvidence last in after.Renderers)
                {
                    if (first.FilterIndex == last.FilterIndex && first.ClassId == last.ClassId
                        && first.FramesDrawn.HasValue && last.FramesDrawn.HasValue
                        && last.FramesDrawn.Value > first.FramesDrawn.Value) { return true; }
                }
            }
            return false;
        }

        private static CameraEvidenceSnapshot ReadOnCameraSta(CameraPreview camera, int generation)
        {
            var result = new CameraEvidenceSnapshot { SessionGeneration = generation,
                WorkerThreadId = Thread.CurrentThread.ManagedThreadId, Apartment = Thread.CurrentThread.GetApartmentState().ToString() };
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            { result.Errors.Add("Query did not execute on the camera STA."); return result; }
            if (!IsCurrent(camera, generation)) { result.Errors.Add("Camera session changed before query."); return result; }
            object session = Field(camera, "session");
            if (session == null) { result.Errors.Add("No current camera session."); return result; }
            object graph = Field(session, "graphObject");
            if (graph == null) { result.Errors.Add("No current DirectShow graph."); return result; }
            result.VideoWidth = (int)Field(session, "videoWidth");
            result.VideoHeight = (int)Field(session, "videoHeight");
            // New renderer exposes only counts; no private image buffers are read.
            PropertyInfo captured = session.GetType().GetProperty("FrameCount");
            PropertyInfo painted = session.GetType().GetProperty("PaintedFrameCount");
            if (captured != null && painted != null)
            {
                result.CapturedFrames = (long)captured.GetValue(session, null);
                result.GdiPaints = (long)painted.GetValue(session, null);
            }
            IntPtr unknown = IntPtr.Zero, filterGraph = IntPtr.Zero, enumeration = IntPtr.Zero;
            try
            {
                // Raw COM pointers own exactly one reference each. No ReleaseComObject on
                // the graph's shared RCW or the production session's filter RCWs.
                unknown = Marshal.GetIUnknownForObject(graph);
                Guid graphId = FilterGraphId;
                Check(Marshal.QueryInterface(unknown, ref graphId, out filterGraph), "IFilterGraph");
                Check(Method<EnumFiltersCall>(filterGraph, 5)(filterGraph, out enumeration), "EnumFilters");
                if (enumeration == IntPtr.Zero) { throw new InvalidOperationException("No filter enumerator."); }
                int index = 0;
                while (true)
                {
                    IntPtr filter = IntPtr.Zero;
                    try
                    {
                        int status = Method<NextFilterCall>(enumeration, 3)(enumeration, 1, out filter, IntPtr.Zero);
                        if (status == 1) { break; }
                        Check(status, "EnumFilters.Next");
                        if (filter == IntPtr.Zero) { throw new InvalidOperationException("Filter enumeration returned no filter."); }
                        string classId = ReadClassId(filter);
                        result.FilterClassIds.Add(classId);
                        ReadQuality(filter, index, classId, result);
                        index++;
                        if (index > 64) { throw new InvalidOperationException("Unexpectedly large preview graph."); }
                    }
                    finally { Release(filter); }
                }
            }
            catch (Exception error) { result.Errors.Add(Describe(error)); }
            finally { Release(enumeration); Release(filterGraph); Release(unknown); }
            result.IsRunning = IsCurrent(camera, generation);
            result.SampledAtUtc = DateTime.UtcNow;
            result.MonotonicTimestamp = Stopwatch.GetTimestamp();
            if (!result.FrameCounterAvailable) { result.Errors.Add("The renderer did not expose readable capture/paint counters or IQualProp; frames are unverified."); }
            return result;
        }

        private static void ReadQuality(IntPtr filter, int index, string classId, CameraEvidenceSnapshot result)
        {
            IntPtr quality = IntPtr.Zero;
            try
            {
                Guid qualityId = QualityId;
                int status = Marshal.QueryInterface(filter, ref qualityId, out quality);
                if (status == unchecked((int)0x80004002)) { return; }
                Check(status, "IQualProp");
                var item = new CameraRendererEvidence { FilterIndex = index, ClassId = classId };
                result.Renderers.Add(item);
                // amvideo.h ABI: dropped, drawn, average rate, jitter, average sync, deviation.
                int value;
                status = Method<GetIntCall>(quality, 4)(quality, out value);
                if (status >= 0 && value >= 0) { item.FramesDrawn = value; }
                else { result.Errors.Add("get_FramesDrawn: 0x" + status.ToString("X8")); }
                status = Method<GetIntCall>(quality, 3)(quality, out value);
                if (status >= 0 && value >= 0) { item.DroppedFrames = value; }
                status = Method<GetIntCall>(quality, 5)(quality, out value);
                if (status >= 0 && value >= 0) { item.AverageFramesPerSecond = value / 100.0; }
            }
            finally { Release(quality); }
        }

        private static string ReadClassId(IntPtr filter)
        {
            IntPtr persist = IntPtr.Zero;
            try
            {
                Guid persistId = PersistId;
                if (Marshal.QueryInterface(filter, ref persistId, out persist) < 0) { return "unknown"; }
                Guid classId;
                return Method<GetClassIdCall>(persist, 3)(persist, out classId) >= 0 ? classId.ToString("D") : "unknown";
            }
            finally { Release(persist); }
        }

        private static bool IsCurrent(CameraPreview camera, int generation)
        { lock (Field(camera, "gate")) { return camera.IsRunning && (int)Field(camera, "generation") == generation; } }
        private static object Field(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, PrivateInstance);
            if (field == null) { throw new MissingFieldException(instance.GetType().FullName, name); }
            return field.GetValue(instance);
        }
        private static CameraEvidenceSnapshot Unavailable(int generation, string message)
        {
            var result = new CameraEvidenceSnapshot { SessionGeneration = generation, SampledAtUtc = DateTime.UtcNow,
                MonotonicTimestamp = Stopwatch.GetTimestamp() };
            result.Errors.Add(message);
            return result;
        }
        private static string Describe(Exception error)
        {
            var wrapper = error as TargetInvocationException;
            if (wrapper != null && wrapper.InnerException != null) { return Describe(wrapper.InnerException); }
            return error.GetType().Name + ": " + error.Message;
        }
        private static void Check(int status, string operation)
        { if (status < 0) { throw new COMException(operation + " failed: 0x" + status.ToString("X8"), status); } }
        private static void Release(IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); } }
        private static void Observe(Task operation)
        {
            operation.ContinueWith(delegate(Task failed) { var observed = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private static T Method<T>(IntPtr instance, int slot) where T : class
        {
            if (instance == IntPtr.Zero) { throw new InvalidOperationException("Null COM pointer."); }
            IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumFiltersCall(IntPtr self, out IntPtr enumeration);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NextFilterCall(IntPtr self, uint count, out IntPtr filter, IntPtr fetched);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntCall(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetClassIdCall(IntPtr self, out Guid classId);
    }
}
