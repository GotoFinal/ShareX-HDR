#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.ImageEditor.Presentation.Rendering;
using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ShareX
{
    public class CaptureRegion : CaptureBase
    {
        protected static RegionCaptureType lastRegionCaptureType = RegionCaptureType.Default;

        private bool rectangleSelectionOnly;
        private Rectangle selectedRectangle;
        private WindowInfo selectedWindowInfo;
        private RegionCaptureOptions regionCaptureOptionsOverride;

        public RegionCaptureType RegionCaptureType { get; protected set; }

        public CaptureRegion()
        {
        }

        public CaptureRegion(RegionCaptureType regionCaptureType)
        {
            RegionCaptureType = regionCaptureType;
        }

        public static bool GetScreenRecordingRectangle(TaskSettings taskSettings,
            out Rectangle rectangle, out WindowInfo windowInfo)
        {
            var capture = new CaptureRegion
            {
                rectangleSelectionOnly = true,
                regionCaptureOptionsOverride = RegionCaptureTasks.CreateRectangleRegionOptions(
                    taskSettings.CaptureSettings.SurfaceOptions)
            };

            RegionCaptureOptions sourceOptions = taskSettings.CaptureSettings.SurfaceOptions;
            capture.regionCaptureOptionsOverride.UseDimming = sourceOptions.UseDimming;
            capture.regionCaptureOptionsOverride.BackgroundDimStrength = sourceOptions.BackgroundDimStrength;

            capture.ExecuteRegionCapture(taskSettings);
            rectangle = capture.selectedRectangle;
            windowInfo = capture.selectedWindowInfo;
            return !rectangle.IsEmpty;
        }

        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            switch (RegionCaptureType)
            {
                default:
                case RegionCaptureType.Default:
                    return ExecuteRegionCapture(taskSettings);
                case RegionCaptureType.Light:
                    return ExecuteRegionCaptureLight(taskSettings);
                case RegionCaptureType.Transparent:
                    return ExecuteRegionCaptureTransparent(taskSettings);
            }
        }

        protected TaskMetadata ExecuteRegionCapture(TaskSettings taskSettings)
        {
            RegionCaptureMode mode;

            if (rectangleSelectionOnly || taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation)
            {
                mode = RegionCaptureMode.Default;
            }
            else
            {
                mode = RegionCaptureMode.Annotation;
            }

            Bitmap canvas = null;
            Screenshot screenshot = rectangleSelectionOnly
                ? TaskHelpers.GetScreenshotWithoutCursor(taskSettings)
                : TaskHelpers.GetScreenshot(taskSettings);
            HdrCaptureSettings hdrSettings =
                taskSettings.CaptureSettings.HdrSettings ?? new HdrCaptureSettings();
            HdrImageDocument hdrCanvasDocument = null;
            var hdrRegionPreviews = new List<(WindowsHdrPreviewPresenter Presenter, Rectangle Bounds)>();
            WindowsHdrRegionDimWindow hdrRegionDim = null;
            WindowsHdrRegionInputWindow hdrRegionInput = null;
            Rectangle captureBounds = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode
                ? CaptureHelpers.GetActiveScreenBounds()
                : CaptureHelpers.GetScreenBounds();
            bool deferredHdrSurface = false;

            if (screenshot.UseHDRSupport && hdrSettings.EnableNativeHdrRegionSelectorPreview)
            {
                try
                {
                    deferredHdrSurface = screenshot.TryCaptureHdr(captureBounds, out hdrCanvasDocument);
                    DebugHelper.WriteLine(
                        deferredHdrSurface
                            ? $"HDR region selector capture | retained size={captureBounds.Width}x{captureBounds.Height} preview=deferred"
                            : "HDR region selector capture | fallback=SDR reason=HDR-capture-unavailable");
                }
                catch (Exception ex)
                {
                    hdrCanvasDocument?.Dispose();
                    hdrCanvasDocument = null;
                    DebugHelper.WriteLine(
                        "HDR region selector capture | fallback=SDR reason=HDR-capture-failed");
                    DebugHelper.WriteException(ex);
                }
            }

            if (!deferredHdrSurface)
            {
                if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
                {
                    canvas = screenshot.CaptureActiveMonitor(out hdrCanvasDocument);
                }
                else
                {
                    canvas = screenshot.CaptureFullscreen(out hdrCanvasDocument);
                }
            }

            try
            {
                using (RegionCaptureForm form = new RegionCaptureForm(mode,
                    regionCaptureOptionsOverride ?? taskSettings.CaptureSettingsReference.SurfaceOptions,
                    canvas,
                    screenshot,
                    allowEmptyCanvas: deferredHdrSurface))
                {
                    if (deferredHdrSurface)
                    {
                        form.SetDeferredCanvasFactory(() => screenshot.CreateSdrPreview(hdrCanvasDocument));
                        form.SetHdrMagnifierFactory(screenRectangle =>
                        {
                            using HdrImageDocument sample =
                                hdrCanvasDocument.CropToScreenRectangle(screenRectangle);
                            return sample.CreateSdrPreview(hdrSettings);
                        });
                    }

                    if (hdrCanvasDocument != null && hdrSettings.EnableNativeHdrRegionSelectorPreview)
                    {
                        try
                        {
                            HdrRgba16FloatBuffer pixels = hdrCanvasDocument.MasterPixels;
                            DebugHelper.WriteLine(
                                $"HDR region selector preview | attempt size={pixels.Width}x{pixels.Height} " +
                                $"bounds={hdrCanvasDocument.RequestedBounds} format=RGBA16F");
                            HdrCaptureSourceSegment[] displaySegments = hdrCanvasDocument.SourceSegments
                                .Where(segment => segment.DestinationRectangle.Width > 0 &&
                                    segment.DestinationRectangle.Height > 0)
                                .GroupBy(segment => segment.DisplayDeviceName ?? string.Empty)
                                .Select(group => group.OrderByDescending(segment =>
                                    (long)segment.DestinationRectangle.Width * segment.DestinationRectangle.Height).First())
                                .ToArray();

                            foreach (HdrCaptureSourceSegment segment in displaySegments)
                            {
                                Rectangle localBounds = Rectangle.Intersect(
                                    segment.DestinationRectangle,
                                    new Rectangle(0, 0, pixels.Width, pixels.Height));
                                if (localBounds.Width <= 0 || localBounds.Height <= 0)
                                {
                                    continue;
                                }

                                Rectangle screenBounds = localBounds;
                                screenBounds.Offset(hdrCanvasDocument.RequestedBounds.Location);
                                if (segment.WasHdrActive)
                                {
                                    int pixelOffset = checked(
                                        localBounds.Y * pixels.RowBytes +
                                        localBounds.X * HdrRgba16FloatBuffer.BytesPerPixel);
                                    var presenter = new WindowsHdrPreviewPresenter(
                                        pixels.PixelBytes.Span.Slice(pixelOffset),
                                        pixels.RowBytes,
                                        localBounds.Width,
                                        localBounds.Height,
                                        requireHdrOutput: true);
                                    hdrRegionPreviews.Add((presenter, screenBounds));
                                }
                                else
                                {
                                    using HdrImageDocument sdrDisplay =
                                        hdrCanvasDocument.CropToScreenRectangle(screenBounds);
                                    Bitmap sdrBackground = screenshot.CreateSdrPreview(sdrDisplay);
                                    form.AddNativeHdrSdrBackground(sdrBackground, screenBounds);
                                }
                            }

                            if (hdrRegionPreviews.Count == 0)
                            {
                                throw new NotSupportedException(
                                    "No HDR-active display segment was available for native presentation.");
                            }

                            hdrRegionInput = new WindowsHdrRegionInputWindow();
                            if (form.Options.BackgroundDimStrength > 0)
                            {
                                try
                                {
                                    hdrRegionDim = new WindowsHdrRegionDimWindow(
                                        form.Options.BackgroundDimStrength);
                                    form.NativeHdrDimRegionChanged += region =>
                                    {
                                        try
                                        {
                                            hdrRegionDim?.UpdateRegion(region);
                                        }
                                        catch (Exception ex)
                                        {
                                            hdrRegionDim?.Dispose();
                                            hdrRegionDim = null;
                                            DebugHelper.WriteLine(
                                                "HDR region selector dim | disabled reason=region-update-failed");
                                            DebugHelper.WriteException(ex);
                                        }
                                    };
                                }
                                catch (Exception ex)
                                {
                                    hdrRegionDim?.Dispose();
                                    hdrRegionDim = null;
                                    DebugHelper.WriteLine(
                                        "HDR region selector dim | disabled reason=initialization-failed");
                                    DebugHelper.WriteException(ex);
                                }
                            }

                            form.EnableNativeHdrSelectionPreview();
                            form.Shown += (_, _) =>
                            {
                                try
                                {
                                    foreach ((WindowsHdrPreviewPresenter presenter, Rectangle bounds) in hdrRegionPreviews)
                                    {
                                        presenter.UpdateLayout(bounds, bounds, form.Handle);
                                        DebugHelper.WriteLine(
                                            $"HDR region selector presenter | display={presenter.OutputDeviceName} " +
                                            $"adapter={presenter.AdapterDescription} " +
                                            $"outputColorSpace={presenter.OutputColorSpace} " +
                                            $"present=0x{presenter.LastPresentResultCode:X8}");
                                    }
                                    if (hdrRegionDim != null)
                                    {
                                        try
                                        {
                                            hdrRegionDim.Show(hdrCanvasDocument.RequestedBounds, form.Handle);
                                            DebugHelper.WriteLine(
                                                $"HDR region selector dim | enabled configuredStrength=" +
                                                $"{form.Options.BackgroundDimStrength} effectiveStrength=" +
                                                $"{hdrRegionDim.EffectiveDimStrength}");
                                        }
                                        catch (Exception ex)
                                        {
                                            hdrRegionDim.Dispose();
                                            hdrRegionDim = null;
                                            DebugHelper.WriteLine(
                                                "HDR region selector dim | disabled reason=presentation-failed");
                                            DebugHelper.WriteException(ex);
                                        }
                                    }
                                    hdrRegionInput?.Show(hdrCanvasDocument.RequestedBounds, form.Handle);
                                    DebugHelper.WriteLine(
                                        "HDR region selector preview | enabled colorSpace=scRGB mode=full-surface");
                                }
                                catch (Exception ex)
                                {
                                    form.DisableNativeHdrSelectionPreview();
                                    hdrRegionDim?.Dispose();
                                    hdrRegionDim = null;
                                    hdrRegionInput?.Dispose();
                                    hdrRegionInput = null;
                                    DisposeHdrRegionPreviews(hdrRegionPreviews);
                                    _ = form.Canvas;
                                    DebugHelper.WriteLine(
                                        "HDR region selector preview | fallback=SDR reason=presentation-failed");
                                    DebugHelper.WriteException(ex);
                                }
                            };
                            form.FormClosed += (_, _) =>
                            {
                                hdrRegionDim?.Hide();
                                hdrRegionInput?.Hide();
                                foreach ((WindowsHdrPreviewPresenter presenter, _) in hdrRegionPreviews)
                                {
                                    presenter.Hide();
                                }
                            };
                        }
                        catch (Exception ex)
                        {
                            form.DisableNativeHdrSelectionPreview();
                            hdrRegionDim?.Dispose();
                            hdrRegionDim = null;
                            hdrRegionInput?.Dispose();
                            hdrRegionInput = null;
                            DisposeHdrRegionPreviews(hdrRegionPreviews);
                            _ = form.Canvas;
                            DebugHelper.WriteLine(
                                "HDR region selector preview | fallback=SDR reason=initialization-failed");
                            DebugHelper.WriteException(ex);
                        }
                    }

                    form.ShowDialog();

                    Rectangle selectedRectangle = form.GetSelectedRectangle();
                    if (rectangleSelectionOnly)
                    {
                        this.selectedRectangle = selectedRectangle;
                        selectedWindowInfo = form.GetWindowInfo();
                        DebugHelper.WriteLine(
                            $"Screen recording region selector | result={form.Result} " +
                            $"quickCrop={form.Options.QuickCrop} selected={selectedRectangle}");
                        return null;
                    }

                    HdrImageDocument resultHdrDocument = null;
                    Bitmap result = null;

                    if (deferredHdrSurface && form.CanCreateOpaqueHdrResult &&
                        form.CanExportHdrDrawingOverlay && !selectedRectangle.IsEmpty)
                    {
                        resultHdrDocument = CreateOpaqueHdrRegionDocument(
                            form,
                            selectedRectangle,
                            hdrCanvasDocument);
                        if (resultHdrDocument != null)
                        {
                            try
                            {
                                result = screenshot.CreateSdrPreview(resultHdrDocument);
                                DebugHelper.WriteLine(
                                    $"HDR region result | SDR derivative=selected-region size={result.Size}");
                            }
                            catch
                            {
                                resultHdrDocument.Dispose();
                                resultHdrDocument = null;
                                throw;
                            }
                        }
                    }

                    if (result == null)
                    {
                        result = form.GetResultImage();
                    }

                    if (result != null)
                    {
                        TaskMetadata metadata = new TaskMetadata(result, resultHdrDocument);

                        if (form.IsImageModified)
                        {
                            AllowAnnotation = false;
                        }

                        if (form.Result == RegionResult.Region)
                        {
                            WindowInfo windowInfo = form.GetWindowInfo();
                            metadata.UpdateInfo(windowInfo);
                        }

                        if (metadata.HdrImageDocument == null)
                        {
                            metadata.HdrImageDocument = CreateHdrRegionDocument(
                                form,
                                result,
                                selectedRectangle,
                                hdrCanvasDocument);
                        }

                        lastRegionCaptureType = RegionCaptureType.Default;
                        return metadata;
                    }
                }
            }
            finally
            {
                hdrRegionDim?.Dispose();
                hdrRegionInput?.Dispose();
                DisposeHdrRegionPreviews(hdrRegionPreviews);
                hdrCanvasDocument?.Dispose();
            }

            return null;
        }

        protected TaskMetadata ExecuteRegionCaptureLight(TaskSettings taskSettings)
        {
            Bitmap canvas;
            HdrImageDocument hdrCanvasDocument;
            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);

            if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
            {
                canvas = screenshot.CaptureActiveMonitor(out hdrCanvasDocument);
            }
            else
            {
                canvas = screenshot.CaptureFullscreen(out hdrCanvasDocument);
            }

            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            try
            {
                using (RegionCaptureLightForm rectangleLight = new RegionCaptureLightForm(canvas, activeMonitorMode))
                {
                    if (rectangleLight.ShowDialog() == DialogResult.OK)
                    {
                        Bitmap result = rectangleLight.GetAreaImage();

                        if (result != null)
                        {
                            lastRegionCaptureType = RegionCaptureType.Light;
                            TaskMetadata metadata = new TaskMetadata(result);

                            if (hdrCanvasDocument != null)
                            {
                                metadata.HdrImageDocument = hdrCanvasDocument.CropToScreenRectangle(
                                    rectangleLight.ScreenSelectionRectangle);
                            }

                            return metadata;
                        }
                    }
                }
            }
            finally
            {
                hdrCanvasDocument?.Dispose();
            }

            return null;
        }

        protected TaskMetadata ExecuteRegionCaptureTransparent(TaskSettings taskSettings)
        {
            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            using (RegionCaptureLightForm rectangleTransparent = new RegionCaptureLightForm(null, activeMonitorMode))
            {
                if (rectangleTransparent.ShowDialog() == DialogResult.OK)
                {
                    Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                    Bitmap result = screenshot.CaptureRectangle(
                        rectangleTransparent.ScreenSelectionRectangle,
                        out HdrImageDocument hdrImageDocument);

                    if (result != null)
                    {
                        lastRegionCaptureType = RegionCaptureType.Transparent;

                        return new TaskMetadata(result, hdrImageDocument);
                    }
                }
            }

            return null;
        }

        private static void DisposeHdrRegionPreviews(
            List<(WindowsHdrPreviewPresenter Presenter, Rectangle Bounds)> previews)
        {
            foreach ((WindowsHdrPreviewPresenter presenter, _) in previews)
            {
                presenter.Dispose();
            }

            previews.Clear();
        }

        private static HdrImageDocument CreateOpaqueHdrRegionDocument(
            RegionCaptureForm form,
            Rectangle selectedRectangle,
            HdrImageDocument hdrCanvasDocument)
        {
            if (hdrCanvasDocument == null || selectedRectangle.IsEmpty)
            {
                return null;
            }

            HdrImageDocument document = hdrCanvasDocument.CropToScreenRectangle(selectedRectangle);
            if (!form.IsImageModified)
            {
                return document;
            }

            try
            {
                float annotationWhiteNits = TaskHelpers.GetHdrAnnotationWhiteNits(document);
                form.ApplyHdrRegionEffects(document, annotationWhiteNits);

                using Bitmap overlay = form.GetHdrDrawingOverlay();
                if (overlay == null || overlay.Size != selectedRectangle.Size)
                {
                    DebugHelper.WriteLine(
                        $"HDR region result unavailable | drawing overlay=" +
                        $"{(overlay == null ? "null" : overlay.Size.ToString())} selection={selectedRectangle.Size}");
                    document.Dispose();
                    return null;
                }

                document.CompositeSdrAnnotationOverlay(overlay, annotationWhiteNits);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        private static HdrImageDocument CreateHdrRegionDocument(
            RegionCaptureForm form,
            Bitmap result,
            Rectangle selectedRectangle,
            HdrImageDocument hdrCanvasDocument)
        {
            if (hdrCanvasDocument == null)
            {
                DebugHelper.WriteLine("HDR region result unavailable | no retained HDR canvas.");
                return null;
            }

            bool isTransparent = ImageHelpers.IsImageTransparent(result);
            if (selectedRectangle.IsEmpty ||
                selectedRectangle.Size != result.Size ||
                isTransparent)
            {
                DebugHelper.WriteLine(
                    $"HDR region result unavailable | selection={selectedRectangle} " +
                    $"result={result.Size} transparent={isTransparent}");
                return null;
            }

            HdrImageDocument document = hdrCanvasDocument.CropToScreenRectangle(selectedRectangle);
            if (!form.IsImageModified)
            {
                return document;
            }

            if (!form.CanExportHdrDrawingOverlay)
            {
                DebugHelper.WriteLine(
                    "HDR region result unavailable | source-dependent region effect requires native HDR replay.");
                document.Dispose();
                return null;
            }

            try
            {
                float annotationWhiteNits = TaskHelpers.GetHdrAnnotationWhiteNits(document);
                form.ApplyHdrRegionEffects(document, annotationWhiteNits);

                using Bitmap overlay = form.GetHdrDrawingOverlay();
                if (overlay == null || overlay.Size != result.Size)
                {
                    DebugHelper.WriteLine(
                        $"HDR region result unavailable | drawing overlay=" +
                        $"{(overlay == null ? "null" : overlay.Size.ToString())} result={result.Size}");
                    document.Dispose();
                    return null;
                }

                document.CompositeSdrAnnotationOverlay(
                    overlay,
                    annotationWhiteNits);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
    }
}
