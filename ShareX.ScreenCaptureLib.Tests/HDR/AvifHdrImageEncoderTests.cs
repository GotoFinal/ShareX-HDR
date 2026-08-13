using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class AvifHdrImageEncoderTests
{
    [Fact]
    public void Encode_ProducesProbedTenBitHdrAvifAndRoundTripsAlpha()
    {
        using HdrRgba16FloatBuffer source = CreateSource(16, 12);
        using var encoded = new MemoryStream();
        var options = new HdrImageEncodingOptions
        {
            AvifQuality = 100,
            AvifSpeed = 10,
            MasteringDisplayMaximumNits = 1000f,
            MasteringDisplayMinimumNits = 0.0005f
        };

        HdrEncodedImageInfo info = new AvifHdrImageEncoder().Encode(source, encoded, options);
        byte[] file = encoded.ToArray();

        Assert.Equal(HdrFileFormat.Avif, info.Format);
        Assert.Equal(".avif", info.FileExtension);
        Assert.Equal("image/avif", info.MediaType);
        Assert.False(info.IsLossless);
        Assert.Equal(file.Length, info.BytesWritten);
        Assert.True(file.Length > 16);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(file, 4, 4));
        Assert.Contains("avif", System.Text.Encoding.ASCII.GetString(file, 8, 8));
        Assert.True(file.AsSpan().IndexOf("nclx"u8) >= 0);
        Assert.True(file.AsSpan().IndexOf("clli"u8) >= 0);
        Assert.True(file.AsSpan().IndexOf("mdcv"u8) >= 0);
        Assert.True(AvifHdrImageEncoder.TryProbeHdr(
            file,
            out int width,
            out int height,
            out float masteringPeak,
            out float maxCll,
            out float maxFall));
        Assert.Equal(16, width);
        Assert.Equal(12, height);
        Assert.Equal(1000f, masteringPeak, 3);
        Assert.True(maxCll > 80f);
        Assert.True(maxFall > 0f);
        Assert.True(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.Avif, 16, 12));

        encoded.Position = 0;
        using HdrRgba16FloatBuffer decoded = new AvifHdrImageDecoder().Decode(encoded);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.InRange(ReadHalf(decoded.GetRowSpan(0), 6), 0.99f, 1f);
        Assert.InRange(ReadHalf(decoded.GetRowSpan(decoded.Height - 1), 6), 0.49f, 0.51f);
        Assert.InRange(ReadHalf(decoded.GetRowSpan(0), 0), 0.45f, 0.55f);
    }

    [Fact]
    public void PackedInput_UnpremultipliesAlphaAndComputesLightMetadata()
    {
        byte[] sourceBytes = new byte[HdrRgba16FloatBuffer.BytesPerPixel];
        WriteHalf(sourceBytes, 0, 0.5f);
        WriteHalf(sourceBytes, 2, 0.5f);
        WriteHalf(sourceBytes, 4, 0.5f);
        WriteHalf(sourceBytes, 6, 0.5f);
        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, 8, 1, 1);

        byte[] packed = AvifHdrImageEncoder.CreatePackedRgba10(
            source,
            1000f,
            out float maxCll,
            out float maxFall);

        ushort red = BinaryPrimitives.ReadUInt16LittleEndian(packed);
        ushort green = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(2));
        ushort blue = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(4));
        ushort alpha = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(6));
        Assert.Equal(red, green);
        Assert.Equal(red, blue);
        Assert.InRange(alpha, 511, 512);
        Assert.Equal(80f, maxCll, 3);
        Assert.Equal(80f, maxFall, 3);
    }

    [Fact]
    public void Probe_RejectsCorruptAndOrdinaryAvifLikeBytes()
    {
        Assert.False(AvifHdrImageEncoder.TryProbeHdr(
            new byte[] { 0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p' },
            out _,
            out _,
            out _,
            out _,
            out _));
    }

    private static HdrRgba16FloatBuffer CreateSource(int width, int height)
    {
        var result = new HdrRgba16FloatBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = result.GetWritableRowSpan(y);
            float alpha = y == height - 1 ? 0.5f : 1f;
            for (int x = 0; x < width; x++)
            {
                float value = 0.5f + x / (float)Math.Max(1, width - 1) * 3.5f;
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(row, offset, value * alpha);
                WriteHalf(row, offset + 2, value * 0.75f * alpha);
                WriteHalf(row, offset + 4, value * 0.5f * alpha);
                WriteHalf(row, offset + 6, alpha);
            }
        }
        return result;
    }

    private static float ReadHalf(ReadOnlySpan<byte> source, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort))));

    private static void WriteHalf(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));
}
