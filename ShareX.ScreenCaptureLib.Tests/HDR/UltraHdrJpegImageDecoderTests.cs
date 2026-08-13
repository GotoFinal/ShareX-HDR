using System.Buffers.Binary;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class UltraHdrJpegImageDecoderTests
{
    [Fact]
    public void Decode_RoundTripsOwnGainMapJpegAndRestoresStreamPosition()
    {
        using HdrRgba16FloatBuffer source = CreateSmoothSource(16, 16);
        using var encoded = new MemoryStream();
        new UltraHdrJpegImageEncoder().Encode(source, encoded, new HdrImageEncodingOptions
        {
            Quality = 100,
            GainMapQuality = 100,
            MasteringDisplayMaximumNits = 1200f
        });
        encoded.Position = 5;

        using HdrRgba16FloatBuffer decoded = new UltraHdrJpegImageDecoder().Decode(encoded);

        Assert.Equal(5, encoded.Position);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        AssertImagesClose(source, decoded, 0.20f);
    }

    [Fact]
    public void DecodeDocumentFile_UsesGainMapPeakAndDetectsFile()
    {
        using HdrRgba16FloatBuffer source = CreateSmoothSource(12, 10);
        using var encoded = new MemoryStream();
        new UltraHdrJpegImageEncoder().Encode(source, encoded, new HdrImageEncodingOptions
        {
            Quality = 100,
            GainMapQuality = 100,
            MasteringDisplayMaximumNits = 1500f
        });

        string path = Path.Combine(Path.GetTempPath(), $"ShareX-UltraHDR-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, encoded.ToArray());
            var decoder = new UltraHdrJpegImageDecoder();
            Assert.True(decoder.IsSupportedFile(path));

            using HdrImageDocument document = decoder.DecodeDocumentFile(path, 225f);
            HdrCaptureSourceSegment segment = Assert.Single(document.SourceSegments);
            Assert.Equal(225f, segment.SdrWhiteNits);
            Assert.InRange(segment.DisplayPeakNits, 1400f, 1600f);
            Assert.Equal(Path.GetFileName(path), segment.DisplayDeviceName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Decoder_RejectsOrdinaryJpegBytes()
    {
        byte[] ordinaryJpeg = { 0xff, 0xd8, 0xff, 0xd9 };
        using var stream = new MemoryStream(ordinaryJpeg);
        stream.Position = 2;

        Assert.Throws<InvalidDataException>(() => new UltraHdrJpegImageDecoder().Decode(stream));
        Assert.Equal(2, stream.Position);
    }

    private static HdrRgba16FloatBuffer CreateSmoothSource(int width, int height)
    {
        var pixels = new HdrRgba16FloatBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = pixels.GetWritableRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                float horizontal = x / (float)Math.Max(1, width - 1);
                float vertical = y / (float)Math.Max(1, height - 1);
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(row, offset, 0.25f + horizontal * 5f);
                WriteHalf(row, offset + 2, 0.20f + vertical * 3f);
                WriteHalf(row, offset + 4, 0.15f + (horizontal + vertical) * 1.5f);
                WriteHalf(row, offset + 6, 1f);
            }
        }

        return pixels;
    }

    private static void AssertImagesClose(
        HdrRgba16FloatBuffer expected,
        HdrRgba16FloatBuffer actual,
        float tolerance)
    {
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<byte> expectedRow = expected.GetRowSpan(y);
            ReadOnlySpan<byte> actualRow = actual.GetRowSpan(y);
            for (int x = 0; x < expected.Width; x++)
            {
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                for (int channel = 0; channel < 3; channel++)
                {
                    Assert.InRange(
                        Math.Abs(ReadHalf(actualRow, offset + channel * 2) -
                            ReadHalf(expectedRow, offset + channel * 2)),
                        0f,
                        tolerance);
                }

                Assert.Equal(1f, ReadHalf(actualRow, offset + 6));
            }
        }
    }

    private static void WriteHalf(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static float ReadHalf(ReadOnlySpan<byte> source, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort))));
}
