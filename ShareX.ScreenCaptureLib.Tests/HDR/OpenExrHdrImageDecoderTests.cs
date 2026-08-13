using System.Buffers.Binary;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class OpenExrHdrImageDecoderTests
{
    [Fact]
    public void Decode_RoundTripsShareXHalfPixelsAndRestoresStreamPosition()
    {
        using HdrRgba16FloatBuffer source = CreateSource(3, 2);
        using var encoded = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(source, encoded);
        encoded.Position = 7;

        using HdrRgba16FloatBuffer decoded = new OpenExrHdrImageDecoder().Decode(encoded);

        Assert.Equal(7, encoded.Position);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        for (int y = 0; y < source.Height; y++)
        {
            Assert.True(source.GetRowSpan(y).SequenceEqual(decoded.GetRowSpan(y)));
        }
    }

    [Fact]
    public void Decode_DisplayReferencedFileRestoresShareXScRgbScale()
    {
        const float referenceWhiteNits = 203f;
        float capturedWhite = referenceWhiteNits / HdrRgba16FloatBuffer.ReferenceWhiteNits;
        byte[] sourceBytes = new byte[8];
        WriteHalf(sourceBytes, 0, capturedWhite);
        WriteHalf(sourceBytes, 2, capturedWhite * 0.5f);
        WriteHalf(sourceBytes, 4, capturedWhite * 2f);
        WriteHalf(sourceBytes, 6, 1f);
        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, 8, 1, 1);
        using var encoded = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(
            source,
            encoded,
            new HdrImageEncodingOptions
            {
                OpenExrExposureMode = OpenExrExposureMode.DisplayReferenced,
                OpenExrReferenceWhiteNits = referenceWhiteNits
            });

        var decoder = new OpenExrHdrImageDecoder();
        Assert.True(decoder.IsSupported(encoded));
        using HdrRgba16FloatBuffer decoded = decoder.Decode(encoded);

        ReadOnlySpan<byte> expected = source.GetRowSpan(0);
        ReadOnlySpan<byte> actual = decoded.GetRowSpan(0);
        Assert.InRange(Math.Abs(ReadHalf(actual, 0) - ReadHalf(expected, 0)), 0f, 0.005f);
        Assert.InRange(Math.Abs(ReadHalf(actual, 2) - ReadHalf(expected, 2)), 0f, 0.005f);
        Assert.InRange(Math.Abs(ReadHalf(actual, 4) - ReadHalf(expected, 4)), 0f, 0.01f);
        Assert.Equal(1f, ReadHalf(actual, 6));
    }

    [Fact]
    public void Decode_RejectsCompressionColorMetadataOffsetsAndTrailingData()
    {
        using HdrRgba16FloatBuffer source = CreateSource(2, 2);
        byte[] valid = Encode(source);

        byte[] compressed = (byte[])valid.Clone();
        compressed[FindAttributeValue(compressed, "compression")] = 1;
        Assert.Throws<InvalidDataException>(() => Decode(compressed));

        byte[] wrongWhite = (byte[])valid.Clone();
        wrongWhite[FindAttributeValue(wrongWhite, "whiteLuminance")] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => Decode(wrongWhite));

        int offsetTable = FindOffsetTable(valid);
        byte[] badOffset = (byte[])valid.Clone();
        long firstChunk = BinaryPrimitives.ReadInt64LittleEndian(badOffset.AsSpan(offsetTable));
        BinaryPrimitives.WriteInt64LittleEndian(badOffset.AsSpan(offsetTable), firstChunk + 1);
        Assert.Throws<InvalidDataException>(() => Decode(badOffset));

        byte[] trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        Assert.Throws<InvalidDataException>(() => Decode(trailing));
        Assert.Throws<InvalidDataException>(() => Decode(valid.AsSpan(0, valid.Length - 1).ToArray()));
    }

    [Fact]
    public void Decode_RejectsHugeDimensionsBeforeAllocatingPixels()
    {
        using HdrRgba16FloatBuffer source = CreateSource(1, 1);
        byte[] encoded = Encode(source);
        int dataWindow = FindAttributeValue(encoded, "dataWindow");
        int displayWindow = FindAttributeValue(encoded, "displayWindow");

        foreach (int window in new[] { dataWindow, displayWindow })
        {
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(window + 8), 100_000);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(window + 12), 100_000);
        }

        Assert.Throws<InvalidDataException>(() => Decode(encoded));
    }

    [Fact]
    public void Decode_SanitizesNonFiniteHalfSamples()
    {
        using HdrRgba16FloatBuffer source = CreateSource(1, 1);
        byte[] encoded = Encode(source);
        int offsetTable = FindOffsetTable(encoded);
        int firstChunk = checked((int)BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(offsetTable)));
        int samples = firstChunk + 8;

        // ShareX planar channel order is A, B, G, R.
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(samples + 2), 0xfc00); // B = -Infinity
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(samples + 4), 0x7c00); // G = +Infinity
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(samples + 6), 0x7e00); // R = NaN

        using HdrRgba16FloatBuffer decoded = Decode(encoded);
        ReadOnlySpan<byte> row = decoded.GetRowSpan(0);
        Assert.Equal(0f, ReadHalf(row, 0));
        Assert.Equal(65504f, ReadHalf(row, 2));
        Assert.Equal(-65504f, ReadHalf(row, 4));
        Assert.Equal(0.5f, ReadHalf(row, 6));
    }

    [Fact]
    public void DecodeFile_RoundTripsOwnedBuffer()
    {
        using HdrRgba16FloatBuffer source = CreateSource(2, 1);
        byte[] encoded = Encode(source);
        string filePath = Path.Combine(Path.GetTempPath(), $"ShareX-HDR-decode-{Guid.NewGuid():N}.exr");
        try
        {
            File.WriteAllBytes(filePath, encoded);
            using HdrRgba16FloatBuffer decoded = new OpenExrHdrImageDecoder().DecodeFile(filePath);
            Assert.True(source.GetRowSpan(0).SequenceEqual(decoded.GetRowSpan(0)));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void IsSupported_RecognizesOnlyShareXHeaderAndRestoresStreamPosition()
    {
        using HdrRgba16FloatBuffer source = CreateSource(2, 2);
        byte[] valid = Encode(source);
        var decoder = new OpenExrHdrImageDecoder();
        using var validStream = new MemoryStream(valid);
        validStream.Position = 7;

        Assert.True(decoder.IsSupported(validStream));
        Assert.Equal(7, validStream.Position);

        byte[] compressed = (byte[])valid.Clone();
        compressed[FindAttributeValue(compressed, "compression")] = 1;
        using var compressedStream = new MemoryStream(compressed);
        Assert.False(decoder.IsSupported(compressedStream));

        using var ordinaryStream = new MemoryStream(new byte[64]);
        Assert.False(decoder.IsSupported(ordinaryStream));

        int offsetTable = FindOffsetTable(valid);
        using var truncatedStream = new MemoryStream(valid.AsSpan(0, offsetTable).ToArray());
        Assert.False(decoder.IsSupported(truncatedStream));
    }

    [Fact]
    public void IsSupportedFile_RejectsUnrelatedExrExtension()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"ShareX-HDR-probe-{Guid.NewGuid():N}.exr");
        try
        {
            File.WriteAllBytes(filePath, new byte[64]);
            Assert.False(new OpenExrHdrImageDecoder().IsSupportedFile(filePath));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void DecodeDocumentFile_CreatesCalibratedOwnedDocument()
    {
        using HdrRgba16FloatBuffer source = CreateSource(2, 1);
        byte[] encoded = Encode(source);
        string filePath = Path.Combine(Path.GetTempPath(), $"ShareX-HDR-document-{Guid.NewGuid():N}.exr");
        try
        {
            File.WriteAllBytes(filePath, encoded);
            using HdrImageDocument document = new OpenExrHdrImageDecoder().DecodeDocumentFile(
                filePath,
                sdrWhiteNits: 250f,
                displayPeakNits: 1200f);

            Assert.Equal(2, document.MasterPixels.Width);
            Assert.Equal(1, document.MasterPixels.Height);
            Assert.True(source.GetRowSpan(0).SequenceEqual(document.MasterPixels.GetRowSpan(0)));
            HdrCaptureSourceSegment segment = Assert.Single(document.SourceSegments);
            Assert.Equal(new System.Drawing.Rectangle(0, 0, 2, 1), segment.DestinationRectangle);
            Assert.Equal(Path.GetFileName(filePath), segment.DisplayDeviceName);
            Assert.True(segment.WasHdrActive);
            Assert.Equal(250f, segment.SdrWhiteNits);
            Assert.Equal(1200f, segment.DisplayPeakNits);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static HdrRgba16FloatBuffer Decode(byte[] encoded) =>
        new OpenExrHdrImageDecoder().Decode(new MemoryStream(encoded));

    private static byte[] Encode(HdrRgba16FloatBuffer source)
    {
        using var encoded = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(source, encoded);
        return encoded.ToArray();
    }

    private static HdrRgba16FloatBuffer CreateSource(int width, int height)
    {
        var pixels = new HdrRgba16FloatBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = pixels.GetWritableRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                float index = y * width + x;
                WriteHalf(row, offset, -0.25f + index);
                WriteHalf(row, offset + 2, 1.5f + index);
                WriteHalf(row, offset + 4, 8f + index);
                WriteHalf(row, offset + 6, 0.5f + 0.5f * (x & 1));
            }
        }

        return pixels;
    }

    private static int FindOffsetTable(byte[] file)
    {
        int position = 8;
        while (file[position] != 0)
        {
            SkipNullTerminatedAscii(file, ref position);
            SkipNullTerminatedAscii(file, ref position);
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position));
            position += sizeof(int) + size;
        }

        return position + 1;
    }

    private static int FindAttributeValue(byte[] file, string expectedName)
    {
        int position = 8;
        while (file[position] != 0)
        {
            string name = ReadNullTerminatedAscii(file, ref position);
            SkipNullTerminatedAscii(file, ref position);
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position));
            position += sizeof(int);
            if (name == expectedName)
            {
                return position;
            }

            position += size;
        }

        throw new InvalidOperationException($"Attribute '{expectedName}' was not found.");
    }

    private static string ReadNullTerminatedAscii(byte[] file, ref int position)
    {
        int start = position;
        while (file[position++] != 0)
        {
        }

        return System.Text.Encoding.ASCII.GetString(file, start, position - start - 1);
    }

    private static void SkipNullTerminatedAscii(byte[] file, ref int position)
    {
        while (file[position++] != 0)
        {
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
