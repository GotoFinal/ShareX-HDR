using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrEncodedImageVerifierTests
{
    [Fact]
    public void OpenExrVerifier_AcceptsEncoderOutputAndRejectsCorruption()
    {
        using HdrRgba16FloatBuffer source = CreateSource(3, 2);
        using var encoded = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(source, encoded);

        Assert.True(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.OpenExr, 3, 2));
        Assert.False(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.OpenExr, 2, 2));

        byte[] corrupted = encoded.ToArray();
        corrupted[0] ^= 0xff;
        using var corruptedStream = new MemoryStream(corrupted);
        Assert.False(HdrEncodedImageVerifier.Verify(corruptedStream, HdrFileFormat.OpenExr, 3, 2));
    }

    [Fact]
    public void OpenExrVerifier_RejectsDamagedScanlineTableAndBody()
    {
        using HdrRgba16FloatBuffer source = CreateSource(3, 2);
        using var encoded = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(source, encoded);
        byte[] valid = encoded.ToArray();
        int offsetTableStart = FindOpenExrOffsetTable(valid);
        long firstChunkOffset = BinaryPrimitives.ReadInt64LittleEndian(
            valid.AsSpan(offsetTableStart, sizeof(long)));

        byte[] badOffset = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(
            badOffset.AsSpan(offsetTableStart, sizeof(long)),
            firstChunkOffset + 1);
        Assert.False(HdrEncodedImageVerifier.Verify(
            new MemoryStream(badOffset), HdrFileFormat.OpenExr, 3, 2));

        byte[] badScanline = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(
            badScanline.AsSpan(checked((int)firstChunkOffset), sizeof(int)),
            1);
        Assert.False(HdrEncodedImageVerifier.Verify(
            new MemoryStream(badScanline), HdrFileFormat.OpenExr, 3, 2));

        byte[] trailingData = new byte[valid.Length + 1];
        valid.CopyTo(trailingData, 0);
        Assert.False(HdrEncodedImageVerifier.Verify(
            new MemoryStream(trailingData), HdrFileFormat.OpenExr, 3, 2));

        Assert.False(HdrEncodedImageVerifier.Verify(
            new MemoryStream(valid.AsSpan(0, valid.Length - 1).ToArray()),
            HdrFileFormat.OpenExr,
            3,
            2));
    }

    [Fact]
    public void HdrPngVerifier_AcceptsEncoderOutputAndRejectsBadCrc()
    {
        using HdrRgba16FloatBuffer source = CreateSource(3, 2);
        using var encoded = new MemoryStream();
        new HdrPngImageEncoder().Encode(source, encoded);

        Assert.True(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.HdrPng, 3, 2));
        Assert.False(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.HdrPng, 3, 3));

        byte[] corrupted = encoded.ToArray();
        corrupted[^8] ^= 0x01;
        using var corruptedStream = new MemoryStream(corrupted);
        Assert.False(HdrEncodedImageVerifier.Verify(corruptedStream, HdrFileFormat.HdrPng, 3, 2));
    }

    [Fact]
    public void UltraHdrVerifier_AcceptsOddDimensionsAndRejectsOrdinaryBytes()
    {
        using HdrRgba16FloatBuffer source = CreateSource(9, 11);
        using var encoded = new MemoryStream();
        new UltraHdrJpegImageEncoder().Encode(source, encoded);

        Assert.True(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.UltraHdrJpeg, 9, 11));
        Assert.False(HdrEncodedImageVerifier.Verify(encoded, HdrFileFormat.UltraHdrJpeg, 11, 9));

        using var ordinaryBytes = new MemoryStream(new byte[] { 0xff, 0xd8, 0xff, 0xd9 });
        Assert.False(HdrEncodedImageVerifier.Verify(
            ordinaryBytes,
            HdrFileFormat.UltraHdrJpeg,
            9,
            11));
    }

    [Fact]
    public void UltraHdrEncoder_RejectsDimensionsBelowCodecMinimumBeforeNativeCall()
    {
        using HdrRgba16FloatBuffer source = CreateSource(7, 8);
        using var encoded = new MemoryStream();

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new UltraHdrJpegImageEncoder().Encode(source, encoded));
        Assert.Contains("at least 8 by 8", exception.Message);
    }

    [Fact]
    public void VerifyFile_RestoresStreamIndependentValidation()
    {
        using HdrRgba16FloatBuffer source = CreateSource(2, 1);
        using var encoded = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(source, encoded);

        string filePath = Path.Combine(Path.GetTempPath(), $"ShareX-HDR-{Guid.NewGuid():N}.exr");
        try
        {
            File.WriteAllBytes(filePath, encoded.ToArray());
            Assert.True(HdrEncodedImageVerifier.VerifyFile(filePath, HdrFileFormat.OpenExr, 2, 1));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static int FindOpenExrOffsetTable(byte[] file)
    {
        int position = 8;

        while (file[position] != 0)
        {
            SkipNullTerminatedString(file, ref position);
            SkipNullTerminatedString(file, ref position);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position, sizeof(int)));
            position += sizeof(int) + valueLength;
        }

        return position + 1;
    }

    private static void SkipNullTerminatedString(byte[] file, ref int position)
    {
        while (file[position++] != 0)
        {
        }
    }

    private static HdrRgba16FloatBuffer CreateSource(int width, int height)
    {
        byte[] pixels = new byte[width * height * HdrRgba16FloatBuffer.BytesPerPixel];

        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int offset = pixel * HdrRgba16FloatBuffer.BytesPerPixel;
            WriteHalf(pixels, offset, 0.5f + pixel * 0.01f);
            WriteHalf(pixels, offset + 2, 1.25f);
            WriteHalf(pixels, offset + 4, 2.5f);
            WriteHalf(pixels, offset + 6, 1f);
        }

        return HdrRgba16FloatBuffer.CopyFrom(
            pixels,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
    }

    private static void WriteHalf(Span<byte> bytes, int offset, float value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));
    }
}
