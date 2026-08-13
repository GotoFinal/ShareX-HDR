using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Diagnostics;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class HdrFileRoundTripStressTests
{
    private const int CycleCount = 500;

    [Fact]
    public void AllFormats_EncodeDecodeCyclesKeepResourcesBounded()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_FILE_STRESS_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        using HdrRgba16FloatBuffer source = CreateSource(32, 24);
        IHdrImageEncoder[] encoders =
        {
            new OpenExrHdrImageEncoder(),
            new HdrPngImageEncoder(),
            new UltraHdrJpegImageEncoder(),
            new AvifHdrImageEncoder()
        };

        foreach (IHdrImageEncoder encoder in encoders)
        {
            RunRoundTrip(source, encoder);
        }

        ForceCollection();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        int baselineHandles = process.HandleCount;
        long baselinePrivateBytes = process.PrivateMemorySize64;
        var stopwatch = Stopwatch.StartNew();

        for (int cycle = 0; cycle < CycleCount; cycle++)
        {
            RunRoundTrip(source, encoders[cycle % encoders.Length]);
        }

        stopwatch.Stop();
        ForceCollection();
        process.Refresh();

        Assert.InRange(process.HandleCount, 0, baselineHandles + 32);
        Assert.InRange(process.PrivateMemorySize64, 0, baselinePrivateBytes + 64L * 1024 * 1024);
        Console.WriteLine(
            $"HDR file stress: {CycleCount} encode/decode cycles in {stopwatch.Elapsed.TotalSeconds:F2}s; " +
            $"handles {baselineHandles}->{process.HandleCount}; " +
            $"private bytes {baselinePrivateBytes}->{process.PrivateMemorySize64}.");
    }

    private static void RunRoundTrip(HdrRgba16FloatBuffer source, IHdrImageEncoder encoder)
    {
        using var encoded = new MemoryStream();
        HdrEncodedImageInfo info = encoder.Encode(source, encoded);
        Assert.True(info.BytesWritten > 0);

        encoded.Position = 0;
        using HdrRgba16FloatBuffer decoded = encoder.Format switch
        {
            HdrFileFormat.OpenExr => new OpenExrHdrImageDecoder().Decode(encoded),
            HdrFileFormat.HdrPng => new HdrPngImageDecoder().Decode(encoded),
            HdrFileFormat.UltraHdrJpeg => new UltraHdrJpegImageDecoder().Decode(encoded),
            HdrFileFormat.Avif => new AvifHdrImageDecoder().Decode(encoded),
            _ => throw new InvalidOperationException($"Unexpected HDR format {encoder.Format}.")
        };

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(decoded.GetRowSpan(0).Length >= source.Width * HdrRgba16FloatBuffer.BytesPerPixel);
    }

    private static HdrRgba16FloatBuffer CreateSource(int width, int height)
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
                WriteHalf(row, offset, 0.25f + horizontal * 9f);
                WriteHalf(row, offset + 2, 0.125f + vertical * 6f);
                WriteHalf(row, offset + 4, 0.5f + (horizontal + vertical) * 3f);
                WriteHalf(row, offset + 6, 1f);
            }
        }

        return pixels;
    }

    private static void WriteHalf(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset, sizeof(ushort)),
            BitConverter.HalfToUInt16Bits((Half)value));

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
