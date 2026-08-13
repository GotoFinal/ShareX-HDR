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
using ShareX.ScreenCaptureLib;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX
{
    public class CaptureRegion : CaptureBase
    {
        protected static RegionCaptureType lastRegionCaptureType = RegionCaptureType.Default;

        public RegionCaptureType RegionCaptureType { get; protected set; }

        public CaptureRegion()
        {
        }

        public CaptureRegion(RegionCaptureType regionCaptureType)
        {
            RegionCaptureType = regionCaptureType;
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

            if (taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation)
            {
                mode = RegionCaptureMode.Default;
            }
            else
            {
                mode = RegionCaptureMode.Annotation;
            }

            Bitmap canvas;
            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
            HdrImageDocument hdrCanvasDocument;

            if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
            {
                canvas = screenshot.CaptureActiveMonitor(out hdrCanvasDocument);
            }
            else
            {
                canvas = screenshot.CaptureFullscreen(out hdrCanvasDocument);
            }

            try
            {
                using (RegionCaptureForm form = new RegionCaptureForm(mode,
                    taskSettings.CaptureSettingsReference.SurfaceOptions, canvas, screenshot))
                {
                    form.ShowDialog();

                    Bitmap result = form.GetResultImage();

                    if (result != null)
                    {
                        TaskMetadata metadata = new TaskMetadata(result);

                        if (form.IsImageModified)
                        {
                            AllowAnnotation = false;
                        }

                        if (form.Result == RegionResult.Region)
                        {
                            WindowInfo windowInfo = form.GetWindowInfo();
                            metadata.UpdateInfo(windowInfo);
                        }

                        Rectangle selectedRectangle = form.GetSelectedRectangle();
                        metadata.HdrImageDocument = CreateHdrRegionDocument(
                            form,
                            result,
                            selectedRectangle,
                            hdrCanvasDocument);

                        lastRegionCaptureType = RegionCaptureType.Default;
                        return metadata;
                    }
                }
            }
            finally
            {
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
                    TaskHelpers.GetHdrAnnotationWhiteNits(document));
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
