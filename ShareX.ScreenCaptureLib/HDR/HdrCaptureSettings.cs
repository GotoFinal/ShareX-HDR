#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.ComponentModel;

namespace ShareX.ScreenCaptureLib
{
    public enum HdrToneMappingMode
    {
        [Description("Content-aware - precise (recommended)")]
        ContentAware = 0,

        [Description("Uniform (entire HDR monitor)")]
        Uniform = 1,

        [Description("Content-aware - per window")]
        PerWindow = 2
    }

    public enum HdrProcessingBackend
    {
        [Description("CPU (compatible)")]
        Cpu,

        [Description("GPU (faster, falls back to CPU)")]
        Gpu
    }

    public enum HdrPeakBrightnessMode
    {
        [Description("Automatic (measure HDR content)")]
        Automatic,

        [Description("Custom")]
        Custom
    }

    public enum HdrPaperWhiteMode
    {
        [Description("Automatic (Windows SDR brightness)")]
        Automatic,

        [Description("Custom")]
        Custom
    }

    public enum HdrMixedMonitorBrightnessMode
    {
        [Description("Match nearest HDR display (recommended)")]
        MatchHdrDisplay,

        [Description("Preserve captured luminance")]
        Preserve,

        [Description("Custom SDR white")]
        Custom
    }

    public enum HdrGameWindowPromotionMode
    {
        [Description("Known/hooked games only")]
        KnownGamesOnly = 0,

        [Description("Automatically promote fullscreen HDR windows (recommended)")]
        AutomaticFullscreenHdr = 1,

        Disabled = 2
    }

    public sealed class HdrCaptureSettings
    {
        public const float MinimumBrightnessNits = 80f;
        public const float MaximumBrightnessNits = 10000f;
        public const float MaximumPaperWhiteNits = 1000f;
        public const float DefaultBrightnessNits = 203f;
        public const float DefaultCustomPeakBrightnessNits = 1000f;

        private float hdrBrightnessNits = DefaultBrightnessNits;
        private HdrToneMappingMode toneMappingMode = HdrToneMappingMode.ContentAware;
        private HdrProcessingBackend processingBackend = HdrProcessingBackend.Gpu;
        private HdrPeakBrightnessMode? peakBrightnessMode;
        private HdrPaperWhiteMode paperWhiteMode;
        private float paperWhiteNits = DefaultBrightnessNits;
        private HdrMixedMonitorBrightnessMode mixedMonitorBrightnessMode;
        private float mixedMonitorCustomSdrWhiteNits = DefaultBrightnessNits;
        private HdrGameWindowPromotionMode gameWindowPromotionMode = HdrGameWindowPromotionMode.AutomaticFullscreenHdr;
        private ObsGameCaptureSettings obsGameCapture = new ObsGameCaptureSettings();
        private HdrFileOutputSettings fileOutput = new HdrFileOutputSettings();

        // Native SDR pixels avoid OS-dependent FP16 white scaling on SDR
        // displays. Disabling this preserves the original WGC capture path.
        public bool UseNativeSdrCapture { get; set; } = true;

        // This intentionally has a new serialized name. The original
        // EnableEditorHdrPreview option defaulted to true; leaving it unmapped
        // resets the experimental native presenter to false once for existing
        // installations. Users can explicitly opt in again afterward.
        public bool EnableNativeHdrEditorPreview { get; set; } = false;

        // This intentionally has a new serialized name. The original
        // EnableRegionSelectorHdrPreview option defaulted to true; leaving that
        // legacy property unmapped resets the experimental presenter to the
        // safer false default once for existing installations. Users can then
        // explicitly opt in again through the renamed setting.
        public bool EnableNativeHdrRegionSelectorPreview { get; set; } = false;

        public HdrFileOutputSettings FileOutput
        {
            get => fileOutput;
            set => fileOutput = value ?? new HdrFileOutputSettings();
        }

        public ObsGameCaptureSettings ObsGameCapture
        {
            get => obsGameCapture;
            set => obsGameCapture = value ?? new ObsGameCaptureSettings();
        }

        public HdrPeakBrightnessMode PeakBrightnessMode
        {
            // Older settings used 203 as the implicit automatic sentinel. An
            // absent mode therefore migrates 203 to Automatic and preserves
            // every other old value as an explicit Custom value.
            get => peakBrightnessMode ??
                (Math.Abs(hdrBrightnessNits - DefaultBrightnessNits) < 0.01f
                    ? HdrPeakBrightnessMode.Automatic
                    : HdrPeakBrightnessMode.Custom);
            set => peakBrightnessMode = Enum.IsDefined(value) ? value : HdrPeakBrightnessMode.Automatic;
        }

        public HdrProcessingBackend ProcessingBackend
        {
            get => processingBackend;
            set => processingBackend = Enum.IsDefined(value) ? value : HdrProcessingBackend.Gpu;
        }

        public HdrToneMappingMode ToneMappingMode
        {
            get => toneMappingMode;
            set => toneMappingMode = Enum.IsDefined(value) ? value : HdrToneMappingMode.ContentAware;
        }

        public HdrPaperWhiteMode PaperWhiteMode
        {
            get => paperWhiteMode;
            set => paperWhiteMode = Enum.IsDefined(value) ? value : HdrPaperWhiteMode.Automatic;
        }

        public float PaperWhiteNits
        {
            get => paperWhiteNits;
            set => paperWhiteNits = Math.Clamp(value, MinimumBrightnessNits, MaximumPaperWhiteNits);
        }

        public HdrMixedMonitorBrightnessMode MixedMonitorBrightnessMode
        {
            get => mixedMonitorBrightnessMode;
            set => mixedMonitorBrightnessMode = Enum.IsDefined(value)
                ? value
                : HdrMixedMonitorBrightnessMode.MatchHdrDisplay;
        }

        public float MixedMonitorCustomSdrWhiteNits
        {
            get => mixedMonitorCustomSdrWhiteNits;
            set => mixedMonitorCustomSdrWhiteNits = Math.Clamp(
                value,
                MinimumBrightnessNits,
                MaximumPaperWhiteNits);
        }

        public HdrGameWindowPromotionMode GameWindowPromotionMode
        {
            get => gameWindowPromotionMode;
            set => gameWindowPromotionMode = Enum.IsDefined(value)
                ? value
                : HdrGameWindowPromotionMode.AutomaticFullscreenHdr;
        }

        public float HdrBrightnessNits
        {
            get => hdrBrightnessNits;
            set => hdrBrightnessNits = Math.Clamp(value, MinimumBrightnessNits, MaximumBrightnessNits);
        }
    }
}
