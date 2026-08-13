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

using ShareX.ImageEditor.Core.Annotations;
using SkiaSharp;

namespace ShareX.ImageEditor.Hosting;

/// <summary>
/// Describes how an existing editor annotation must be replayed over a retained
/// high-precision source image.
/// </summary>
public enum EditorOverlayOperationKind
{
    /// <summary>
    /// A transparent, premultiplied BGRA8/sRGB layer rendered by the existing editor.
    /// </summary>
    AnnotationOverlay,

    /// <summary>
    /// One or more annotations which depend on the source pixels and therefore cannot
    /// be represented by a transparent overlay alone.
    /// </summary>
    SourceEffect,

    /// <summary>
    /// An annotation had no exportable visual. HDR replay must not silently omit it.
    /// </summary>
    UnsupportedAnnotation
}

public enum EditorOverlayDialogOutcome
{
    Cancelled,
    ApplyAnnotations,
    ContinueWithoutApplying
}

/// <summary>
/// Host callbacks which consume semantic editor exports instead of flattened
/// SDR snapshots. This lets a high-precision document owner implement verified
/// Save and Save As operations without introducing an HDR dependency here.
/// </summary>
public sealed class EditorOverlayEvents
{
    public Func<string, bool>? OpenImageRequested { get; set; }
    public Func<EditorOverlayExport, string?, string?>? SaveImageRequested { get; set; }
    public Func<EditorOverlayExport, string?, string?>? SaveImageAsRequested { get; set; }
    public string? ImageFilePath { get; set; }
}

/// <summary>
/// Result of the overlay-only editor workflow used by an HDR document host.
/// </summary>
public sealed class EditorOverlayDialogResult : IDisposable
{
    public EditorOverlayDialogOutcome Outcome { get; }
    public EditorOverlayExport? Export { get; private set; }

    internal EditorOverlayDialogResult(EditorOverlayDialogOutcome outcome, EditorOverlayExport? export = null)
    {
        Outcome = outcome;
        Export = export;
    }

    public void Dispose()
    {
        Export?.Dispose();
        Export = null;
    }
}

/// <summary>
/// Cropped antialiased coverage rendered by the editor for one source-effect
/// operation. Pixels are straight 8-bit coverage, independent of source color.
/// </summary>
public sealed class EditorEffectCoverageMask
{
    private readonly byte[] alphaBytes;

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsEmpty => Width == 0 || Height == 0;
    public ReadOnlyMemory<byte> AlphaBytes => alphaBytes;

    private EditorEffectCoverageMask(int x, int y, int width, int height, byte[] alphaBytes)
    {
        if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0) throw new ArgumentOutOfRangeException(nameof(y));
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(alphaBytes);
        if (alphaBytes.Length != checked(width * height))
        {
            throw new ArgumentException("Coverage byte count does not match its dimensions.", nameof(alphaBytes));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
        this.alphaBytes = alphaBytes;
    }

    internal static EditorEffectCoverageMask FromBitmap(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
        {
            throw new InvalidOperationException("Effect coverage must be rendered as BGRA8 or RGBA8.");
        }

        SKPixmap pixmap = bitmap.PeekPixels() ??
            throw new InvalidOperationException("Effect coverage bitmap has no readable pixels.");
        ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan<byte>();
        int minimumX = bitmap.Width;
        int minimumY = bitmap.Height;
        int maximumX = -1;
        int maximumY = -1;

        for (int y = 0; y < bitmap.Height; y++)
        {
            int rowOffset = y * pixmap.RowBytes;
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (pixels[rowOffset + x * 4 + 3] == 0)
                {
                    continue;
                }

                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }

        if (maximumX < minimumX || maximumY < minimumY)
        {
            return new EditorEffectCoverageMask(0, 0, 0, 0, Array.Empty<byte>());
        }

        int width = maximumX - minimumX + 1;
        int height = maximumY - minimumY + 1;
        byte[] coverage = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            int sourceOffset = (minimumY + y) * pixmap.RowBytes + minimumX * 4 + 3;
            int destinationOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                coverage[destinationOffset + x] = pixels[sourceOffset + x * 4];
            }
        }

        return new EditorEffectCoverageMask(minimumX, minimumY, width, height, coverage);
    }
}

/// <summary>
/// One ordered operation in an annotation-overlay export.
/// </summary>
public sealed class EditorOverlayOperation : IDisposable
{
    private readonly List<Annotation> annotations;

    public EditorOverlayOperationKind Kind { get; }

