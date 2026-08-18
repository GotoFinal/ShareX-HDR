#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Presentation.Rendering;

namespace ShareX.ImageEditor.Presentation.Views;

public partial class EditorView
{
    private EditorHdrPreviewSource? _pendingHdrPreview;
    private WindowsHdrPreviewPresenter? _hdrPreviewPresenter;
    private bool _hdrPreviewActivationQueued;
    private bool _hdrPreviewFallbackQueued;
    private bool _hdrPreviewVisualStateApplied;
    private IBrush? _hdrOriginalMainBackground;
    private IBrush? _hdrOriginalPreviewFrameBackground;
    private IBrush? _hdrOriginalSmartPaddingBackground;
    private bool _hdrOriginalCheckeredVisibility;
    private double _hdrOriginalCanvasOpacity;

    /// <summary>
    /// Enables the experimental Windows HDR source presentation. Ownership of
    /// <paramref name="source"/> is transferred to this view.
    /// </summary>
    internal void EnableHdrPreview(EditorHdrPreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _pendingHdrPreview?.Dispose();
        _pendingHdrPreview = source;
        ActivatePendingHdrPreview();
    }

    private void ActivatePendingHdrPreview()
    {
        if (!IsLoaded || _pendingHdrPreview == null || _hdrPreviewActivationQueued)
        {
            return;
        }

        _hdrPreviewActivationQueued = true;
        Dispatcher.UIThread.Post(ActivatePendingHdrPreviewCore, DispatcherPriority.Render);
    }

    private void ActivatePendingHdrPreviewCore()
    {
        _hdrPreviewActivationQueued = false;
        EditorHdrPreviewSource? source = _pendingHdrPreview;
        _pendingHdrPreview = null;

        if (source == null)
        {
            return;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("HDR preview is currently available only on Windows.");
            }

            if (_parentWindow == null)
            {
                throw new InvalidOperationException("The editor window is not ready for HDR presentation.");
            }

            if (_parentWindow.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            {
                throw new InvalidOperationException(
                    $"The editor compositor did not provide transparent window composition " +
                    $"(actual={_parentWindow.ActualTransparencyLevel}).");
            }

            DisableHdrPreview(null, reportDiagnostic: false);
            _hdrPreviewPresenter = new WindowsHdrPreviewPresenter(source);
            ApplyHdrPreviewVisualState();
            UpdateHdrPreviewLayout();
            EditorServices.ReportInformation(
                nameof(EditorView),
                $"HDR editor preview enabled | size={source.Width}x{source.Height} " +
                "format=RGBA16F colorSpace=scRGB");
        }
        catch (Exception ex)
        {
            DisableHdrPreview(null, reportDiagnostic: false);
            EditorServices.ReportWarning(
                nameof(EditorView),
                "HDR editor preview unavailable; continuing with the SDR preview.",
                ex);

            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.ShowNotification(
                    "Native HDR preview unavailable; showing SDR. See Debug → Show debug log.",
                    duration: TimeSpan.FromSeconds(6));
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    private void QueueHdrPreviewFallbackAfterSourceChange()
    {
        if (_hdrPreviewPresenter == null || _hdrPreviewFallbackQueued)
        {
            return;
        }

        _hdrPreviewFallbackQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _hdrPreviewFallbackQueued = false;
            DisableHdrPreview(
                "the editor source pixels changed and no longer match the retained HDR preview",
                reportDiagnostic: true);
        }, DispatcherPriority.Render);
    }

    private void UpdateHdrPreviewLayout()
    {
        WindowsHdrPreviewPresenter? presenter = _hdrPreviewPresenter;
        if (presenter == null || _parentWindow == null || _canvasControl == null)
        {
            return;
        }

        try
        {
            if (!_parentWindow.IsVisible || _parentWindow.WindowState == WindowState.Minimized)
            {
                presenter.Hide();
                return;
            }

            IPlatformHandle? platformHandle = _parentWindow.TryGetPlatformHandle();
            if (platformHandle == null ||
                !string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The editor did not expose a Windows HWND.");
            }

            ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("CanvasScrollViewer");
            Grid? centralHost = this.FindControl<Grid>("CentralPreviewHost");
            if (scrollViewer == null || centralHost == null ||
                _canvasControl.Bounds.Width <= 0 || _canvasControl.Bounds.Height <= 0)
            {
                presenter.Hide();
                return;
            }

            System.Drawing.Rectangle imageScreenRectangle = CreateScreenRectangle(_canvasControl);
            System.Drawing.Rectangle viewportScreenRectangle = CreateScreenRectangle(scrollViewer);
            presenter.UpdateLayout(
                imageScreenRectangle,
                viewportScreenRectangle,
                platformHandle.Handle);
            UpdateHdrPreviewBackdrop(centralHost, scrollViewer);
        }
        catch (Exception ex)
        {
            DisableHdrPreview(null, reportDiagnostic: false);
            EditorServices.ReportWarning(
                nameof(EditorView),
                "HDR editor preview failed during layout; continuing with the SDR preview.",
                ex);
        }
    }

