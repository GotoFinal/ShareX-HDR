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
using System.Collections.Generic;
using System.Drawing;

namespace ShareX.ScreenCaptureLib
{
    internal static class HdrRegionEffectReplayer
    {
        public static bool CanReplay(IReadOnlyList<BaseEffectShape> effects)
        {
            if (effects == null)
            {
                return false;
            }

            foreach (BaseEffectShape effect in effects)
            {
                if (effect is not BlurEffectShape and
                    not PixelateEffectShape and
                    not HighlightEffectShape)
                {
                    return false;
                }
            }

            return true;
        }

        public static void Replay(
            HdrImageDocument document,
            IReadOnlyList<BaseEffectShape> effects,
            Rectangle selectedClientRectangle,
            float annotationWhiteNits)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(effects);

            if (selectedClientRectangle.Width != document.MasterPixels.Width ||
                selectedClientRectangle.Height != document.MasterPixels.Height)
            {
                throw new ArgumentException(
                    "The selected region must match the retained HDR document.",
                    nameof(selectedClientRectangle));
            }

            if (!CanReplay(effects))
            {
                throw new NotSupportedException("The region contains an unsupported HDR source effect.");
            }

            Rectangle canvas = new Rectangle(
                Point.Empty,
                new Size(document.MasterPixels.Width, document.MasterPixels.Height));

            foreach (BaseEffectShape effect in effects)
            {
                Rectangle effectRectangle = Rectangle.Round(effect.Rectangle);
                effectRectangle.Offset(
                    -selectedClientRectangle.X,
                    -selectedClientRectangle.Y);
                effectRectangle = Rectangle.Intersect(canvas, effectRectangle);
                if (effectRectangle.Width <= 0 || effectRectangle.Height <= 0)
                {
                    continue;
                }

                switch (effect)
                {
                    case BlurEffectShape blur when blur.BlurRadius > 1:
                        document.BoxBlurRectangle(effectRectangle, blur.BlurRadius);
                        break;
                    case PixelateEffectShape pixelate when pixelate.PixelSize > 1:
                        document.PixelateBlocksRectangle(effectRectangle, pixelate.PixelSize);
                        break;
                    case HighlightEffectShape highlight:
                        document.ApplyHighlightRectangle(
                            effectRectangle,
                            highlight.HighlightColor.R,
                            highlight.HighlightColor.G,
                            highlight.HighlightColor.B,
                            annotationWhiteNits);
                        break;
                }
            }
        }
    }
}
