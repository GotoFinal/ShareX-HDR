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

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ShareX.HelpersLib
{
    public sealed class HdrClipboardFallbackData : IDisposable
    {
        internal Image BitmapFallback { get; }
        internal byte[] DibBytes { get; }
        internal ArraySegment<byte> PngBytes { get; }
        internal bool OwnsBitmapFallback { get; }

        public bool ReusedPngBytes { get; }
        public int DibLength => DibBytes?.Length ?? 0;
        public int PngLength => PngBytes.Count;
        public double FillMilliseconds { get; }
        public double DibMilliseconds { get; }
        public double PngMilliseconds { get; }
        public double TotalMilliseconds { get; }

        internal HdrClipboardFallbackData(
            Image bitmapFallback,
            bool ownsBitmapFallback,
            byte[] dibBytes,
            ArraySegment<byte> pngBytes,
            bool reusedPngBytes,
            double fillMilliseconds,
            double dibMilliseconds,
            double pngMilliseconds,
            double totalMilliseconds)
        {
            BitmapFallback = bitmapFallback;
            OwnsBitmapFallback = ownsBitmapFallback;
            DibBytes = dibBytes;
            PngBytes = pngBytes;
            ReusedPngBytes = reusedPngBytes;
            FillMilliseconds = fillMilliseconds;
            DibMilliseconds = dibMilliseconds;
            PngMilliseconds = pngMilliseconds;
            TotalMilliseconds = totalMilliseconds;
        }

        internal MemoryStream CreateDibStream() => new MemoryStream(DibBytes, writable: false);

        internal MemoryStream CreatePngStream() => new MemoryStream(
            PngBytes.Array,
            PngBytes.Offset,
            PngBytes.Count,
            writable: false,
            publiclyVisible: true);

        public void Dispose()
        {
            if (OwnsBitmapFallback)
            {
                BitmapFallback?.Dispose();
            }
        }
    }

    public static class ClipboardHelpers
    {
        public const string FORMAT_PNG = "PNG";
        public const string FORMAT_17 = "Format17";
        public const string FORMAT_SHAREX_HDR_IMAGE = "ShareX HDR Image";
        public const string FORMAT_SHAREX_HDR_FILE_EXTENSION = "ShareX HDR File Extension";

        private const int RetryTimes = 20;
        private const int RetryDelay = 100;

        private static readonly object ClipboardLock = new object();

        private static bool CopyData(IDataObject data, bool copy = true)
        {
            if (data != null)
            {
                lock (ClipboardLock)
                {
                    Clipboard.SetDataObject(data, copy, RetryTimes, RetryDelay);
                }

                return true;
            }

            return false;
        }

        public static bool Clear()
        {
            try
            {
                IDataObject data = new DataObject();
                CopyData(data, false);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Clipboard clear failed.");
            }

            return false;
        }

        public static bool CopyText(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    IDataObject data = new DataObject();
                    string dataFormat;

                    if (Environment.OSVersion.Platform != PlatformID.Win32NT || Environment.OSVersion.Version.Major < 5)
                    {
                        dataFormat = DataFormats.Text;
                    }
                    else
                    {
                        dataFormat = DataFormats.UnicodeText;
                    }

                    data.SetData(dataFormat, false, text);
                    return CopyData(data);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy text failed.");
                }
            }

            return false;
        }

        public static bool CopyImage(Image img, string fileName = null)
        {
            if (img != null)
            {
                try
                {
                    if (HelpersOptions.UseAlternativeClipboardCopyImage)
                    {
                        return CopyImageAlternative2(img, fileName);
                    }

                    if (HelpersOptions.DefaultCopyImageFillBackground)
                    {
                        return CopyImageDefaultFillBackground(img, Color.White);
                    }

                    return CopyImageDefault(img);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy image failed.");
                }
            }

            return false;
        }

        /// <summary>
        /// Publishes an encoded HDR image without decoding it. HDR-only mode
        /// exposes the conventional registered format name. Combined mode
        /// reserves conventional bitmap/PNG formats for an SDR compatibility
        /// rendition and exposes HDR bytes under explicit MIME/ShareX formats.
        /// Ultra HDR JPEG can remain conventional because its base JPEG is SDR.
        /// </summary>
        public static bool CopyHdrImage(
            Stream hdrImage,
            string encodedClipboardFormat,
            string mediaType,
            string fileExtension,
            Image sdrFallback = null,
            string fileName = null,
            bool encodedImageContainsSdrFallback = false)
        {
            try
            {
                using HdrClipboardFallbackData preparedFallback =
                    PrepareHdrClipboardFallback(sdrFallback);
                return CopyPreparedHdrImage(
                    hdrImage,
                    encodedClipboardFormat,
                    mediaType,
                    fileExtension,
                    preparedFallback,
                    fileName,
                    encodedImageContainsSdrFallback);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR image clipboard copy failed.");
                return false;
            }
        }

        public static HdrClipboardFallbackData PrepareHdrClipboardFallback(
            Image sdrFallback,
            Stream reusableSdrPng = null)
        {
            if (sdrFallback == null)
            {
                return null;
            }

            Stopwatch totalTimer = Stopwatch.StartNew();
            Image bitmapFallback = sdrFallback;
            bool ownsBitmapFallback = false;

            try
            {
                Stopwatch fillTimer = Stopwatch.StartNew();
                if (HelpersOptions.DefaultCopyImageFillBackground)
                {
                    Bitmap opaqueFallback = sdrFallback.CreateEmptyBitmap(PixelFormat.Format24bppRgb);
                    using Graphics graphics = Graphics.FromImage(opaqueFallback);
                    graphics.Clear(Color.White);
                    graphics.DrawImage(sdrFallback, 0, 0, sdrFallback.Width, sdrFallback.Height);
                    bitmapFallback = opaqueFallback;
                    ownsBitmapFallback = true;
                }
                fillTimer.Stop();

                Stopwatch dibTimer = Stopwatch.StartNew();
                byte[] dibBytes = ClipboardHelpersEx.ConvertToDib(bitmapFallback);
                dibTimer.Stop();

                Stopwatch pngTimer = Stopwatch.StartNew();
                bool reusedPngBytes = TryGetStreamBytes(reusableSdrPng, out ArraySegment<byte> pngBytes);
                if (!reusedPngBytes)
                {
                    using var pngStream = new MemoryStream();
                    bitmapFallback.Save(pngStream, ImageFormat.Png);
                    if (!pngStream.TryGetBuffer(out pngBytes))
                    {
                        byte[] pngCopy = pngStream.ToArray();
                        pngBytes = new ArraySegment<byte>(pngCopy);
                    }
                    else
                    {
                        pngBytes = new ArraySegment<byte>(
                            pngBytes.Array,
                            pngBytes.Offset,
                            checked((int)pngStream.Length));
                    }
                }
                pngTimer.Stop();
                totalTimer.Stop();

                DebugHelper.WriteLine(
                    $"HDR clipboard fallback preparation | size={sdrFallback.Width}x{sdrFallback.Height} " +
                    $"pngReused={reusedPngBytes} fillMs={fillTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"dibMs={dibTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"pngMs={pngTimer.Elapsed.TotalMilliseconds:F1} " +
                    $"totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                return new HdrClipboardFallbackData(
                    bitmapFallback,
                    ownsBitmapFallback,
                    dibBytes,
                    pngBytes,
                    reusedPngBytes,
                    fillTimer.Elapsed.TotalMilliseconds,
                    dibTimer.Elapsed.TotalMilliseconds,
                    pngTimer.Elapsed.TotalMilliseconds,
                    totalTimer.Elapsed.TotalMilliseconds);
            }
            catch
            {
                if (ownsBitmapFallback)
                {
                    bitmapFallback?.Dispose();
                }

                throw;
            }
        }

        public static bool CopyPreparedHdrImage(
            Stream hdrImage,
            string encodedClipboardFormat,
            string mediaType,
            string fileExtension,
            HdrClipboardFallbackData sdrFallback = null,
            string fileName = null,
            bool encodedImageContainsSdrFallback = false)
        {
            if (hdrImage == null || !hdrImage.CanRead ||
                string.IsNullOrWhiteSpace(encodedClipboardFormat))
            {
                return false;
            }

            try
            {
                Stopwatch totalTimer = Stopwatch.StartNew();
                Stopwatch encodedCopyTimer = Stopwatch.StartNew();
                byte[] encodedBytes;
                long originalPosition = hdrImage.CanSeek ? hdrImage.Position : 0;

                try
                {
                    if (hdrImage.CanSeek)
                    {
                        hdrImage.Position = 0;
                    }

                    using var copy = new MemoryStream();
                    hdrImage.CopyTo(copy);
                    encodedBytes = copy.ToArray();
                }
                finally
                {
                    if (hdrImage.CanSeek)
                    {
                        hdrImage.Position = originalPosition;
                    }
                }

                if (encodedBytes.Length == 0)
                {
                    return false;
                }
                encodedCopyTimer.Stop();

                IDataObject dataObject = new DataObject();
                MemoryStream dibStream = null;
                MemoryStream sdrPngStream = null;
                var encodedStreams = new System.Collections.Generic.List<MemoryStream>();

                try
                {
                    // Ultra HDR JPEG has a normal SDR JPEG base, so applications
                    // that only understand JPEG can still consume these bytes.
                    if (sdrFallback == null || encodedImageContainsSdrFallback)
                    {
                        AddEncodedFormat(encodedClipboardFormat);
                    }

                    if (sdrFallback != null)
                    {
                        dataObject.SetData(DataFormats.Bitmap, true, sdrFallback.BitmapFallback);
                        dibStream = sdrFallback.CreateDibStream();
                        dataObject.SetData(DataFormats.Dib, false, dibStream);

                        // Many applications prefer the registered PNG format to
                        // Bitmap/DIB. Give it SDR bytes in compatibility mode so
                        // recognizing PNG cannot lead to a failed HDR decode.
                        sdrPngStream = sdrFallback.CreatePngStream();
                        dataObject.SetData(FORMAT_PNG, false, sdrPngStream);

                        if (HelpersOptions.UseAlternativeClipboardCopyImage &&
                            !string.IsNullOrEmpty(fileName))
                        {
                            string htmlFragment = GenerateHTMLFragment($"<img src=\"{fileName}\"/>");
                            dataObject.SetData(DataFormats.Html, htmlFragment);
                        }
                    }

                    // Keep the selected format discoverable after the standard
                    // SDR fallbacks. PNG is the one exception because the same
                    // registered name already carries SDR PNG in combined mode;
                    // its HDR bytes remain available through image/png and the
                    // explicit ShareX HDR format below.
                    if (!(sdrFallback != null &&
                        string.Equals(encodedClipboardFormat, FORMAT_PNG, StringComparison.OrdinalIgnoreCase)))
                    {
                        AddEncodedFormat(encodedClipboardFormat);
                    }

                    AddEncodedFormat(FORMAT_SHAREX_HDR_IMAGE);
                    if (!string.IsNullOrWhiteSpace(mediaType) &&
                        !string.Equals(mediaType, encodedClipboardFormat, StringComparison.OrdinalIgnoreCase))
                    {
                        AddEncodedFormat(mediaType);
                    }

                    var extensionStream = new MemoryStream(
                        Encoding.UTF8.GetBytes((fileExtension ?? string.Empty).TrimStart('.')),
                        writable: false);
                    encodedStreams.Add(extensionStream);
                    dataObject.SetData(FORMAT_SHAREX_HDR_FILE_EXTENSION, false, extensionStream);

                    Stopwatch publishTimer = Stopwatch.StartNew();
                    bool copied = CopyData(dataObject);
                    publishTimer.Stop();
                    totalTimer.Stop();
                    DebugHelper.WriteLine(
                        $"HDR clipboard stages | encodedBytes={encodedBytes.Length} " +
                        $"sdrFallback={sdrFallback != null} " +
                        $"encodedCopyMs={encodedCopyTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"fallbackPreparedMs={(sdrFallback?.TotalMilliseconds ?? 0d):F1} " +
                        $"sdrPngReused={sdrFallback?.ReusedPngBytes ?? false} " +
                        $"publishMs={publishTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"totalMs={totalTimer.Elapsed.TotalMilliseconds:F1}");
                    return copied;

                    void AddEncodedFormat(string format)
                    {
                        if (string.IsNullOrWhiteSpace(format) || dataObject.GetDataPresent(format, false))
                        {
                            return;
                        }

                        var stream = new MemoryStream(encodedBytes, writable: false);
                        encodedStreams.Add(stream);
                        dataObject.SetData(format, false, stream);
                    }
                }
                finally
                {
                    foreach (MemoryStream encodedStream in encodedStreams)
                    {
                        encodedStream.Dispose();
                    }

                    sdrPngStream?.Dispose();
                    dibStream?.Dispose();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR image clipboard copy failed.");
            }

            return false;
        }

        private static bool TryGetStreamBytes(Stream source, out ArraySegment<byte> bytes)
        {
            bytes = default;
            if (source == null || !source.CanRead)
            {
                return false;
            }

            if (source is MemoryStream memoryStream && memoryStream.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                if (memoryStream.Length <= 0 || memoryStream.Length > int.MaxValue)
                {
                    return false;
                }

                bytes = new ArraySegment<byte>(
                    buffer.Array,
                    buffer.Offset,
                    checked((int)memoryStream.Length));
                return true;
            }

            long originalPosition = source.CanSeek ? source.Position : 0;
            try
            {
                if (source.CanSeek)
                {
                    source.Position = 0;
                }

                using var copy = new MemoryStream();
                source.CopyTo(copy);
                byte[] copiedBytes = copy.ToArray();
                bytes = new ArraySegment<byte>(copiedBytes);
                return copiedBytes.Length > 0;
            }
            finally
            {
                if (source.CanSeek)
                {
                    source.Position = originalPosition;
                }
            }
        }

        private static bool CopyImageDefault(Image img)
        {
            IDataObject dataObject = new DataObject();
            dataObject.SetData(DataFormats.Bitmap, true, img);

            return CopyData(dataObject);
        }

        private static bool CopyImageDefaultFillBackground(Image img, Color background)
        {
            using (Bitmap bmp = img.CreateEmptyBitmap(PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(background);
                g.DrawImage(img, 0, 0, img.Width, img.Height);

                IDataObject dataObject = new DataObject();
                dataObject.SetData(DataFormats.Bitmap, true, bmp);

                return CopyData(dataObject);
            }
        }

        private static bool CopyImageAlternative(Image img)
        {
            using (MemoryStream msPNG = new MemoryStream())
            using (MemoryStream msBMP = new MemoryStream())
            using (MemoryStream msDIB = new MemoryStream())
            {
                IDataObject dataObject = new DataObject();

                img.Save(msPNG, ImageFormat.Png);
                dataObject.SetData(FORMAT_PNG, false, msPNG);

                img.Save(msBMP, ImageFormat.Bmp);
                msBMP.CopyStreamTo(msDIB, 14, (int)msBMP.Length - 14);
                dataObject.SetData(DataFormats.Dib, true, msDIB);

                return CopyData(dataObject);
            }
        }

        private static bool CopyImageAlternative2(Image img, string fileName = null)
        {
            using (Bitmap bmpNonTransparent = img.CreateEmptyBitmap(PixelFormat.Format24bppRgb))
            using (MemoryStream msPNG = new MemoryStream())
            using (MemoryStream msDIB = new MemoryStream())
            {
                IDataObject dataObject = new DataObject();

                using (Graphics g = Graphics.FromImage(bmpNonTransparent))
                {
                    g.Clear(Color.White);
                    g.DrawImage(img, 0, 0, img.Width, img.Height);
                }

                dataObject.SetData(DataFormats.Bitmap, true, bmpNonTransparent);

                img.Save(msPNG, ImageFormat.Png);
                dataObject.SetData(FORMAT_PNG, false, msPNG);

                byte[] dibData = ClipboardHelpersEx.ConvertToDib(img);
                msDIB.Write(dibData, 0, dibData.Length);
                dataObject.SetData(DataFormats.Dib, false, msDIB);

                if (!string.IsNullOrEmpty(fileName))
                {
                    string htmlFragment = GenerateHTMLFragment($"<img src=\"{fileName}\"/>");
                    dataObject.SetData(DataFormats.Html, htmlFragment);
                }

                return CopyData(dataObject);
            }
        }

        public static bool CopyFile(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                return CopyFile(new string[] { path });
            }

            return false;
        }

        public static bool CopyFile(string[] paths)
        {
            if (paths != null && paths.Length > 0)
            {
                try
                {
                    IDataObject dataObject = new DataObject();
                    dataObject.SetData(DataFormats.FileDrop, true, paths);

                    return CopyData(dataObject);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy file failed.");
                }
            }

            return false;
        }

        public static bool CopyImageFromFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using (Bitmap bmp = ImageHelpers.LoadImage(path))
                    {
                        string fileName = Path.GetFileName(path);
                        return CopyImage(bmp, fileName);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy image from file failed.");
                }
            }

            return false;
        }

        public static bool CopyTextFromFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    return CopyText(text);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy text from file failed.");
                }
            }

            return false;
        }

        public static Bitmap GetImage(bool checkContainsImage = false)
        {
            try
            {
                lock (ClipboardLock)
                {
                    if (!checkContainsImage || Clipboard.ContainsImage())
                    {
                        if (HelpersOptions.UseAlternativeClipboardGetImage)
                        {
                            return GetImageAlternative2();
                        }

                        return (Bitmap)Clipboard.GetImage();
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Clipboard get image failed.");
            }

            return null;
        }

        private static Image GetImageAlternative()
        {
            IDataObject dataObject = Clipboard.GetDataObject();

            if (dataObject != null)
            {
                string[] dataFormats = dataObject.GetFormats(false);

                if (dataFormats.Contains(FORMAT_PNG))
                {
                    using (MemoryStream ms = dataObject.GetData(FORMAT_PNG) as MemoryStream)
                    {
                        if (ms != null)
                        {
                            using (Image img = Image.FromStream(ms))
                            {
                                return (Image)img.Clone();
                            }
                        }
                    }
                }
                else
                {
                    foreach (string format in new[] { DataFormats.Dib, FORMAT_17 })
                    {
                        if (dataFormats.Contains(format))
                        {
                            using (MemoryStream ms = dataObject.GetData(format) as MemoryStream)
                            {
                                if (ms != null)
                                {
                                    try
                                    {
                                        return GetDIBImage(ms);
                                    }
                                    catch (Exception e)
                                    {
                                        DebugHelper.WriteException(e);
                                    }
                                }
                            }
                        }
                    }
                }

                if (dataObject.GetDataPresent(DataFormats.Bitmap, true))
                {
                    return dataObject.GetData(DataFormats.Bitmap, true) as Image;
                }
            }

            return null;
        }

        public static Bitmap GetImageAlternative2()
        {
            IDataObject dataObject = Clipboard.GetDataObject();

            if (dataObject != null)
            {
                string[] dataFormats = dataObject.GetFormats(false);

                if (dataFormats.Contains(FORMAT_PNG))
                {
                    using (MemoryStream ms = dataObject.GetData(FORMAT_PNG) as MemoryStream)
                    {
                        if (ms != null)
                        {
                            using (Bitmap bmp = new Bitmap(ms))
                            {
                                return ClipboardHelpersEx.CloneImage(bmp);
                            }
                        }
                    }
                }
                else if (dataFormats.Contains(DataFormats.Dib))
                {
                    using (MemoryStream ms = dataObject.GetData(DataFormats.Dib) as MemoryStream)
                    {
                        if (ms != null)
                        {
                            return ClipboardHelpersEx.ImageFromClipboardDib(ms.ToArray());
                        }
                    }
                }
                else if (dataFormats.Contains(FORMAT_17))
                {
                    using (MemoryStream ms = dataObject.GetData(FORMAT_17) as MemoryStream)
                    {
                        if (ms != null)
                        {
                            return ClipboardHelpersEx.DIBV5ToBitmap(ms.ToArray());
                        }
                    }
                }
                else if (dataFormats.Contains(DataFormats.Bitmap))
                {
                    return dataObject.GetData(DataFormats.Bitmap, true) as Bitmap;
                }
            }

            return null;
        }

        private static Image GetDIBImage(MemoryStream ms)
        {
            byte[] dib = ms.ToArray();

            BITMAPINFOHEADER infoHeader = Helpers.ByteArrayToStructure<BITMAPINFOHEADER>(dib);

            IntPtr gcHandle = IntPtr.Zero;

            try
            {
                GCHandle handle = GCHandle.Alloc(dib, GCHandleType.Pinned);
                gcHandle = GCHandle.ToIntPtr(handle);

                if (infoHeader.biSizeImage == 0)
                {
                    infoHeader.biSizeImage = (uint)(infoHeader.biWidth * infoHeader.biHeight * (infoHeader.biBitCount >> 3));
                }

                using (Bitmap bmp = new Bitmap(infoHeader.biWidth, infoHeader.biHeight, -(int)(infoHeader.biSizeImage / infoHeader.biHeight),
                    infoHeader.biBitCount == 32 ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb,
                    new IntPtr((long)handle.AddrOfPinnedObject() + infoHeader.OffsetToPixels + ((infoHeader.biHeight - 1) * (int)(infoHeader.biSizeImage / infoHeader.biHeight)))))
                {
                    return new Bitmap(bmp);
                }
            }
            finally
            {
                if (gcHandle != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(gcHandle).Free();
                }
            }
        }

        public static string GetText(bool checkContainsText = false)
        {
            try
            {
                lock (ClipboardLock)
                {
                    if (!checkContainsText || Clipboard.ContainsText())
                    {
                        return Clipboard.GetText();
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Clipboard get text failed.");
            }

            return null;
        }

        public static string[] GetFileDropList(bool checkContainsFileDropList = false)
        {
            try
            {
                lock (ClipboardLock)
                {
                    if (!checkContainsFileDropList || Clipboard.ContainsFileDropList())
                    {
                        return Clipboard.GetFileDropList().Cast<string>().ToArray();
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Clipboard get file drop list failed.");
            }

            return null;
        }

        public static Bitmap TryGetImage()
        {
            if (ContainsImage())
            {
                return GetImage();
            }
            else if (ContainsFileDropList())
            {
                string[] files = GetFileDropList();

                if (files != null)
                {
                    string imageFilePath = files.FirstOrDefault(x => FileHelpers.IsImageFile(x));

                    if (!string.IsNullOrEmpty(imageFilePath))
                    {
                        return ImageHelpers.LoadImage(imageFilePath);
                    }
                }
            }

            return null;
        }

        private static string GenerateHTMLFragment(string html)
        {
            StringBuilder sb = new StringBuilder();

            string header = "Version:0.9\r\nStartHTML:<<<<<<<<<1\r\nEndHTML:<<<<<<<<<2\r\nStartFragment:<<<<<<<<<3\r\nEndFragment:<<<<<<<<<4\r\n";
            string startHTML = "<html>\r\n<body>\r\n";
            string startFragment = "<!--StartFragment-->";
            string endFragment = "<!--EndFragment-->";
            string endHTML = "\r\n</body>\r\n</html>";

            sb.Append(header);

            int startHTMLLength = header.Length;
            int startFragmentLength = startHTMLLength + startHTML.Length + startFragment.Length;
            int endFragmentLength = startFragmentLength + Encoding.UTF8.GetByteCount(html);
            int endHTMLLength = endFragmentLength + endFragment.Length + endHTML.Length;

            sb.Replace("<<<<<<<<<1", startHTMLLength.ToString("D10"));
            sb.Replace("<<<<<<<<<2", endHTMLLength.ToString("D10"));
            sb.Replace("<<<<<<<<<3", startFragmentLength.ToString("D10"));
            sb.Replace("<<<<<<<<<4", endFragmentLength.ToString("D10"));

            sb.Append(startHTML);
            sb.Append(startFragment);
            sb.Append(html);
            sb.Append(endFragment);
            sb.Append(endHTML);

            return sb.ToString();
        }

        public static bool ContainsImage()
        {
            try
            {
                return Clipboard.ContainsImage();
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static bool ContainsText()
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static bool ContainsFileDropList()
        {
            try
            {
                return Clipboard.ContainsFileDropList();
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }
    }
}
