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

namespace ShareX.ImageEditor.Hosting;

/// <summary>
/// An editor-agnostic copy of linear, premultiplied RGBA16F scRGB pixels.
/// One scRGB unit represents 80 nits. The HDR editor host transfers ownership
/// of this object to the editor preview window.
/// </summary>
public sealed class EditorHdrPreviewSource : IDisposable
{
    public const int BytesPerPixel = 8;

    private byte[]? pixels;

    public int Width { get; }
    public int Height { get; }
    public int RowBytes { get; }
    public bool IsDisposed => pixels == null;

    internal byte[] PixelBytes =>
        pixels ?? throw new ObjectDisposedException(nameof(EditorHdrPreviewSource));

    private EditorHdrPreviewSource(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        RowBytes = checked(width * BytesPerPixel);
        this.pixels = pixels;
    }

    public static EditorHdrPreviewSource CopyFrom(
        ReadOnlySpan<byte> source,
        int sourceRowBytes,
        int width,
        int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        int packedRowBytes = checked(width * BytesPerPixel);
        if (sourceRowBytes < packedRowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRowBytes));
        }

        int requiredSourceBytes = checked((height - 1) * sourceRowBytes + packedRowBytes);
        if (source.Length < requiredSourceBytes)
        {
            throw new ArgumentException("The HDR preview source buffer is too small.", nameof(source));
        }

        byte[] copy = new byte[checked(packedRowBytes * height)];
        for (int y = 0; y < height; y++)
        {
            source.Slice(y * sourceRowBytes, packedRowBytes)
                .CopyTo(copy.AsSpan(y * packedRowBytes, packedRowBytes));
        }

        return new EditorHdrPreviewSource(width, height, copy);
    }

    public void Dispose()
    {
        pixels = null;
    }
}
