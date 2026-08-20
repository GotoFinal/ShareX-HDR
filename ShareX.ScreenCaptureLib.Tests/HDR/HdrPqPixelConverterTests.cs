using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrPqPixelConverterTests
{
    [Fact]
    public void FastPqLookup_StaysWithinOneSixteenBitCodeValue()
    {
        int maximumDifference = 0;
        for (int sample = 0; sample <= 100000; sample++)
        {
            float nits = sample * 10000f / 100000f;
            ushort expected = QuantizePqExact(nits, ushort.MaxValue);
            ushort actual = HdrPqPixelConverter.QuantizePq(nits, ushort.MaxValue);
            maximumDifference = Math.Max(maximumDifference, Math.Abs(expected - actual));
        }

        Assert.InRange(maximumDifference, 0, 1);
    }

    [Theory]
    [InlineData((int)HdrPqPixelLayout.Rgba10LittleEndian)]
    [InlineData((int)HdrPqPixelLayout.PngRgba16BigEndian)]
    public void CpuConversion_ProducesStableParallelOutput(int layoutValue)
    {
        HdrPqPixelLayout layout = (HdrPqPixelLayout)layoutValue;
        using HdrRgba16FloatBuffer source = CreateSource(641, 129);

        HdrPqPixelConversionResult first = HdrPqPixelConverter.ConvertCpu(
            source,
            1000f,
            layout);
        HdrPqPixelConversionResult second = HdrPqPixelConverter.ConvertCpu(
            source,
            1000f,
            layout);

        Assert.Equal(first.Pixels, second.Pixels);
        Assert.Equal(first.MaxCll, second.MaxCll);
        Assert.Equal(first.MaxFall, second.MaxFall);
        Assert.Equal("CPU", first.Backend);
    }

    [Theory]
    [InlineData((int)HdrPqPixelLayout.Rgba10LittleEndian, 1)]
    [InlineData((int)HdrPqPixelLayout.PngRgba16BigEndian, 6)]
    public void GpuConversion_MatchesCpuWithinQuantizationTolerance(
        int layoutValue,
        int sampleTolerance)
    {
        HdrPqPixelLayout layout = (HdrPqPixelLayout)layoutValue;
        using HdrRgba16FloatBuffer source = CreateSource(127, 73);
        HdrPqPixelConversionResult cpu = HdrPqPixelConverter.ConvertCpu(
            source,
            1000f,
            layout);

        if (!GpuHdrPqPixelConverter.TryConvert(
            source,
            1000f,
            layout,
            out HdrPqPixelConversionResult gpu,
            out string fallbackReason))
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_OUTPUT_PERFORMANCE_TESTS"),
                    "1",
                    StringComparison.Ordinal),
                $"The requested GPU output performance probe fell back: {fallbackReason}");
            return;
        }

        Assert.Equal(cpu.Pixels.Length, gpu.Pixels.Length);
        AssertPackedSamplesClose(cpu.Pixels, gpu.Pixels, layout, sampleTolerance);
        Assert.InRange(Math.Abs(cpu.MaxCll - gpu.MaxCll), 0f, 0.1f);
        Assert.InRange(Math.Abs(cpu.MaxFall - gpu.MaxFall), 0f, 0.1f);
        Assert.Equal("GPU", gpu.Backend);
    }

    private static HdrRgba16FloatBuffer CreateSource(int width, int height)
    {
        var result = new HdrRgba16FloatBuffer(width, height);
        Span<byte> pixels = result.GetWritablePixelSpan();
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = pixels.Slice(y * result.RowBytes, width * 8);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 8;
                float alpha = ((x + y) % 17) / 16f;
                float red = ((x * 13 + y * 3) % 257) / 32f;
                float green = ((x * 5 + y * 11) % 193) / 28f;
                float blue = ((x * 7 + y * 17) % 149) / 24f;
                WriteHalf(row, offset, red * alpha);
                WriteHalf(row, offset + 2, green * alpha);
                WriteHalf(row, offset + 4, blue * alpha);
                WriteHalf(row, offset + 6, alpha);
            }
        }

        return result;
    }

    private static void AssertPackedSamplesClose(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        HdrPqPixelLayout layout,
        int tolerance)
    {
        int rowPrefix = layout == HdrPqPixelLayout.PngRgba16BigEndian ? 1 : 0;
        for (int index = 0; index < expected.Length;)
        {
            if (rowPrefix != 0 && expected[index] == 0 && actual[index] == 0 &&
                (index == 0 || (index % (127 * 8 + 1)) == 0))
            {
                index++;
                continue;
            }

            ushort expectedSample = layout == HdrPqPixelLayout.PngRgba16BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(expected.Slice(index, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(expected.Slice(index, 2));
            ushort actualSample = layout == HdrPqPixelLayout.PngRgba16BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(actual.Slice(index, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(actual.Slice(index, 2));
            Assert.InRange(Math.Abs(expectedSample - actualSample), 0, tolerance);
            index += 2;
        }
    }

    private static void WriteHalf(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static ushort QuantizePqExact(float nits, int maximumSample)
    {
        const double m1 = 2610d / 16384d;
        const double m2 = 2523d / 32d;
        const double c1 = 3424d / 4096d;
        const double c2 = 2413d / 128d;
        const double c3 = 2392d / 128d;
        double normalized = Math.Clamp(nits / 10000d, 0d, 1d);
        double luminancePower = Math.Pow(normalized, m1);
        double pq = Math.Pow(
            (c1 + c2 * luminancePower) / (1d + c3 * luminancePower),
            m2);
        return checked((ushort)Math.Clamp(
            (int)Math.Round(pq * maximumSample),
            0,
            maximumSample));
    }
}
