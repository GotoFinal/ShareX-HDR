using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Presentation.ViewModels;

namespace ShareX.ScreenCaptureLib.Tests.HDR;

public sealed class EditorHostOpenDelegationTests
{
    [Fact]
    public void TryOpenImageWithHost_WhenAccepted_ClosesWithoutReturningFlattenedResult()
    {
        var viewModel = new MainViewModel(new ImageEditorOptions());
        string? openedPath = null;
        bool closeRequested = false;
        viewModel.HostOpenImageRequested += path =>
        {
            openedPath = path;
            return true;
        };
        viewModel.CloseRequested += (_, _) => closeRequested = true;

        bool handled = viewModel.TryOpenImageWithHost("capture.exr");

        Assert.True(handled);
        Assert.Equal("capture.exr", openedPath);
        Assert.True(closeRequested);
        Assert.Equal(MainViewModel.EditorTaskResult.Cancel, viewModel.TaskResult);
    }

    [Fact]
    public void TryOpenImageWithHost_WhenDeclined_LeavesCurrentEditorOpen()
    {
        var viewModel = new MainViewModel(new ImageEditorOptions());
        bool closeRequested = false;
        viewModel.HostOpenImageRequested += _ => false;
        viewModel.CloseRequested += (_, _) => closeRequested = true;

        bool handled = viewModel.TryOpenImageWithHost("ordinary.png");

        Assert.False(handled);
        Assert.False(closeRequested);
        Assert.Equal(MainViewModel.EditorTaskResult.None, viewModel.TaskResult);
    }
}
