using ShareX.ImageEditor.Core.Editor;
using ShareX.ImageEditor.Hosting;
using SkiaSharp;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class EditorCoreSourceMutationTests
{
    [Fact]
    public void SourceImageMutated_TracksUnsupportedOperationAndResetsOnLoad()
    {
        using var editor = new EditorCore();
        editor.LoadImage(CreateSolidBitmap(SKColors.Black));

        Assert.False(editor.SourceImageMutated);
        Assert.True(editor.ApplyImageOperation(source => source.Copy()));
        Assert.True(editor.SourceImageMutated);
        Assert.Equal(EditorSourceOperationKind.Unsupported, Assert.Single(editor.SourceOperations).Kind);

        editor.LoadImage(CreateSolidBitmap(SKColors.White));

        Assert.False(editor.SourceImageMutated);
        Assert.Empty(editor.SourceOperations);
    }

    [Fact]
    public void GeometryOperations_RecordInOrderAndUndoRedoRestoresSemanticHistory()
    {
        using var editor = new EditorCore();
        editor.LoadImage(CreatePatternBitmap(4, 3));

        Assert.True(editor.FlipHorizontal());
        Assert.True(editor.Rotate90Clockwise());
        Assert.True(editor.Crop(new SKRect(0, 1, 2, 3)));

        Assert.Collection(
            editor.SourceOperations,
            operation => Assert.Equal(EditorSourceOperationKind.FlipHorizontal, operation.Kind),
            operation => Assert.Equal(EditorSourceOperationKind.Rotate90Clockwise, operation.Kind),
            operation =>
            {
                Assert.Equal(EditorSourceOperationKind.Crop, operation.Kind);
                Assert.Equal(0, operation.X);
                Assert.Equal(1, operation.Y);
                Assert.Equal(2, operation.Width);
                Assert.Equal(2, operation.Height);
            });

        editor.Undo();
        Assert.Collection(
            editor.SourceOperations,
            operation => Assert.Equal(EditorSourceOperationKind.FlipHorizontal, operation.Kind),
            operation => Assert.Equal(EditorSourceOperationKind.Rotate90Clockwise, operation.Kind));

        editor.Redo();
        Assert.Equal(EditorSourceOperationKind.Crop, editor.SourceOperations[^1].Kind);
    }

    [Theory]
    [InlineData(true, EditorSourceOperationKind.CutOutVertical)]
    [InlineData(false, EditorSourceOperationKind.CutOutHorizontal)]
    public void CutOut_RecordsCoordinatesAndSurvivesRedo(
        bool vertical,
        EditorSourceOperationKind expectedKind)
    {
        using var editor = new EditorCore();
        editor.LoadImage(CreatePatternBitmap(4, 3));

        Assert.True(editor.CutOut(1, 2, vertical));
        EditorSourceOperation operation = Assert.Single(editor.SourceOperations);
        Assert.Equal(expectedKind, operation.Kind);
        Assert.Equal(1, vertical ? operation.X : operation.Y);
        Assert.Equal(1, vertical ? operation.Width : operation.Height);

        editor.Undo();
        Assert.Empty(editor.SourceOperations);

        editor.Redo();
        Assert.Equal(expectedKind, Assert.Single(editor.SourceOperations).Kind);
    }

    [Fact]
    public void Resize_RecordsTargetDimensionsAndSurvivesUndoRedo()
    {
        using var editor = new EditorCore();
        editor.LoadImage(CreatePatternBitmap(4, 3));

        Assert.True(editor.ResizeImage(7, 5));
        EditorSourceOperation operation = Assert.Single(editor.SourceOperations);
        Assert.Equal(EditorSourceOperationKind.Resize, operation.Kind);
        Assert.Equal(7, operation.Width);
        Assert.Equal(5, operation.Height);

        editor.Undo();
        Assert.Empty(editor.SourceOperations);
        Assert.Equal(4, editor.SourceImage!.Width);
        Assert.Equal(3, editor.SourceImage.Height);

        editor.Redo();
        Assert.Equal(EditorSourceOperationKind.Resize, Assert.Single(editor.SourceOperations).Kind);
        Assert.Equal(7, editor.SourceImage!.Width);
        Assert.Equal(5, editor.SourceImage.Height);
    }

    [Fact]
    public void ResizeCanvas_RecordsSignedMarginsColorAndSurvivesUndoRedo()
    {
        using var editor = new EditorCore();
        editor.LoadImage(CreatePatternBitmap(4, 3));
        var background = new SKColor(10, 20, 30, 40);

        Assert.True(editor.ResizeCanvas(top: 1, right: 2, bottom: -1, left: -1, background));

        EditorSourceOperation operation = Assert.Single(editor.SourceOperations);
        Assert.Equal(EditorSourceOperationKind.ResizeCanvas, operation.Kind);
        Assert.Equal(-1, operation.X);
        Assert.Equal(1, operation.Y);
        Assert.Equal(2, operation.Width);
        Assert.Equal(-1, operation.Height);
        Assert.Equal(background.Red, operation.Red);
        Assert.Equal(background.Green, operation.Green);
        Assert.Equal(background.Blue, operation.Blue);
        Assert.Equal(background.Alpha, operation.Alpha);
        Assert.Equal(5, editor.SourceImage!.Width);
        Assert.Equal(3, editor.SourceImage.Height);

        editor.Undo();
        Assert.Empty(editor.SourceOperations);
        Assert.Equal(4, editor.SourceImage!.Width);
        Assert.Equal(3, editor.SourceImage.Height);

        editor.Redo();
        operation = Assert.Single(editor.SourceOperations);
        Assert.Equal(EditorSourceOperationKind.ResizeCanvas, operation.Kind);
        Assert.Equal(background.Alpha, operation.Alpha);
        Assert.Equal(5, editor.SourceImage!.Width);
        Assert.Equal(3, editor.SourceImage.Height);
    }

    private static SKBitmap CreatePatternBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(
                    checked((byte)(x * 20)),
                    checked((byte)(y * 30)),
                    checked((byte)(x + y)),
                    255));
            }
        }

        return bitmap;
    }

    private static SKBitmap CreateSolidBitmap(SKColor color)
    {
        var bitmap = new SKBitmap(4, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        return bitmap;
    }
}
