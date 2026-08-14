using ShareX.ImageEditor.Hosting;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class EditorHdrPreviewSourceTests
{
    [Fact]
    public void CopyFrom_RemovesSourceRowPadding()
    {
        byte[] source = new byte[40];
        source.AsSpan(0, 16).Fill(1);
        source.AsSpan(24, 16).Fill(2);

        using EditorHdrPreviewSource preview = EditorHdrPreviewSource.CopyFrom(
            source,
            sourceRowBytes: 24,
            width: 2,
            height: 2);

        Assert.Equal(2, preview.Width);
        Assert.Equal(2, preview.Height);
        Assert.Equal(16, preview.RowBytes);
        Assert.Equal(32, preview.PixelBytes.Length);
        Assert.All(preview.PixelBytes.AsSpan(0, 16).ToArray(), value => Assert.Equal((byte)1, value));
        Assert.All(preview.PixelBytes.AsSpan(16, 16).ToArray(), value => Assert.Equal((byte)2, value));
    }

    [Fact]
    public void Dispose_RejectsFurtherPixelAccess()
    {
        EditorHdrPreviewSource preview = EditorHdrPreviewSource.CopyFrom(
            new byte[EditorHdrPreviewSource.BytesPerPixel],
            EditorHdrPreviewSource.BytesPerPixel,
            width: 1,
            height: 1);

        preview.Dispose();

        Assert.True(preview.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = preview.PixelBytes);
    }
}
