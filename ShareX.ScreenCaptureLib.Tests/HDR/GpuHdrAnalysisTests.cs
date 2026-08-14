using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class GpuHdrAnalysisTests
{
    [Fact]
    public void BoundaryMedianSelection_MatchesSortedOrder()
    {
        var random = new Random(17391);

        for (int length = 1; length <= 257; length++)
        {
            for (int iteration = 0; iteration < 8; iteration++)
            {
                float[] values = Enumerable.Range(0, length)
                    .Select(_ => (float)random.Next(-12, 13) / 4f)
                    .ToArray();
                float expected = values.Order().ElementAt(length / 2);

                Assert.Equal(
                    expected,
                    HdrToSdrToneMapper.SelectMedianForTests((float[])values.Clone()));
            }
        }
    }

    [Fact]
    public void HeadroomMask_MatchesCpuForPackedRowsAndEdgeWords()
    {
        const int width = 67;
        const int height = 19;
        const float paperWhiteScRgb = 203f / 80f;
        byte[] pixels = CreateSolidFrame(width, height, 0.5f);
        SetPixel(pixels, width, 0, 0, 4f);
        SetPixel(pixels, width, 31, 0, 4f);
        SetPixel(pixels, width, 32, 0, 4f);
        SetPixel(pixels, width, 66, 0, 4f);
        SetPixel(pixels, width, 5, 18, 4f);
        SetPixel(pixels, width, 10, 10, -4f);

        using HdrRgba16FloatBuffer buffer = HdrRgba16FloatBuffer.CopyFrom(
            pixels,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        using var session = new GpuHdrToSdrToneMapper.Session();
        byte[] gpuMask = session.CreateHeadroomMaskForTests(
            buffer,
            HdrToSdrToneMapper.GetHeadroomThresholdScRgb(
                new HdrCaptureSettings(),
                203f));
        byte[] cpuMask = CreateCpuMask(
            pixels,
            width,
            height,
            paperWhiteScRgb);

        Assert.Equal(cpuMask, gpuMask);
    }

    [Fact]
    public void GpuHeadroomInput_ProducesSamePreciseAnalysisAsCpuScan()
    {
        const int width = 256;
        const int height = 256;
        byte[] pixels = CreateSolidFrame(width, height, 0.5f);
        FillRectangle(pixels, width, 12, 17, 151, 213, 4f);
        var settings = new HdrCaptureSettings
        {
            ProcessingBackend = HdrProcessingBackend.Gpu,
            ToneMappingMode = HdrToneMappingMode.ContentAware,
            PeakBrightnessMode = HdrPeakBrightnessMode.Automatic
        };
        using HdrRgba16FloatBuffer buffer = HdrRgba16FloatBuffer.CopyFrom(
            pixels,
            width * HdrRgba16FloatBuffer.BytesPerPixel,
            width,
            height);
        using var session = new GpuHdrToSdrToneMapper.Session();
        byte[] gpuHeadroom = session.CreateHeadroomMaskForTests(
            buffer,
            HdrToSdrToneMapper.GetHeadroomThresholdScRgb(settings, 203f));

        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            HdrToSdrToneMapper.ToneMapInputAnalysis cpu =
                HdrToSdrToneMapper.AnalyzeToneMapInput(
                    source,
                    width * HdrRgba16FloatBuffer.BytesPerPixel,
                    width,
                    height,
                    settings,
                    203f,
                    1000f,
                    preserveAlpha: true);
            HdrToSdrToneMapper.ToneMapInputAnalysis gpu =
                HdrToSdrToneMapper.AnalyzeToneMapInputWithHeadroomMask(
                    source,
                    width * HdrRgba16FloatBuffer.BytesPerPixel,
                    width,
                    height,
                    settings,
                    203f,
                    1000f,
                    gpuHeadroom,
                    preserveAlpha: true);

            Assert.Equal(cpu.ToneMapMask, gpu.ToneMapMask);
            Assert.Equal(cpu.ContentPeak.SampleCount, gpu.ContentPeak.SampleCount);
            Assert.Equal(cpu.ContentPeak.SampleStep, gpu.ContentPeak.SampleStep);
            Assert.Equal(cpu.ContentPeak.ObservedMaximumNits, gpu.ContentPeak.ObservedMaximumNits, 3);
            Assert.Equal(cpu.Parameters.SourcePeakNits, gpu.Parameters.SourcePeakNits, 3);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] CreateCpuMask(
        byte[] pixels,
        int width,
        int height,
        float paperWhiteScRgb)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return HdrToSdrToneMapper.CreateToneMapMaskR8(
                handle.AddrOfPinnedObject(),
                width * HdrRgba16FloatBuffer.BytesPerPixel,
                width,
                height,
                paperWhiteScRgb);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] CreateSolidFrame(int width, int height, float value)
    {
        byte[] pixels = new byte[checked(width * height * HdrRgba16FloatBuffer.BytesPerPixel)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SetPixel(pixels, width, x, y, value);
            }
        }
        return pixels;
    }

    private static void FillRectangle(
        byte[] pixels,
        int width,
        int left,
        int top,
        int rectangleWidth,
        int rectangleHeight,
        float value)
    {
        for (int y = top; y < top + rectangleHeight; y++)
        {
            for (int x = left; x < left + rectangleWidth; x++)
            {
                SetPixel(pixels, width, x, y, value);
            }
        }
    }

    private static void SetPixel(
        Span<byte> pixels,
        int width,
        int x,
        int y,
        float value)
    {
        int offset = (y * width + x) * HdrRgba16FloatBuffer.BytesPerPixel;
        WriteHalf(pixels, offset, value);
        WriteHalf(pixels, offset + 2, value);
        WriteHalf(pixels, offset + 4, value);
        WriteHalf(pixels, offset + 6, 1f);
    }

    private static void WriteHalf(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(offset, 2),
            BitConverter.HalfToUInt16Bits((Half)value));
}
