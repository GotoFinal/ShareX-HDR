using ShareX.ImageEditor.Hosting;
using SkiaSharp;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class EditorSourceOperationDetectorTests
{
    [Fact]
    public void Detect_ReturnsNoneForUnchangedSource()
    {
        using SKBitmap initial = CreatePattern(6, 5);
        using SKBitmap current = initial.Copy();

        EditorSourceOperation operation = EditorSourceOperationDetector.Detect(
            initial,
            current,
            initialFingerprint: null,
            sourceImageMutated: false);

        Assert.Equal(EditorSourceOperationKind.None, operation.Kind);
    }

    [Fact]
    public void Detect_ReturnsUniqueExactCrop()
    {
        using SKBitmap initial = CreatePattern(6, 5);
        using var current = new SKBitmap(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        Assert.True(initial.ExtractSubset(current, new SKRectI(2, 1, 5, 3)));

        EditorSourceOperation operation = EditorSourceOperationDetector.Detect(
            initial,
            current,
            initialFingerprint: null,
            sourceImageMutated: true);

        Assert.Equal(EditorSourceOperationKind.Crop, operation.Kind);
        Assert.Equal(2, operation.X);
        Assert.Equal(1, operation.Y);
        Assert.Equal(3, operation.Width);
        Assert.Equal(2, operation.Height);
    }

    [Fact]
    public void Detect_RejectsPixelChangingSource()
    {
        using SKBitmap initial = CreatePattern(6, 5);
        using SKBitmap current = initial.Copy();
        current.SetPixel(2, 2, SKColors.Magenta);

        EditorSourceOperation operation = EditorSourceOperationDetector.Detect(
            initial,
            current,
            initialFingerprint: null,
            sourceImageMutated: true);

        Assert.Equal(EditorSourceOperationKind.Unsupported, operation.Kind);
    }

    [Fact]
    public void Detect_RejectsAmbiguousCrop()
    {
        using var initial = new SKBitmap(6, 5, SKColorType.Bgra8888, SKAlphaType.Premul);
        initial.Erase(SKColors.DarkBlue);
        using var current = new SKBitmap(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        current.Erase(SKColors.DarkBlue);

        EditorSourceOperation operation = EditorSourceOperationDetector.Detect(
            initial,
            current,
            initialFingerprint: null,
            sourceImageMutated: true);

        Assert.Equal(EditorSourceOperationKind.Unsupported, operation.Kind);
    }

    [Fact]
    public void Detect_RejectsExpandedCanvas()
    {
        using SKBitmap initial = CreatePattern(3, 2);
        using SKBitmap current = CreatePattern(4, 2);

        EditorSourceOperation operation = EditorSourceOperationDetector.Detect(
            initial,
            current,
            initialFingerprint: null,
            sourceImageMutated: true);

        Assert.Equal(EditorSourceOperationKind.Unsupported, operation.Kind);
    }

    [Fact]
    public void Detect_RejectsByteIdenticalSourceWhenMutationWasRecorded()
    {
        using var initial = new SKBitmap(4, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        initial.Erase(SKColors.Black);
        using SKBitmap current = initial.Copy();

        EditorSourceOperation operation = EditorSourceOperationDetector.Detect(
            initial,
            current,
            initialFingerprint: null,
            sourceImageMutated: true);

        Assert.Equal(EditorSourceOperationKind.Unsupported, operation.Kind);
    }

    private static SKBitmap CreatePattern(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(
                    checked((byte)(20 + x * 13)),
                    checked((byte)(30 + y * 17)),
                    checked((byte)(40 + x * 7 + y * 5)),
                    255));
            }
        }

        return bitmap;
    }
}
