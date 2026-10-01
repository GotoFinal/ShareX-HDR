using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ShareX.HelpersLib;
using ShareX.HistoryLib;
using ShareX.ScreenCaptureLib;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareX.ScreenCaptureLib.Tests.Imaging;

public class SdrImageCodecTests(ITestOutputHelper output)
{
    [Fact]
    public void ImageSettings_LegacyDefaultsAndNewFormatsRoundTripWithoutOrdinalChanges()
    {
        TaskSettingsImage legacy = JsonConvert.DeserializeObject<TaskSettingsImage>(
            """{"ImageFormat":"TIFF","ImageAutoUseJPEG":false,"ImageAutoUseJPEGSize":321,"ImageJPEGQuality":72}""",
            new StringEnumConverter())!;
        Assert.Equal(EImageFormat.TIFF, legacy.ImageFormat);
        Assert.False(legacy.ImageAutoUseJPEG);
        Assert.Equal(321, legacy.ImageAutoUseJPEGSize);
        Assert.Equal(72, legacy.ImageJPEGQuality);
        Assert.Equal(EImageFormat.JPEG, legacy.ImageSizeFallbackFormat);
        Assert.Equal(90, legacy.ImageAVIFQuality);
        Assert.Equal(6, legacy.ImageAVIFSpeed);
        var defaults = new TaskSettingsImage();
        Assert.Equal(EImageFormat.PNG, defaults.ImageFormat);
        Assert.True(defaults.ImageAutoUseJPEG);
        Assert.Equal(2048, defaults.ImageAutoUseJPEGSize);
        Assert.False(defaults.ImageAutoJPEGQuality);
        Assert.Equal(4, (int)EImageFormat.TIFF);
        foreach (EImageFormat format in new[] { EImageFormat.AVIF, EImageFormat.EXR })
        {
            var hotkey = new TaskSettings { UseDefaultImageSettings = false };
            hotkey.ImageSettings.ImageFormat = format;
            hotkey.ImageSettings.ImageSizeFallbackFormat = EImageFormat.AVIF;
            hotkey.ImageSettings.ImageAVIFQuality = 73;
            string json = JsonConvert.SerializeObject(hotkey, new JsonSerializerSettings
            {
                ContractResolver = new WritablePropertiesOnlyResolver(),
                Converters = { new StringEnumConverter() }
            });
            TaskSettings loaded = JsonConvert.DeserializeObject<TaskSettings>(json, new StringEnumConverter())!;
            Assert.False(loaded.UseDefaultImageSettings);
            Assert.Equal(format, loaded.ImageSettings.ImageFormat);
            Assert.Equal(EImageFormat.AVIF, loaded.ImageSettings.ImageSizeFallbackFormat);
            Assert.Equal(73, loaded.ImageSettings.ImageAVIFQuality);
            Assert.Contains($"\"ImageFormat\":\"{format}\"", json);
        }
    }

    [Fact]
    public void NativeSdrSetting_DefaultsOnAndPersistsOptOut()
    {
        HdrCaptureSettings legacy = JsonConvert.DeserializeObject<HdrCaptureSettings>("{}")!;
        Assert.True(legacy.UseNativeSdrCapture);
        legacy.UseNativeSdrCapture = false;
        HdrCaptureSettings restored = JsonConvert.DeserializeObject<HdrCaptureSettings>(JsonConvert.SerializeObject(legacy))!;
        Assert.False(restored.UseNativeSdrCapture);
        Assert.Equal(HdrMixedMonitorBrightnessMode.MatchHdrDisplay, restored.MixedMonitorBrightnessMode);
    }