    /// <summary>
    /// Premultiplied BGRA8/sRGB pixels. Set only for <see cref="EditorOverlayOperationKind.AnnotationOverlay"/>.
    /// </summary>
    public SKBitmap? Overlay { get; private set; }

    /// <summary>
    /// Editor-rasterized antialiased coverage for a source-effect operation.
    /// </summary>
    public EditorEffectCoverageMask? CoverageMask { get; }

    /// <summary>
    /// Cloned source-effect or unsupported annotations. The export owns these clones.
    /// </summary>
    public IReadOnlyList<Annotation> Annotations => annotations;

    private EditorOverlayOperation(EditorOverlayOperationKind kind, SKBitmap? overlay, EditorEffectCoverageMask? coverageMask, IEnumerable<Annotation>? annotations)
    {
        Kind = kind;
        Overlay = overlay;
        CoverageMask = coverageMask;
        this.annotations = annotations?.ToList() ?? new List<Annotation>();
    }

    internal static EditorOverlayOperation CreateOverlay(SKBitmap overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return new EditorOverlayOperation(EditorOverlayOperationKind.AnnotationOverlay, overlay, null, null);
    }

    internal static EditorOverlayOperation CreateSourceEffect(IEnumerable<Annotation> annotations, EditorEffectCoverageMask coverageMask)
    {
        return new EditorOverlayOperation(EditorOverlayOperationKind.SourceEffect, null, coverageMask, annotations);
    }

    internal static EditorOverlayOperation CreateUnsupported(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return new EditorOverlayOperation(EditorOverlayOperationKind.UnsupportedAnnotation, null, null, new[] { annotation });
    }

