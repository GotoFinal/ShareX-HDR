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

    public sealed class HdrCaptureSettings
    {
        public const float MinimumBrightnessNits = 80f;
        public const float MaximumBrightnessNits = 1000f;
        public const float DefaultBrightnessNits = 203f;

        private float hdrBrightnessNits = DefaultBrightnessNits;
        private HdrToneMappingMode toneMappingMode = HdrToneMappingMode.ContentAware;

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
