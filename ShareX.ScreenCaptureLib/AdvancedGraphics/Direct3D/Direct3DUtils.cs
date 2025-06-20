using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public static class Direct3DUtils
{
    public static ID3D11Texture2D CreateCanvasTexture(uint width, uint height, ID3D11Device device)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        return device.CreateTexture2D(desc);
    }


    /// After you finish copying all regions into this “canvas,” you can do:
    ///    var staging = CreateStagingFor(canvasTex);
    ///    ctx.CopyResource(staging, canvasTex);
    ///    Map+Encode…
    public static ID3D11Texture2D CreateStagingFor(ID3D11Texture2D gpuTex)
    {
        var desc = gpuTex.Description;
        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        return gpuTex.Device.CreateTexture2D(stagingDesc);
    }

    public static Vector4[] GetPixelSpan(this ID3D11Texture2D frame)
    {
        var device = frame.Device;

        // If the texture is not already CPU-readable, create a staging copy.
        var desc = frame.Description;

        bool isF32 = desc.Format == Format.R32G32B32A32_Float;
        bool isF16 = desc.Format == Format.R16G16B16A16_Float;

        if (!isF32 && !isF16)
            throw new InvalidOperationException(
                $"Format {desc.Format} not handled. Only R32G32B32A32_FLOAT & R16G16B16A16_FLOAT are supported.");

        ID3D11Texture2D stagingTex = frame;
        if ((desc.CPUAccessFlags & CpuAccessFlags.Read) == 0 ||
            desc.Usage != ResourceUsage.Staging)
        {
            var stagingDesc = desc;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.BindFlags = BindFlags.None;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            stagingDesc.MiscFlags = ResourceOptionFlags.None;

            stagingTex = device.CreateTexture2D(stagingDesc);
            device.ImmediateContext.CopyResource(stagingTex, frame);
        }

        // Map, copy row-by-row into managed storage, then unmap.
        var ctx = device.ImmediateContext;
        var mapped = ctx.Map(stagingTex, 0);

        int width = (int)desc.Width;
        int height = (int)desc.Height;
        int totalPixels = width * height;

        var backingStore = new Vector4[totalPixels]; // managed backing array

        unsafe
        {
            byte* srcRow = (byte*)mapped.DataPointer;

            fixed (Vector4* dstBase = backingStore)
            {
                Vector4* dstRow = dstBase;

                if (isF32)
                {
                    int bytesPerRow = width * sizeof(float) * 4; // 16 bytes per pixel
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(srcRow, dstRow, bytesPerRow, bytesPerRow);
                        srcRow += mapped.RowPitch;
                        dstRow += width;
                    }
                }
                else // isF16
                {
                    for (int y = 0; y < height; y++)
                    {
                        ushort* halfPtr = (ushort*)srcRow;

                        for (int x = 0; x < width; x++)
                        {
                            int i = y * width + x;
                            backingStore[i] = new Vector4(
                                HalfToSingle(halfPtr[0]),
                                HalfToSingle(halfPtr[1]),
                                HalfToSingle(halfPtr[2]),
                                HalfToSingle(halfPtr[3]));

                            halfPtr += 4;
                        }

                        srcRow += mapped.RowPitch;
                    }
                }
            }
        }

        ctx.Unmap(stagingTex, 0);

        if (!ReferenceEquals(stagingTex, frame))
            stagingTex.Dispose(); // Only dispose the temp copy

        return backingStore;
    }

    public static PixelReader GetPixelReader(this ID3D11Texture2D frame, HdrSettings settings)
    {
        PixelReader reader = new PixelReader { frame = frame, };
        reader.Start(settings);
        return reader;
    }

    public unsafe class PixelReader : IDisposable
    {
        public ID3D11Texture2D frame;
        public ID3D11Texture2D stagingTex;
        private float* floats;
        private ushort* halfs;
        private bool isF32;

        private uint size;
        // private uint cacheIndex = uint.MaxValue;
        // private Vector4[] cachedLine;
        // private uint lastRead = 0;

        private Vector4[] pixels;
        private bool isInMemory;

        public void Start(HdrSettings settings)
        {
            var device = frame.Device;
            var width = frame.Description.Width;
            var height = frame.Description.Height;
            size = width * height;

            // If the texture is not already CPU-readable, create a staging copy.
            var desc = frame.Description;

            isF32 = desc.Format == Format.R32G32B32A32_Float;
            bool isF16 = desc.Format == Format.R16G16B16A16_Float;

            if (!isF32 && !isF16)
                throw new InvalidOperationException(
                    $"Format {desc.Format} not handled. Only R32G32B32A32_FLOAT & R16G16B16A16_FLOAT are supported.");

            stagingTex = frame;
            if ((desc.CPUAccessFlags & CpuAccessFlags.Read) == 0 ||
                desc.Usage != ResourceUsage.Staging)
            {
                throw new Exception("Expected readable texture here.");
            }

            var ctx = device.ImmediateContext;
            var mapped = ctx.Map(stagingTex, 0);
            var basePtr = (byte*)mapped.DataPointer;
            if (isF32) floats = (float*)basePtr;
            else halfs = (ushort*)basePtr;
            // cachedLine = new Vector4[frame.Description.Width];
            // InitCache();

            if (!settings.AvoidBuffering)
            {
                var backingStore = new Vector4[size];

                byte* srcRow = (byte*)mapped.DataPointer;
                fixed (Vector4* dstBase = backingStore)
                {
                    Vector4* dstRow = dstBase;

                    if (isF32)
                    {
                        uint bytesPerRow = width * sizeof(float) * 4; // 16 bytes per pixel
                        for (uint y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(srcRow, dstRow, bytesPerRow, bytesPerRow);
                            srcRow += mapped.RowPitch;
                            dstRow += width;
                        }
                    }
                    else // isF16
                    {
                        for (uint y = 0; y < height; y++)
                        {
                            ushort* halfPtr = (ushort*)srcRow;

                            for (uint x = 0; x < width; x++)
                            {
                                uint i = y * width + x;
                                backingStore[i] = new Vector4(
                                    HalfToSingle(halfPtr[0]),
                                    HalfToSingle(halfPtr[1]),
                                    HalfToSingle(halfPtr[2]),
                                    HalfToSingle(halfPtr[3]));

                                halfPtr += 4;
                            }

                            srcRow += mapped.RowPitch;
                        }
                    }
                }

                pixels = backingStore;
                isInMemory = true;
            }
        }

        public uint Pixels => size;

        // [MethodImpl(MethodImplOptions.NoInlining)]
        // private void InitCache()
        // {
        //     cacheIndex = 0;
        //     if (isF32)
        //     {
        //         for (uint i = 0; i < cachedLine.Length; i++)
        //         {
        //             cachedLine[i] = GetPixelF32Raw(lastRead + i);
        //         }
        //     }
        //     else
        //     {
        //         for (uint i = 0; i < cachedLine.Length; i++)
        //         {
        //             cachedLine[i] = GetPixelF16Raw(lastRead + i);
        //         }
        //     }
        // }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 GetPixel(uint index)
        {
            if (isInMemory) return pixels[index];
            if (isF32) return GetPixelF32Raw(index);
            return GetPixelF16Raw(index);
            // lastRead = index;
            // var vector4 = cachedLine[cacheIndex++];
            // if (cacheIndex >= cachedLine.Length)
            // {
            //     InitCache();
            // }
            //
            // return vector4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector4 GetPixelF32Raw(uint index)
        {
            var f = floats;
            var baseIndex = index * 4;
            return new Vector4(f[baseIndex], f[baseIndex + 1], f[baseIndex + 2], f[baseIndex + 3]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector4 GetPixelF16Raw(uint index)
        {
            var halfs1 = halfs;
            var baseIndex = index * 4;
            return new Vector4(
                HalfToSingle(halfs1[baseIndex]),
                HalfToSingle(halfs1[baseIndex + 1]),
                HalfToSingle(halfs1[baseIndex + 2]),
                HalfToSingle(halfs1[baseIndex + 3]));
        }

        public void Dispose()
        {
            if (stagingTex != null)
            {
                var ctx = stagingTex.Device.ImmediateContext;
                ctx.Unmap(stagingTex, 0);
            }

            if (!ReferenceEquals(stagingTex, frame))
            {
                stagingTex?.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HalfToSingle(ushort bits)
        => (float)BitConverter.UInt16BitsToHalf(bits);
}