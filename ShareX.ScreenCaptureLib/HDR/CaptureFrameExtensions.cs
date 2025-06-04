using System;
using System.Runtime.CompilerServices;
using Windows.Graphics.Capture;
using Vector4 = System.Numerics.Vector4;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ShareX.ScreenCaptureLib.HDR;

public static class CaptureFrameExtensions
{
    /// <summary>
    /// Copies the contents of the frame’s backing texture into a managed
    /// <see cref="Span{Vector4}"/>.  The span is backed by a GC-managed
    /// <see cref="Vector4"/> array, so it is safe to use after the method
    /// returns (no unsafe pointers leaking out).
    /// </summary>
    /// <remarks>
    /// * Requires the texture to be <c>DXGI_FORMAT_R32G32B32A32_FLOAT</c>.
    /// * If the source texture is already CPU-readable (rare), the staging
    ///   copy is skipped automatically.
    /// * Works on the thread that owns <paramref name="device"/>.
    /// </remarks>
    public static Span<Vector4> GetPixelSpan(
        this ID3D11Texture2D frame,
        ID3D11Device device,
        out Vector4[] backingStore)
    {
        // Obtain the underlying ID3D11Texture2D.

        // If the texture is not already CPU-readable, create a staging copy.
        var desc = frame.Description;

        bool isF32    = desc.Format == Format.R32G32B32A32_Float;
        bool isF16    = desc.Format == Format.R16G16B16A16_Float;

        if (!isF32 && !isF16)
            throw new InvalidOperationException(
                $"Format {desc.Format} not handled. Only R32G32B32A32_FLOAT & R16G16B16A16_FLOAT are supported.");

        ID3D11Texture2D stagingTex = frame;
        if ((desc.CPUAccessFlags & CpuAccessFlags.Read) == 0 ||
            desc.Usage != ResourceUsage.Staging)
        {
            var stagingDesc = desc;
            stagingDesc.Usage          = ResourceUsage.Staging;
            stagingDesc.BindFlags      = BindFlags.None;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            stagingDesc.MiscFlags    = ResourceOptionFlags.None;

            stagingTex = device.CreateTexture2D(stagingDesc);
            device.ImmediateContext.CopyResource(stagingTex, frame);
        }

        // Map, copy row-by-row into managed storage, then unmap.
        var ctx   = device.ImmediateContext;
        var mapped = ctx.Map(stagingTex, 0, MapMode.Read, MapFlags.None);

        int width        = (int)desc.Width;
        int height       = (int)desc.Height;
        int totalPixels  = width * height;

        backingStore = new Vector4[totalPixels];        // managed backing array

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
            stagingTex.Dispose();      // Only dispose the temp copy

        return backingStore.AsSpan();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HalfToSingle(ushort bits)
        => (float)BitConverter.UInt16BitsToHalf(bits);
}