    public void Dispose()
    {
        Overlay?.Dispose();
        Overlay = null;

        foreach (Annotation annotation in annotations)
        {
            if (annotation is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        annotations.Clear();
    }
}

public enum EditorSourceOperationKind
{
    None,
    Crop,
    Rotate90Clockwise,
    Rotate90CounterClockwise,
    Rotate180,
    FlipHorizontal,
    FlipVertical,
    CutOutVertical,
    CutOutHorizontal,
    Resize,
    ResizeCanvas,
    ImageEffect,
    Unsupported
}

public sealed class EditorSourceOperation
{
    public EditorSourceOperationKind Kind { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public byte Red { get; }
    public byte Green { get; }
    public byte Blue { get; }
    public byte Alpha { get; }
    public EditorImageEffectDescriptor? ImageEffectDescriptor { get; }

    private EditorSourceOperation(
        EditorSourceOperationKind kind,
        int x = 0,
        int y = 0,
        int width = 0,
        int height = 0,
        byte red = 0,
        byte green = 0,
        byte blue = 0,
        byte alpha = 0,
        EditorImageEffectDescriptor? imageEffectDescriptor = null)
    {
        Kind = kind;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        ImageEffectDescriptor = imageEffectDescriptor;
    }

    internal static EditorSourceOperation None() => new(EditorSourceOperationKind.None);
    internal static EditorSourceOperation Unsupported() => new(EditorSourceOperationKind.Unsupported);
    internal static EditorSourceOperation Crop(int x, int y, int width, int height) =>
        new(EditorSourceOperationKind.Crop, x, y, width, height);
    internal static EditorSourceOperation Rotate90Clockwise() =>
        new(EditorSourceOperationKind.Rotate90Clockwise);
    internal static EditorSourceOperation Rotate90CounterClockwise() =>
        new(EditorSourceOperationKind.Rotate90CounterClockwise);
    internal static EditorSourceOperation Rotate180() =>
        new(EditorSourceOperationKind.Rotate180);
    internal static EditorSourceOperation FlipHorizontal() =>
        new(EditorSourceOperationKind.FlipHorizontal);
    internal static EditorSourceOperation CutOutVertical(int x, int width) =>
        new(EditorSourceOperationKind.CutOutVertical, x: x, width: width);
    internal static EditorSourceOperation CutOutHorizontal(int y, int height) =>
        new(EditorSourceOperationKind.CutOutHorizontal, y: y, height: height);
    internal static EditorSourceOperation FlipVertical() =>
        new(EditorSourceOperationKind.FlipVertical);
    internal static EditorSourceOperation Resize(int width, int height) =>
        new(EditorSourceOperationKind.Resize, width: width, height: height);
    internal static EditorSourceOperation ResizeCanvas(
        int top,
        int right,
        int bottom,
        int left,
        SKColor backgroundColor) =>
        new(
            EditorSourceOperationKind.ResizeCanvas,
            x: left,
            y: top,
            width: right,
            height: bottom,
            red: backgroundColor.Red,
            green: backgroundColor.Green,
            blue: backgroundColor.Blue,
            alpha: backgroundColor.Alpha);

    internal static EditorSourceOperation ImageEffect(EditorImageEffectDescriptor descriptor) =>
        new(EditorSourceOperationKind.ImageEffect, imageEffectDescriptor: descriptor ??
            throw new ArgumentNullException(nameof(descriptor)));

}
/// <summary>
/// Recognizes only source changes that can be proven without interpreting editor
/// command history. A crop must be one unique, byte-exact rectangular subset of
/// the original SDR preview. Ambiguous or pixel-changing results remain unsupported.
/// </summary>
public static class EditorSourceOperationDetector
{
    public static unsafe EditorSourceOperation Detect(
        SKBitmap? initialSource,
        SKBitmap currentSource,
        ulong? initialFingerprint,
        bool sourceImageMutated)
    {
        ArgumentNullException.ThrowIfNull(currentSource);

        if (!sourceImageMutated && initialSource != null &&
            initialSource.Width == currentSource.Width &&
            initialSource.Height == currentSource.Height &&
            EditorImageFingerprint.Compute(currentSource) ==
                (initialFingerprint ?? EditorImageFingerprint.Compute(initialSource)))
        {
            return EditorSourceOperation.None();
        }

        if (initialSource == null || currentSource.Width > initialSource.Width ||
            currentSource.Height > initialSource.Height ||
            (currentSource.Width == initialSource.Width && currentSource.Height == initialSource.Height) ||
            initialSource.ColorType != currentSource.ColorType ||
            initialSource.AlphaType != currentSource.AlphaType)
        {
            return EditorSourceOperation.Unsupported();
        }

        SKPixmap? initialPixels = initialSource.PeekPixels();
        SKPixmap? currentPixels = currentSource.PeekPixels();
        int bytesPerPixel = initialSource.Info.BytesPerPixel;

        if (initialPixels == null || currentPixels == null || bytesPerPixel <= 0 ||
            bytesPerPixel != currentSource.Info.BytesPerPixel)
        {
            return EditorSourceOperation.Unsupported();
        }

        ReadOnlySpan<byte> initialBytes = GetReadablePixelBytes(initialPixels, initialSource.Width, initialSource.Height, bytesPerPixel);
        ReadOnlySpan<byte> currentBytes = GetReadablePixelBytes(currentPixels, currentSource.Width, currentSource.Height, bytesPerPixel);
        int maximumX = initialSource.Width - currentSource.Width;
        int maximumY = initialSource.Height - currentSource.Height;
        int matchedX = -1;
        int matchedY = -1;

        for (int y = 0; y <= maximumY; y++)
        {
            for (int x = 0; x <= maximumX; x++)
            {
                if (!PixelEquals(initialBytes, initialPixels.RowBytes, x, y,
                        currentBytes, currentPixels.RowBytes, 0, 0, bytesPerPixel) ||
                    !PixelEquals(initialBytes, initialPixels.RowBytes,
                        x + currentSource.Width - 1, y + currentSource.Height - 1,
                        currentBytes, currentPixels.RowBytes,
                        currentSource.Width - 1, currentSource.Height - 1, bytesPerPixel) ||
                    !IsExactSubset(initialBytes, initialPixels.RowBytes, x, y,
                        currentBytes, currentPixels.RowBytes,
                        currentSource.Width, currentSource.Height, bytesPerPixel))
                {
                    continue;
                }

                if (matchedX >= 0)
                {
                    return EditorSourceOperation.Unsupported();
                }

                matchedX = x;
                matchedY = y;
            }
        }

        return matchedX >= 0
            ? EditorSourceOperation.Crop(matchedX, matchedY, currentSource.Width, currentSource.Height)
            : EditorSourceOperation.Unsupported();
    }

    private static unsafe ReadOnlySpan<byte> GetReadablePixelBytes(
        SKPixmap pixels,
        int width,
        int height,
        int bytesPerPixel)
    {
        IntPtr address = pixels.GetPixels();
        if (address == IntPtr.Zero)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        int length = checked((height - 1) * pixels.RowBytes + width * bytesPerPixel);
        return new ReadOnlySpan<byte>(address.ToPointer(), length);
    }

    private static bool PixelEquals(
        ReadOnlySpan<byte> first,
        int firstRowBytes,
        int firstX,
        int firstY,
        ReadOnlySpan<byte> second,
        int secondRowBytes,
        int secondX,
        int secondY,
        int bytesPerPixel) =>
        first.Slice(firstY * firstRowBytes + firstX * bytesPerPixel, bytesPerPixel)
            .SequenceEqual(second.Slice(
                secondY * secondRowBytes + secondX * bytesPerPixel,
                bytesPerPixel));

    private static bool IsExactSubset(
        ReadOnlySpan<byte> initial,
        int initialRowBytes,
        int x,
        int y,
        ReadOnlySpan<byte> current,
        int currentRowBytes,
        int width,
        int height,
        int bytesPerPixel)
    {
        int activeRowBytes = checked(width * bytesPerPixel);
        for (int row = 0; row < height; row++)
        {
            if (!initial.Slice((y + row) * initialRowBytes + x * bytesPerPixel, activeRowBytes)
                .SequenceEqual(current.Slice(row * currentRowBytes, activeRowBytes)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Ordered transparent annotation layers and source-dependent operations produced
/// by the existing editor without flattening its SDR preview into the HDR master.
/// </summary>
public sealed class EditorOverlayExport : IDisposable
{
    private readonly List<EditorOverlayOperation> operations;
    private readonly List<EditorSourceOperation> sourceOperations;

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// True when the editor's raster source differs from the image loaded by the host.
    /// The first HDR integration must block/fall back until those destructive operations
    /// have replayable HDR implementations.
    /// </summary>
    public bool SourceImageChanged => sourceOperations.Count > 0 ||
        SourceOperation.Kind != EditorSourceOperationKind.None;
    public EditorSourceOperation SourceOperation { get; }
    public IReadOnlyList<EditorSourceOperation> SourceOperations => sourceOperations;

    public IReadOnlyList<EditorOverlayOperation> Operations => operations;

    public bool HasSourceEffects => operations.Any(x => x.Kind == EditorOverlayOperationKind.SourceEffect);
    public bool HasUnsupportedAnnotations => operations.Any(x => x.Kind == EditorOverlayOperationKind.UnsupportedAnnotation);
    public bool HasUnsupportedSourceOperations => sourceOperations.Any(IsUnsupportedSourceOperation);
    public bool CanReplayOverHdr => !IsUnsupportedSourceOperation(SourceOperation) &&
        !HasUnsupportedSourceOperations && !HasUnsupportedAnnotations;

    private static bool IsUnsupportedSourceOperation(EditorSourceOperation operation) =>
        operation.Kind == EditorSourceOperationKind.Unsupported ||
        (operation.Kind == EditorSourceOperationKind.ImageEffect &&
            (operation.ImageEffectDescriptor == null ||
                operation.ImageEffectDescriptor.SchemaVersion != EditorImageEffectDescriptor.CurrentSchemaVersion ||
                operation.ImageEffectDescriptor.ReplayDeterminism == EditorImageEffectReplayDeterminism.Unspecified ||
                (operation.ImageEffectDescriptor.ReplayDeterminism == EditorImageEffectReplayDeterminism.Seeded && operation.ImageEffectDescriptor.ReplaySeed == null) ||
                EditorImageEffectHdrCapabilities.GetCapability(operation.ImageEffectDescriptor.EffectId) !=
                    EditorImageEffectHdrCapability.NativeFp16));

    internal EditorOverlayExport(
        int width,
        int height,
        EditorSourceOperation sourceOperation,
        IEnumerable<EditorSourceOperation> sourceOperations,
        IEnumerable<EditorOverlayOperation> operations)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        SourceOperation = sourceOperation ?? throw new ArgumentNullException(nameof(sourceOperation));
        this.sourceOperations = sourceOperations?.ToList() ?? throw new ArgumentNullException(nameof(sourceOperations));
        this.operations = operations?.ToList() ?? throw new ArgumentNullException(nameof(operations));
    }

    public void Dispose()
    {
        foreach (EditorOverlayOperation operation in operations)
        {
            operation.Dispose();
        }

        operations.Clear();
    }
}

internal static class EditorImageFingerprint
{
    // FNV-1a is used only as a fast mutation guard, not for security or file identity.
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Compute(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        ulong hash = OffsetBasis;
        Add(ref hash, bitmap.Width);
        Add(ref hash, bitmap.Height);
        Add(ref hash, (int)bitmap.ColorType);
        Add(ref hash, (int)bitmap.AlphaType);

        SKPixmap? pixmap = bitmap.PeekPixels();
        if (pixmap == null)
        {
            throw new InvalidOperationException("The editor source bitmap has no readable pixels.");
        }

        ReadOnlySpan<byte> bytes = pixmap.GetPixelSpan<byte>();
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash;
    }

    private static void Add(ref ulong hash, int value)
    {
        unchecked
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= Prime;
            }
        }
    }
}