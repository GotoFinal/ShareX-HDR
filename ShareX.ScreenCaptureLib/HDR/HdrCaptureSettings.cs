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

namespace ShareX.ScreenCaptureLib
{
    public sealed class HdrCaptureSettings
    {
        public const float MinimumBrightnessNits = 80f;
        public const float MaximumBrightnessNits = 1000f;
        public const float DefaultBrightnessNits = 203f;

        private float hdrBrightnessNits = DefaultBrightnessNits;

        public float HdrBrightnessNits
        {
            get => hdrBrightnessNits;
            set => hdrBrightnessNits = Math.Clamp(value, MinimumBrightnessNits, MaximumBrightnessNits);
        }
    }
}
