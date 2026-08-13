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

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib
{
    public sealed record ObsGameCaptureFrameMetadata(
        string DisplayDeviceName,
        bool WasHdrActive,
        float SdrWhiteNits,
        float DisplayPeakNits,
        ObsGameCaptureRgb10A2ColorSpace? Rgb10A2ColorSpace = null);

    public static class ObsGameCaptureDocumentFactory
    {
        public static HdrImageDocument CopyToOwnedDocument(
            ObsGameCapturePublication publication,
            ObsGameCaptureFrameMetadata metadata,
            bool allowTransparency = true)
        {
            return CopyToOwnedDocument(
                publication,
                metadata,
                allowTransparency ? ObsGameCaptureAlphaMode.Straight : ObsGameCaptureAlphaMode.Opaque);
        }

        public static HdrImageDocument CopyToOwnedDocument(
            ObsGameCapturePublication publication,
            ObsGameCaptureFrameMetadata metadata,
            ObsGameCaptureAlphaMode alphaMode)
        {
            ArgumentNullException.ThrowIfNull(publication);
            ArgumentNullException.ThrowIfNull(metadata);

            if (string.IsNullOrWhiteSpace(metadata.DisplayDeviceName))
            {
                throw new ArgumentException("A display/source name is required.", nameof(metadata));
            }

            if (!float.IsFinite(metadata.SdrWhiteNits) || metadata.SdrWhiteNits <= 0f ||
                !float.IsFinite(metadata.DisplayPeakNits) || metadata.DisplayPeakNits <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(metadata));
            }

            HdrRgba16FloatBuffer pixels = metadata.Rgb10A2ColorSpace is { } rgb10A2ColorSpace
                ? ObsGameCaptureTextureReader.CopyRgba16FloatToOwnedBuffer(
                    publication, rgb10A2ColorSpace, alphaMode)
                : ObsGameCaptureTextureReader.CopyRgba16FloatToOwnedBuffer(
                    publication, alphaMode);

            try
            {
                var bounds = new Rectangle(0, 0, pixels.Width, pixels.Height);
                var segment = new HdrCaptureSourceSegment(
                    bounds,
                    metadata.DisplayDeviceName,
                    metadata.WasHdrActive,
                    metadata.SdrWhiteNits,
                    metadata.DisplayPeakNits);

                return new HdrImageDocument(bounds, pixels, new[] { segment });
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }
    }
}
