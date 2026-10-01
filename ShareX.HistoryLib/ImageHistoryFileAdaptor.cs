#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

#endregion License Information (GPL v3)

using Manina.Windows.Forms;
using Manina.Windows.Forms.ImageListViewItemAdaptors;
using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace ShareX.HistoryLib
{
    /// <summary>Uses registered non-GDI codecs while keeping normal file-system metadata/cache identity.</summary>
    public sealed class ImageHistoryFileAdaptor : FileSystemAdaptor
    {
        public override Image GetThumbnail(object key, Size size, UseEmbeddedThumbnails useEmbeddedThumbnails, bool autoRotate)
        {
            string path = key as string;
            string extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".avif", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".exr", StringComparison.OrdinalIgnoreCase))
                return base.GetThumbnail(key, size, useEmbeddedThumbnails, autoRotate);

            using Bitmap image = ImageHelpers.LoadImage(path);
            if (image == null || size.Width <= 0 || size.Height <= 0) return null;
            double scale = Math.Min(size.Width / (double)image.Width, size.Height / (double)image.Height);
            var thumbnail = new Bitmap(Math.Max(1, (int)Math.Round(image.Width * scale)),
                Math.Max(1, (int)Math.Round(image.Height * scale)));
            try
            {
                using Graphics graphics = Graphics.FromImage(thumbnail);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, new Rectangle(Point.Empty, thumbnail.Size));
                return thumbnail;
            }
            catch { thumbnail.Dispose(); throw; }
        }
    }
}
