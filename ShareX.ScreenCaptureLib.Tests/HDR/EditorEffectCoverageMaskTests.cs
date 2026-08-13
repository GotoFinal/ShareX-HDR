using ShareX.ImageEditor.Hosting;
using SkiaSharp;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class EditorEffectCoverageMaskTests
{
    [Fact]
    public void FromBitmap_CropsNonzeroAlphaAndPreservesCoverageBytes()
    {
        using var bitmap = new SKBitmap(4, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(1, 1, new SKColor(255, 255, 255, 128));
        bitmap.SetPixel(2, 1, SKColors.White);

        EditorEffectCoverageMask mask = EditorEffectCoverageMask.FromBitmap(bitmap);

        Assert.Equal(1, mask.X);
        Assert.Equal(1, mask.Y);
        Assert.Equal(2, mask.Width);
        Assert.Equal(1, mask.Height);
        Assert.False(mask.IsEmpty);
        Assert.Equal(new byte[] { 128, 255 }, mask.AlphaBytes.ToArray());
    }

    [Fact]
    public void FromBitmap_ReturnsOwnedEmptyMaskForTransparentBitmap()
    {
        using var bitmap = new SKBitmap(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        EditorEffectCoverageMask mask = EditorEffectCoverageMask.FromBitmap(bitmap);

        Assert.True(mask.IsEmpty);
        Assert.Equal(0, mask.Width);
        Assert.Equal(0, mask.Height);
        Assert.Empty(mask.AlphaBytes.ToArray());
    }
}
