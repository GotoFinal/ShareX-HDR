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
            ArgumentNullException.ThrowIfNull(document);
            settings ??= new HdrFileOutputSettings();
            HdrEncoderCapabilities.EnsureAvailable(settings.FileFormat);

            IHdrImageEncoder encoder = settings.FileFormat switch
            {
                HdrFileFormat.UltraHdrJpeg => new UltraHdrJpegImageEncoder(),
                HdrFileFormat.OpenExr => new OpenExrHdrImageEncoder(),
                HdrFileFormat.HdrPng => new HdrPngImageEncoder(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(settings),
                    settings.FileFormat,
                    "Unsupported HDR image format.")
            };

            HdrImageEncodingOptions options = new HdrImageEncodingOptions
            {
                MasteringDisplayMaximumNits = settings.MasteringDisplayMaximumNits,
                MasteringDisplayMinimumNits = settings.MasteringDisplayMinimumNits,
                Quality = settings.JpegQuality,
                GainMapQuality = settings.GainMapQuality,
                FlattenTransparencyForUltraHdr = settings.FlattenTransparencyForUltraHdr
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
                _ => HelpersLib.EImageFormat.PNG
            };
    }
}
