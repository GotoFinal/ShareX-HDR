using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrImageEncoderTests
{
    [Fact]
    public void OpenExr_PreservesHalfSamplesAndWritesAlphabeticalPlanarChannels()
    {
        byte[] sourceBytes = new byte[16];
        WritePixel(sourceBytes, 0, 1f, 2f, 3f, 0.5f);
        WritePixel(sourceBytes, 8, -0.25f, 4f, 0.125f, 1f);

        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, 16, 2, 1);
        using var encoded = new MemoryStream();
        HdrEncodedImageInfo info = new OpenExrHdrImageEncoder().Encode(source, encoded);
        byte[] file = encoded.ToArray();

        Assert.Equal(20000630, BinaryPrimitives.ReadInt32LittleEndian(file));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal(file.Length, info.BytesWritten);
        Assert.True(info.IsLossless);

        int position = 8;
        Dictionary<string, byte[]> attributes = ReadExrAttributes(file, ref position);
        Assert.Equal(new[] { "A", "B", "G", "R" }, ReadExrChannelNames(attributes["channels"]));
        Assert.Equal(80f, BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(attributes["whiteLuminance"])));

        long firstChunkOffset = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(position, 8));
        Assert.Equal(position + 8, firstChunkOffset);
        position = checked((int)firstChunkOffset);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position + 4)));

        ReadOnlySpan<byte> samples = file.AsSpan(position + 8, 16);
        AssertHalfSamples(samples.Slice(0, 4), 0.5f, 1f);      // A
        AssertHalfSamples(samples.Slice(4, 4), 3f, 0.125f);    // B
        AssertHalfSamples(samples.Slice(8, 4), 2f, 4f);        // G
        AssertHalfSamples(samples.Slice(12, 4), 1f, -0.25f);   // R
    }

    [Fact]
    public void HdrPng_WritesPqBt2020MetadataAndSixteenBitPixels()
    {
        byte[] sourceBytes = new byte[8];
        WritePixel(sourceBytes, 0, 1f, 1f, 1f, 1f);

        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, 8, 1, 1);
        using var encoded = new MemoryStream();
        HdrEncodedImageInfo info = new HdrPngImageEncoder().Encode(source, encoded);
        byte[] file = encoded.ToArray();

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, file.AsSpan(0, 8).ToArray());
        Assert.Equal(file.Length, info.BytesWritten);
        Assert.Equal(80f, info.MaxContentLightLevelNits, 2);
        Assert.Equal(80f, info.MaxFrameAverageLightLevelNits, 2);

        Dictionary<string, byte[]> chunks = ReadPngChunks(file);
        Assert.Equal(new byte[] { 9, 16, 0, 1 }, chunks["cICP"]);
        Assert.Equal((uint)10000000, BinaryPrimitives.ReadUInt32BigEndian(chunks["mDCV"].AsSpan(16)));
        Assert.Equal((uint)5, BinaryPrimitives.ReadUInt32BigEndian(chunks["mDCV"].AsSpan(20)));
        Assert.Equal((uint)800000, BinaryPrimitives.ReadUInt32BigEndian(chunks["cLLI"]));
        Assert.Equal((uint)800000, BinaryPrimitives.ReadUInt32BigEndian(chunks["cLLI"].AsSpan(4)));

        using var compressed = new MemoryStream(chunks["IDAT"]);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var pixels = new MemoryStream();
        zlib.CopyTo(pixels);
        byte[] row = pixels.ToArray();

        Assert.Equal(9, row.Length);
        Assert.Equal(0, row[0]);
        ushort red = BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(1));
        ushort green = BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(3));
        ushort blue = BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(5));
        Assert.InRange(Math.Abs(red - green), 0, 1);
        Assert.InRange(Math.Abs(red - blue), 0, 1);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(7)));

        ushort expectedPq80Nits = QuantizePq(80f);
        Assert.InRange(Math.Abs(red - expectedPq80Nits), 0, 1);
    }

    [Fact]
    public void UltraHdrInput_RejectsTransparencyUnlessExplicitlyFlattenedToBlack()
    {
        byte[] sourceBytes = new byte[8];
        WritePixel(sourceBytes, 0, 1f, 0.5f, 0.25f, 0.5f);

        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, 8, 1, 1);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            UltraHdrJpegImageEncoder.CreateLibUltraHdrInput(source));
        Assert.Contains("OpenEXR/HDR PNG", exception.Message);

        byte[] converted = UltraHdrJpegImageEncoder.CreateLibUltraHdrInput(
            source,
            flattenTransparencyToBlack: true);

        float scale = 80f / 203f;
        Assert.Equal(1f * scale, ReadHalf(converted, 0), 3);
        Assert.Equal(0.5f * scale, ReadHalf(converted, 2), 3);
        Assert.Equal(0.25f * scale, ReadHalf(converted, 4), 3);
        Assert.Equal(1f, ReadHalf(converted, 6));
    }

    [Fact]
    public void UltraHdrInput_ConvertsOpaqueScRgbReferenceWhite()
    {
        byte[] sourceBytes = new byte[8];
        WritePixel(sourceBytes, 0, 1f, 0.5f, 0.25f, 1f);
        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(sourceBytes, 8, 1, 1);

        byte[] converted = UltraHdrJpegImageEncoder.CreateLibUltraHdrInput(source);

        float scale = 80f / 203f;
        Assert.Equal(1f * scale, ReadHalf(converted, 0), 3);
        Assert.Equal(0.5f * scale, ReadHalf(converted, 2), 3);
        Assert.Equal(0.25f * scale, ReadHalf(converted, 4), 3);
        Assert.Equal(1f, ReadHalf(converted, 6));
    }

    [Fact]
    public void UltraHdrJpeg_EncodesBackwardCompatibleGainMapJpeg()
    {
        const int width = 8;
        const int height = 8;
        byte[] sourceBytes = new byte[width * height * 8];

        for (int index = 0; index < width * height; index++)
        {
            float value = index < width ? 1f : 8f;
            WritePixel(sourceBytes, index * 8, value, value * 0.75f, value * 0.5f, 1f);
        }

        using HdrRgba16FloatBuffer source = HdrRgba16FloatBuffer.CopyFrom(
            sourceBytes,
            width * 8,
            width,
            height);
        using var encoded = new MemoryStream();
        HdrEncodedImageInfo info = new UltraHdrJpegImageEncoder().Encode(source, encoded);
        byte[] jpeg = encoded.ToArray();

        Assert.Equal(0xff, jpeg[0]);
        Assert.Equal(0xd8, jpeg[1]);
        Assert.Equal(0xff, jpeg[^2]);
        Assert.Equal(0xd9, jpeg[^1]);
        Assert.Equal(jpeg.Length, info.BytesWritten);
        Assert.True(UltraHdrJpegImageEncoder.IsUltraHdrImage(jpeg));
    }

    [Fact]
    public void HdrOutputSettings_DefaultToExistingSdrBehaviorAndClampMetadata()
    {
        var settings = new HdrFileOutputSettings();

        Assert.Equal(HdrOutputMode.SdrOnly, settings.OutputMode);
        Assert.Equal(HdrFileFormat.UltraHdrJpeg, settings.FileFormat);

        settings.MasteringDisplayMaximumNits = 50000f;
        settings.MasteringDisplayMinimumNits = -1f;
        settings.JpegQuality = 500;
        settings.GainMapQuality = 0;

        Assert.Equal(10000f, settings.MasteringDisplayMaximumNits);
        Assert.Equal(0f, settings.MasteringDisplayMinimumNits);
        Assert.Equal(100, settings.JpegQuality);
        Assert.Equal(1, settings.GainMapQuality);
    }

    private static Dictionary<string, byte[]> ReadExrAttributes(byte[] file, ref int position)
    {
        var attributes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        while (file[position] != 0)
        {
            string name = ReadNullTerminatedAscii(file, ref position);
            _ = ReadNullTerminatedAscii(file, ref position);
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position, 4));
            position += 4;
            attributes.Add(name, file.AsSpan(position, length).ToArray());
            position += length;
        }

        position++;
        return attributes;
    }

    private static string[] ReadExrChannelNames(byte[] channelList)
    {
        var names = new List<string>();
        int position = 0;

        while (channelList[position] != 0)
        {
            names.Add(ReadNullTerminatedAscii(channelList, ref position));
            position += 16;
        }

        return names.ToArray();
    }

    private static string ReadNullTerminatedAscii(byte[] data, ref int position)
    {
        int start = position;

        while (data[position] != 0)
        {
            position++;
        }

        string result = Encoding.ASCII.GetString(data, start, position - start);
        position++;
        return result;
    }

    private static Dictionary<string, byte[]> ReadPngChunks(byte[] file)
    {
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int position = 8;

        while (position < file.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position, 4)));
            string type = Encoding.ASCII.GetString(file, position + 4, 4);
            chunks[type] = file.AsSpan(position + 8, length).ToArray();
            position = checked(position + 12 + length);
        }

        return chunks;
    }

    private static void WritePixel(
        Span<byte> destination,
        int offset,
        float red,
        float green,
        float blue,
        float alpha)
    {
        WriteHalf(destination, offset, red);
        WriteHalf(destination, offset + 2, green);
        WriteHalf(destination, offset + 4, blue);
        WriteHalf(destination, offset + 6, alpha);
    }

    private static void WriteHalf(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static void AssertHalfSamples(ReadOnlySpan<byte> samples, float first, float second)
    {
        Assert.Equal(first, ReadHalf(samples, 0));
        Assert.Equal(second, ReadHalf(samples, 2));
    }

    private static float ReadHalf(ReadOnlySpan<byte> samples, int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(samples.Slice(offset, 2)));

    private static ushort QuantizePq(float nits)
    {
        const double m1 = 2610d / 16384d;
        const double m2 = 2523d / 32d;
        const double c1 = 3424d / 4096d;
        const double c2 = 2413d / 128d;
        const double c3 = 2392d / 128d;
        double value = Math.Pow(nits / 10000d, m1);
        double pq = Math.Pow((c1 + c2 * value) / (1d + c3 * value), m2);
        return (ushort)Math.Round(pq * ushort.MaxValue);
    }
}