    private static System.Drawing.Rectangle CreateScreenRectangle(Control control)
    {
        PixelPoint topLeft = control.PointToScreen(default);
        PixelPoint bottomRight = control.PointToScreen(
            new Point(control.Bounds.Width, control.Bounds.Height));

        return System.Drawing.Rectangle.FromLTRB(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
    }

    private void UpdateHdrPreviewBackdrop(Grid centralHost, ScrollViewer scrollViewer)
    {
        Canvas? backdrop = this.FindControl<Canvas>("HdrPreviewBackdrop");
        Border? top = this.FindControl<Border>("HdrBackdropTop");
        Border? bottom = this.FindControl<Border>("HdrBackdropBottom");
        Border? left = this.FindControl<Border>("HdrBackdropLeft");
        Border? right = this.FindControl<Border>("HdrBackdropRight");
        Point? imageStart = _canvasControl?.TranslatePoint(default, centralHost);
        Point? imageEnd = _canvasControl?.TranslatePoint(
            new Point(_canvasControl.Bounds.Width, _canvasControl.Bounds.Height),
            centralHost);
        Point? viewportStart = scrollViewer.TranslatePoint(default, centralHost);
        Point? viewportEnd = scrollViewer.TranslatePoint(
            new Point(scrollViewer.Bounds.Width, scrollViewer.Bounds.Height),
            centralHost);

        if (backdrop == null || top == null || bottom == null || left == null || right == null ||
            !imageStart.HasValue || !imageEnd.HasValue || !viewportStart.HasValue || !viewportEnd.HasValue)
        {
            return;
        }

        double hostWidth = Math.Max(0, centralHost.Bounds.Width);
        double hostHeight = Math.Max(0, centralHost.Bounds.Height);
        double visibleLeft = Math.Clamp(
            Math.Max(Math.Min(imageStart.Value.X, imageEnd.Value.X),
                Math.Min(viewportStart.Value.X, viewportEnd.Value.X)),
            0,
            hostWidth);
        double visibleTop = Math.Clamp(
            Math.Max(Math.Min(imageStart.Value.Y, imageEnd.Value.Y),
                Math.Min(viewportStart.Value.Y, viewportEnd.Value.Y)),
            0,
            hostHeight);
        double visibleRight = Math.Clamp(
            Math.Min(Math.Max(imageStart.Value.X, imageEnd.Value.X),
                Math.Max(viewportStart.Value.X, viewportEnd.Value.X)),
            visibleLeft,
            hostWidth);
        double visibleBottom = Math.Clamp(
            Math.Min(Math.Max(imageStart.Value.Y, imageEnd.Value.Y),
                Math.Max(viewportStart.Value.Y, viewportEnd.Value.Y)),
            visibleTop,
            hostHeight);

        backdrop.Width = hostWidth;
        backdrop.Height = hostHeight;
        SetBackdropRectangle(top, 0, 0, hostWidth, visibleTop);
        SetBackdropRectangle(bottom, 0, visibleBottom, hostWidth, hostHeight - visibleBottom);
        SetBackdropRectangle(left, 0, visibleTop, visibleLeft, visibleBottom - visibleTop);
        SetBackdropRectangle(right, visibleRight, visibleTop, hostWidth - visibleRight, visibleBottom - visibleTop);
    }

    private static void SetBackdropRectangle(
        Border border,
        double x,
        double y,
        double width,
        double height)
    {
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        border.Width = Math.Max(0, width);
        border.Height = Math.Max(0, height);
    }

    private void ApplyHdrPreviewVisualState()
    {
        if (_hdrPreviewVisualStateApplied || _canvasControl == null)
        {
            return;
        }

        Grid? mainContent = this.FindControl<Grid>("MainContentGrid");
        Border? checkered = this.FindControl<Border>("CheckeredBackground");
        Border? previewFrame = this.FindControl<Border>("PreviewFrame");
        Border? smartPadding = this.FindControl<Border>("SmartPaddingBorder");
        Canvas? backdrop = this.FindControl<Canvas>("HdrPreviewBackdrop");
        if (mainContent == null || checkered == null || previewFrame == null ||
            smartPadding == null || backdrop == null)
        {
            throw new InvalidOperationException("The editor HDR preview visual host was not found.");
        }

        _hdrOriginalMainBackground = mainContent.Background;
        _hdrOriginalPreviewFrameBackground = previewFrame.Background;
        _hdrOriginalSmartPaddingBackground = smartPadding.Background;
        _hdrOriginalCheckeredVisibility = checkered.IsVisible;
        _hdrOriginalCanvasOpacity = _canvasControl.Opacity;

        mainContent.SetCurrentValue(Panel.BackgroundProperty, Brushes.Transparent);
        previewFrame.SetCurrentValue(Border.BackgroundProperty, Brushes.Transparent);
        smartPadding.SetCurrentValue(Border.BackgroundProperty, Brushes.Transparent);
        checkered.SetCurrentValue(Visual.IsVisibleProperty, false);
        _canvasControl.SetCurrentValue(Visual.OpacityProperty, 0d);
        backdrop.SetCurrentValue(Visual.IsVisibleProperty, true);
        _hdrPreviewVisualStateApplied = true;
    }

    private void RestoreHdrPreviewVisualState()
    {
        if (!_hdrPreviewVisualStateApplied)
        {
            return;
        }

        Grid? mainContent = this.FindControl<Grid>("MainContentGrid");
        Border? checkered = this.FindControl<Border>("CheckeredBackground");
        Border? previewFrame = this.FindControl<Border>("PreviewFrame");
        Border? smartPadding = this.FindControl<Border>("SmartPaddingBorder");
        Canvas? backdrop = this.FindControl<Canvas>("HdrPreviewBackdrop");

        mainContent?.SetCurrentValue(Panel.BackgroundProperty, _hdrOriginalMainBackground);
        previewFrame?.SetCurrentValue(Border.BackgroundProperty, _hdrOriginalPreviewFrameBackground);
        smartPadding?.SetCurrentValue(Border.BackgroundProperty, _hdrOriginalSmartPaddingBackground);
        checkered?.SetCurrentValue(Visual.IsVisibleProperty, _hdrOriginalCheckeredVisibility);
        _canvasControl?.SetCurrentValue(Visual.OpacityProperty, _hdrOriginalCanvasOpacity);
        backdrop?.SetCurrentValue(Visual.IsVisibleProperty, false);
        _hdrPreviewVisualStateApplied = false;
    }

    /// <summary>
    /// Off-screen Avalonia exports cannot see the external DXGI presenter. Make
    /// the retained SDR canvas renderable while GetSnapshot captures the visual
    /// tree, then restore the native HDR presentation immediately afterward.
    /// </summary>
    private bool SuspendHdrPreviewVisualStateForSnapshot()
    {
        if (!_hdrPreviewVisualStateApplied)
        {
            return false;
        }

        RestoreHdrPreviewVisualState();
        return true;
    }

    private void ResumeHdrPreviewVisualStateAfterSnapshot(bool wasSuspended)
    {
        if (wasSuspended && _hdrPreviewPresenter != null)
        {
            ApplyHdrPreviewVisualState();
        }
    }

    private void DisableHdrPreview(string? reason, bool reportDiagnostic)
    {
        _hdrPreviewPresenter?.Dispose();
        _hdrPreviewPresenter = null;
        RestoreHdrPreviewVisualState();

        if (reportDiagnostic && !string.IsNullOrEmpty(reason))
        {
            EditorServices.ReportInformation(
                nameof(EditorView),
                $"HDR editor preview fallback | reason={reason}");

            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.ShowNotification(
                    "HDR preview switched to SDR because the source image changed.",
                    duration: TimeSpan.FromSeconds(4));
            }
        }
    }

    internal void DisposeHdrPreview()
    {
        _pendingHdrPreview?.Dispose();
        _pendingHdrPreview = null;
        DisableHdrPreview(null, reportDiagnostic: false);
    }

    private void OnHdrPreviewWindowPositionChanged(object? sender, PixelPointEventArgs e) =>
        UpdateHdrPreviewLayout();

    private void OnHdrPreviewParentWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
        {
            UpdateHdrPreviewLayout();
        }
    }
}
