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

using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Core.Annotations;
using ShareX.HelpersLib;
using ShareX.ScreenCaptureLib;
using SkiaSharp;
using System;
using System.Drawing;
using System.Linq;

namespace ShareX
{
    internal enum HdrEditorBridgeResult
    {
        Cancelled,
        ContinueWithoutApplying,
        Applied,
        RequiresHdrOperationSupport
    }

    /// <summary>
    /// Connects a retained HDR document to the existing SDR-preview editor without
    /// allowing its flattened preview to replace the FP16 master.
    /// </summary>
    internal static class HdrEditorBridge
    {
        public static HdrEditorBridgeResult ShowEditor(
            HdrImageDocument source,
            HdrCaptureSettings previewSettings,
            ImageEditorOptions editorOptions,
            float annotationWhiteNits,
            out HdrImageDocument editedDocument,
            EditorOverlayEvents editorEvents = null,
            bool useContinueWorkflow = true)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(previewSettings);
            ArgumentNullException.ThrowIfNull(editorOptions);
            editedDocument = null;

            using Bitmap preview = source.CreateSdrPreview(previewSettings);
            SKBitmap skPreview = TaskHelpers.GdiBitmapToSkBitmap(preview);
            DebugHelper.WriteLine(
                $"HDR editor preview | enabled={previewSettings.EnableEditorHdrPreview} " +
                $"source={source.MasterPixels.Width}x{source.MasterPixels.Height} format=RGBA16F");
            EditorHdrPreviewSource hdrPreview = previewSettings.EnableEditorHdrPreview
                ? EditorHdrPreviewSource.CopyFrom(
                    source.MasterPixels.PixelBytes.Span,
                    source.MasterPixels.RowBytes,
                    source.MasterPixels.Width,
                    source.MasterPixels.Height)
                : null;

            using EditorOverlayDialogResult editorResult =
                AvaloniaIntegration.ShowEditorOverlayDialog(
                    skPreview,
                    editorOptions,
                    editorEvents,
                    hdrPreview,
                    showTaskButtons: true,
                    useContinueWorkflow);

            if (editorResult.Outcome == EditorOverlayDialogOutcome.Cancelled)
            {
                return HdrEditorBridgeResult.Cancelled;
            }

            if (editorResult.Outcome == EditorOverlayDialogOutcome.ContinueWithoutApplying)
            {
                return HdrEditorBridgeResult.ContinueWithoutApplying;
            }

            return ApplyExport(source, editorResult.Export, annotationWhiteNits, out editedDocument);
        }

