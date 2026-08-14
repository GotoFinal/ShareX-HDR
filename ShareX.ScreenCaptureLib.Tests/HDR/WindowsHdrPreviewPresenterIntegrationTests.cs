using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Presentation.Rendering;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class WindowsHdrPreviewPresenterIntegrationTests
{
    [Fact]
    public void Presenter_CreatesFp16SwapChainAndUploadsFrame()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_PREVIEW_TESTS"),
            "1",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        byte[] pixels = new byte[2 * 2 * EditorHdrPreviewSource.BytesPerPixel];
        using EditorHdrPreviewSource source = EditorHdrPreviewSource.CopyFrom(
            pixels,
            2 * EditorHdrPreviewSource.BytesPerPixel,
            width: 2,
            height: 2);
        using var presenter = new WindowsHdrPreviewPresenter(source);
    }
}
