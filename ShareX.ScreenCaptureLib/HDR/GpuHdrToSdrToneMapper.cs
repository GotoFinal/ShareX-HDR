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

using System;
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
        private static readonly Lazy<CompiledShaders> Shaders = new Lazy<CompiledShaders>(CompileShaders);

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
            bool preserveAlpha)
        {
            CompiledShaders shaderBytecode = Shaders.Value;
            Texture2DDescription sourceDescription = sourceTexture.Description;
            HdrToSdrToneMapper.ToneMapParameters parameters = analysis.Parameters;
            using ID3D11Texture2D shaderInput = CreateTexture(
                device,
                sourceDescription.Width,
                sourceDescription.Height,
                Format.R16G16B16A16_Float,
                BindFlags.ShaderResource);
            context.CopyResource(shaderInput, sourceTexture);
            using ID3D11Texture2D toneMapMask = analysis.ToneMapMask != null
                ? CreateToneMapMask(device, context, analysis.ToneMapMask, width, height)
                : null;
            using ID3D11Texture2D output = CreateTexture(
                device,
                (uint)width,
                (uint)height,
                Format.B8G8R8A8_UNorm,
                BindFlags.RenderTarget);
            using ID3D11Texture2D stagingOutput = CreateStagingTexture(
                device,
                (uint)width,
                (uint)height,
                Format.B8G8R8A8_UNorm);
            using ID3D11VertexShader vertexShader = device.CreateVertexShader(shaderBytecode.Vertex);
            using ID3D11PixelShader pixelShader = device.CreatePixelShader(shaderBytecode.Pixel);
            using ID3D11Buffer constants = device.CreateBuffer(
                new GpuToneMapConstants[]
                {
                    new GpuToneMapConstants(parameters, toneMapMask != null, preserveAlpha)
                },
                BindFlags.ConstantBuffer);
            using ID3D11ShaderResourceView sourceView = device.CreateShaderResourceView(shaderInput);
            using ID3D11ShaderResourceView maskView = toneMapMask != null
                ? device.CreateShaderResourceView(toneMapMask)
                : null;
            using ID3D11RenderTargetView outputView = device.CreateRenderTargetView(output);

            try
            {
                context.OMSetRenderTargets(outputView);
                context.RSSetViewport(new Viewport(width, height));
                context.IASetInputLayout(null);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.VSSetShader(vertexShader);
                context.PSSetShader(pixelShader);
                context.PSSetConstantBuffer(0, constants);
                context.PSSetShaderResource(0, sourceView);
                context.PSSetShaderResource(1, maskView);
                context.Draw(3, 0);

                context.PSSetShaderResource(0, null);
                context.PSSetShaderResource(1, null);
                context.OMSetRenderTargets((ID3D11RenderTargetView)null);
                context.CopyResource(stagingOutput, output);

                return ReadBgra8Bitmap(context, stagingOutput, width, height);
            }
            finally
            {
                // Never release resources while the immediate context retains
                // references to them. This device is private to one capture.
                context.ClearState();
                context.Flush();
            }
        }

        private static ID3D11Texture2D CreateToneMapMask(
            ID3D11Device device,
            ID3D11DeviceContext context,
            byte[] mask,
            int width,
            int height)
        {
            ID3D11Texture2D maskTexture = CreateTexture(
                device,
                (uint)width,
                (uint)height,
                Format.R8_UNorm,
                BindFlags.ShaderResource);

            try
            {
                context.UpdateSubresource(mask, maskTexture, 0, (uint)width);
                return maskTexture;
            }
            catch
            {
                maskTexture.Dispose();
                throw;
            }
        }

        internal sealed class Session : IDisposable
        {
            private readonly ID3D11Device device;
            private readonly ID3D11DeviceContext context;
            private bool disposed;

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
                HdrToSdrToneMapper.LogAnalysis("GPU", settings, analysis, displayMaxLuminanceNits);
                using ID3D11Texture2D sourceTexture = CreateTexture(
                    device,
                    (uint)width,
                    (uint)height,
                    Format.R16G16B16A16_Float,
                    BindFlags.ShaderResource);
                context.UpdateSubresource(
                    sourceTexture,
                    0,
                    null,
                    source,
                    (uint)sourceRowPitch,
                    0);
                return ToneMapAnalyzed(
                    device,
                    context,
                    sourceTexture,
                    width,
                    height,
                    analysis,
                    preserveAlpha);
            }

            public void Dispose()
            {
                if (!disposed)
                {
                    context.ClearState();
                    context.Flush();
                    context.Dispose();
                    device.Dispose();
                    disposed = true;
                }
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
            public readonly uint Padding0;
            public readonly uint Padding1;
            public readonly uint Padding2;

            public GpuToneMapConstants(
                HdrToSdrToneMapper.ToneMapParameters parameters,
                bool useToneMapMask,
                bool preserveAlpha)
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
                Padding0 = 0;
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