        internal static HdrEditorBridgeResult ApplyExport(
            HdrImageDocument source,
            EditorOverlayExport export,
            float annotationWhiteNits,
            out HdrImageDocument editedDocument)
        {
            ArgumentNullException.ThrowIfNull(source);
            editedDocument = null;

            if (export == null || !export.CanReplayOverHdr)
            {
                return HdrEditorBridgeResult.RequiresHdrOperationSupport;
            }

            foreach (EditorOverlayOperation operation in export.Operations)
            {
                if (!CanReplayOperation(operation))
                {
                    return HdrEditorBridgeResult.RequiresHdrOperationSupport;
                }
            }

            HdrImageDocument candidate = ReplaySourceOperations(source, export, annotationWhiteNits);

            try
            {
                if (export.Width != candidate.MasterPixels.Width ||
                    export.Height != candidate.MasterPixels.Height)
                {
                    candidate.Dispose();
                    return HdrEditorBridgeResult.RequiresHdrOperationSupport;
                }

                foreach (EditorOverlayOperation operation in export.Operations)
                {
                    switch (operation.Kind)
                    {
                        case EditorOverlayOperationKind.AnnotationOverlay:
                            CompositeOverlay(candidate, operation.Overlay!, annotationWhiteNits);
                            break;
                        case EditorOverlayOperationKind.SourceEffect:
                            ApplySourceEffects(candidate, operation, annotationWhiteNits);
                            break;
                        default:
                            throw new InvalidOperationException("The editor returned an unsupported HDR operation.");
                    }
                }

                editedDocument = candidate;
                return HdrEditorBridgeResult.Applied;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        private static HdrImageDocument ReplaySourceOperations(
            HdrImageDocument source,
            EditorOverlayExport export,
            float annotationWhiteNits)
        {
            HdrImageDocument candidate = source.Clone();

            try
            {
                if (export.SourceOperation.Kind == EditorSourceOperationKind.Crop)
                {
                    candidate = ReplaceWithOperationResult(
                        candidate,
                        document => document.CropToLocalRectangle(ToRectangle(export.SourceOperation)));
                }

                foreach (EditorSourceOperation operation in export.SourceOperations)
                {
                    candidate = operation.Kind switch
                    {
                        EditorSourceOperationKind.Crop => ReplaceWithOperationResult(
                            candidate,
                            document => document.CropToLocalRectangle(ToRectangle(operation))),
                        EditorSourceOperationKind.Rotate90Clockwise => ReplaceWithOperationResult(
                            candidate,
                            document => document.Rotate90Clockwise()),
                        EditorSourceOperationKind.Rotate90CounterClockwise => ReplaceWithOperationResult(
                            candidate,
                            document => document.Rotate90CounterClockwise()),
                        EditorSourceOperationKind.Rotate180 => ReplaceWithOperationResult(
                            candidate,
                            document => document.Rotate180()),
                        EditorSourceOperationKind.ResizeCanvas => ReplaceWithOperationResult(
                            candidate,
                            document => document.ResizeCanvas(
                                operation.Y,
                                operation.Width,
                                operation.Height,
                                operation.X,
                                operation.Red,
                                operation.Green,
                                operation.Blue,
                                operation.Alpha,
                                annotationWhiteNits)),
                        EditorSourceOperationKind.CutOutVertical => ReplaceWithOperationResult(
                            candidate,
                            document => document.CutOutVertical(operation.X, operation.Width)),
                        EditorSourceOperationKind.CutOutHorizontal => ReplaceWithOperationResult(
                            candidate,
                            document => document.CutOutHorizontal(operation.Y, operation.Height)),
                        EditorSourceOperationKind.Resize => ReplaceWithOperationResult(
                            candidate,
                            document => document.ResizeCatmullRom(operation.Width, operation.Height)),
                        EditorSourceOperationKind.FlipHorizontal => ReplaceWithOperationResult(
                            candidate,
                            document => document.FlipHorizontal()),
                        EditorSourceOperationKind.FlipVertical => ReplaceWithOperationResult(
                            candidate,
                            document => document.FlipVertical()),
                        EditorSourceOperationKind.ImageEffect => ReplayImageEffect(
                            candidate, operation.ImageEffectDescriptor, annotationWhiteNits),
                        EditorSourceOperationKind.None => candidate,
                        _ => throw new InvalidOperationException(
                            "The editor returned an unsupported HDR source operation.")
                    };
                }

                return candidate;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        private static HdrImageDocument ReplayImageEffect(
            HdrImageDocument document,
            EditorImageEffectDescriptor descriptor,
            float annotationWhiteNits)
        {
            if (descriptor == null ||
                descriptor.SchemaVersion != EditorImageEffectDescriptor.CurrentSchemaVersion ||
                descriptor.ReplayDeterminism != EditorImageEffectReplayDeterminism.Deterministic ||
                EditorImageEffectHdrCapabilities.GetCapability(descriptor.EffectId) != EditorImageEffectHdrCapability.NativeFp16)
            {
                throw new InvalidOperationException("The editor returned an unsupported HDR image effect descriptor.");
            }

            switch (descriptor.EffectId.ToLowerInvariant())
            {
                case "alpha":
                    float alpha = GetRequiredFiniteNumber(descriptor, "amount");
                    if (alpha < 0f || alpha > 100f)
                    {
                        throw new InvalidOperationException("The Alpha effect amount is outside its supported range.");
                    }

                    document.ApplyAlphaAdjustment(alpha);
                    return document;
                case "black_and_white":
                    document.ApplyThresholdAdjustment(0.5f, annotationWhiteNits);
                    return document;
                case "blur":
                    document.BlurRectangle(
                        new Rectangle(0, 0, document.MasterPixels.Width, document.MasterPixels.Height),
                        GetRequiredFiniteNumberInRange(
                            descriptor,
                            "radius",
                            1f,
                            HdrImageDocument.MaximumSupportedBlurSigma));
                    return document;
                case "brightness":
                    float brightness = GetRequiredFiniteNumberInRange(descriptor, "amount", -100f, 100f) / 100f;
                    document.ApplyLinearColorMatrixAdjustment(
                        new float[]
                        {
                            1f, 0f, 0f, brightness,
                            0f, 1f, 0f, brightness,
                            0f, 0f, 1f, brightness
                        },
                        annotationWhiteNits);
                    return document;
                case "contrast":
                    float contrastAmount = GetRequiredFiniteNumberInRange(descriptor, "amount", -100f, 100f);
                    float contrastScale = MathF.Pow((100f + contrastAmount) / 100f, 2f);
                    float contrastShift = 0.5f * (1f - contrastScale);
                    document.ApplyLinearColorMatrixAdjustment(
                        new float[]
                        {
                            contrastScale, 0f, 0f, contrastShift,
                            0f, contrastScale, 0f, contrastShift,
                            0f, 0f, contrastScale, contrastShift
                        },
                        annotationWhiteNits);
                    return document;
                case "exposure":
                    document.ApplyExposureAdjustment(GetRequiredFiniteNumber(descriptor, "amount"));
                    return document;
                case "gamma":
                    document.ApplyGammaAdjustment(
                        GetRequiredFiniteNumberInRange(descriptor, "amount", 0.1f, 5f),
                        annotationWhiteNits);
                    return document;
                case "gaussian_blur":
                    float radius = GetRequiredFiniteNumberInRange(
                        descriptor,
                        "radius",
                        1f,
                        HdrImageDocument.MaximumSupportedBlurSigma * 3f);
                    document.BlurRectangle(
                        new Rectangle(0, 0, document.MasterPixels.Width, document.MasterPixels.Height),
                        radius / 3f);
                    return document;
                case "grayscale":
                    float strength = GetRequiredFiniteNumber(descriptor, "strength");
                    if (strength < 0f || strength > 100f)
                    {
                        throw new InvalidOperationException("The Grayscale effect strength is outside its supported range.");
                    }

                    document.ApplySaturationAdjustment(1f - strength / 100f);
                    return document;
                case "hue":
                    float hue = GetRequiredFiniteNumberInRange(descriptor, "amount", -180f, 180f);
                    float radians = hue * MathF.PI / 180f;
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
                        annotationWhiteNits);
                    return document;
                case "invert":
                    document.ApplyLinearColorMatrixAdjustment(
                        new float[]
                        {
                            -1f, 0f, 0f, 1f,
                            0f, -1f, 0f, 1f,
                            0f, 0f, -1f, 1f
                        },
                        annotationWhiteNits);
                    return document;
                case "pixelate":
                    int pixelSize = checked((int)MathF.Round(
                        GetRequiredFiniteNumberInRange(descriptor, "size", 1f, 200f)));
                    document.PixelateRectangle(
                        new Rectangle(0, 0, document.MasterPixels.Width, document.MasterPixels.Height),
                        pixelSize);
                    return document;
                case "saturation":
                    float saturationAmount = GetRequiredFiniteNumber(descriptor, "amount");
                    if (saturationAmount < -100f || saturationAmount > 100f)
                    {
                        throw new InvalidOperationException("The Saturation effect amount is outside its supported range.");
                    }

                    document.ApplySaturationAdjustment(1f + saturationAmount / 100f);
                    return document;
                case "sepia":
                    float sepiaStrength = GetRequiredFiniteNumberInRange(descriptor, "strength", 0f, 100f) / 100f;
                    float inverseSepiaStrength = 1f - sepiaStrength;
                    document.ApplyLinearColorMatrixAdjustment(
                        new float[]
                        {
                            inverseSepiaStrength + 0.393f * sepiaStrength, 0.769f * sepiaStrength, 0.189f * sepiaStrength, 0f,
                            0.349f * sepiaStrength, inverseSepiaStrength + 0.686f * sepiaStrength, 0.168f * sepiaStrength, 0f,
                            0.272f * sepiaStrength, 0.534f * sepiaStrength, inverseSepiaStrength + 0.131f * sepiaStrength, 0f
                        },
                        annotationWhiteNits);
                    return document;
                case "temperature_tint":
                    float temperature = GetRequiredFiniteNumberInRange(descriptor, "temperature", -100f, 100f);
                    float tint = GetRequiredFiniteNumberInRange(descriptor, "tint", -100f, 100f);
                    float temperatureDelta = temperature / 100f * (64f / 255f);
                    float tintDelta = tint / 100f * (64f / 255f);
                    document.ApplyLinearColorMatrixAdjustment(
                        new float[]
                        {
                            1f, 0f, 0f, temperatureDelta - tintDelta * 0.25f,
                            0f, 1f, 0f, tintDelta,
                            0f, 0f, 1f, -temperatureDelta - tintDelta * 0.25f
                        },
                        annotationWhiteNits);
                    return document;
                case "threshold":
                    document.ApplyThresholdAdjustment(
                        GetRequiredFiniteNumberInRange(descriptor, "value", 0f, 255f) / 255f,
                        annotationWhiteNits);
                    return document;
                case "vibrance":
                    document.ApplyVibranceAdjustment(
                        GetRequiredFiniteNumberInRange(descriptor, "amount", -100f, 100f) / 100f,
                        annotationWhiteNits);
                    return document;
                default:
                    throw new InvalidOperationException($"HDR replay is not implemented for image effect '{descriptor.EffectId}'.");
            }
        }

        private static float GetRequiredFiniteNumber(
            EditorImageEffectDescriptor descriptor,
            string key)
        {
            if (!descriptor.TryGetNumber(key, out double value) ||
                !double.IsFinite(value) ||
                value < -float.MaxValue ||
                value > float.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Image effect '{descriptor.EffectId}' has an invalid '{key}' parameter.");
            }

            return (float)value;
        }

        private static float GetRequiredFiniteNumberInRange(
            EditorImageEffectDescriptor descriptor,
            string key,
            float minimum,
            float maximum)
        {
            float value = GetRequiredFiniteNumber(descriptor, key);
            if (value < minimum || value > maximum)
            {
                throw new InvalidOperationException(
                    $"The '{key}' parameter for HDR image effect '{descriptor.EffectId}' is outside its supported range.");
            }

            return value;
        }

        private static Rectangle ToRectangle(EditorSourceOperation operation) =>
            new(operation.X, operation.Y, operation.Width, operation.Height);

        private static HdrImageDocument ReplaceWithOperationResult(
            HdrImageDocument current,
            Func<HdrImageDocument, HdrImageDocument> operation)
        {
            HdrImageDocument replacement = operation(current);
            current.Dispose();
            return replacement;
        }

        private static bool CanReplayOperation(EditorOverlayOperation operation)
        {
            if (operation.Kind == EditorOverlayOperationKind.AnnotationOverlay)
            {
                return operation.Overlay != null;
            }

            if (operation.Kind != EditorOverlayOperationKind.SourceEffect ||
                operation.Annotations.Count == 0 ||
                operation.CoverageMask == null)
            {
                return false;
            }

            bool isSpotlightGroup = operation.Annotations[0] is SpotlightAnnotation;
            if ((isSpotlightGroup && operation.Annotations.Any(annotation => annotation is not SpotlightAnnotation)) ||
                (!isSpotlightGroup && operation.Annotations.Count != 1))
            {
                return false;
            }

            foreach (Annotation annotation in operation.Annotations)
            {
                if (annotation is BlurAnnotation blur)
                {
                    if (!IsMaskedGlobalEffect(blur) ||
                        !float.IsFinite(blur.Amount) ||
                        blur.Amount <= 0f ||
                        blur.Amount > HdrImageDocument.MaximumSupportedBlurSigma)
                    {
                        return false;
                    }
                }
                else if (annotation is PixelateAnnotation pixelate)
                {
                    if (!IsMaskedGlobalEffect(pixelate) ||
                        !float.IsFinite(pixelate.Amount) ||
                        pixelate.Amount < 1f)
                    {
                        return false;
                    }
                }
                else if (annotation is MagnifyAnnotation magnify)
                {
                    if (!IsMaskedGlobalEffect(magnify) ||
                        !float.IsFinite(magnify.Amount) ||
                        magnify.Amount < 1f ||
                        magnify.Amount > HdrImageDocument.MaximumSupportedMagnification)
                    {
                        return false;
                    }
                }
                else if (annotation is HighlightAnnotation highlight)
                {
                    if (!IsMaskedGlobalEffect(highlight) ||
                        !SKColor.TryParse(highlight.FillColor ?? "#FFFF00", out _))
                    {
                        return false;
                    }
                }
                else if (annotation is SpotlightAnnotation spotlight)
                {
                    if (!float.IsFinite(spotlight.BlurAmount) ||
                        spotlight.BlurAmount < 0f ||
                        spotlight.BlurAmount > HdrImageDocument.MaximumSupportedBlurSigma ||
                        !HasFiniteIntegerBounds(spotlight.GetBounds()))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasFiniteIntegerBounds(SKRect bounds) =>
            float.IsFinite(bounds.Left) &&
            float.IsFinite(bounds.Top) &&
            float.IsFinite(bounds.Right) &&
            float.IsFinite(bounds.Bottom) &&
            bounds.Left >= int.MinValue &&
            bounds.Top >= int.MinValue &&
            bounds.Right <= int.MaxValue &&
            bounds.Bottom <= int.MaxValue &&
            bounds.Width >= 1f &&
            bounds.Height >= 1f;

        private static bool IsMaskedGlobalEffect(BaseEffectAnnotation effect) =>
            // The editor exports the final antialiased coverage mask for both
            // geometric and freehand effects. HDR replay must use that mask as
            // the authority instead of trying to reconstruct the stroke from
            // the annotation's coarse bounds.
            float.IsFinite(effect.RotationAngle) &&
            HasFiniteIntegerBounds(effect.GetBounds());

        private static void ApplySourceEffects(
            HdrImageDocument document,
            EditorOverlayOperation operation,
            float annotationWhiteNits)
        {
            EditorEffectCoverageMask mask = operation.CoverageMask ??
                throw new InvalidOperationException("The editor returned a source effect without coverage.");
            if (mask.IsEmpty)
            {
                return;
            }

            if (operation.Annotations[0] is SpotlightAnnotation)
            {
                byte darkenOpacity = 0;
                float blurAmount = 0f;

                foreach (Annotation annotation in operation.Annotations)
                {
                    var spotlight = (SpotlightAnnotation)annotation;
                    darkenOpacity = Math.Max(darkenOpacity, spotlight.DarkenOpacity);
                    blurAmount = Math.Max(blurAmount, spotlight.BlurAmount);
                }

                using HdrRgba16FloatBuffer originalPixels = document.MasterPixels.Clone();
                document.ApplySpotlightEverywhere(darkenOpacity, blurAmount);
                document.CompositeCurrentEffectThroughCoverage(
                    originalPixels,
                    new Rectangle(0, 0, document.MasterPixels.Width, document.MasterPixels.Height),
                    mask.X,
                    mask.Y,
                    mask.Width,
                    mask.Height,
                    mask.AlphaBytes,
                    compositeCurrentOverOriginal: false);
                return;
            }

            if (operation.Annotations.Count != 1)
            {
                throw new InvalidOperationException("Non-Spotlight source effects must be exported one at a time.");
            }

            foreach (Annotation annotation in operation.Annotations)
            {
                SKRect bounds = annotation.GetBounds();
                int left = (int)bounds.Left;
                int top = (int)bounds.Top;
                int right = (int)bounds.Right;
                int bottom = (int)bounds.Bottom;

                Rectangle rectangle = Rectangle.FromLTRB(left, top, right, bottom);
                Rectangle effectRectangle = new Rectangle(
                    mask.X,
                    mask.Y,
                    mask.Width,
                    mask.Height);
                using HdrRgba16FloatBuffer originalPixels = document.MasterPixels.Clone();
                switch (annotation)
                {
                    case PixelateAnnotation pixelate:
                        document.PixelateRectangle(
                            effectRectangle,
                            Math.Max(1, (int)pixelate.Amount));
                        break;
                    case MagnifyAnnotation magnify:
                        if (Math.Abs(magnify.RotationAngle) <= 0.001f)
                        {
                            document.MagnifyRectangle(
                                rectangle,
                                magnify.Amount);
                            effectRectangle = rectangle;
                        }
                        else
                        {
                            document.MagnifyAroundPoint(
                                originalPixels,
                                effectRectangle,
                                bounds.MidX,
                                bounds.MidY,
                                magnify.Amount);
                        }
                        break;
                    case HighlightAnnotation highlight:
                        if (!SKColor.TryParse(highlight.FillColor ?? "#FFFF00", out SKColor color))
                        {
                            throw new InvalidOperationException("The editor returned an invalid highlight color.");
                        }

                        document.ApplyHighlightRectangle(
                            effectRectangle,
                            color.Red,
                            color.Green,
                            color.Blue,
                            annotationWhiteNits);
                        break;
                    case BlurAnnotation blur:
                        document.BlurRectangle(
                            effectRectangle,
                            blur.Amount);
                        break;
                    default:
                        throw new InvalidOperationException("The editor returned an unsupported HDR source effect.");
                }

                document.CompositeCurrentEffectThroughCoverage(
                    originalPixels,
                    effectRectangle,
                    mask.X,
                    mask.Y,
                    mask.Width,
                    mask.Height,
                    mask.AlphaBytes,
                    compositeCurrentOverOriginal: true);
            }
        }

        private static void CompositeOverlay(
            HdrImageDocument document,
            SKBitmap overlay,
            float annotationWhiteNits)
        {
            SKImageInfo info = overlay.Info;
            if (info.ColorType != SKColorType.Bgra8888 ||
                (info.AlphaType != SKAlphaType.Premul && info.AlphaType != SKAlphaType.Opaque))
            {
                throw new InvalidOperationException("The editor returned an unexpected annotation overlay format.");
            }

            SKPixmap pixels = overlay.PeekPixels() ??
                throw new InvalidOperationException("The editor annotation overlay has no readable pixels.");
            document.CompositeSdrAnnotationOverlay(
                pixels.GetPixelSpan<byte>(),
                overlay.RowBytes,
                annotationWhiteNits);
        }
    }
}
