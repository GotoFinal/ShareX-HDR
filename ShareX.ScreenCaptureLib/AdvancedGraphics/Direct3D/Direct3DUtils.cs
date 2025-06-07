using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public static class Direct3DUtils
{
    public static ID3D11Texture2D CreateCanvasTexture(uint width, uint height, ID3D11Device device)
    {
        // We'll build one big RGBA8_UNORM texture that can be used as a render‐target
        // or as a copy‐destination.  (We do NOT need mipmaps, and we will copy into it.)
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None, // Not a render‐target, just a copy target
            CPUAccessFlags = CpuAccessFlags.None, // We will COPY from this into a staging at the very end
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

    public static Vertex[] ConstructForScreen(ModernCaptureMonitorDescription region)
    {
        return
        [ // Left-Top
            new Vertex
            {
                Position = new Vector2(region.DestD3DVsTopLeft.X, region.DestD3DVsTopLeft.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerTopLeft.X, region.DestD3DPsSamplerTopLeft.Y),
            },
            // Right-Top
            new Vertex
            {
                Position = new Vector2(region.DestD3DVsBottomRight.X, region.DestD3DVsTopLeft.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerBottomRight.X, region.DestD3DPsSamplerTopLeft.Y)
            },
            // Left-Bottom
            new Vertex
            {
                Position = new Vector2(region.DestD3DVsTopLeft.X, region.DestD3DVsBottomRight.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerTopLeft.X, region.DestD3DPsSamplerBottomRight.Y)
            },
            // Right-Top
            new Vertex
            {
                Position = new Vector2(region.DestD3DVsBottomRight.X, region.DestD3DVsTopLeft.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerBottomRight.X, region.DestD3DPsSamplerTopLeft.Y)
            },
            // Right-Bottom
            new Vertex
            {
                Position = new Vector2(region.DestD3DVsBottomRight.X, region.DestD3DVsBottomRight.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerBottomRight.X, region.DestD3DPsSamplerBottomRight.Y)
            },
            // Left-Bottom
            new Vertex
            {
                Position = new Vector2(region.DestD3DVsTopLeft.X, region.DestD3DVsBottomRight.Y),
                TextureCoord = new Vector2(region.DestD3DPsSamplerTopLeft.X, region.DestD3DPsSamplerBottomRight.Y)
            }
        ];
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HalfToSingle(ushort bits)
        => (float)BitConverter.UInt16BitsToHalf(bits);
}