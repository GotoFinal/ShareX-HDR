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
                Bitmap opaqueFallback = null;
                MemoryStream dibStream = null;
                MemoryStream sdrPngStream = null;
                var encodedStreams = new System.Collections.Generic.List<MemoryStream>();

                try
                {
                    double fallbackFillMilliseconds = 0d;
                    double dibMilliseconds = 0d;
                    double sdrPngMilliseconds = 0d;
                    Image bitmapFallback = sdrFallback;
                    if (sdrFallback != null && HelpersOptions.DefaultCopyImageFillBackground)
                    {
                        Stopwatch fallbackFillTimer = Stopwatch.StartNew();
                        opaqueFallback = sdrFallback.CreateEmptyBitmap(PixelFormat.Format24bppRgb);
                        using Graphics graphics = Graphics.FromImage(opaqueFallback);
                        graphics.Clear(Color.White);
                        graphics.DrawImage(sdrFallback, 0, 0, sdrFallback.Width, sdrFallback.Height);
                        bitmapFallback = opaqueFallback;
                        fallbackFillTimer.Stop();
                        fallbackFillMilliseconds = fallbackFillTimer.Elapsed.TotalMilliseconds;
                    }

                    // Ultra HDR JPEG has a normal SDR JPEG base, so applications
                    // that only understand JPEG can still consume these bytes.
                    if (sdrFallback == null || encodedImageContainsSdrFallback)
                    {
                        AddEncodedFormat(encodedClipboardFormat);
                    }

                    if (bitmapFallback != null)
                    {
                        dataObject.SetData(DataFormats.Bitmap, true, bitmapFallback);
                        Stopwatch dibTimer = Stopwatch.StartNew();
                        dibStream = new MemoryStream(ClipboardHelpersEx.ConvertToDib(bitmapFallback));
                        dataObject.SetData(DataFormats.Dib, false, dibStream);
                        dibTimer.Stop();
                        dibMilliseconds = dibTimer.Elapsed.TotalMilliseconds;

                        // Many applications prefer the registered PNG format to
                        // Bitmap/DIB. Give it SDR bytes in compatibility mode so
                        // recognizing PNG cannot lead to a failed HDR decode.
                        Stopwatch sdrPngTimer = Stopwatch.StartNew();
                        sdrPngStream = new MemoryStream();
                        bitmapFallback.Save(sdrPngStream, ImageFormat.Png);
                        sdrPngStream.Position = 0;
                        dataObject.SetData(FORMAT_PNG, false, sdrPngStream);
                        sdrPngTimer.Stop();
                        sdrPngMilliseconds = sdrPngTimer.Elapsed.TotalMilliseconds;

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
                    if (!(bitmapFallback != null &&
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
                        $"sdrFallback={bitmapFallback != null} " +
                        $"encodedCopyMs={encodedCopyTimer.Elapsed.TotalMilliseconds:F1} " +
                        $"fallbackFillMs={fallbackFillMilliseconds:F1} " +
                        $"dibMs={dibMilliseconds:F1} sdrPngMs={sdrPngMilliseconds:F1} " +
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
                    opaqueFallback?.Dispose();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR image clipboard copy failed.");
            }

            return false;
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
