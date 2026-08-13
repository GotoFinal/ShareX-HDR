using System.Buffers.Binary;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrPngImageDecoderTests
{
    [Fact]
    public void Decode_RoundTripsShareXHdrPngWithinPqPrecisionAndRestoresPosition()
    {
        using HdrRgba16FloatBuffer source = CreateSource();
        using var encoded = new MemoryStream();
        new HdrPngImageEncoder().Encode(source, encoded, new HdrImageEncodingOptions
        {
            MasteringDisplayMaximumNits = 1200f
        });
        encoded.Position = 3;

        var decoder = new HdrPngImageDecoder();
        Assert.True(decoder.IsSupported(encoded));
        Assert.Equal(3, encoded.Position);

        using HdrRgba16FloatBuffer decoded = decoder.Decode(encoded);
        Assert.Equal(3, encoded.Position);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                ReadOnlySpan<byte> expected = source.GetRowSpan(y);
                ReadOnlySpan<byte> actual = decoded.GetRowSpan(y);
                Assert.InRange(Math.Abs(ReadHalf(actual, offset) - ReadHalf(expected, offset)), 0f, 0.025f);
                Assert.InRange(Math.Abs(ReadHalf(actual, offset + 2) - ReadHalf(expected, offset + 2)), 0f, 0.025f);
                Assert.InRange(Math.Abs(ReadHalf(actual, offset + 4) - ReadHalf(expected, offset + 4)), 0f, 0.025f);
                Assert.InRange(Math.Abs(ReadHalf(actual, offset + 6) - ReadHalf(expected, offset + 6)), 0f, 0.001f);
            }
        }
    }

    [Fact]
    public void DecodeDocumentFile_UsesMasteringPeakAndOwnsPixels()
    {
        using HdrRgba16FloatBuffer source = CreateSource();
        byte[] encoded = Encode(source, 1500f);
        string path = Path.Combine(Path.GetTempPath(), $"ShareX-HDR-PNG-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, encoded);
            var decoder = new HdrPngImageDecoder();
            Assert.True(decoder.IsSupportedFile(path));

            using HdrImageDocument document = decoder.DecodeDocumentFile(path, 220f);
            HdrCaptureSourceSegment segment = Assert.Single(document.SourceSegments);
            Assert.Equal(220f, segment.SdrWhiteNits);
            Assert.Equal(1500f, segment.DisplayPeakNits);
            Assert.Equal(Path.GetFileName(path), segment.DisplayDeviceName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsSupported_RejectsOrdinaryPngWithoutChangingPosition()
    {
        using HdrRgba16FloatBuffer source = CreateSource();
        byte[] signatureAndHeader = Encode(source, 1000f);
        int cicpType = FindChunk(signatureAndHeader, "cICP") + 4;
        signatureAndHeader[cicpType] = (byte)'s';
        using var stream = new MemoryStream(signatureAndHeader);
        stream.Position = 5;

        Assert.False(new HdrPngImageDecoder().IsSupported(stream));
        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public void Decode_RejectsBadCrcMetadataAndTrailingData()
    {
        using HdrRgba16FloatBuffer source = CreateSource();
        byte[] valid = Encode(source, 1000f);

        byte[] badCrc = (byte[])valid.Clone();
        int idat = FindChunk(badCrc, "IDAT");
        int idatLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(badCrc.AsSpan(idat)));
        badCrc[idat + 8 + idatLength] ^= 1;
        Assert.Throws<InvalidDataException>(() => Decode(badCrc));

        byte[] badCicp = (byte[])valid.Clone();
        int cicp = FindChunk(badCicp, "cICP");
        badCicp[cicp + 8] = 1;
        RewriteChunkCrc(badCicp, cicp);
        Assert.Throws<InvalidDataException>(() => Decode(badCicp));

        byte[] trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        Assert.Throws<InvalidDataException>(() => Decode(trailing));
    }

    [Fact]
    public void Decode_RejectsHugeDimensionsBeforePixelAllocation()
    {
        using HdrRgba16FloatBuffer source = CreateSource();
        byte[] encoded = Encode(source, 1000f);
        int ihdr = FindChunk(encoded, "IHDR");
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(ihdr + 8), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(ihdr + 12), 100_000);
        RewriteChunkCrc(encoded, ihdr);

        Assert.Throws<InvalidDataException>(() => Decode(encoded));
    }

    [Fact]
    public void Decode_RejectsImpossibleChunkLengthBeforeAllocatingChunk()
    {
        using HdrRgba16FloatBuffer source = CreateSource();
        byte[] encoded = Encode(source, 1000f);
        int idat = FindChunk(encoded, "IDAT");
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(idat), 500_000_000);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Decode(encoded));
        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HdrRgba16FloatBuffer Decode(byte[] encoded) =>
        new HdrPngImageDecoder().Decode(new MemoryStream(encoded));

    private static byte[] Encode(HdrRgba16FloatBuffer source, float peak)
    {
        using var encoded = new MemoryStream();
        new HdrPngImageEncoder().Encode(source, encoded, new HdrImageEncodingOptions
        {
            MasteringDisplayMaximumNits = peak
        });
        return encoded.ToArray();
    }

    private static HdrRgba16FloatBuffer CreateSource()
    {
        var pixels = new HdrRgba16FloatBuffer(3, 2);
        for (int y = 0; y < pixels.Height; y++)
        {
            Span<byte> row = pixels.GetWritableRowSpan(y);
            for (int x = 0; x < pixels.Width; x++)
            {
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                float scale = 0.5f + x + y;
                float alpha = x == 2 ? 0.5f : 1f;
                WriteHalf(row, offset, 0.20f * scale * alpha);
                WriteHalf(row, offset + 2, 0.35f * scale * alpha);
                WriteHalf(row, offset + 4, 0.50f * scale * alpha);
                WriteHalf(row, offset + 6, alpha);
            }
        }

        return pixels;
    }

    private static int FindChunk(byte[] png, string type)
    {
        int position = 8;
        while (position < png.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position)));
            string current = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            if (current == type)
            {
                return position;
            }

            position += 12 + length;
        }

        throw new InvalidOperationException($"Chunk '{type}' was not found.");
    }

    private static void RewriteChunkCrc(byte[] png, int chunk)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(chunk)));
        uint crc = uint.MaxValue;
        for (int index = chunk + 4; index < chunk + 8 + length; index++)
        {
            crc ^= png[index];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(chunk + 8 + length), crc ^ uint.MaxValue);
    }

    private static void WriteHalf(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static float ReadHalf(ReadOnlySpan<byte> source, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort))));
}
