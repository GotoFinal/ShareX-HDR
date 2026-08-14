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

using ShareX.HelpersLib;
using ShareX.ImageEffectsLib;
using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ShareX;

/// <summary>
/// Conservative FP16 replay for the older after-capture image-effect preset
/// system. The legacy effect types are intentionally internal, so their public
/// serialized property contract is inspected by name here. Unknown effects fail
/// preflight before the retained HDR document is mutated.
/// </summary>
internal static class LegacyHdrImageEffectReplayer
{
    public static bool TryApply(
        HdrImageDocument source,
        ImageEffectPreset preset,
        float sdrWhiteNits,
        out HdrImageDocument result,
        out string unsupportedEffect)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preset);
        result = null;
        unsupportedEffect = null;

        ImageEffect[] effects = preset.Effects?
            .Where(effect => effect?.Enabled == true)
            .ToArray() ?? Array.Empty<ImageEffect>();
        foreach (ImageEffect effect in effects)
        {
            if (!CanReplay(effect, out unsupportedEffect))
            {
                return false;
            }
        }

        HdrImageDocument candidate = source.Clone();
        try
        {
            foreach (ImageEffect effect in effects)
            {
                ApplyEffect(ref candidate, effect, sdrWhiteNits);
            }

            result = candidate;
            return true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private static bool CanReplay(ImageEffect effect, out string unsupportedEffect)
    {
        string typeName = effect.GetType().Name;
        bool supported = typeName switch
        {
            "Alpha" => Get<float>(effect, "Value") is >= 0f and <= 1f &&
                Math.Abs(Get<float>(effect, "Addition")) <= 0.0001f,
            "Brightness" or "Contrast" or "Grayscale" or "Hue" or "Sepia" =>
                IsFinite(Get<float>(effect,
                typeName == "Hue" ? "Angle" : "Value")),
            "Gamma" => Get<float>(effect, "Value") is >= 0.1f and <= 5f,
            "Saturation" => Get<float>(effect, "Value") is >= 0f and <= 2f,
            "Inverse" or "BlackWhite" or "Flip" or "Crop" or "Canvas" or
            "RoundedCorners" or "Resize" or "DrawText" or "DrawTextEx" => true,
            "Blur" => Get<int>(effect, "Radius") is > 1 and <= HdrImageDocument.MaximumSupportedBoxBlurRange,
            "GaussianBlur" => Get<int>(effect, "Radius") is > 0 and <= 600,
            "Pixelate" => Get<int>(effect, "Size") > 0 && Get<int>(effect, "BorderSize") == 0,
            _ => false
        };

        unsupportedEffect = supported ? null : effect.ToString();
        return supported;
    }

    private static void ApplyEffect(
        ref HdrImageDocument document,
        ImageEffect effect,
        float sdrWhiteNits)
    {
        string typeName = effect.GetType().Name;
        switch (typeName)
        {
            case "Alpha":
                document.ApplyAlphaAdjustment(Math.Clamp(Get<float>(effect, "Value"), 0f, 1f) * 100f);
                break;
            case "Brightness":
                ApplyDiagonalMatrix(document, 1f, Get<float>(effect, "Value"), sdrWhiteNits);
                break;
            case "Contrast":
                ApplyDiagonalMatrix(document, Get<float>(effect, "Value"), 0f, sdrWhiteNits);
                break;
            case "Gamma":
                document.ApplyGammaAdjustment(
                    Math.Clamp(Get<float>(effect, "Value"), 0.1f, 5f),
                    sdrWhiteNits);
                break;
            case "Grayscale":
                document.ApplySaturationAdjustment(0f);
                ApplyDiagonalMatrix(document, Get<float>(effect, "Value"), 0f, sdrWhiteNits);
                break;
            case "Hue":
                ApplyHue(document, Get<float>(effect, "Angle"), sdrWhiteNits);
                break;
            case "Inverse":
                document.ApplyLinearColorMatrixAdjustment(
                    new float[]
                    {
                        -1f, 0f, 0f, 1f,
                        0f, -1f, 0f, 1f,
                        0f, 0f, -1f, 1f
                    },
                    sdrWhiteNits);
                break;
            case "Saturation":
                document.ApplySaturationAdjustment(Math.Clamp(Get<float>(effect, "Value"), 0f, 2f));
                break;
            case "Sepia":
                float sepia = Get<float>(effect, "Value");
                document.ApplyLinearColorMatrixAdjustment(
                    new float[]
                    {
                        0.393f * sepia, 0.769f * sepia, 0.189f * sepia, 0f,
                        0.349f * sepia, 0.686f * sepia, 0.168f * sepia, 0f,
                        0.272f * sepia, 0.534f * sepia, 0.131f * sepia, 0f
                    },
                    sdrWhiteNits);
                break;
            case "BlackWhite":
                document.ApplyThresholdAdjustment(0.5f, sdrWhiteNits);
                break;
            case "Blur":
                document.BoxBlurRectangle(FullBounds(document), Get<int>(effect, "Radius"));
                break;
            case "GaussianBlur":
                document.BlurRectangle(FullBounds(document), Get<int>(effect, "Radius") / 3f);
                break;
            case "Pixelate":
                document.PixelateRectangle(FullBounds(document), Get<int>(effect, "Size"));
                break;
            case "Crop":
                Padding crop = Get<Padding>(effect, "Margin");
                Replace(
                    ref document,
                    document.CropToLocalRectangle(new Rectangle(
                        crop.Left,
                        crop.Top,
                        document.MasterPixels.Width - crop.Horizontal,
                        document.MasterPixels.Height - crop.Vertical)));
                break;
            case "Flip":
                bool horizontally = Get<bool>(effect, "Horizontally");
                bool vertically = Get<bool>(effect, "Vertically");
                if (horizontally)
                {
                    Replace(ref document, document.FlipHorizontal());
                }
                if (vertically)
                {
                    Replace(ref document, document.FlipVertical());
                }
                break;
            case "Resize":
                ApplyResize(ref document, effect);
                break;
            case "Canvas":
                ApplyCanvas(ref document, effect, sdrWhiteNits);
                break;
            case "RoundedCorners":
                ApplyRoundedCorners(document, Get<int>(effect, "CornerRadius"));
                break;
            case "DrawText":
            case "DrawTextEx":
                ApplySdrOverlay(document, effect, sdrWhiteNits);
                break;
            default:
                throw new InvalidOperationException($"Unsupported HDR image effect '{typeName}'.");
        }
    }

    private static void ApplyResize(ref HdrImageDocument document, ImageEffect effect)
    {
        int requestedWidth = Get<int>(effect, "Width");
        int requestedHeight = Get<int>(effect, "Height");
        if (requestedWidth <= 0 && requestedHeight <= 0)
        {
            return;
        }

        int width = requestedWidth;
        int height = requestedHeight;
        if (width <= 0)
        {
            width = Math.Max(1, checked((int)Math.Round(
                document.MasterPixels.Width * (double)height / document.MasterPixels.Height)));
        }
        else if (height <= 0)
        {
            height = Math.Max(1, checked((int)Math.Round(
                document.MasterPixels.Height * (double)width / document.MasterPixels.Width)));
        }

        string mode = GetObject(effect, "Mode")?.ToString() ?? "ResizeAll";
        if ((mode == "ResizeIfBigger" && document.MasterPixels.Width <= width && document.MasterPixels.Height <= height) ||
            (mode == "ResizeIfSmaller" && document.MasterPixels.Width >= width && document.MasterPixels.Height >= height))
        {
            return;
        }

        Replace(ref document, document.ResizeCatmullRom(width, height));
    }

    private static void ApplyCanvas(
        ref HdrImageDocument document,
        ImageEffect effect,
        float sdrWhiteNits)
    {
        Padding margin = Get<Padding>(effect, "Margin");
        if (string.Equals(GetObject(effect, "MarginMode")?.ToString(), "PercentageOfCanvas", StringComparison.Ordinal))
        {
            margin = new Padding(
                checked((int)Math.Round(margin.Left / 100f * document.MasterPixels.Width)),
                checked((int)Math.Round(margin.Top / 100f * document.MasterPixels.Height)),
                checked((int)Math.Round(margin.Right / 100f * document.MasterPixels.Width)),
                checked((int)Math.Round(margin.Bottom / 100f * document.MasterPixels.Height)));
        }

        Color color = Get<Color>(effect, "Color");
        Replace(
            ref document,
            document.ResizeCanvas(
                margin.Top,
                margin.Right,
                margin.Bottom,
                margin.Left,
                color.R,
                color.G,
                color.B,
                color.A,
                sdrWhiteNits));
    }

    private static void ApplyRoundedCorners(HdrImageDocument document, int cornerRadius)
    {
        if (cornerRadius <= 0)
        {
            return;
        }

        Bitmap maskSource = new Bitmap(
            document.MasterPixels.Width,
            document.MasterPixels.Height,
            PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(maskSource))
        {
            graphics.Clear(Color.White);
        }

        using Bitmap roundedMask = ImageHelpers.RoundedCorners(maskSource, cornerRadius);
        document.ApplyAlphaMask(roundedMask);
    }

    private static void ApplySdrOverlay(
        HdrImageDocument document,
        ImageEffect effect,
        float sdrWhiteNits)
    {
        Bitmap transparent = new Bitmap(
            document.MasterPixels.Width,
            document.MasterPixels.Height,
            PixelFormat.Format32bppPArgb);
        Bitmap overlay = null;
        try
        {
            overlay = effect.Apply(transparent);
            transparent = null;
            if (overlay == null ||
                overlay.Width != document.MasterPixels.Width ||
                overlay.Height != document.MasterPixels.Height)
            {
                throw new InvalidOperationException(
                    $"Image effect '{effect.GetType().Name}' returned an invalid HDR overlay.");
            }

            document.CompositeSdrAnnotationOverlay(overlay, sdrWhiteNits);
        }
        finally
        {
            transparent?.Dispose();
            overlay?.Dispose();
        }
    }

    private static void ApplyDiagonalMatrix(
        HdrImageDocument document,
        float scale,
        float offset,
        float sdrWhiteNits) =>
        document.ApplyLinearColorMatrixAdjustment(
            new float[]
            {
                scale, 0f, 0f, offset,
                0f, scale, 0f, offset,
                0f, 0f, scale, offset
            },
            sdrWhiteNits);

    private static void ApplyHue(HdrImageDocument document, float degrees, float sdrWhiteNits)
    {
        float radians = degrees * MathF.PI / 180f;
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        document.ApplyLinearColorMatrixAdjustment(
            new float[]
            {
                0.213f + cosine * 0.787f - sine * 0.213f,
                0.715f - cosine * 0.715f - sine * 0.715f,
                0.072f - cosine * 0.072f + sine * 0.928f,
                0f,
                0.213f - cosine * 0.213f + sine * 0.143f,
                0.715f + cosine * 0.285f + sine * 0.140f,
                0.072f - cosine * 0.072f - sine * 0.283f,
                0f,
                0.213f - cosine * 0.213f - sine * 0.787f,
                0.715f - cosine * 0.715f + sine * 0.715f,
                0.072f + cosine * 0.928f + sine * 0.072f,
                0f
            },
            sdrWhiteNits);
    }

    private static Rectangle FullBounds(HdrImageDocument document) =>
        new(0, 0, document.MasterPixels.Width, document.MasterPixels.Height);

    private static void Replace(ref HdrImageDocument current, HdrImageDocument replacement)
    {
        current.Dispose();
        current = replacement;
    }

    private static T Get<T>(ImageEffect effect, string propertyName)
    {
        object value = GetObject(effect, propertyName) ??
            throw new InvalidOperationException(
                $"Image effect '{effect.GetType().Name}' did not expose '{propertyName}'.");
        return (T)value;
    }

    private static object GetObject(ImageEffect effect, string propertyName) =>
        effect.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(effect);

    private static bool IsFinite(float value) => float.IsFinite(value);
}
