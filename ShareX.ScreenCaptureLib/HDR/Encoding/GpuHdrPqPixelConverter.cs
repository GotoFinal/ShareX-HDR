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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ShareX.ScreenCaptureLib
{
    internal static unsafe class GpuHdrPqPixelConverter
    {
        private const string ShaderResourceName = "ShareX.ScreenCaptureLib.HDR.HdrEncodePq.hlsl";
        private static readonly object SessionSync = new object();
        private static readonly Lazy<byte[]> Shader = new Lazy<byte[]>(() =>
            GpuHdrToSdrToneMapper.CompileComputeShaderResource(
                ShaderResourceName,
                "EncodeMain"));
        private static Session session;

        internal static void Prewarm()
        {
            try
            {
                lock (SessionSync)
                {
                    session ??= new Session();
                }
            }
            catch (Exception exception)
            {
                HdrEncodingPerformance.Log(
                    $"HDR output GPU prewarm failed | reason={exception.GetType().Name}: {exception.Message}");
            }
        }

        public static bool TryConvert(
            HdrRgba16FloatBuffer source,
            float masteringMaximumNits,
            HdrPqPixelLayout layout,
            out HdrPqPixelConversionResult result,
            out string fallbackReason)
        {
            result = null;
            fallbackReason = null;

            try
            {
                lock (SessionSync)
                {
                    session ??= new Session();
                    result = session.Convert(source, masteringMaximumNits, layout);
                    return true;
                }
            }
            catch (Exception exception)
            {
                fallbackReason = $"{exception.GetType().Name}: {exception.Message}";
                lock (SessionSync)
                {
                    session?.Dispose();
                    session = null;
                }

                return false;
            }
        }

        internal static void Shutdown()
        {
            lock (SessionSync)
            {
                session?.Dispose();
                session = null;
            }
        }

        private sealed class Session : IDisposable
        {
            private readonly ID3D11Device device;
            private readonly ID3D11DeviceContext context;
            private readonly ID3D11ComputeShader shader;
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
                try
                {
                    shader = device.CreateComputeShader(Shader.Value);
                }
                catch
                {
                    context?.Dispose();
                    device?.Dispose();
                    throw;
                }
            }

            public HdrPqPixelConversionResult Convert(
                HdrRgba16FloatBuffer source,
                float masteringMaximumNits,
                HdrPqPixelLayout layout)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Stopwatch totalTimer = Stopwatch.StartNew();
                uint width = checked((uint)source.Width);
                uint height = checked((uint)source.Height);
                uint groupWidth = (width + 7u) / 8u;
                uint groupHeight = (height + 7u) / 8u;
                uint maximumSample = layout == HdrPqPixelLayout.Rgba10LittleEndian
                    ? 1023u
                    : 65535u;

                Stopwatch resourceTimer = Stopwatch.StartNew();
                using ID3D11Texture2D input = CreateTexture(
                    width,
                    height,
                    Format.R16G16B16A16_Float,
                    BindFlags.ShaderResource);
                using ID3D11Texture2D output = CreateTexture(
                    width,
                    height,
                    Format.R16G16B16A16_UInt,
                    BindFlags.UnorderedAccess);
                using ID3D11Texture2D stagingOutput = CreateStagingTexture(
                    width,
                    height,
                    Format.R16G16B16A16_UInt);
                using ID3D11Texture2D stats = CreateTexture(
                    groupWidth,
                    groupHeight,
                    Format.R32G32_Float,
                    BindFlags.UnorderedAccess);
                using ID3D11Texture2D stagingStats = CreateStagingTexture(
                    groupWidth,
                    groupHeight,
                    Format.R32G32_Float);
                using ID3D11ShaderResourceView inputView = device.CreateShaderResourceView(input);
                using ID3D11UnorderedAccessView outputView = device.CreateUnorderedAccessView(output);
                using ID3D11UnorderedAccessView statsView = device.CreateUnorderedAccessView(stats);
                var constantsValue = new GpuEncodeConstants(
                    masteringMaximumNits,
                    maximumSample,
                    width,
                    height);
                using ID3D11Buffer constants = device.CreateBuffer(
                    new[] { constantsValue },
                    BindFlags.ConstantBuffer);
                resourceTimer.Stop();

                try
                {
                    Stopwatch uploadTimer = Stopwatch.StartNew();
                    fixed (byte* sourcePointer = source.GetWritablePixelSpan())
                    {
                        context.UpdateSubresource(
                            input,
                            0,
                            null,
                            (IntPtr)sourcePointer,
                            checked((uint)source.RowBytes),
                            0);
                    }
                    uploadTimer.Stop();

                    Stopwatch submitTimer = Stopwatch.StartNew();
                    context.CSSetShader(shader);
                    context.CSSetConstantBuffer(0, constants);
                    context.CSSetShaderResource(0, inputView);
                    context.CSSetUnorderedAccessView(0, outputView);
                    context.CSSetUnorderedAccessView(1, statsView);
                    context.Dispatch(groupWidth, groupHeight, 1);
                    context.CSSetShaderResource(0, null);
                    context.CSSetUnorderedAccessView(0, null);
                    context.CSSetUnorderedAccessView(1, null);
                    context.CopyResource(stagingOutput, output);
                    context.CopyResource(stagingStats, stats);
                    submitTimer.Stop();

                    byte[] packed = ReadPackedPixels(
                        stagingOutput,
                        source.Width,
                        source.Height,
                        layout,
                        out double mapWaitMilliseconds,
                        out double copyMilliseconds);
                    ReadStats(
                        stagingStats,
                        checked((int)groupWidth),
                        checked((int)groupHeight),
                        source.Width,
                        source.Height,
                        out float maxCll,
                        out float maxFall,
                        out double statsMilliseconds);

                    totalTimer.Stop();
                    HdrEncodingPerformance.Log(
                        $"HDR output conversion | backend=GPU size={source.Width}x{source.Height} " +
                        $"layout={layout} resourceMs={resourceTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"uploadMs={uploadTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"submitMs={submitTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"mapWaitMs={mapWaitMilliseconds:F1} copyMs={copyMilliseconds:F1} " +
                        $"statsMs={statsMilliseconds:F1} totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");

                    return new HdrPqPixelConversionResult
                    {
                        Pixels = packed,
                        MaxCll = maxCll,
                        MaxFall = maxFall,
                        Backend = "GPU",
                        TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds
                    };
                }
                finally
                {
                    // Release all immediate-context references before the per-call
                    // textures and views are disposed, including failure paths.
                    context.ClearState();
                    context.Flush();
                }
            }

            private byte[] ReadPackedPixels(
                ID3D11Texture2D texture,
                int width,
                int height,
                HdrPqPixelLayout layout,
                out double mapWaitMilliseconds,
                out double copyMilliseconds)
            {
                Stopwatch mapTimer = Stopwatch.StartNew();
                MappedSubresource mapped = context.Map(
                    texture,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None);
                mapTimer.Stop();
                mapWaitMilliseconds = mapTimer.Elapsed.TotalMilliseconds;

                try
                {
                    Stopwatch copyTimer = Stopwatch.StartNew();
                    int sourceRowBytes = checked(width * sizeof(ushort) * 4);
                    int destinationRowBytes = layout == HdrPqPixelLayout.PngRgba16BigEndian
                        ? checked(sourceRowBytes + 1)
                        : sourceRowBytes;
                    byte[] result = new byte[checked(destinationRowBytes * height)];

                    fixed (byte* destinationPointer = result)
                    {
                        nint sourceAddress = (nint)mapped.DataPointer;
                        nint destinationAddress = (nint)destinationPointer;
                        Action<int> copyRow = y =>
                        {
                            byte* sourceRow = (byte*)sourceAddress + y * mapped.RowPitch;
                            byte* destinationRow = (byte*)destinationAddress + y * destinationRowBytes;
                            if (layout == HdrPqPixelLayout.PngRgba16BigEndian)
                            {
                                destinationRow[0] = 0;
                                destinationRow++;
                                for (int offset = 0; offset < sourceRowBytes; offset += 2)
                                {
                                    destinationRow[offset] = sourceRow[offset + 1];
                                    destinationRow[offset + 1] = sourceRow[offset];
                                }
                            }
                            else
                            {
                                Buffer.MemoryCopy(
                                    sourceRow,
                                    destinationRow,
                                    sourceRowBytes,
                                    sourceRowBytes);
                            }
                        };

                        if (height >= 64)
                        {
                            Parallel.For(0, height, copyRow);
                        }
                        else
                        {
                            for (int y = 0; y < height; y++)
                            {
                                copyRow(y);
                            }
                        }
                    }

                    copyTimer.Stop();
                    copyMilliseconds = copyTimer.Elapsed.TotalMilliseconds;
                    return result;
                }
                finally
                {
                    context.Unmap(texture, 0);
                }
            }

            private void ReadStats(
                ID3D11Texture2D texture,
                int groupWidth,
                int groupHeight,
                int width,
                int height,
                out float maxCll,
                out float maxFall,
                out double elapsedMilliseconds)
            {
                Stopwatch timer = Stopwatch.StartNew();
                MappedSubresource mapped = context.Map(
                    texture,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None);

                try
                {
                    maxCll = 0f;
                    double luminanceSum = 0d;
                    for (int y = 0; y < groupHeight; y++)
                    {
                        float* row = (float*)((byte*)mapped.DataPointer + y * mapped.RowPitch);
                        for (int x = 0; x < groupWidth; x++)
                        {
                            maxCll = Math.Max(maxCll, row[x * 2]);
                            luminanceSum += row[x * 2 + 1];
                        }
                    }

                    maxFall = (float)(luminanceSum / checked((long)width * height));
                }
                finally
                {
                    context.Unmap(texture, 0);
                }

                timer.Stop();
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
            }

            private ID3D11Texture2D CreateTexture(
                uint width,
                uint height,
                Format format,
                BindFlags bindFlags)
            {
                return device.CreateTexture2D(new Texture2DDescription
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
                });
            }

            private ID3D11Texture2D CreateStagingTexture(uint width, uint height, Format format)
            {
                return device.CreateTexture2D(new Texture2DDescription
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
                });
            }

            public void Dispose()
            {
                if (!disposed)
                {
                    context.ClearState();
                    context.Flush();
                    shader.Dispose();
                    context.Dispose();
                    device.Dispose();
                    disposed = true;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct GpuEncodeConstants
        {
            public readonly float MasteringMaximumNits;
            public readonly uint MaximumSample;
            public readonly uint ImageWidth;
            public readonly uint ImageHeight;

            public GpuEncodeConstants(
                float masteringMaximumNits,
                uint maximumSample,
                uint imageWidth,
                uint imageHeight)
            {
                MasteringMaximumNits = masteringMaximumNits;
                MaximumSample = maximumSample;
                ImageWidth = imageWidth;
                ImageHeight = imageHeight;
            }
        }
    }
}
