using ShareX.HelpersLib;
using System.Drawing;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class ClipboardHdrFallbackTests
{
    [Fact]
    public void PrepareHdrClipboardFallback_ReusesProvidedPngBytes()
    {
        using var bitmap = new Bitmap(32, 16, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.CornflowerBlue);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);

        using (HdrClipboardFallbackData fallback =
            ClipboardHelpers.PrepareHdrClipboardFallback(bitmap, png))
        {
            Assert.True(fallback.ReusedPngBytes);
            Assert.Equal(png.Length, fallback.PngLength);
            Assert.True(fallback.DibLength > bitmap.Width * bitmap.Height);
        }

        Assert.Equal(32, bitmap.Width);
    }

    [Fact]
    public void PrepareHdrClipboardFallback_EncodesPngWhenNotProvided()
    {
        using var bitmap = new Bitmap(32, 16, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.OrangeRed);
        }

        using HdrClipboardFallbackData fallback =
            ClipboardHelpers.PrepareHdrClipboardFallback(bitmap);

        Assert.False(fallback.ReusedPngBytes);
        Assert.True(fallback.PngLength > 0);
        Assert.True(fallback.DibLength > bitmap.Width * bitmap.Height);
    }
}
