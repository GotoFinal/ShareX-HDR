#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.MediaLib;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
    public class ScreenRecorder : IDisposable
    {
        public bool IsRecording { get; private set; }

        public int FPS
        {
            get
            {
                return fps;
            }
            set
            {
                if (!IsRecording)
                {
                    fps = value;
                    UpdateInfo();
                }
            }
        }

        public float DurationSeconds
        {
            get
            {
                return durationSeconds;
            }
            set
            {
                if (!IsRecording)
                {
                    durationSeconds = value;
                    UpdateInfo();
                }
            }
        }

        public Rectangle CaptureRectangle
        {
            get
            {
                return captureRectangle;
            }
            private set
            {
                if (!IsRecording)
                {
                    captureRectangle = value;
                }
            }
        }

        public string CachePath { get; private set; }

        public ScreenRecordOutput OutputType { get; private set; }

        public ScreenRecordingOptions Options { get; set; }

        public event Action RecordingStarted;

        public delegate void ProgressEventHandler(int progress);
        public event ProgressEventHandler EncodingProgressChanged;

        private int fps, delay, frameCount, previousProgress;
        private float durationSeconds;
        private Screenshot screenshot;
        private Rectangle captureRectangle;
        private ImageCache imgCache;
        private FFmpegCLIManager ffmpeg;
        private volatile bool stopRequested;
        private ShareXFrameInputMode frameInputMode;
        private int recordingStartedRaised;
        private static readonly ConcurrentDictionary<string, bool> hdrInputMetadataSupport =
            new(StringComparer.OrdinalIgnoreCase);

        public ScreenRecorder(ScreenRecordOutput outputType, ScreenRecordingOptions options, Screenshot screenshot, Rectangle captureRectangle)
        {
            if (string.IsNullOrEmpty(options.OutputPath))
            {
                throw new Exception("Screen recorder cache path is empty.");
            }

            FPS = options.FPS;
            DurationSeconds = options.Duration;
            CaptureRectangle = captureRectangle;
            CachePath = options.OutputPath;
            OutputType = outputType;

            Options = options;

            switch (OutputType)
            {
                default:
                case ScreenRecordOutput.FFmpeg:
                    FileHelpers.CreateDirectoryFromFilePath(Options.OutputPath);
                    ffmpeg = new FFmpegCLIManager(Options.FFmpeg.FFmpegPath);
                    ffmpeg.ShowError = true;
                    ffmpeg.EncodeStarted += OnRecordingStarted;
                    ffmpeg.EncodeProgressChanged += OnEncodingProgressChanged;
                    break;
                case ScreenRecordOutput.GIF:
                    imgCache = new HardDiskCache(Options);
                    break;
            }

            this.screenshot = screenshot;
        }

        private void UpdateInfo()
        {
            delay = 1000 / fps;
            frameCount = (int)(fps * durationSeconds);
        }

        public void StartRecording()
        {
            if (!IsRecording)
            {
                IsRecording = true;
                stopRequested = false;
                recordingStartedRaised = 0;

                try
                {
                    if (OutputType == ScreenRecordOutput.FFmpeg)
                    {
                        frameInputMode = ResolveShareXFrameInputMode();

                        if (frameInputMode != ShareXFrameInputMode.None)
                        {
                            bool includeHdrStaticMetadata =
                                frameInputMode != ShareXFrameInputMode.Hdr10P010 ||
                                SupportsFfmpegHdrInputMetadata(Options.FFmpeg.FFmpegPath);
                            if (frameInputMode == ShareXFrameInputMode.Hdr10P010)
                            {
                                DebugHelper.WriteLine(
                                    $"HDR10 FFmpeg metadata | staticSei={includeHdrStaticMetadata}");
                                if (!includeHdrStaticMetadata)
                                {
                                    DebugHelper.WriteLine(
                                        "HDR10 FFmpeg metadata fallback | static mastering-display and " +
                                        "content-light input options are unavailable; retaining " +
                                        "10-bit BT.2020/PQ signaling without static HDR SEI");
                                }
                            }

                            string commands = Options.GetFFmpegCommands(
                                frameInputMode,
                                includeHdrStaticMetadata);
                            RunWithFrameInput(
                                commands,
                                frameInputMode == ShareXFrameInputMode.Hdr10P010
                                    ? RecordUsingHdr10FrameInput
                                    : RecordUsingToneMappedFrameInput);
                        }
                        else
                        {
                            ffmpeg.Run(Options.GetFFmpegCommands());
                        }
                    }
                    else
                    {
                        OnRecordingStarted();
                        RecordUsingCache();
                    }
                }
                finally
                {
                    IsRecording = false;
                }
            }
        }

        private static bool SupportsFfmpegHdrInputMetadata(string ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(ffmpegPath);
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                var file = new FileInfo(fullPath);
                string cacheKey = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{fullPath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                return hdrInputMetadataSupport.GetOrAdd(
                    cacheKey,
                    _ => ProbeFfmpegHdrInputMetadata(fullPath));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ProbeFfmpegHdrInputMetadata(string ffmpegPath)
        {
            if (!File.Exists(ffmpegPath))
            {
                return false;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.StartInfo.ArgumentList.Add("-hide_banner");
                process.StartInfo.ArgumentList.Add("-loglevel");
                process.StartInfo.ArgumentList.Add("error");
                process.StartInfo.ArgumentList.Add("-mastering_display");
                process.StartInfo.ArgumentList.Add(
                    "G(8500,39850)B(6550,2300)R(35400,14600)" +
                    "WP(15635,16450)L(10000000,5)");
                process.StartInfo.ArgumentList.Add("-content_light");
                process.StartInfo.ArgumentList.Add("1000,400");
                process.StartInfo.ArgumentList.Add("-f");
                process.StartInfo.ArgumentList.Add("lavfi");
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add("color=size=16x16");
                process.StartInfo.ArgumentList.Add("-frames:v");
                process.StartInfo.ArgumentList.Add("1");
                process.StartInfo.ArgumentList.Add("-f");
                process.StartInfo.ArgumentList.Add("null");
                process.StartInfo.ArgumentList.Add("NUL");

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                DebugHelper.WriteLine(
                    $"HDR10 FFmpeg metadata capability probe failed: {ex.Message}");
                return false;
            }
        }

        private void RunWithFrameInput(string commands, Action<Stream> frameWriter)
        {
            bool showError = ffmpeg.ShowError;
            ffmpeg.ShowError = false;

            try
            {
                bool succeeded;
                try
                {
                    succeeded = ffmpeg.RunWithStandardInput(commands, frameWriter);
                }
                catch (Exception ex) when (!stopRequested &&
                    (ex is IOException || ex is ObjectDisposedException))
                {
                    throw CreateFrameInputException(ex);
                }

                if (!succeeded && !stopRequested)
                {
                    throw CreateFrameInputException();
                }
            }
            finally
            {
                ffmpeg.ShowError = showError;
            }
        }

        private IOException CreateFrameInputException(Exception innerException = null)
        {
            string output = ffmpeg?.Output?.ToString()?.Trim();
            string message = "FFmpeg stopped accepting video frames.";

            if (!string.IsNullOrEmpty(output))
            {
                DebugHelper.WriteLine("FFmpeg frame-input failure output:\r\n" + output);
                string[] lines = output.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                int firstLine = Math.Max(0, lines.Length - 12);
                string summary = string.Join(Environment.NewLine, lines, firstLine, lines.Length - firstLine);
                message += Environment.NewLine + Environment.NewLine + summary;
            }

            return new IOException(message, innerException);
        }

        private ShareXFrameInputMode ResolveShareXFrameInputMode()
        {
            bool shouldCheckHdrRegion =
                Options.HdrMode != ScreenRecordingHdrMode.Disabled &&
                screenshot.UseHDRSupport &&
                Options.SupportsShareXFrameInput;
            bool hdrCaptureRequired = shouldCheckHdrRegion &&
                screenshot.ShouldUseHdrFrameCapture(CaptureRectangle);
            ShareXFrameInputMode resolvedMode = Options.ResolveShareXFrameInputMode(
                screenshot.UseHDRSupport,
                hdrCaptureRequired,
                out string fallbackReason);

            if (!string.IsNullOrEmpty(fallbackReason))
            {
                DebugHelper.WriteLine(
                    $"HDR video capture fallback | requested={Options.HdrMode} " +
                    $"effective={resolvedMode} reason={fallbackReason}");
            }

            DebugHelper.WriteLine(
                $"HDR video capture | mode={Options.HdrMode} frameInput={resolvedMode} " +
                $"bounds={CaptureRectangle} fps={FPS} source={Options.FFmpeg.VideoSource} " +
                $"encoder={Options.GetEffectiveVideoCodec(resolvedMode)}");
            return resolvedMode;
        }

        private void RecordUsingToneMappedFrameInput(Stream input)
        {
            int packedStride = checked(CaptureRectangle.Width * 4);
            byte[] pixels = new byte[checked(packedStride * CaptureRectangle.Height)];
            int capturedFrames = 0;
            int writtenFrames = 0;
            Stopwatch recordingTimer = Stopwatch.StartNew();

            OnRecordingStarted();

            try
            {
                using Screenshot.CaptureSession captureSession = screenshot.CreateCaptureSession();

                while (!stopRequested && (frameCount == 0 || writtenFrames < frameCount))
                {
                    WaitForNextFrame(recordingTimer, writtenFrames);

                    if (stopRequested)
                    {
                        break;
                    }

                    using Bitmap frame = captureSession.CaptureRectangle(CaptureRectangle);
                    CopyFramePixels(frame, pixels, packedStride, CaptureRectangle.Size);
                    capturedFrames++;

                    int desiredFrameCount = GetDesiredFrameCount(recordingTimer.ElapsedTicks, writtenFrames);
                    if (frameCount > 0)
                    {
                        desiredFrameCount = Math.Min(desiredFrameCount, frameCount);
                    }

                    do
                    {
                        input.Write(pixels, 0, pixels.Length);
                        writtenFrames++;
                    }
                    while (!stopRequested && writtenFrames < desiredFrameCount);
                }
            }
            catch (IOException) when (stopRequested)
            {
            }
            catch (ObjectDisposedException) when (stopRequested)
            {
            }
            finally
            {
                recordingTimer.Stop();
                DebugHelper.WriteLine(
                    $"HDR video capture complete | capturedFrames={capturedFrames} writtenFrames={writtenFrames} " +
                    $"elapsedMs={recordingTimer.Elapsed.TotalMilliseconds:F1} effectiveCaptureFps=" +
                    $"{(recordingTimer.Elapsed.TotalSeconds > 0 ? capturedFrames / recordingTimer.Elapsed.TotalSeconds : 0):F1}");
            }
        }

        private void RecordUsingHdr10FrameInput(Stream input)
        {
            float masteringMaximumNits = Math.Clamp(
                Options.HdrMasteringMaximumNits,
                HdrFileOutputSettings.MinimumMasteringDisplayNits,
                HdrFileOutputSettings.MaximumMasteringDisplayNits);
            var converter = new HdrP010FrameConverter(
                CaptureRectangle.Width,
                CaptureRectangle.Height,
                masteringMaximumNits);
            byte[] pixels = new byte[converter.FrameByteCount];
            byte[] sdrPixels = null;
            int sdrPackedStride = checked(CaptureRectangle.Width * 4);
            int capturedFrames = 0;
            int writtenFrames = 0;
            int sdrFallbackFrames = 0;
            bool usingSdrFallback = false;
            float measuredMaxCll = 0f;
            float measuredMaxFall = 0f;
            double conversionMilliseconds = 0d;
            Stopwatch recordingTimer = Stopwatch.StartNew();

            OnRecordingStarted();

            try
            {
                using Screenshot.CaptureSession captureSession = screenshot.CreateCaptureSession();

                while (!stopRequested && (frameCount == 0 || writtenFrames < frameCount))
                {
                    WaitForNextFrame(recordingTimer, writtenFrames);

                    if (stopRequested)
                    {
                        break;
                    }

                    HdrVideoFrameLightLevels lightLevels;
                    Stopwatch conversionTimer = Stopwatch.StartNew();

                    if (captureSession.TryCaptureHdr(CaptureRectangle, out HdrImageDocument document))
                    {
                        if (usingSdrFallback)
                        {
                            DebugHelper.WriteLine("HDR10 video capture recovered native HDR frames.");
                            usingSdrFallback = false;
                        }

                        using (document)
                        {
                            lightLevels = converter.Convert(document.MasterPixels, pixels);
                        }
                    }
                    else
                    {
                        if (!usingSdrFallback)
                        {
                            DebugHelper.WriteLine(
                                "HDR10 video capture fallback | native HDR frame unavailable; " +
                                "embedding the SDR capture in the HDR10 stream");
                            usingSdrFallback = true;
                        }

                        sdrPixels ??= new byte[checked(sdrPackedStride * CaptureRectangle.Height)];
                        using Bitmap sdrFrame = captureSession.CaptureRectangle(CaptureRectangle);
                        CopyFramePixels(sdrFrame, sdrPixels, sdrPackedStride, CaptureRectangle.Size);
                        lightLevels = converter.ConvertSdrBgra(
                            sdrPixels,
                            sdrPackedStride,
                            pixels);
                        sdrFallbackFrames++;
                    }

                    conversionTimer.Stop();
                    conversionMilliseconds += conversionTimer.Elapsed.TotalMilliseconds;
                    measuredMaxCll = Math.Max(measuredMaxCll, lightLevels.MaxCll);
                    measuredMaxFall = Math.Max(measuredMaxFall, lightLevels.MaxFall);
                    capturedFrames++;

                    int desiredFrameCount = GetDesiredFrameCount(recordingTimer.ElapsedTicks, writtenFrames);
                    if (frameCount > 0)
                    {
                        desiredFrameCount = Math.Min(desiredFrameCount, frameCount);
                    }

                    do
                    {
                        input.Write(pixels, 0, pixels.Length);
                        writtenFrames++;
                    }
                    while (!stopRequested && writtenFrames < desiredFrameCount);
                }
            }
            catch (IOException) when (stopRequested)
            {
            }
            catch (ObjectDisposedException) when (stopRequested)
            {
            }
            finally
            {
                recordingTimer.Stop();
                DebugHelper.WriteLine(
                    $"HDR10 video capture complete | capturedFrames={capturedFrames} writtenFrames={writtenFrames} " +
                    $"elapsedMs={recordingTimer.Elapsed.TotalMilliseconds:F1} effectiveCaptureFps=" +
                    $"{(recordingTimer.Elapsed.TotalSeconds > 0 ? capturedFrames / recordingTimer.Elapsed.TotalSeconds : 0):F1} " +
                    $"averageP010Ms={(capturedFrames > 0 ? conversionMilliseconds / capturedFrames : 0):F1} " +
                    $"sdrFallbackFrames={sdrFallbackFrames} " +
                    $"measuredMaxCll={measuredMaxCll:F1}nits measuredMaxFall={measuredMaxFall:F1}nits " +
                    $"masteringPeak={masteringMaximumNits:F1}nits");
            }
        }

        private int GetDesiredFrameCount(long elapsedTicks, int writtenFrames)
        {
            long desired = (elapsedTicks * FPS + Stopwatch.Frequency - 1) / Stopwatch.Frequency;
            desired = Math.Max(desired, (long)writtenFrames + 1);
            return (int)Math.Min(desired, int.MaxValue);
        }

        private void WaitForNextFrame(Stopwatch recordingTimer, int writtenFrames)
        {
            long targetTicks = writtenFrames * Stopwatch.Frequency / FPS;

            while (!stopRequested)
            {
                long remainingTicks = targetTicks - recordingTimer.ElapsedTicks;
                if (remainingTicks <= 0)
                {
                    return;
                }

                int sleepMilliseconds = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                if (sleepMilliseconds > 1)
                {
                    Thread.Sleep(sleepMilliseconds - 1);
                }
                else
                {
                    Thread.Yield();
                }
            }
        }

        internal static void CopyFramePixels(Bitmap frame, byte[] destination, int packedStride, Size expectedSize)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(destination);

            if (frame.Size != expectedSize)
            {
                throw new InvalidDataException(
                    $"Captured frame size {frame.Width}x{frame.Height} did not match " +
                    $"the FFmpeg input size {expectedSize.Width}x{expectedSize.Height}.");
            }

            int requiredLength = checked(packedStride * expectedSize.Height);
            if (destination.Length < requiredLength)
            {
                throw new ArgumentException("The destination frame buffer is too small.", nameof(destination));
            }

            using Bitmap converted = frame.PixelFormat == PixelFormat.Format32bppArgb
                ? null
                : frame.Clone(new Rectangle(Point.Empty, frame.Size), PixelFormat.Format32bppArgb);
            Bitmap source = converted ?? frame;
            BitmapData bitmapData = source.LockBits(
                new Rectangle(Point.Empty, source.Size),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                if (bitmapData.Stride == packedStride)
                {
                    Marshal.Copy(bitmapData.Scan0, destination, 0, requiredLength);
                }
                else
                {
                    for (int y = 0; y < expectedSize.Height; y++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride),
                            destination,
                            y * packedStride,
                            packedStride);
                    }
                }
            }
            finally
            {
                source.UnlockBits(bitmapData);
            }
        }

        private void RecordUsingCache()
        {
            try
            {
                using Screenshot.CaptureSession captureSession = screenshot.CreateCaptureSession();

                for (int i = 0; !stopRequested && (frameCount == 0 || i < frameCount); i++)
                {
                    Stopwatch timer = Stopwatch.StartNew();

                    Image img = captureSession.CaptureRectangle(CaptureRectangle);
                    //DebugHelper.WriteLine("Screen capture: " + (int)timer.ElapsedMilliseconds);

                    imgCache.AddImageAsync(img);

                    if (!stopRequested && (frameCount == 0 || i + 1 < frameCount))
                    {
                        int sleepTime = delay - (int)timer.ElapsedMilliseconds;

                        if (sleepTime > 0)
                        {
                            Thread.Sleep(sleepTime);
                        }
                        else if (sleepTime < 0)
                        {
                            // Need to handle FPS drops
                        }
                    }
                }
            }
            finally
            {
                imgCache.Finish();
            }
        }

        public void StopRecording()
        {
            stopRequested = true;

            if (ffmpeg != null && frameInputMode == ShareXFrameInputMode.None)
            {
                ffmpeg.Close();
            }

            // For ShareX frame input, the producer loop observes stopRequested
            // and returns. ExternalCLIManager then closes stdin on its worker
            // thread so the UI never blocks flushing a full or broken pipe.
        }

        public void SaveAsGIF(string path, GIFQuality quality)
        {
            if (imgCache != null && imgCache is HardDiskCache && !IsRecording)
            {
                FileHelpers.CreateDirectoryFromFilePath(path);

                HardDiskCache hdCache = imgCache as HardDiskCache;

                using (AnimatedGifCreator gifEncoder = new AnimatedGifCreator(path, delay))
                {
                    int i = 0;
                    int count = hdCache.Count;

                    foreach (Image img in hdCache.GetImageEnumerator())
                    {
                        i++;
                        OnEncodingProgressChanged((int)((float)i / count * 100));

                        using (img)
                        {
                            gifEncoder.AddFrame(img, quality);
                        }
                    }
                }
            }
        }

        public bool FFmpegEncodeVideo(string input, string output)
        {
            FileHelpers.CreateDirectoryFromFilePath(output);

            Options.IsRecording = false;
            Options.IsLossless = false;
            Options.InputPath = input;
            Options.OutputPath = output;

            try
            {
                ffmpeg.TrackEncodeProgress = true;

                return ffmpeg.Run(Options.GetFFmpegCommands());
            }
            finally
            {
                ffmpeg.TrackEncodeProgress = false;
            }
        }

        public bool FFmpegEncodeAsGIF(string input, string output)
        {
            FileHelpers.CreateDirectoryFromFilePath(output);

            try
            {
                ffmpeg.TrackEncodeProgress = true;

                StringBuilder args = new StringBuilder();

                args.Append($"-i \"{input}\" ");

                // https://ffmpeg.org/ffmpeg-filters.html#palettegen-1
                args.Append($"-lavfi \"palettegen=stats_mode={Options.FFmpeg.GIFStatsMode}[palette],");

                // https://ffmpeg.org/ffmpeg-filters.html#paletteuse
                args.Append($"[0:v][palette]paletteuse=dither={Options.FFmpeg.GIFDither}");

                if (Options.FFmpeg.GIFDither == FFmpegPaletteUseDither.bayer)
                {
                    args.Append($":bayer_scale={Options.FFmpeg.GIFBayerScale}");
                }

                if (Options.FFmpeg.GIFStatsMode == FFmpegPaletteGenStatsMode.single)
                {
                    args.Append(":new=1");
                }

                args.Append("\" ");
                args.Append("-y ");
                args.Append($"\"{output}\"");

                return ffmpeg.Run(args.ToString());
            }
            finally
            {
                ffmpeg.TrackEncodeProgress = false;
            }
        }

        protected void OnRecordingStarted()
        {
            if (Interlocked.Exchange(ref recordingStartedRaised, 1) == 0)
            {
                RecordingStarted?.Invoke();
            }
        }

        protected void OnEncodingProgressChanged(float progress)
        {
            int currentProgress = (int)progress;

            if (EncodingProgressChanged != null && currentProgress != previousProgress)
            {
                EncodingProgressChanged(currentProgress);
                previousProgress = currentProgress;
            }
        }

        public void Dispose()
        {
            if (ffmpeg != null)
            {
                ffmpeg.Dispose();
            }

            if (imgCache != null)
            {
                imgCache.Dispose();
            }
        }
    }
}
