#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ShareX.ScreenCaptureLib
{
    internal static unsafe class GpuHdrToSdrToneMapper
    {
        private const string ShaderResourceName = "ShareX.ScreenCaptureLib.HDR.HdrToneMap.hlsl";
        private const uint D3DCompileOptimizationLevel3 = 1u << 15;
        private const int MaximumCachedPreviewSizes = 2;
        private const int SharedSessionIdleMilliseconds = 10000;
        private static readonly Lazy<CompiledShaders> Shaders = new Lazy<CompiledShaders>(CompileShaders);
        private static readonly object sharedSessionSync = new object();
        private static Session sharedSession;
        private static System.Threading.Timer sharedSessionExpirationTimer;

        public static void PrewarmShaders()
        {
            _ = Shaders.Value;
            using SessionLease lease = AcquireSharedSession();
        }

        public static SessionLease AcquireSharedSession()
        {
            System.Threading.Monitor.Enter(sharedSessionSync);

            try
            {
                sharedSessionExpirationTimer?.Change(
                    System.Threading.Timeout.Infinite,
                    System.Threading.Timeout.Infinite);
                sharedSession ??= new Session();
                return new SessionLease(sharedSession);
            }
            catch
            {
                System.Threading.Monitor.Exit(sharedSessionSync);
                throw;
            }
        }

        public static void ShutdownSharedSession()
        {
            lock (sharedSessionSync)
            {
                sharedSession?.Dispose();
                sharedSession = null;
                sharedSessionExpirationTimer?.Dispose();
                sharedSessionExpirationTimer = null;
            }
        }

        public static Bitmap ToneMap(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D sourceTexture,
            int width,
            int height,
            HdrCaptureSettings settings,
            float sdrWhiteNits,
            float displayMaxLuminanceNits)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(sourceTexture);
            ArgumentNullException.ThrowIfNull(settings);

            if (settings.PeakBrightnessMode == HdrPeakBrightnessMode.Custom &&
                settings.ToneMappingMode == HdrToneMappingMode.Uniform)
            {
                var analysis = new HdrToSdrToneMapper.ToneMapInputAnalysis(
                    HdrToSdrToneMapper.CreateToneMapParameters(
                        settings.HdrBrightnessNits,
                        settings.PeakBrightnessMode,
                        settings.ToneMappingMode,
                        sdrWhiteNits,
                        displayMaxLuminanceNits,
                        paperWhiteMode: settings.PaperWhiteMode,
                        customPaperWhiteNits: settings.PaperWhiteNits),
                    null,
                    HdrToSdrToneMapper.ContentPeakMeasurement.NotMeasured);
                HdrToSdrToneMapper.LogAnalysis("GPU", settings, analysis, displayMaxLuminanceNits);
                return ToneMapAnalyzed(
                    device,
                    context,
                    sourceTexture,
                    width,
                    height,
                    analysis,
                    preserveAlpha: false);
            }

            Texture2DDescription sourceDescription = sourceTexture.Description;
            using ID3D11Texture2D stagingInput = CreateStagingTexture(
                device,
                sourceDescription.Width,
                sourceDescription.Height,
                Format.R16G16B16A16_Float);
            context.CopyResource(stagingInput, sourceTexture);
            MappedSubresource mapped = context.Map(
                stagingInput,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);

            try
            {
                HdrToSdrToneMapper.ToneMapInputAnalysis analysis =
                    HdrToSdrToneMapper.AnalyzeToneMapInput(
                        mapped.DataPointer,
                        (int)mapped.RowPitch,
                        width,
                        height,
                        settings,
                        sdrWhiteNits,
                        displayMaxLuminanceNits);
                HdrToSdrToneMapper.LogAnalysis("GPU", settings, analysis, displayMaxLuminanceNits);
                return ToneMapAnalyzed(
                    device,
                    context,
                    sourceTexture,
                    width,
                    height,
                    analysis,
                    preserveAlpha: false);
            }
            finally
            {
                context.Unmap(stagingInput, 0);
            }
        }

        private static Bitmap ToneMapAnalyzed(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D sourceTexture,
            int width,
            int height,
            HdrToSdrToneMapper.ToneMapInputAnalysis analysis,
            bool preserveAlpha,
            ToneMapResources reusableResources = null,
            bool sourceAlreadyUploaded = false)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            Stopwatch resourceTimer = Stopwatch.StartNew();
            HdrToSdrToneMapper.ToneMapParameters parameters = analysis.Parameters;
            using ToneMapResources ownedResources = reusableResources == null
                ? new ToneMapResources(device, width, height)
                : null;
            ToneMapResources resources = reusableResources ?? ownedResources;

            if (!sourceAlreadyUploaded)
            {
                ArgumentNullException.ThrowIfNull(sourceTexture);
                context.CopyResource(resources.ShaderInput, sourceTexture);
            }

            bool useToneMapMask = analysis.ToneMapMask != null;
            if (useToneMapMask)
            {
                context.UpdateSubresource(
                    analysis.ToneMapMask,
                    resources.ToneMapMask,
                    0,
                    (uint)width);
            }

            var constants = new GpuToneMapConstants(
                parameters,
                useToneMapMask,
                preserveAlpha,
                analysis.DefaultToneMapAmount);
            context.UpdateSubresource(
                resources.Constants,
                0,
                null,
                (IntPtr)(&constants),
                0,
                0);
            resourceTimer.Stop();

            try
            {
                Stopwatch submitTimer = Stopwatch.StartNew();
                context.OMSetRenderTargets(resources.OutputView);
                context.RSSetViewport(new Viewport(width, height));
                context.IASetInputLayout(null);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(resources.VertexShader);
                context.PSSetShader(resources.PixelShader);
                context.PSSetConstantBuffer(0, resources.Constants);
                context.PSSetShaderResource(0, resources.SourceView);
                context.PSSetShaderResource(1, useToneMapMask ? resources.MaskView : null);
                context.Draw(3, 0);

                context.PSSetShaderResource(0, null);
                context.PSSetShaderResource(1, null);
                context.OMSetRenderTargets((ID3D11RenderTargetView)null);
                context.CopyResource(resources.StagingOutput, resources.Output);
                submitTimer.Stop();

                Stopwatch readbackTimer = Stopwatch.StartNew();
                Bitmap bitmap = ReadBgra8Bitmap(
                    context,
                    resources.StagingOutput,
                    width,
                    height);
                readbackTimer.Stop();
                totalTimer.Stop();
                LogPerformance(
                    $"HDR GPU preview stages | size={width}x{height} " +
                    $"reused={reusableResources != null} " +
                    $"resourcesMs={resourceTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"submitMs={submitTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"readbackMs={readbackTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                return bitmap;
            }
            finally
            {
                // Never release resources while the immediate context retains
                // references to them. This device is private to one capture.
                context.ClearState();
                context.Flush();
            }
        }

        private static void LogPerformance(string message)
        {
            DebugHelper.WriteLine(message);
            if (string.Equals(
                Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_CAPTURE_PERFORMANCE_TESTS"),
                "1",
                StringComparison.Ordinal))
            {
                Console.WriteLine(message);
            }
        }

        private sealed class ToneMapResources : IDisposable
        {
            public ID3D11Texture2D ShaderInput { get; private set; }
            public ID3D11Texture2D ToneMapMask { get; private set; }
            public ID3D11Texture2D Output { get; private set; }
            public ID3D11Texture2D StagingOutput { get; private set; }
            public ID3D11VertexShader VertexShader { get; private set; }
            public ID3D11PixelShader PixelShader { get; private set; }
            public ID3D11Buffer Constants { get; private set; }
            public ID3D11ShaderResourceView SourceView { get; private set; }
            public ID3D11ShaderResourceView MaskView { get; private set; }
            public ID3D11RenderTargetView OutputView { get; private set; }

            public ToneMapResources(ID3D11Device device, int width, int height)
            {
                try
                {
                    CompiledShaders shaders = Shaders.Value;
                    ShaderInput = CreateTexture(
                        device,
                        (uint)width,
                        (uint)height,
                        Format.R16G16B16A16_Float,
                        BindFlags.ShaderResource);
                    ToneMapMask = CreateTexture(
                        device,
                        (uint)width,
                        (uint)height,
                        Format.R8_UNorm,
                        BindFlags.ShaderResource);
                    Output = CreateTexture(
                        device,
                        (uint)width,
                        (uint)height,
                        Format.B8G8R8A8_UNorm,
                        BindFlags.RenderTarget);
                    StagingOutput = CreateStagingTexture(
                        device,
                        (uint)width,
                        (uint)height,
                        Format.B8G8R8A8_UNorm);
                    VertexShader = device.CreateVertexShader(shaders.Vertex);
                    PixelShader = device.CreatePixelShader(shaders.Pixel);
                    Constants = device.CreateBuffer(
                        new GpuToneMapConstants[1],
                        BindFlags.ConstantBuffer);
                    SourceView = device.CreateShaderResourceView(ShaderInput);
                    MaskView = device.CreateShaderResourceView(ToneMapMask);
                    OutputView = device.CreateRenderTargetView(Output);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                OutputView?.Dispose();
                OutputView = null;
                MaskView?.Dispose();
                MaskView = null;
                SourceView?.Dispose();
                SourceView = null;
                Constants?.Dispose();
                Constants = null;
                PixelShader?.Dispose();
                PixelShader = null;
                VertexShader?.Dispose();
                VertexShader = null;
                StagingOutput?.Dispose();
                StagingOutput = null;
                Output?.Dispose();
                Output = null;
                ToneMapMask?.Dispose();
                ToneMapMask = null;
                ShaderInput?.Dispose();
                ShaderInput = null;
            }
        }

        internal sealed class Session : IDisposable
        {
            private readonly ID3D11Device device;
            private readonly ID3D11DeviceContext context;
            private readonly Dictionary<System.Drawing.Size, ToneMapResources> resourceCache =
                new Dictionary<System.Drawing.Size, ToneMapResources>();
            private bool disposed;

            public bool IsDisposed => disposed;

            public Session()
            {
                D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    null,
                    out device,
                    out _,
                    out context).CheckError();
            }

            public Bitmap ToneMap(
                IntPtr source,
                int sourceRowPitch,
                int width,
                int height,
                HdrCaptureSettings settings,
                float sdrWhiteNits,
                float displayMaxLuminanceNits,
                bool preserveAlpha,
                System.Collections.Generic.IReadOnlyList<HdrWindowRegion> windowRegions,
                out HdrToSdrToneMapper.ToneMapInputAnalysis analysis)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Stopwatch analysisTimer = Stopwatch.StartNew();
                analysis = HdrToSdrToneMapper.AnalyzeToneMapInput(
                    source,
                    sourceRowPitch,
                    width,
                    height,
                    settings,
                    sdrWhiteNits,
                    displayMaxLuminanceNits,
                    preserveAlpha,
                    windowRegions);
                analysisTimer.Stop();
                HdrToSdrToneMapper.LogAnalysis("GPU", settings, analysis, displayMaxLuminanceNits);
                Stopwatch uploadTimer = Stopwatch.StartNew();
                ToneMapResources resources = GetResources(width, height);
                context.UpdateSubresource(
                    resources.ShaderInput,
                    0,
                    null,
                    source,
                    (uint)sourceRowPitch,
                    0);
                uploadTimer.Stop();
                Stopwatch renderTimer = Stopwatch.StartNew();
                Bitmap bitmap = ToneMapAnalyzed(
                    device,
                    context,
                    sourceTexture: null,
                    width,
                    height,
                    analysis,
                    preserveAlpha,
                    resources,
                    sourceAlreadyUploaded: true);
                renderTimer.Stop();
                LogPerformance(
                    $"HDR GPU preview input | size={width}x{height} " +
                    $"analysisMs={analysisTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"uploadMs={uploadTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"renderMs={renderTimer.Elapsed.TotalMilliseconds:F1}");
                return bitmap;
            }

            public Bitmap ToneMapKnownSdr(
                IntPtr source,
                int sourceRowPitch,
                int width,
                int height,
                float paperWhiteNits,
                bool preserveAlpha)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                HdrToSdrToneMapper.ToneMapInputAnalysis analysis =
                    HdrToSdrToneMapper.CreateKnownSdrAnalysis(paperWhiteNits);
                Stopwatch uploadTimer = Stopwatch.StartNew();
                ToneMapResources resources = GetResources(width, height);
                context.UpdateSubresource(
                    resources.ShaderInput,
                    0,
                    null,
                    source,
                    (uint)sourceRowPitch,
                    0);
                uploadTimer.Stop();
                Stopwatch renderTimer = Stopwatch.StartNew();
                Bitmap bitmap = ToneMapAnalyzed(
                    device,
                    context,
                    sourceTexture: null,
                    width,
                    height,
                    analysis,
                    preserveAlpha,
                    resources,
                    sourceAlreadyUploaded: true);
                renderTimer.Stop();
                LogPerformance(
                    $"HDR GPU preview input | size={width}x{height} analysisMs=0.0 " +
                    $"uploadMs={uploadTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"renderMs={renderTimer.Elapsed.TotalMilliseconds:F1}");
                return bitmap;
            }

            private ToneMapResources GetResources(int width, int height)
            {
                var size = new System.Drawing.Size(width, height);
                if (!resourceCache.TryGetValue(size, out ToneMapResources resources))
                {
                    if (resourceCache.Count >= MaximumCachedPreviewSizes)
                    {
                        foreach (ToneMapResources cachedResources in resourceCache.Values)
                        {
                            cachedResources.Dispose();
                        }

                        resourceCache.Clear();
                    }

                    resources = new ToneMapResources(device, width, height);
                    resourceCache.Add(size, resources);
                }

                return resources;
            }

            public void Dispose()
            {
                if (!disposed)
                {
                    context.ClearState();
                    context.Flush();

                    foreach (ToneMapResources resources in resourceCache.Values)
                    {
                        resources.Dispose();
                    }

                    resourceCache.Clear();
                    context.Dispose();
                    device.Dispose();
                    disposed = true;
                }
            }
        }

        internal sealed class SessionLease : IDisposable
        {
            private Session session;

            public Session Session => session ??
                throw new ObjectDisposedException(nameof(SessionLease));

            public SessionLease(Session session)
            {
                this.session = session;
            }

            public void Dispose()
            {
                Session released = System.Threading.Interlocked.Exchange(ref session, null);
                if (released == null)
                {
                    return;
                }

                if (released.IsDisposed && ReferenceEquals(sharedSession, released))
                {
                    sharedSession = null;
                }

                if (!released.IsDisposed)
                {
                    sharedSessionExpirationTimer ??= new System.Threading.Timer(
                        _ => ShutdownSharedSession(),
                        null,
                        System.Threading.Timeout.Infinite,
                        System.Threading.Timeout.Infinite);
                    sharedSessionExpirationTimer.Change(
                        SharedSessionIdleMilliseconds,
                        System.Threading.Timeout.Infinite);
                }

                System.Threading.Monitor.Exit(sharedSessionSync);
            }
        }

        private static ID3D11Texture2D CreateTexture(
            ID3D11Device device,
            uint width,
            uint height,
            Format format,
            BindFlags bindFlags)
        {
            Texture2DDescription description = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bindFlags,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            return device.CreateTexture2D(description);
        }

        private static ID3D11Texture2D CreateStagingTexture(
            ID3D11Device device,
            uint width,
            uint height,
            Format format)
        {
            Texture2DDescription description = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            return device.CreateTexture2D(description);
        }

        private static Bitmap ReadBgra8Bitmap(
            ID3D11DeviceContext context,
            ID3D11Texture2D texture,
            int width,
            int height)
        {
            MappedSubresource mapped = context.Map(
                texture,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);

            try
            {
                Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                try
                {
                    BitmapData destination = bitmap.LockBits(
                        new Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);

                    try
                    {
                        int destinationPitch = Math.Abs(destination.Stride);
                        int rowBytes = checked(width * 4);

                        for (int y = 0; y < height; y++)
                        {
                            byte* sourceRow = (byte*)mapped.DataPointer + y * mapped.RowPitch;
                            byte* destinationRow = destination.Stride >= 0
                                ? (byte*)destination.Scan0 + y * destination.Stride
                                : (byte*)destination.Scan0 + (height - 1 - y) * destinationPitch;
                            Buffer.MemoryCopy(sourceRow, destinationRow, destinationPitch, rowBytes);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(destination);
                    }

                    return bitmap;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }
            finally
            {
                context.Unmap(texture, 0);
            }
        }

        private static CompiledShaders CompileShaders()
        {
            Assembly assembly = typeof(GpuHdrToSdrToneMapper).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(ShaderResourceName)
                ?? throw new InvalidOperationException("The embedded HDR GPU shader was not found.");
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            string source = reader.ReadToEnd();

            return new CompiledShaders(
                CompileShader(source, "VertexMain", "vs_5_0"),
                CompileShader(source, "PixelMain", "ps_5_0"));
        }

        private static byte[] CompileShader(string source, string entryPoint, string target)
        {
            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            IntPtr code = IntPtr.Zero;
            IntPtr errors = IntPtr.Zero;

            fixed (byte* sourcePointer = sourceBytes)
            {
                int result = D3DCompile(
                    (IntPtr)sourcePointer,
                    (UIntPtr)sourceBytes.Length,
                    ShaderResourceName,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    entryPoint,
                    target,
                    D3DCompileOptimizationLevel3,
                    0,
                    out code,
                    out errors);

                try
                {
                    if (result < 0)
                    {
                        string message = errors != IntPtr.Zero
                            ? Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0', '\r', '\n')
                            : Marshal.GetExceptionForHR(result)?.Message;
                        throw new InvalidOperationException($"HDR GPU shader compilation failed: {message}");
                    }

                    return ReadBlob(code);
                }
                finally
                {
                    if (errors != IntPtr.Zero) Marshal.Release(errors);
                    if (code != IntPtr.Zero) Marshal.Release(code);
                }
            }
        }

        private static byte[] ReadBlob(IntPtr blob)
        {
            IntPtr vtable = Marshal.ReadIntPtr(blob);
            GetBufferPointerDelegate getBufferPointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(
                Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            GetBufferSizeDelegate getBufferSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(
                Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));
            IntPtr buffer = getBufferPointer(blob);
            int length = checked((int)getBufferSize(blob).ToUInt64());
            byte[] result = new byte[length];
            Marshal.Copy(buffer, result, 0, length);
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct GpuToneMapConstants
        {
            public readonly float PaperWhiteScRgb;
            public readonly float InputMaximum;
            public readonly float OutputWhite;
            public readonly float CurveXA;
            public readonly float CurveXB;
            public readonly float CurveYA;
            public readonly float CurveYB;
            public readonly uint UseToneMapMask;
            public readonly uint PreserveAlpha;
            public readonly float DefaultToneMapAmount;
            public readonly uint Padding1;
            public readonly uint Padding2;

            public GpuToneMapConstants(
                HdrToSdrToneMapper.ToneMapParameters parameters,
                bool useToneMapMask,
                bool preserveAlpha,
                float defaultToneMapAmount)
            {
                PaperWhiteScRgb = parameters.PaperWhiteScRgb;
                InputMaximum = parameters.InputMaximum;
                OutputWhite = parameters.OutputWhite;
                CurveXA = parameters.CurveXA;
                CurveXB = parameters.CurveXB;
                CurveYA = parameters.CurveYA;
                CurveYB = parameters.CurveYB;
                UseToneMapMask = useToneMapMask ? 1u : 0u;
                PreserveAlpha = preserveAlpha ? 1u : 0u;
                DefaultToneMapAmount = defaultToneMapAmount;
                Padding1 = 0;
                Padding2 = 0;
            }
        }

        private sealed class CompiledShaders
        {
            public byte[] Vertex { get; }
            public byte[] Pixel { get; }

            public CompiledShaders(byte[] vertex, byte[] pixel)
            {
                Vertex = vertex;
                Pixel = pixel;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetBufferPointerDelegate(IntPtr blob);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate UIntPtr GetBufferSizeDelegate(IntPtr blob);

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(
            IntPtr sourceData,
            UIntPtr sourceDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string sourceName,
            IntPtr defines,
            IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
            [MarshalAs(UnmanagedType.LPStr)] string target,
            uint flags1,
            uint flags2,
            out IntPtr code,
            out IntPtr errorMessages);
    }
}