    [Theory]
    [InlineData(EImageFormat.AVIF, "avif", "image/avif")]
    [InlineData(EImageFormat.EXR, "exr", "image/x-exr")]
    public void PrepareImage_NewOrdinaryFormatsHaveCorrectArtifactsAndStaySdr(EImageFormat format, string extension, string mime)
    {
        using Bitmap bitmap = CreatePattern(32, 24);
        var settings = CreateSettings(format);
        using ImageData artifact = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.False(artifact.IsHdr);
        Assert.Equal(format, artifact.ImageFormat);
        Assert.Equal(extension, artifact.FileExtension);
        Assert.Equal(mime, artifact.MediaType);
        Assert.Equal(mime, MimeTypes.GetMimeTypeFromFileName("test." + extension));
        Assert.True(MimeTypes.IsImageMimeType(mime));
        Assert.True(FileHelpers.IsImageFile("test." + extension));
        Assert.True(artifact.ImageStream.Length > 0);
        if (format == EImageFormat.AVIF)
        {
            Assert.True(SdrAvifImageCodec.TryProbe(artifact.ImageStream.ToArray(), out int width, out int height));
            Assert.Equal(bitmap.Width, width);
            Assert.Equal(bitmap.Height, height);
            using Bitmap decoded = SdrAvifImageCodec.Decode(artifact.ImageStream);
            AssertPixelsEqual(bitmap, decoded);
        }
        else
        {
            var decoder = new OpenExrHdrImageDecoder();
            Assert.True(decoder.IsSdr(artifact.ImageStream));
            using HdrRgba16FloatBuffer pixels = decoder.Decode(artifact.ImageStream);
            // The source's first pixel is opaque white: ordinary EXR white is 1.0, not 203/80.
            Assert.Equal(1f, ReadHalf(pixels.GetRowSpan(0), 0));
            Assert.Equal(1f, ReadHalf(pixels.GetRowSpan(0), 6));
            Color translucent = bitmap.GetPixel(1, 0);
            ReadOnlySpan<byte> row = pixels.GetRowSpan(0);
            float alpha = translucent.A / 255f;
            Assert.InRange(Math.Abs(ReadHalf(row, 14) - alpha), 0f, 0.0005f);
            Assert.InRange(Math.Abs(ReadHalf(row, 8) - DecodeSrgb(translucent.R) * alpha), 0f, 0.0005f);
        }
    }

