using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrSdrCaptureImportTests
{
    [Theory]
    [InlineData(80f)]
    [InlineData(203f)]
    [InlineData(250f)]
    [InlineData(1000f)]
    public void OpaqueSdrImport_DecodesAllByteValuesAndPreservesDestinationPadding(float whiteNits)
    {
        const int width = 256, height = 65, sourceStride = width * 4 + 12;
        byte[] source = CreateBgrxPattern(width, height, sourceStride);
        using var destination = new HdrRgba16FloatBuffer(width + 2, height + 3,
            (width + 2) * HdrRgba16FloatBuffer.BytesPerPixel + 16);
        Import(destination, source, sourceStride, width, height, new Point(1, 2), whiteNits);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = destination.GetRowSpan(y + 2);
            Assert.Equal(new byte[8], row[..8].ToArray());
            for (int x = 0; x < width; x++)
            {
                int offset = (x + 1) * HdrRgba16FloatBuffer.BytesPerPixel;
                int sourceOffset = y * sourceStride + x * 4;
                Assert.Equal((float)(Half)(DecodeSrgb(source[sourceOffset + 2]) * (whiteNits / 80f)), ReadHalf(row, offset));
                Assert.Equal((float)(Half)(DecodeSrgb(source[sourceOffset + 1]) * (whiteNits / 80f)), ReadHalf(row, offset + 2));
                Assert.Equal((float)(Half)(DecodeSrgb(source[sourceOffset]) * (whiteNits / 80f)), ReadHalf(row, offset + 4));
                Assert.Equal(1f, ReadHalf(row, offset + 6));
            }
            Assert.All(destination.PixelBytes.Span.Slice((y + 2) * destination.RowBytes +
                (width + 1) * 8, 24).ToArray(), value => Assert.Equal(0, value));
        }
        Assert.All(destination.GetRowSpan(0).ToArray(), value => Assert.Equal(0, value));
        Assert.All(destination.GetRowSpan(height + 2).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void OpaqueSdrImport_UsesSignedStrideWithoutReversingTheImage()
    {
        const int width = 3, height = 2, stride = 16;
        byte[] source = CreateBgrxPattern(width, height, stride);
        using var destination = new HdrRgba16FloatBuffer(width, height);
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            destination.CopyFromOpaqueSdrBgra8(IntPtr.Add(handle.AddrOfPinnedObject(), stride),
                -stride, width, height, Point.Empty, 80f);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte red = source[(height - 1 - y) * stride + x * 4 + 2];
                    Assert.Equal((float)(Half)DecodeSrgb(red), ReadHalf(destination.GetRowSpan(y), x * 8));
                }
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [Theory]
    [InlineData(HdrProcessingBackend.Cpu, HdrMixedMonitorBrightnessMode.MatchHdrDisplay, 250f)]
    [InlineData(HdrProcessingBackend.Cpu, HdrMixedMonitorBrightnessMode.Custom, 300f)]
    [InlineData(HdrProcessingBackend.Cpu, HdrMixedMonitorBrightnessMode.Preserve, 203f)]
    [InlineData(HdrProcessingBackend.Gpu, HdrMixedMonitorBrightnessMode.MatchHdrDisplay, 250f)]
    [InlineData(HdrProcessingBackend.Gpu, HdrMixedMonitorBrightnessMode.Custom, 300f)]
    [InlineData(HdrProcessingBackend.Gpu, HdrMixedMonitorBrightnessMode.Preserve, 203f)]
    public void MixedDocument_SdrBytesRoundTripWithoutDoubleNormalizationOrChangingHdr(
        HdrProcessingBackend backend, HdrMixedMonitorBrightnessMode brightnessMode, float embeddingWhite)
    {
        const int width = 256, height = 2, stride = width * 4;
        byte[] source = CreateBgrxPattern(width, height, stride);
        var pixels = new HdrRgba16FloatBuffer(width + 2, height + 2);
        WriteHalf(pixels.GetWritableRowSpan(0), (width + 1) * 8, 7.5f);
        WriteHalf(pixels.GetWritableRowSpan(0), (width + 1) * 8 + 2, -0.5f);
        WriteHalf(pixels.GetWritableRowSpan(0), (width + 1) * 8 + 6, 1f);
        Import(pixels, source, stride, width, height, new Point(1, 1), embeddingWhite);
        Assert.Equal(7.5f, ReadHalf(pixels.GetRowSpan(0), (width + 1) * 8));
        Assert.Equal(-0.5f, ReadHalf(pixels.GetRowSpan(0), (width + 1) * 8 + 2));
        using var document = new HdrImageDocument(new Rectangle(-100, 50, pixels.Width, pixels.Height), pixels,
            new[]
            {
                new HdrCaptureSourceSegment(new Rectangle(1, 1, width, height), "SDR", false, embeddingWhite, 300f),
                new HdrCaptureSourceSegment(new Rectangle(width + 1, 0, 1, 1), "HDR", true, 250f, 1000f)
            });
        byte[] before = document.MasterPixels.PixelBytes.ToArray();
        var settings = new HdrCaptureSettings
        {
            ProcessingBackend = backend,
            MixedMonitorBrightnessMode = brightnessMode,
            MixedMonitorCustomSdrWhiteNits = embeddingWhite
        };
        document.NormalizeMixedMonitorBrightness(settings);
        Assert.Equal(before, document.MasterPixels.PixelBytes.ToArray());
        Assert.Equal(embeddingWhite, document.SourceSegments[0].SdrWhiteNits);

        using Bitmap preview = document.CreateSdrPreview(settings);
        AssertPattern(preview, source, stride, new Point(1, 1), width, height);
        Assert.Equal(0, preview.GetPixel(0, 0).A);
        using HdrImageDocument sdrCrop = document.CropToScreenRectangle(new Rectangle(-99, 51, width, height));
        using Bitmap croppedPreview = sdrCrop.CreateSdrPreview(settings);
        AssertPattern(croppedPreview, source, stride, Point.Empty, width, height);
        Assert.False(Assert.Single(sdrCrop.SourceSegments).WasHdrActive);
    }

    [Fact]
    public void OpaqueSdrImport_RejectsInvalidLayoutAndWhiteBeforeWriting()
    {
        byte[] source = new byte[16];
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            using var destination = new HdrRgba16FloatBuffer(2, 2);
            IntPtr pointer = handle.AddrOfPinnedObject();
            Assert.Throws<ArgumentNullException>(() => destination.CopyFromOpaqueSdrBgra8(IntPtr.Zero, 8, 2, 2, Point.Empty, 80f));
            Assert.Throws<ArgumentOutOfRangeException>(() => destination.CopyFromOpaqueSdrBgra8(pointer, 7, 2, 2, Point.Empty, 80f));
            Assert.Throws<ArgumentOutOfRangeException>(() => destination.CopyFromOpaqueSdrBgra8(pointer, -7, 2, 2, Point.Empty, 80f));
            Assert.Throws<ArgumentOutOfRangeException>(() => destination.CopyFromOpaqueSdrBgra8(pointer, 8, 2, 2, new Point(1, 0), 80f));
            Assert.Throws<ArgumentOutOfRangeException>(() => destination.CopyFromOpaqueSdrBgra8(pointer, 8, 2, 2, Point.Empty, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => destination.CopyFromOpaqueSdrBgra8(pointer, 8, 2, 2, Point.Empty, 0f));
            Assert.All(destination.PixelBytes.ToArray(), value => Assert.Equal(0, value));
            destination.Dispose();
            Assert.Throws<ObjectDisposedException>(() => destination.CopyFromOpaqueSdrBgra8(pointer, 8, 2, 2, Point.Empty, 80f));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void NativeHbitmap_CanBeLockedAsOpaqueBgrxWithoutChangingSdrColors()
    {
        using var original = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
        original.SetPixel(0, 0, Color.Red);
        original.SetPixel(1, 0, Color.Lime);
        original.SetPixel(2, 1, Color.FromArgb(17, 91, 203));
        IntPtr hbitmap = original.GetHbitmap();
        try
        {
            using Bitmap native = Image.FromHbitmap(hbitmap);
            using var pixels = new HdrRgba16FloatBuffer(native.Width, native.Height);
            BitmapData data = native.LockBits(new Rectangle(Point.Empty, native.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                pixels.CopyFromOpaqueSdrBgra8(data.Scan0, data.Stride, native.Width, native.Height, Point.Empty, 203f);
            }
            finally
            {
                native.UnlockBits(data);
            }
            using var document = new HdrImageDocument(new Rectangle(Point.Empty, native.Size), pixels.Clone(),
                new[] { new HdrCaptureSourceSegment(new Rectangle(Point.Empty, native.Size), "SDR", false, 203f, 300f) });
            using Bitmap preview = document.CreateSdrPreview(new HdrCaptureSettings { ProcessingBackend = HdrProcessingBackend.Cpu });
            Assert.Equal(original.GetPixel(0, 0), preview.GetPixel(0, 0));
            Assert.Equal(original.GetPixel(1, 0), preview.GetPixel(1, 0));
            Assert.Equal(original.GetPixel(2, 1), preview.GetPixel(2, 1));
        }
        finally
        {
            ShareX.HelpersLib.NativeMethods.DeleteObject(hbitmap);
        }
    }

    private static byte[] CreateBgrxPattern(int width, int height, int stride)
    {
        byte[] source = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                source[offset] = (byte)(x + y * 7);
                source[offset + 1] = (byte)(255 - x + y * 13);
                source[offset + 2] = (byte)(x + y * 19);
                source[offset + 3] = (byte)(x % 3 == 0 ? 0 : x % 3 == 1 ? 127 : 255);
            }
        }
        return source;
    }

    private static void Import(HdrRgba16FloatBuffer destination, byte[] source, int stride,
        int width, int height, Point location, float white)
    {
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            destination.CopyFromOpaqueSdrBgra8(handle.AddrOfPinnedObject(), stride, width, height, location, white);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void AssertPattern(Bitmap preview, byte[] source, int stride, Point location, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                Color actual = preview.GetPixel(location.X + x, location.Y + y);
                Assert.Equal(Color.FromArgb(source[offset + 2], source[offset + 1], source[offset]), actual);
            }
        }
    }

    private static float DecodeSrgb(byte value)
    {
        float encoded = value / 255f;
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }
    private static float ReadHalf(ReadOnlySpan<byte> row, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset, 2)));
    private static void WriteHalf(Span<byte> row, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));
}
