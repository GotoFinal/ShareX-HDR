using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public class HdrFileOutputSettingsTests
{
    [Fact]
    public void NewSettings_DefaultToBytePreservingUploads()
    {
        var settings = new HdrFileOutputSettings();

        Assert.True(settings.UploadWithFileUploader);
        Assert.False(settings.FlattenTransparencyForUltraHdr);
    }
}
