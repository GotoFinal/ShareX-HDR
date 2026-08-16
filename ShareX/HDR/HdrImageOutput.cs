#region License Information (GPL v3)

/*
    ShareX - A program to capture and share images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using ShareX.ScreenCaptureLib;
using System;
using System.IO;

namespace ShareX
{
    internal static class HdrImageOutput
    {
        public static ImageData Encode(
            HdrImageDocument document,
            HdrFileOutputSettings settings)
        {
            settings ??= new HdrFileOutputSettings();
            return Encode(document, settings, settings.FileFormat);
        }

        public static ImageData EncodeClipboard(
            HdrImageDocument document,
            HdrFileOutputSettings settings)
        {
            settings ??= new HdrFileOutputSettings();
            return Encode(document, settings, settings.ClipboardFileFormat);
        }

        public static ImageData Encode(
            HdrImageDocument document,
            HdrFileOutputSettings settings,
            HdrFileFormat format)
        {
            ArgumentNullException.ThrowIfNull(document);
            settings ??= new HdrFileOutputSettings();
            HdrEncoderCapabilities.EnsureAvailable(format);
            return Encode(document, settings, CreateEncoder(format));
        }

        private static IHdrImageEncoder CreateEncoder(HdrFileFormat format) =>
            format switch
            {
                HdrFileFormat.UltraHdrJpeg => new UltraHdrJpegImageEncoder(),
                HdrFileFormat.OpenExr => new OpenExrHdrImageEncoder(),
                HdrFileFormat.HdrPng => new HdrPngImageEncoder(),
                HdrFileFormat.Avif => new AvifHdrImageEncoder(),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported HDR image format.")
            };

        private static ImageData Encode(
            HdrImageDocument document,
            HdrFileOutputSettings settings,
            IHdrImageEncoder encoder)
        {
            HdrImageEncodingOptions options = new HdrImageEncodingOptions
            {
                MasteringDisplayMaximumNits = settings.MasteringDisplayMaximumNits,
                MasteringDisplayMinimumNits = settings.MasteringDisplayMinimumNits,
                Quality = settings.JpegQuality,
                GainMapQuality = settings.GainMapQuality,
                AvifQuality = settings.AvifQuality,
                AvifSpeed = settings.AvifSpeed,
                FlattenTransparencyForUltraHdr = settings.FlattenTransparencyForUltraHdr,
                OpenExrExposureMode = settings.OpenExrExportExposureMode,
                OpenExrReferenceWhiteNits = TaskHelpers.GetHdrAnnotationWhiteNits(document)
            };

            MemoryStream stream = new MemoryStream();

            try
            {
                HdrEncodedImageInfo info = encoder.Encode(document.MasterPixels, stream, options);

                if (!HdrEncodedImageVerifier.Verify(stream, info.Format, document.MasterPixels.Width, document.MasterPixels.Height))
                {
                    throw new InvalidDataException("The HDR encoder produced an invalid artifact.");
                }

                return new ImageData
                {
                    ImageStream = stream,
                    ImageFormat = GetCompatibilityImageFormat(info.Format),
                    FileExtension = info.FileExtension.TrimStart('.'),
                    MediaType = info.MediaType,
                    IsHdr = true,
                    FileVerifier = filePath => HdrEncodedImageVerifier.VerifyFile(
                        filePath, info.Format, document.MasterPixels.Width, document.MasterPixels.Height)
                };
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static HelpersLib.EImageFormat GetCompatibilityImageFormat(HdrFileFormat format) =>
            format switch
            {
                HdrFileFormat.UltraHdrJpeg => HelpersLib.EImageFormat.JPEG,
                HdrFileFormat.HdrPng => HelpersLib.EImageFormat.PNG,
                HdrFileFormat.Avif => HelpersLib.EImageFormat.PNG,
                _ => HelpersLib.EImageFormat.PNG
            };
    }
}
