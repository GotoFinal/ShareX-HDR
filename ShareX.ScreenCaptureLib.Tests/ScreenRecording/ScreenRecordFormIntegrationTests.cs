using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ShareX.ScreenCaptureLib;

namespace ShareX.ScreenCaptureLib.Tests.ScreenRecording;

public sealed class ScreenRecordFormIntegrationTests
{
    [Theory]
    [InlineData(800, 400)]
    [InlineData(376, 2)]
    public void SdrMonitorToolbar_RendersControlsInsideWindowRegion(int requestedWidth, int requestedHeight)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SHAREX_RUN_RECORD_FORM_TESTS"),
            "1",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Screen? screen = Screen.AllScreens.FirstOrDefault(candidate =>
            !WindowsGraphicsCapture.HasActiveHdrDisplay(candidate.Bounds));
        Assert.NotNull(screen);

        Exception? failure = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                Rectangle workingArea = screen!.WorkingArea;
                int width = Math.Min(requestedWidth, workingArea.Width - 100);
                int height = Math.Min(requestedHeight, workingArea.Height - 100);
                var captureRegion = new Rectangle(
                    workingArea.Left + 50,
                    workingArea.Top + 50,
                    width,
                    height);

                using var form = new ScreenRecordForm(captureRegion)
                {
                    ActivateWindow = false
                };
                form.Show();
                form.ChangeState(ScreenRecordState.AfterStart);
                form.ChangeState(ScreenRecordState.AfterRecordingStart);
                for (int i = 0; i < 20; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }

                Panel panel = Assert.Single(form.Controls.OfType<Panel>());
                Assert.True(panel.Visible);
                Assert.Equal(captureRegion, form.RecordingRegion);
                Assert.All(panel.Controls.Cast<Control>(), control =>
                {
                    Assert.True(control.Visible, $"{control.Name} was hidden.");
                    Point center = form.PointToClient(control.PointToScreen(
                        new Point(control.Width / 2, control.Height / 2)));
                    Assert.True(
                        form.Region?.IsVisible(center) != false,
                        $"{control.Name} was excluded from the form region at {center}.");
                });

                string? outputPath = Environment.GetEnvironmentVariable(
                    "SHAREX_RECORD_FORM_TEST_OUTPUT");
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = Path.Combine(
                        Path.GetDirectoryName(outputPath)!,
                        $"{Path.GetFileNameWithoutExtension(outputPath)}-{width}x{height}{Path.GetExtension(outputPath)}");
                    using var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(form.Location, Point.Empty, form.Size);
                    }

                    bitmap.Save(outputPath, ImageFormat.Png);
                }

                form.Close();
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The toolbar UI thread did not exit.");
        Assert.Null(failure);
    }
}