    [Fact]
    public void OpenExr_OrdinaryMarkerDoesNotChangeHdrEncodingAndSdrReloadRetainsWhite()
    {
        using var pixels = new HdrRgba16FloatBuffer(1, 1);
        using var hdr = new MemoryStream();
        new OpenExrHdrImageEncoder().Encode(pixels, hdr);
        var decoder = new OpenExrHdrImageDecoder();
        Assert.True(decoder.IsSupported(hdr));
        Assert.False(decoder.IsSdr(hdr));
        using var bitmap = new Bitmap(2, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.White);
        bitmap.SetPixel(1, 0, Color.FromArgb(128, 128, 64, 192));
        using var sdr = new MemoryStream();
        SdrImageCodecs.EncodeOpenExr(bitmap, sdr);
        Assert.True(decoder.IsSdr(sdr));
        SdrImageCodecs.RegisterImageHelpers();
        string path = Path.Combine(Path.GetTempPath(), "ShareX-SdrExr-" + Guid.NewGuid().ToString("N") + ".exr");
        try
        {
            File.WriteAllBytes(path, sdr.ToArray());
            Assert.True(decoder.IsSdrFile(path));
            using Bitmap loaded = ImageHelpers.LoadImage(path);
            Assert.Equal(Color.White.ToArgb(), loaded.GetPixel(0, 0).ToArgb());
            Color translucent = loaded.GetPixel(1, 0);
            Assert.InRange(Math.Abs(translucent.A - 128), 0, 1);
            Assert.InRange(Math.Abs(translucent.R - 128), 0, 1);
            Assert.InRange(Math.Abs(translucent.G - 64), 0, 1);
            Assert.InRange(Math.Abs(translucent.B - 192), 0, 1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    public void OpenExr_OrdinaryLowAlphaColorsAreAssociatedOnlyAfterLinearDecoding(int alphaByte)
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        Color original = Color.FromArgb(alphaByte, 128, 64, 192);
        bitmap.SetPixel(0, 0, original);
        using var encoded = new MemoryStream();
        SdrImageCodecs.EncodeOpenExr(bitmap, encoded);
        using HdrRgba16FloatBuffer pixels = new OpenExrHdrImageDecoder().Decode(encoded);
        float alpha = alphaByte / 255f;
        ReadOnlySpan<byte> row = pixels.GetRowSpan(0);
        foreach ((byte channel, int offset) in new[] { (original.R, 0), (original.G, 2), (original.B, 4) })
        {
            float expected = DecodeSrgb(channel) * alpha;
            Assert.InRange(Math.Abs(ReadHalf(row, offset) - expected), 0f, Math.Max(0.0000001f, expected * 0.001f));
        }
        Assert.Equal((float)(Half)alpha, ReadHalf(row, 6));
        AssertExrReloadColor(encoded, original, Point.Empty);
    }

    [Fact]
    public void OpenExr_OrdinarySignedStrideRetainsLogicalRowOrderAndTransparency()
    {
        const int width = 2, height = 2, stride = 12;
        byte[] bytes =
        [
            33, 91, 203, 255, 192, 64, 128, 17, 0, 0, 0, 0,
            192, 64, 128, 1, 17, 127, 231, 2, 0, 0, 0, 0
        ];
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, -stride, PixelFormat.Format32bppArgb,
                IntPtr.Add(handle.AddrOfPinnedObject(), stride));
            using var encoded = new MemoryStream();
            SdrImageCodecs.EncodeOpenExr(bitmap, encoded);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    AssertExrReloadColor(encoded, bitmap.GetPixel(x, y), new Point(x, y));
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void Avif_LosslessRoundTripUsesSdrCicpAndNeverHdrMetadata()
    {
        using Bitmap bitmap = CreatePattern(48, 32);
        using var encoded = new MemoryStream();
        SdrAvifImageCodec.Encode(bitmap, encoded, 100, 10);
        byte[] bytes = encoded.ToArray();
        Assert.False(AvifHdrImageEncoder.TryProbeHdr(bytes, out _, out _, out _, out _, out _));
        int colorBox = FindBox(bytes, "nclx");
        Assert.True(colorBox >= 0);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(colorBox + 4, 2))); // Rec.709
        Assert.Equal(13, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(colorBox + 6, 2))); // sRGB, not PQ
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(colorBox + 8, 2))); // identity
        Assert.Equal(0x80, bytes[colorBox + 10]); // full range
        int pixelBox = FindBox(bytes, "pixi");
        Assert.True(pixelBox >= 0);
        Assert.Equal(3, bytes[pixelBox + 8]);
        Assert.Equal(new byte[] { 8, 8, 8 }, bytes.AsSpan(pixelBox + 9, 3).ToArray());
        Assert.Equal(-1, FindBox(bytes, "mdcv"));
        Assert.Equal(-1, FindBox(bytes, "clli"));
        using Bitmap decoded = SdrAvifImageCodec.Decode(encoded);
        AssertPixelsEqual(bitmap, decoded);
    }

    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppRgb)]
    public void Avif_OpaqueGdiBitmapFormatsConvertWithoutChangingSdrColors(PixelFormat format)
    {
        using var bitmap = new Bitmap(4, 2, format);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(x * 63, y * 127, 255 - x * 47));
        using var encoded = new MemoryStream();
        SdrAvifImageCodec.Encode(bitmap, encoded, 100, 10);
        using Bitmap decoded = SdrAvifImageCodec.Decode(encoded);
        AssertPixelsEqual(bitmap, decoded);
    }

    [Fact]
    public void Avif_QualityChangesSizeAndPreservesAlphaAtLossyQuality()
    {
        using Bitmap bitmap = CreatePattern(128, 96);
        using var best = new MemoryStream();
        using var low = new MemoryStream();
        SdrAvifImageCodec.Encode(bitmap, best, 100, 10);
        SdrAvifImageCodec.Encode(bitmap, low, 20, 10);
        Assert.True(low.Length < best.Length, $"low={low.Length}, best={best.Length}");
        using Bitmap decoded = SdrAvifImageCodec.Decode(low);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                Assert.Equal(bitmap.GetPixel(x, y).A, decoded.GetPixel(x, y).A);
    }

    [Fact]
    public void Avif_SdrDecoderRejectsHdrAndUnsupportedColorSignaling()
    {
        using var pixels = new HdrRgba16FloatBuffer(8, 8);
        using var hdr = new MemoryStream();
        new AvifHdrImageEncoder().Encode(pixels, hdr, new HdrImageEncodingOptions { AvifQuality = 100, AvifSpeed = 10 });
        Assert.False(SdrAvifImageCodec.TryProbe(hdr.ToArray(), out _, out _));
        Assert.Throws<InvalidDataException>(() => SdrAvifImageCodec.Decode(hdr));
        using Bitmap bitmap = CreatePattern(8, 8);
        using var sdr = new MemoryStream();
        SdrAvifImageCodec.Encode(bitmap, sdr, 100, 10);
        byte[] bytes = sdr.ToArray();
        int colorBox = FindBox(bytes, "nclx");
        bytes[colorBox + 7] = 18; // Unsupported HLG cannot be silently treated as SDR.
        Assert.False(SdrAvifImageCodec.TryProbe(bytes, out _, out _));
        Assert.Throws<InvalidDataException>(() => SdrAvifImageCodec.Decode(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(EImageFormat.JPEG)]
    [InlineData(EImageFormat.AVIF)]
    public void PrepareImage_SizeFallbackChangesEncodedFormatAndMetadataTogether(EImageFormat fallback)
    {
        using Bitmap bitmap = CreatePattern(128, 96);
        var settings = CreateSettings(EImageFormat.PNG);
        settings.ImageSettings.ImageAutoUseJPEG = true;
        settings.ImageSettings.ImageSizeFallbackFormat = fallback;
        settings.ImageSettings.ImageAutoUseJPEGSize = 1;
        using ImageData artifact = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.Equal(fallback, artifact.ImageFormat);
        Assert.False(artifact.IsHdr);
        Assert.Equal(fallback.GetDescription(), artifact.FileExtension);
        Assert.Equal(MimeTypes.GetMimeTypeFromExtension(artifact.FileExtension), artifact.MediaType);
        if (fallback == EImageFormat.AVIF)
        {
            using Bitmap decoded = SdrAvifImageCodec.Decode(artifact.ImageStream);
            AssertPixelsEqual(bitmap, decoded);
        }
        else
        {
            artifact.ImageStream.Position = 0;
            using var decoded = new Bitmap(artifact.ImageStream);
            Assert.Equal(255, decoded.GetPixel(1, 0).A);
            Assert.Equal(bitmap.Size, decoded.Size);
        }
        settings.ImageSettings.ImageAutoUseJPEGSize = int.MaxValue;
        using ImageData belowLimit = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.Equal(EImageFormat.PNG, belowLimit.ImageFormat);
    }

    [Fact]
    public void PrepareImage_AvifPrimaryKeepsAvifWhenSameFallbackAndPreservesLegacyJpegOptIn()
    {
        using Bitmap bitmap = CreatePattern(64, 48);
        var settings = CreateSettings(EImageFormat.AVIF);
        settings.ImageSettings.ImageAutoUseJPEG = true;
        settings.ImageSettings.ImageAutoUseJPEGSize = 0;
        settings.ImageSettings.ImageSizeFallbackFormat = EImageFormat.AVIF;
        using ImageData avif = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.Equal(EImageFormat.AVIF, avif.ImageFormat);
        settings.ImageSettings.ImageSizeFallbackFormat = EImageFormat.JPEG;
        settings.ImageSettings.ImageAutoJPEGQuality = true;
        settings.ImageSettings.ImageAutoUseJPEGSize = 1;
        using ImageData jpeg = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.Equal(EImageFormat.JPEG, jpeg.ImageFormat);
        Assert.Equal("jpg", jpeg.FileExtension);
        Assert.Equal("image/jpeg", jpeg.MediaType);
    }

    [Theory]
    [InlineData(HdrFileFormat.Avif, "avif")]
    [InlineData(HdrFileFormat.OpenExr, "exr")]
    public void ImageHelpers_KnownHdrFilesLoadAsToneMappedPreviewsAndRemainBytePreservingUploads(HdrFileFormat format, string extension)
    {
        SdrImageCodecs.RegisterImageHelpers();
        using var source = new HdrRgba16FloatBuffer(4, 2);
        for (int y = 0; y < source.Height; y++)
        {
            Span<byte> row = source.GetWritableRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                WriteHalf(row, x * 8, 8f);
                WriteHalf(row, x * 8 + 2, 2f);
                WriteHalf(row, x * 8 + 4, 0.5f);
                WriteHalf(row, x * 8 + 6, 1f);
            }
        }
        using var encoded = new MemoryStream();
        IHdrImageEncoder encoder = format == HdrFileFormat.Avif ? new AvifHdrImageEncoder() : new OpenExrHdrImageEncoder();
        encoder.Encode(source, encoded, new HdrImageEncodingOptions { AvifQuality = 100, AvifSpeed = 10 });
        string path = Path.Combine(Path.GetTempPath(), "ShareX-HdrLoader-" + Guid.NewGuid().ToString("N") + "." + extension);
        try
        {
            File.WriteAllBytes(path, encoded.ToArray());
            using Bitmap loaded = ImageHelpers.LoadImage(path);
            Assert.NotNull(loaded);
            Assert.Equal(new Size(4, 2), loaded.Size);
            Color preview = loaded.GetPixel(0, 0);
            Assert.True(preview.R > preview.G && preview.G > preview.B);
            var settings = new TaskSettings();
            settings.AdvancedSettings.ProcessImagesDuringFileUpload = true;
            using WorkerTask upload = WorkerTask.CreateFileUploaderTask(path, settings);
            Assert.Equal(TaskJob.FileUpload, upload.Info.Job);
            Assert.Null(upload.Image);
            using var actualBytes = new MemoryStream();
            upload.Data.CopyTo(actualBytes);
            Assert.Equal(encoded.ToArray(), actualBytes.ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(EImageFormat.AVIF, "avif")]
    [InlineData(EImageFormat.EXR, "exr")]
    public void WorkerTask_OrdinaryCodecFilesCanUseOptInImageProcessing(EImageFormat format, string extension)
    {
        SdrImageCodecs.RegisterImageHelpers();
        using Bitmap bitmap = CreatePattern(8, 6);
        using ImageData artifact = TaskHelpers.PrepareImage(bitmap, CreateSettings(format));
        string path = Path.Combine(Path.GetTempPath(), "ShareX-SdrUpload-" + Guid.NewGuid().ToString("N") + "." + extension);
        try
        {
            File.WriteAllBytes(path, artifact.ImageStream.ToArray());
            var settings = new TaskSettings();
            settings.AdvancedSettings.ProcessImagesDuringFileUpload = true;
            using WorkerTask upload = WorkerTask.CreateFileUploaderTask(path, settings);
            Assert.Equal(TaskJob.Job, upload.Info.Job);
            Assert.NotNull(upload.Image);
            Assert.Equal(bitmap.Size, upload.Image.Size);
            settings.AdvancedSettings.ProcessImagesDuringFileUpload = false;
            using WorkerTask raw = WorkerTask.CreateFileUploaderTask(path, settings);
            Assert.Equal(TaskJob.FileUpload, raw.Info.Job);
            Assert.Null(raw.Image);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Avif_SdrDecoderRechecksActualBitstreamDepth()
    {
        using var pixels = new HdrRgba16FloatBuffer(8, 8);
        using var hdr = new MemoryStream();
        new AvifHdrImageEncoder().Encode(pixels, hdr, new HdrImageEncodingOptions { AvifQuality = 100, AvifSpeed = 10 });
        byte[] bytes = hdr.ToArray();
        int color = FindBox(bytes, "nclx");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(color + 4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(color + 6, 2), 13);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(color + 8, 2), 0);
        for (int offset = 0; offset < bytes.Length - 12; offset++)
        {
            if (bytes.AsSpan(offset, 4).SequenceEqual("pixi"u8))
            {
                int channels = bytes[offset + 8];
                for (int i = 0; i < channels; i++) bytes[offset + 9 + i] = 8;
            }
            else if (bytes.AsSpan(offset, 4).SequenceEqual("clli"u8)) "skip"u8.CopyTo(bytes.AsSpan(offset, 4));
            else if (bytes.AsSpan(offset, 4).SequenceEqual("av1C"u8)) bytes[offset + 6] &= 0xbf;
        }
        Assert.True(SdrAvifImageCodec.TryProbe(bytes, out _, out _));
        Assert.Throws<InvalidDataException>(() => SdrAvifImageCodec.Decode(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("exr")]
    public void ImageHelpers_SaveAndReloadOurNormalFormatsWithRealSignatures(string extension)
    {
        SdrImageCodecs.RegisterImageHelpers();
        using Bitmap bitmap = CreatePattern(16, 12);
        string path = Path.Combine(Path.GetTempPath(), "ShareX-SdrCodec-" + Guid.NewGuid().ToString("N") + "." + extension);
        try
        {
            Assert.True(ImageHelpers.SaveImage(bitmap, path));
            byte[] bytes = File.ReadAllBytes(path);
            if (extension == "avif") Assert.True(SdrAvifImageCodec.TryProbe(bytes, out _, out _));
            else Assert.Equal(20000630, BinaryPrimitives.ReadInt32LittleEndian(bytes));
            using Bitmap decoded = ImageHelpers.LoadImage(path);
            Assert.NotNull(decoded);
            Assert.Equal(bitmap.Size, decoded.Size);
            Assert.InRange(Math.Abs(bitmap.GetPixel(1, 0).A - decoded.GetPixel(1, 0).A), 0, 1);
            using var adaptor = new ImageHistoryFileAdaptor();
            using Image thumbnail = adaptor.GetThumbnail(path, new Size(8, 8),
                Manina.Windows.Forms.UseEmbeddedThumbnails.Never, false);
            Assert.NotNull(thumbnail);
            Assert.Equal(new Size(8, 6), thumbnail.Size);
            Assert.Equal(path, adaptor.GetSourceImage(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PrepareImage_WithoutSdrAvifExportsUsesConsistentPngAndSkipsUnavailableFallback()
    {
        if (Environment.GetEnvironmentVariable("SHAREX_EXPECT_SDR_AVIF_UNAVAILABLE") != "1") return;
        Assert.False(SdrAvifImageCodec.TryGetAvailability(out string reason));
        Assert.Contains("sharex_avif_encode_bgra8", reason);
        Assert.True(HdrEncoderCapabilities.TryGetAvailability(HdrFileFormat.Avif, out _));
        using Bitmap bitmap = CreatePattern(32, 24);
        var settings = CreateSettings(EImageFormat.AVIF);
        using ImageData artifact = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.Equal(EImageFormat.PNG, artifact.ImageFormat);
        Assert.Equal("png", artifact.FileExtension);
        Assert.Equal("image/png", artifact.MediaType);
        Assert.False(artifact.IsHdr);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, artifact.ImageStream.ToArray().AsSpan(0, 8).ToArray());
        Assert.Throws<NotSupportedException>(() => TaskHelpers.SaveImageAsStream(bitmap, EImageFormat.AVIF, settings));
        settings.ImageSettings.ImageFormat = EImageFormat.JPEG;
        settings.ImageSettings.ImageAutoUseJPEG = true;
        settings.ImageSettings.ImageAutoUseJPEGSize = 0;
        settings.ImageSettings.ImageSizeFallbackFormat = EImageFormat.AVIF;
        using ImageData kept = TaskHelpers.PrepareImage(bitmap, settings);
        Assert.Equal(EImageFormat.JPEG, kept.ImageFormat);
        Assert.Equal("jpg", kept.FileExtension);
        Assert.Equal("image/jpeg", kept.MediaType);
    }

    [Fact]
    public void SdrAvif_OutputPerformanceProbe()
    {
        if (Environment.GetEnvironmentVariable("SHAREX_RUN_SDR_AVIF_OUTPUT_PERFORMANCE_TESTS") != "1") return;
        using Bitmap bitmap = CreatePattern(1280, 720);
        foreach (int speed in new[] { 6, 8 })
        {
            using var warmup = new MemoryStream();
            SdrAvifImageCodec.Encode(bitmap, warmup, 90, speed);
            var elapsed = new List<double>();
            var sizes = new List<long>();
            for (int i = 0; i < 5; i++)
            {
                using var encoded = new MemoryStream();
                Stopwatch timer = Stopwatch.StartNew();
                SdrAvifImageCodec.Encode(bitmap, encoded, 90, speed);
                timer.Stop();
                elapsed.Add(timer.Elapsed.TotalMilliseconds);
                sizes.Add(encoded.Length);
            }
            output.WriteLine($"SDR AVIF synthetic 1280x720 quality=90 speed={speed} n=5 medianMs={elapsed.Order().ElementAt(2):F2} rangeMs={elapsed.Min():F2}..{elapsed.Max():F2} bytes={sizes.Min()}..{sizes.Max()}");
        }
    }

    private static TaskSettings CreateSettings(EImageFormat format)
    {
        var settings = new TaskSettings();
        settings.ImageSettings.ImageFormat = format;
        settings.ImageSettings.ImageAutoUseJPEG = false;
        settings.ImageSettings.ImageAVIFQuality = 100;
        settings.ImageSettings.ImageAVIFSpeed = 10;
        return settings;
    }

    private static Bitmap CreatePattern(int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                result.SetPixel(x, y, x == 0 && y == 0 ? Color.White :
                    Color.FromArgb((x + y) % 4 * 64 + 63, (x * 37 + y * 61) % 256, (x * 13 + y * 47) % 256, (x * 79 + y * 7) % 256));
        return result;
    }

    private static void AssertPixelsEqual(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y).ToArgb(), actual.GetPixel(x, y).ToArgb());
    }

    private static void AssertExrReloadColor(MemoryStream encoded, Color original, Point point)
    {
        SdrImageCodecs.RegisterImageHelpers();
        string path = Path.Combine(Path.GetTempPath(), "ShareX-SdrAlpha-" + Guid.NewGuid().ToString("N") + ".exr");
        try
        {
            File.WriteAllBytes(path, encoded.ToArray());
            using Bitmap loaded = ImageHelpers.LoadImage(path);
            Color actual = loaded.GetPixel(point.X, point.Y);
            Assert.Equal(original.A, actual.A);
            Assert.InRange(Math.Abs(original.R - actual.R), 0, 1);
            Assert.InRange(Math.Abs(original.G - actual.G), 0, 1);
            Assert.InRange(Math.Abs(original.B - actual.B), 0, 1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static int FindBox(byte[] bytes, string marker) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker));
    private static void WriteHalf(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));
    private static float ReadHalf(ReadOnlySpan<byte> bytes, int offset) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
    private static float DecodeSrgb(byte value) => value / 255f <= 0.04045f ? value / 255f / 12.92f : (float)Math.Pow((value / 255f + 0.055f) / 1.055f, 2.4);
}
