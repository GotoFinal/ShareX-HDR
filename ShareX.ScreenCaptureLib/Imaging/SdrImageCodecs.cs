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

using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ShareX.ScreenCaptureLib
{
    public static class SdrImageCodecs
    {
        public static void RegisterImageHelpers()
        {
            ImageHelpers.RegisterImageFileCodec("avif", "AVIF",
                (image, destination) => SdrAvifImageCodec.Encode(image, destination),
                DecodeAvif,
                () => SdrAvifImageCodec.TryGetAvailability(out _));
            ImageHelpers.RegisterImageFileCodec("exr", "OpenEXR", EncodeOpenExr, DecodeOpenExr);
        }

        /// <summary>Writes linear Rec.709 HALF, with SDR white at 1.0 and premultiplied alpha.</summary>
        public static unsafe void EncodeOpenExr(Image source, Stream destination)
        {
            ArgumentNullException.ThrowIfNull(source);
            using Bitmap converted = source is Bitmap { PixelFormat: PixelFormat.Format32bppArgb }
                ? null : SdrAvifImageCodec.CreateArgbBitmap(source);
            Bitmap bitmap = converted ?? (Bitmap)source;
            using var pixels = new HdrRgba16FloatBuffer(bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = checked(bitmap.Width * 4);
                // Decode straight sRGB before associating alpha. Converting to 8-bit
                // PArgb first irreversibly rounds low-alpha colors. Row-wise import
                // also accepts bitmaps whose logical rows have a negative stride.
                for (int y = 0; y < bitmap.Height; y++)
                {
                    SdrAnnotationOverlayCompositor.CompositeBgra8Straight(
                        pixels.GetWritableRowSpan(y), pixels.RowBytes, bitmap.Width, 1,
                        new ReadOnlySpan<byte>((byte*)data.Scan0 + (nint)y * data.Stride, rowBytes),
                        rowBytes, bitmap.Width, 1, 0, 0, HdrRgba16FloatBuffer.ReferenceWhiteNits);
                }
            }
            finally { bitmap.UnlockBits(data); }
            new OpenExrHdrImageEncoder().EncodeSdr(pixels, destination);
        }

        private static Bitmap DecodeOpenExr(Stream source)
        {
            var decoder = new OpenExrHdrImageDecoder();
            bool isSdr = decoder.IsSdr(source);
            HdrRgba16FloatBuffer pixels = decoder.Decode(source);
            return CreatePreview(pixels, isSdr,
                isSdr ? HdrRgba16FloatBuffer.ReferenceWhiteNits : HdrCaptureSettings.DefaultBrightnessNits,
                HdrFileOutputSettings.DefaultMasteringDisplayMaximumNits);
        }

        private static Bitmap DecodeAvif(Stream source)
        {
            if (!source.CanRead || !source.CanSeek || source.Length <= 0 || source.Length > 512L * 1024 * 1024)
                throw new InvalidDataException("The AVIF source exceeds the supported decode limit.");
            long position = source.Position;
            byte[] encoded = new byte[checked((int)source.Length)];
            try
            {
                source.Position = 0;
                source.ReadExactly(encoded);
            }
            finally { source.Position = position; }
            if (SdrAvifImageCodec.TryProbe(encoded, out _, out _)) return SdrAvifImageCodec.Decode(source);
            if (AvifHdrImageEncoder.TryProbeHdr(encoded, out _, out _, out float peakNits, out _, out _))
                return CreatePreview(new AvifHdrImageDecoder().Decode(source), isSdr: false,
                    HdrCaptureSettings.DefaultBrightnessNits, peakNits);
            throw new InvalidDataException("This AVIF color space or encoding is not supported by ShareX.");
        }

        private static unsafe Bitmap CreatePreview(HdrRgba16FloatBuffer pixels, bool isSdr, float whiteNits, float peakNits)
        {
            if (isSdr)
            {
                // Preserve straight-alpha RGB when loading ordinary EXR. The
                // multi-segment HDR preview compositor uses GDI for composition.
                using (pixels)
                fixed (byte* source = pixels.PixelBytes.Span)
                    return HdrToSdrToneMapper.ToneMapKnownSdrRgba16Float((IntPtr)source,
                        pixels.RowBytes, pixels.Width, pixels.Height, whiteNits, preserveAlpha: true);
            }
            HdrImageDocument document;
            try
            {
                document = new HdrImageDocument(new Rectangle(0, 0, pixels.Width, pixels.Height), pixels,
                    [new HdrCaptureSourceSegment(new Rectangle(0, 0, pixels.Width, pixels.Height), "", !isSdr, whiteNits, peakNits)]);
            }
            catch { pixels.Dispose(); throw; }
            using (document)
                return document.CreateSdrPreview(new HdrCaptureSettings { ProcessingBackend = HdrProcessingBackend.Cpu });
        }
    }
}
