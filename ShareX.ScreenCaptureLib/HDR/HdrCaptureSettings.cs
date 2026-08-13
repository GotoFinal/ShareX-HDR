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
        [Description("Content-aware (recommended)")]
        ContentAware,

        [Description("Uniform (entire HDR monitor)")]
        Uniform
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
        [Description("Automatic")]
        Automatic,

        [Description("Custom")]
        Custom
    }

    public sealed class HdrCaptureSettings
    {
        public const float MinimumBrightnessNits = 80f;
        public const float MaximumBrightnessNits = 1000f;
        public const float DefaultBrightnessNits = 203f;

        private float hdrBrightnessNits = DefaultBrightnessNits;
        private HdrToneMappingMode toneMappingMode = HdrToneMappingMode.ContentAware;
        private HdrProcessingBackend processingBackend = HdrProcessingBackend.Cpu;
        private HdrPeakBrightnessMode? peakBrightnessMode;
        private ObsGameCaptureSettings obsGameCapture = new ObsGameCaptureSettings();
        private HdrFileOutputSettings fileOutput = new HdrFileOutputSettings();

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
            set => processingBackend = Enum.IsDefined(value) ? value : HdrProcessingBackend.Cpu;
        }

        public HdrToneMappingMode ToneMappingMode
        {
            get => toneMappingMode;
            set => toneMappingMode = Enum.IsDefined(value) ? value : HdrToneMappingMode.ContentAware;
        }

        public float HdrBrightnessNits
        {
            get => hdrBrightnessNits;
            set => hdrBrightnessNits = Math.Clamp(value, MinimumBrightnessNits, MaximumBrightnessNits);
        }
    }
}
