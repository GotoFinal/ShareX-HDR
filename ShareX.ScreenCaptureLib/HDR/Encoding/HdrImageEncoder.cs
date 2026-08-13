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
using System.ComponentModel;
using System.IO;

namespace ShareX.ScreenCaptureLib
{
    public enum HdrOutputMode
    {
        [Description("SDR only")]
        SdrOnly,

        [Description("HDR only")]
        HdrOnly,

        [Description("HDR and SDR")]
        HdrAndSdr
    }

    public enum HdrClipboardOutputMode
    {
        [Description("HDR + SDR fallback (recommended)")]
        HdrAndSdr,

        [Description("HDR only")]
        HdrOnly,

        [Description("SDR only (legacy)")]
        SdrOnly
    }

    public enum HdrFileFormat
    {
        [Description("Ultra HDR JPEG")]
        UltraHdrJpeg,

        [Description("OpenEXR (lossless)")]
        OpenExr,

        [Description("HDR PNG (experimental)")]
        HdrPng,

        [Description("HDR AVIF (10-bit)")]
        Avif
    }

    public enum OpenExrExposureMode
    {
        [Description("Match captured display (recommended)")]
        DisplayReferenced,

        [Description("Raw scRGB samples (lossless)")]
        RawScRgb
    }

    public sealed class HdrFileOutputSettings
    {
        public const float MinimumMasteringDisplayNits = 80f;
        public const float MaximumMasteringDisplayNits = 10000f;
        public const float DefaultMasteringDisplayMaximumNits = 1000f;
        public const float DefaultMasteringDisplayMinimumNits = 0.0005f;

        private HdrOutputMode outputMode = HdrOutputMode.SdrOnly;
        private HdrFileFormat fileFormat = HdrFileFormat.HdrPng;
        private float masteringDisplayMaximumNits = DefaultMasteringDisplayMaximumNits;
        private float masteringDisplayMinimumNits = DefaultMasteringDisplayMinimumNits;
        private int jpegQuality = 95;
        private int gainMapQuality = 90;
        private int avifQuality = 90;
        private int avifSpeed = 6;
        private OpenExrExposureMode openExrExposureMode = OpenExrExposureMode.DisplayReferenced;
        private HdrClipboardOutputMode clipboardOutputMode = HdrClipboardOutputMode.HdrAndSdr;
        private HdrFileFormat clipboardFileFormat = HdrFileFormat.HdrPng;

        public bool UploadWithFileUploader { get; set; } = true;

        public HdrClipboardOutputMode ClipboardOutputMode
        {
            get => clipboardOutputMode;
            set => clipboardOutputMode = Enum.IsDefined(value)
                ? value
                : HdrClipboardOutputMode.HdrAndSdr;
        }

        public HdrOutputMode OutputMode
        {
            get => outputMode;
            set => outputMode = Enum.IsDefined(value) ? value : HdrOutputMode.SdrOnly;
        }

        public HdrFileFormat FileFormat
        {
            get => fileFormat;
            set => fileFormat = Enum.IsDefined(value) ? value : HdrFileFormat.HdrPng;
        }

        public float MasteringDisplayMaximumNits
        {
            get => masteringDisplayMaximumNits;
            set => masteringDisplayMaximumNits = Math.Clamp(
                value,
                MinimumMasteringDisplayNits,
                MaximumMasteringDisplayNits);
        }

        public float MasteringDisplayMinimumNits
        {
            get => masteringDisplayMinimumNits;
            set => masteringDisplayMinimumNits = Math.Clamp(
                value,
                0f,
                Math.Min(1f, MasteringDisplayMaximumNits));
        }

        public int JpegQuality
        {
            get => jpegQuality;
            set => jpegQuality = Math.Clamp(value, 1, 100);
        }

        public int GainMapQuality
        {
            get => gainMapQuality;
            set => gainMapQuality = Math.Clamp(value, 1, 100);
        }

        public HdrFileFormat ClipboardFileFormat
        {
            get => clipboardFileFormat;
            set => clipboardFileFormat = Enum.IsDefined(value) ? value : HdrFileFormat.HdrPng;
        }

        public int AvifQuality
        {
            get => avifQuality;
            set => avifQuality = Math.Clamp(value, 1, 100);
        }

        public int AvifSpeed
        {
            get => avifSpeed;
            set => avifSpeed = Math.Clamp(value, 0, 10);
        }

        public OpenExrExposureMode OpenExrExposureMode
        {
            get => openExrExposureMode;
            set => openExrExposureMode = Enum.IsDefined(value) ? value : OpenExrExposureMode.DisplayReferenced;
        }

        /// <summary>
        /// JPEG has no alpha channel. When enabled, transparent FP16 pixels are
        /// composited over black before Ultra HDR encoding; otherwise encoding is rejected.
        /// </summary>
        public bool FlattenTransparencyForUltraHdr { get; set; }
    }

    public sealed class HdrImageEncodingOptions
    {
        public float MasteringDisplayMaximumNits { get; init; } =
            HdrFileOutputSettings.DefaultMasteringDisplayMaximumNits;

        public float MasteringDisplayMinimumNits { get; init; } =
            HdrFileOutputSettings.DefaultMasteringDisplayMinimumNits;

        public int Quality { get; init; } = 95;
        public int GainMapQuality { get; init; } = 90;
        public int AvifQuality { get; init; } = 90;
        public int AvifSpeed { get; init; } = 6;

        public bool FlattenTransparencyForUltraHdr { get; init; }

        public OpenExrExposureMode OpenExrExposureMode { get; init; } =
            OpenExrExposureMode.RawScRgb;

        public float OpenExrReferenceWhiteNits { get; init; } =
            HdrCaptureSettings.DefaultBrightnessNits;
        internal float GetValidatedMasteringDisplayMaximumNits() => Math.Clamp(
            MasteringDisplayMaximumNits,
            HdrFileOutputSettings.MinimumMasteringDisplayNits,
            HdrFileOutputSettings.MaximumMasteringDisplayNits);

        internal float GetValidatedMasteringDisplayMinimumNits(float maximumNits) => Math.Clamp(
            MasteringDisplayMinimumNits,
            0f,
            Math.Min(1f, maximumNits));

        internal float GetValidatedOpenExrReferenceWhiteNits() => Math.Clamp(
            OpenExrReferenceWhiteNits,
            HdrFileOutputSettings.MinimumMasteringDisplayNits,
            HdrFileOutputSettings.MaximumMasteringDisplayNits);
    }

    public readonly record struct HdrEncodedImageInfo(
        HdrFileFormat Format,
        string FileExtension,
        string MediaType,
        bool IsLossless,
        long BytesWritten,
        float MaxContentLightLevelNits = 0f,
        float MaxFrameAverageLightLevelNits = 0f);

    public interface IHdrImageEncoder
    {
        HdrFileFormat Format { get; }

        HdrEncodedImageInfo Encode(
            HdrRgba16FloatBuffer source,
            Stream destination,
            HdrImageEncodingOptions options = null);
    }
}
