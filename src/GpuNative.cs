using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PcPulse
{
    // COM slots and native layouts match the Windows SDK d3d11.h / dxgi.h.
    // No swap chain, software rasterizer, driver installation or third-party native DLL is used.
    internal sealed class GpuNative : IGpuFrameRenderer
    {
        private IntPtr device, context, texture, target, vertexShader, pixelShader, constants, rasterizer;
        private IntPtr disjointQuery, startQuery, finishQuery, queryData;
        private GCHandle constantValuesHandle;
        private readonly float[] constantValues = new float[] { 0, 1280, 720, 0 };
        private QueryAction begin, end;
        private GetQueryData getData;
        private Draw draw;
        private UpdateResource update;
        private ContextAction flush;
        private DeviceStatus removedReason;
        private bool disposed;

        internal GpuNative(GpuSnapshot selected)
        {
            try { Initialize(selected); }
            catch { Dispose(); throw; }
        }

        private void Initialize(GpuSnapshot selected)
        {
            IntPtr factory = IntPtr.Zero;
            IntPtr adapter = IntPtr.Zero;
            try
            {
                Guid factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                Check(CreateDXGIFactory1(ref factoryId, out factory), "Открыть DXGI");
                EnumAdapter enumerate = Method<EnumAdapter>(factory, 12);
                var identities = new List<AdapterIdentity>();
                for (uint index = 0; index < 32; index++)
                {
                    IntPtr candidate = IntPtr.Zero;
                    try
                    {
                        int result = enumerate(factory, index, out candidate);
                        if (result == unchecked((int)0x887A0002)) { break; }
                        Check(result, "Перечислить видеокарты DXGI");
                        AdapterDescription candidateDescription;
                        Check(Method<GetAdapterDescription>(candidate, 10)(candidate, out candidateDescription), "Прочитать DXGI-адаптер");
                        identities.Add(Identity(index, candidateDescription));
                    }
                    finally { Release(ref candidate); }
                }
                uint selectedIndex = SelectAdapterIndex(selected, identities);
                Check(enumerate(factory, selectedIndex, out adapter), "Найти выбранную видеокарту");
                AdapterDescription description;
                Check(Method<GetAdapterDescription>(adapter, 10)(adapter, out description), "Прочитать DXGI-адаптер");
                // Recheck the opened interface if the device changed during enumeration.
                SelectAdapterIndex(selected, new[] { Identity(selectedIndex, description) });
                uint featureLevel;
                CheckDeviceCreation(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, new uint[] { 0xb000 }, 1, 7,
                    out device, out featureLevel, out context));
            }
            finally { Release(ref adapter); Release(ref factory); }

            var textureDescription = new TextureDescription
            {
                Width = 1280, Height = 720, MipLevels = 1, ArraySize = 1,
                Format = 28, SampleCount = 1, BindFlags = 0x20
            };
            Check(Method<CreateTexture>(device, 5)(device, ref textureDescription, IntPtr.Zero, out texture), "Создать изображение GPU");
            Check(Method<CreateView>(device, 9)(device, texture, IntPtr.Zero, out target), "Создать цель рендеринга");
            vertexShader = CreateShader("VertexMain", "vs_5_0", 12);
            pixelShader = CreateShader("PixelMain", "ps_5_0", 15);
            var bufferDescription = new BufferDescription { ByteWidth = 16, BindFlags = 4 };
            Check(Method<CreateBuffer>(device, 3)(device, ref bufferDescription, IntPtr.Zero, out constants), "Создать параметры сцены");
            var rasterizerDescription = new RasterizerDescription { FillMode = 3, CullMode = 1, DepthClipEnable = 1 };
            Check(Method<CreateRasterizer>(device, 22)(device, ref rasterizerDescription, out rasterizer), "Создать режим растеризации");

            Method<SetShader>(context, 11)(context, vertexShader, IntPtr.Zero, 0);
            Method<SetShader>(context, 9)(context, pixelShader, IntPtr.Zero, 0);
            Method<SetObject>(context, 17)(context, IntPtr.Zero);
            Method<SetTopology>(context, 24)(context, 4);
            Method<SetTargets>(context, 33)(context, 1, ref target, IntPtr.Zero);
            Method<SetBuffers>(context, 16)(context, 0, 1, ref constants);
            Method<SetObject>(context, 43)(context, rasterizer);
            var viewport = new Viewport { Width = 1280, Height = 720, MaxDepth = 1 };
            Method<SetViewports>(context, 44)(context, 1, ref viewport);

            disjointQuery = NewQuery(3);
            startQuery = NewQuery(2);
            finishQuery = NewQuery(2);
            queryData = Marshal.AllocHGlobal(16);
            constantValuesHandle = GCHandle.Alloc(constantValues, GCHandleType.Pinned);
            begin = Method<QueryAction>(context, 27);
            end = Method<QueryAction>(context, 28);
            getData = Method<GetQueryData>(context, 29);
            draw = Method<Draw>(context, 13);
            update = Method<UpdateResource>(context, 48);
            flush = Method<ContextAction>(context, 111);
            removedReason = Method<DeviceStatus>(device, 39);
        }

        internal static uint SelectAdapterIndex(GpuSnapshot selected, IEnumerable<AdapterIdentity> adapters)
        {
            uint? match = null;
            foreach (AdapterIdentity adapter in adapters)
            {
                if (adapter.LuidLow != selected.AdapterLuidLow || adapter.LuidHigh != selected.AdapterLuidHigh) { continue; }
                // Basic Render Driver may lack SOFTWARE when it supplies the primary display.
                if ((adapter.Flags & 2) != 0 || (adapter.VendorId == 0x1414 && adapter.DeviceId == 0x008c))
                {
                    throw new InvalidOperationException("Программный GPU исключён из аппаратного теста.");
                }
                if (match.HasValue) { throw new InvalidOperationException("Не удалось однозначно определить выбранную видеокарту."); }
                match = adapter.Index;
            }
            if (!match.HasValue)
            {
                throw new InvalidOperationException("Выбранная видеокарта больше недоступна. Обновите информацию перед тестом.");
            }
            return match.Value;
        }

        internal static void CheckDeviceCreation(int result)
        {
            if (result == unchecked((int)0x887A0004))
            {
                throw new InvalidOperationException("GPU-тест требует аппаратного Direct3D feature level 11_0. Выбранная видеокарта или её драйвер не поддерживает этот режим.");
            }
            Check(result, "Создать аппаратное устройство Direct3D 11");
        }

        private static AdapterIdentity Identity(uint index, AdapterDescription description)
        {
            return new AdapterIdentity(index, description.LuidLow, description.LuidHigh, description.Flags,
                description.VendorId, description.DeviceId);
        }

        internal struct AdapterIdentity
        {
            internal readonly uint Index, LuidLow, Flags, VendorId, DeviceId;
            internal readonly int LuidHigh;

            internal AdapterIdentity(uint index, uint luidLow, int luidHigh, uint flags, uint vendorId, uint deviceId)
            {
                Index = index;
                LuidLow = luidLow;
                LuidHigh = luidHigh;
                Flags = flags;
                VendorId = vendorId;
                DeviceId = deviceId;
            }
        }

        public GpuFrameSample Render(float elapsedSeconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(removedReason(device), "Драйвер GPU прервал тест");
            constantValues[0] = elapsedSeconds;
            update(context, constants, 0, IntPtr.Zero, constantValuesHandle.AddrOfPinnedObject(), 0, 0);
            begin(context, disjointQuery);
            end(context, startQuery);
            draw(context, 3, 0);
            end(context, finishQuery);
            end(context, disjointQuery);
            flush(context);

            // Only one frame is outstanding; cancellation never leaves a long queued workload.
            WaitForQuery(disjointQuery, 16, token);
            ulong frequency = unchecked((ulong)Marshal.ReadInt64(queryData));
            bool disjoint = Marshal.ReadInt32(queryData, 8) != 0;
            WaitForQuery(startQuery, 8, token);
            ulong started = unchecked((ulong)Marshal.ReadInt64(queryData));
            WaitForQuery(finishQuery, 8, token);
            ulong finished = unchecked((ulong)Marshal.ReadInt64(queryData));
            return new GpuFrameSample(started, finished, frequency, disjoint);
        }

        private void WaitForQuery(IntPtr query, uint size, CancellationToken token)
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int result = getData(context, query, queryData, size, 1);
                if (result == 0) { return; }
                Check(result, "Прочитать GPU timestamps");
                Check(removedReason(device), "Драйвер GPU прервал тест");
                if (wait.Elapsed.TotalSeconds >= 5)
                {
                    throw new TimeoutException("GPU не завершил кадр за 5 секунд; тест остановлен.");
                }
                if (token.WaitHandle.WaitOne(1)) { token.ThrowIfCancellationRequested(); }
            }
        }

        private IntPtr NewQuery(uint kind)
        {
            var description = new QueryDescription { Query = kind };
            IntPtr result;
            Check(Method<CreateQuery>(device, 24)(device, ref description, out result), "Создать GPU timestamp query");
            return result;
        }

        private IntPtr CreateShader(string entry, string profile, int slot)
        {
            IntPtr blob = Compile(entry, profile);
            try
            {
                IntPtr shader;
                Check(Method<CreateShaderObject>(device, slot)(device, Method<BlobPointer>(blob, 3)(blob),
                    Method<BlobSize>(blob, 4)(blob), IntPtr.Zero, out shader), "Создать GPU shader");
                return shader;
            }
            finally { Release(ref blob); }
        }

        internal static void ValidateShaders()
        {
            IntPtr vertex = IntPtr.Zero;
            IntPtr pixel = IntPtr.Zero;
            try { vertex = Compile("VertexMain", "vs_5_0"); pixel = Compile("PixelMain", "ps_5_0"); }
            finally { Release(ref pixel); Release(ref vertex); }
        }

        private static IntPtr Compile(string entry, string profile)
        {
            byte[] source = Encoding.ASCII.GetBytes(ShaderSource);
            IntPtr code = IntPtr.Zero;
            IntPtr errors = IntPtr.Zero;
            try
            {
                int result = D3DCompile(source, new UIntPtr((uint)source.Length), "PCPulseRaymarchV1.hlsl", IntPtr.Zero,
                    IntPtr.Zero, entry, profile, (1u << 11) | (1u << 15), 0, out code, out errors);
                if (result < 0)
                {
                    string detail = errors == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(Method<BlobPointer>(errors, 3)(errors));
                    throw new InvalidOperationException("Не удалось скомпилировать GPU-сцену: " + detail);
                }
                IntPtr compiled = code;
                code = IntPtr.Zero;
                return compiled;
            }
            finally { Release(ref errors); Release(ref code); }
        }

        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            if (context != IntPtr.Zero) { Method<ContextAction>(context, 110)(context); }
            Release(ref disjointQuery); Release(ref startQuery); Release(ref finishQuery);
            Release(ref rasterizer); Release(ref constants); Release(ref pixelShader); Release(ref vertexShader);
            Release(ref target); Release(ref texture); Release(ref context); Release(ref device);
            if (queryData != IntPtr.Zero) { Marshal.FreeHGlobal(queryData); queryData = IntPtr.Zero; }
            if (constantValuesHandle.IsAllocated) { constantValuesHandle.Free(); }
        }

        private static T Method<T>(IntPtr instance, int slot) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T));
        }

        private static void Release(ref IntPtr instance)
        {
            if (instance != IntPtr.Zero) { Marshal.Release(instance); instance = IntPtr.Zero; }
        }

        private static void Check(int result, string operation)
        {
            if (result < 0) { throw new InvalidOperationException(operation + " (0x" + unchecked((uint)result).ToString("X8") + ")."); }
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
            [In] uint[] featureLevels, uint count, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);
        [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
        private static extern int D3DCompile([In] byte[] source, UIntPtr length, string name, IntPtr definitions,
            IntPtr include, string entry, string profile, uint flags, uint effectsFlags, out IntPtr code, out IntPtr errors);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapter(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterDescription(IntPtr self, out AdapterDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self, ref TextureDescription description, IntPtr initial, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateView(IntPtr self, IntPtr resource, IntPtr description, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShaderObject(IntPtr self, IntPtr bytes, UIntPtr length, IntPtr linkage, out IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBuffer(IntPtr self, ref BufferDescription description, IntPtr initial, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateRasterizer(IntPtr self, ref RasterizerDescription description, out IntPtr rasterizer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateQuery(IntPtr self, ref QueryDescription description, out IntPtr query);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetShader(IntPtr self, IntPtr shader, IntPtr instances, uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetObject(IntPtr self, IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTopology(IntPtr self, uint topology);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTargets(IntPtr self, uint count, ref IntPtr target, IntPtr depth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetBuffers(IntPtr self, uint start, uint count, ref IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewports(IntPtr self, uint count, ref Viewport viewport);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void QueryAction(IntPtr self, IntPtr query);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetQueryData(IntPtr self, IntPtr query, IntPtr data, uint size, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Draw(IntPtr self, uint vertices, uint start);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UpdateResource(IntPtr self, IntPtr resource, uint subresource, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ContextAction(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeviceStatus(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPointer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr BlobSize(IntPtr self);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
            public uint VendorId, DeviceId, SubSystemId, Revision;
            public UIntPtr DedicatedVideo, DedicatedSystem, Shared;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BufferDescription
        {
            public uint ByteWidth, Usage, BindFlags, CpuAccess, MiscFlags, StructureStride;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RasterizerDescription
        {
            public uint FillMode, CullMode;
            public int FrontCounterClockwise, DepthBias;
            public float DepthBiasClamp, SlopeScaledDepthBias;
            public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
        }
        [StructLayout(LayoutKind.Sequential)] private struct QueryDescription { public uint Query, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct Viewport { public float Left, Top, Width, Height, MinDepth, MaxDepth; }

        private const string ShaderSource = @"
cbuffer Scene : register(b0) { float elapsed; float width; float height; float padding; };
float4 VertexMain(uint id : SV_VertexID) : SV_Position {
    float2 corner = float2((id << 1) & 2, id & 2);
    return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
}
float distanceToScene(float3 p) {
    float angle = elapsed * 0.35;
    float2 q = float2(p.x * cos(angle) - p.z * sin(angle), p.x * sin(angle) + p.z * cos(angle));
    float3 r = float3(q.x, p.y, q.y);
    float sphere = length(r - float3(0, 0.15, 0)) - 0.85;
    float torus = length(float2(length(r.xz) - 1.42, r.y + 0.05)) - 0.21;
    float satellite = length(r - float3(1.65, 0.6, 0.75)) - 0.4;
    return min(min(sphere, torus), min(satellite, p.y + 1.0));
}
float4 PixelMain(float4 position : SV_Position) : SV_Target {
    float2 uv = (position.xy * 2 - float2(width, height)) / height;
    float3 eye = float3(0, 0.8, -4.4);
    float3 ray = normalize(float3(uv.x, -uv.y - 0.18, 1.8));
    float distance = 0;
    float glow = 0;
    [loop] for (int step = 0; step < 48; step++) {
        float3 samplePosition = eye + ray * distance;
        float field = distanceToScene(samplePosition);
        glow += exp(-abs(field) * 9) * 0.008;
        distance += clamp(field * 0.72, 0.004, 0.5);
    }
    float3 hit = eye + ray * distance;
    float e = 0.002;
    float3 normal = normalize(float3(
        distanceToScene(hit + float3(e, 0, 0)) - distanceToScene(hit - float3(e, 0, 0)),
        distanceToScene(hit + float3(0, e, 0)) - distanceToScene(hit - float3(0, e, 0)),
        distanceToScene(hit + float3(0, 0, e)) - distanceToScene(hit - float3(0, 0, e))));
    float lighting = 0.18 + 0.82 * saturate(dot(normal, normalize(float3(-0.4, 0.8, -0.6))));
    float checker = 0.5 + 0.5 * sin(hit.x * 3) * sin(hit.z * 3);
    float3 material = lerp(float3(0.05, 0.2, 0.55), float3(0.2, 0.8, 0.7), checker);
    float fog = exp(-max(0, distance - 3) * 0.25);
    return float4(saturate(material * lighting * fog + float3(0.16, 0.08, 0.3) * glow), 1);
}";
    }
}
