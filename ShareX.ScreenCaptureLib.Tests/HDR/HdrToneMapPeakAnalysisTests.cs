using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrToneMapPeakAnalysisTests
{
    [Fact]
    public void AutomaticPeak_UsesRobustContentPercentileInsteadOfFixedThousandNits()
    {
        using HdrRgba16FloatBuffer pixels = CreateGrayPixels(1000, 400f);
        WriteGrayPixel(pixels, 999, 10000f);
        var settings = new HdrCaptureSettings
        {
            ToneMappingMode = HdrToneMappingMode.Uniform,
            PeakBrightnessMode = HdrPeakBrightnessMode.Automatic
        };

        HdrToSdrToneMapper.ToneMapInputAnalysis analysis = Analyze(pixels, settings, 203f);

        Assert.Equal(1000, analysis.ContentPeak.SampleCount);
        Assert.InRange(analysis.Parameters.SourcePeakNits, 400f, 450f);
        Assert.Equal(10000f, analysis.ContentPeak.ObservedMaximumNits);
    }

    [Fact]
    public void AutomaticPeak_WithNoHdrSamples_PreservesPaperWhite()
    {
        using HdrRgba16FloatBuffer pixels = CreateGrayPixels(256, 180f);
        var settings = new HdrCaptureSettings
        {
            ToneMappingMode = HdrToneMappingMode.Uniform,
            PeakBrightnessMode = HdrPeakBrightnessMode.Automatic
        };

        HdrToSdrToneMapper.ToneMapInputAnalysis analysis = Analyze(pixels, settings, 203f);

        Assert.Equal(0, analysis.ContentPeak.SampleCount);
        Assert.InRange(analysis.Parameters.SourcePeakNits, 203f, 203.1f);
    }

    [Fact]
    public void ManualOverrides_ControlSourcePeakAndPaperWhiteExactly()
    {
        using HdrRgba16FloatBuffer pixels = CreateGrayPixels(256, 1000f);
        var settings = new HdrCaptureSettings
        {
            ToneMappingMode = HdrToneMappingMode.Uniform,
            PeakBrightnessMode = HdrPeakBrightnessMode.Custom,
            HdrBrightnessNits = 4000f,
            PaperWhiteMode = HdrPaperWhiteMode.Custom,
            PaperWhiteNits = 250f
        };

        HdrToSdrToneMapper.ToneMapInputAnalysis analysis = Analyze(pixels, settings, 500f);

        Assert.Equal(4000f, analysis.Parameters.SourcePeakNits);
        Assert.Equal(250f, analysis.Parameters.PaperWhiteNits);
        Assert.True(float.IsNaN(analysis.ContentPeak.EffectivePeakNits));
    }

    [Fact]
    public void GpuToneMap_WhenHardwareIsAvailable_MatchesCpuOutput()
    {
        const int width = 64;
        const int height = 32;
        using var pixels = new HdrRgba16FloatBuffer(width, height);

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = pixels.GetWritableRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                float alpha = (x + y) % 11 == 0 ? 0.5f : 1f;
                float nits = 80f + 3920f * x / (width - 1f);
                float scRgb = nits / HdrRgba16FloatBuffer.ReferenceWhiteNits;
                int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;
                WriteHalf(row, offset, scRgb * alpha);
                WriteHalf(row, offset + 2, scRgb * 0.7f * alpha);
                WriteHalf(row, offset + 4, scRgb * 0.35f * alpha);
                WriteHalf(row, offset + 6, alpha);
            }
        }

        GpuHdrToSdrToneMapper.Session gpuSession;
        try
        {
            gpuSession = new GpuHdrToSdrToneMapper.Session();
        }
        catch
        {
            // Headless Windows CI can lack a hardware D3D11 adapter. The
            // production path handles this by falling back to the CPU.
            return;
        }

        IntPtr source = Marshal.AllocHGlobal(pixels.PixelBytes.Length);
        try
        {
            Marshal.Copy(pixels.PixelBytes.ToArray(), 0, source, pixels.PixelBytes.Length);
            var settings = new HdrCaptureSettings
            {
                ProcessingBackend = HdrProcessingBackend.Gpu,
                ToneMappingMode = HdrToneMappingMode.Uniform,
                PeakBrightnessMode = HdrPeakBrightnessMode.Automatic
            };
            using Bitmap cpu = HdrToSdrToneMapper.ToneMapRgba16Float(
                source,
                pixels.RowBytes,
                width,
                height,
                settings,
                203f,
                1000f,
                preserveAlpha: true);
            using Bitmap gpu = gpuSession.ToneMap(
                source,
                pixels.RowBytes,
                width,
                height,
                settings,
                203f,
                1000f,
                preserveAlpha: true,
                windowRegions: null,
                out _);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color expected = cpu.GetPixel(x, y);
                    Color actual = gpu.GetPixel(x, y);
                    Assert.InRange(Math.Abs(expected.A - actual.A), 0, 1);
                    Assert.InRange(Math.Abs(expected.R - actual.R), 0, 2);
                    Assert.InRange(Math.Abs(expected.G - actual.G), 0, 2);
                    Assert.InRange(Math.Abs(expected.B - actual.B), 0, 2);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            gpuSession.Dispose();
        }
    }

    private static HdrToSdrToneMapper.ToneMapInputAnalysis Analyze(
        HdrRgba16FloatBuffer pixels,
        HdrCaptureSettings settings,
        float displayPaperWhiteNits)
    {
        return HdrToSdrToneMapper.AnalyzeToneMapInput(
            pixels,
            settings,
            displayPaperWhiteNits,
            1000f,
            preserveAlpha: true);
    }

    private static HdrRgba16FloatBuffer CreateGrayPixels(int pixelCount, float nits)
    {
        var result = new HdrRgba16FloatBuffer(pixelCount, 1);

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            WriteGrayPixel(result, pixel, nits);
        }

        return result;
    }

    private static void WriteGrayPixel(HdrRgba16FloatBuffer pixels, int pixel, float nits)
    {
        Span<byte> row = pixels.GetWritableRowSpan(0);
        int offset = pixel * HdrRgba16FloatBuffer.BytesPerPixel;
        float scRgb = nits / HdrRgba16FloatBuffer.ReferenceWhiteNits;
        WriteHalf(row, offset, scRgb);
        WriteHalf(row, offset + 2, scRgb);
        WriteHalf(row, offset + 4, scRgb);
        WriteHalf(row, offset + 6, 1f);
    }

    private static void WriteHalf(Span<byte> bytes, int offset, float value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));
    }
}
