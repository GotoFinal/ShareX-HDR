using ShareX.HelpersLib;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib.Tests.Imaging;

public class TaskSettingsImageUiTests
{
    [Fact]
    public void TaskSettings_ImageAndHdrControlsPersistOptionsAndFitThePanel()
    {
        if (Environment.GetEnvironmentVariable("SHAREX_RUN_TASK_SETTINGS_UI_TESTS") != "1") return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyControls(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyControls()
    {
        Type program = typeof(TaskSettings).Assembly.GetType("ShareX.Program")!;
        PropertyInfo settingsProperty = program.GetProperty("Settings", BindingFlags.Static | BindingFlags.NonPublic)!;
        PropertyInfo taskProperty = program.GetProperty("DefaultTaskSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousSettings = settingsProperty.GetValue(null);
        object? previousTask = taskProperty.GetValue(null);
        try
        {
            var settings = new TaskSettings();
            settingsProperty.SetValue(null, new ApplicationConfig());
            taskProperty.SetValue(null, settings);
            using var form = new OffscreenSettingsForm(settings);
            form.Show();
            Application.DoEvents();
            TabToTreeView tabs = Get<TabToTreeView>(form, "tttvMain");
            TabPage imagePage = Get<TabPage>(form, "tpQuality");
            tabs.NavigateToTabPage(imagePage);
            form.PerformLayout();
            Application.DoEvents();
            ComboBox format = Get<ComboBox>(form, "cbImageFormat");
            Assert.Contains("AVIF", format.Items.Cast<string>());
            Assert.Contains("EXR", format.Items.Cast<string>());
            format.SelectedIndex = (int)EImageFormat.AVIF;
            NumericUpDown quality = Get<NumericUpDown>(form, "nudImageAvifQuality");
            quality.Value = 77;
            Assert.Equal(77, settings.ImageSettings.ImageAVIFQuality);
            ComboBox fallback = Get<ComboBox>(form, "cbImageSizeFallbackFormat");
            fallback.SelectedItem = EImageFormat.AVIF;
            Assert.Equal(EImageFormat.AVIF, settings.ImageSettings.ImageSizeFallbackFormat);
            Assert.False(Get<CheckBox>(form, "cbImageAutoJPEGQuality").Enabled);
            Assert.DoesNotContain(Get<Panel>(form, "pImage").Controls.Cast<Control>(),
                control => control.Visible && (control.Right > control.Parent!.ClientSize.Width ||
                    control.Bottom > control.Parent.ClientSize.Height));
            Assert.True(quality.Visible);
            SavePanel(Get<Panel>(form, "pImage"), "image-format-controls.png");
            fallback.SelectedItem = EImageFormat.JPEG;
            Assert.True(Get<CheckBox>(form, "cbImageAutoJPEGQuality").Enabled);
            format.SelectedIndex = (int)EImageFormat.PNG;
            Assert.False(quality.Visible);
            Assert.Equal(280, Get<Label>(form, "lblImageFileExist").Top);

            Panel hdrPanel = Get<Panel>(form, "pHdrFileOutput");
            tabs.NavigateToTabPage((TabPage)hdrPanel.Parent!);
            Application.DoEvents();
            form.PerformLayout();
            hdrPanel.Parent!.PerformLayout();
            Assert.True(hdrPanel.Width >= 500, $"HDR panel width was {hdrPanel.Width}.");
            Assert.DoesNotContain(hdrPanel.Controls.Cast<Control>(),
                control => control.Text.Contains("Ultra HDR JPEG is the recommended shareable format"));
            CheckBox native = Get<CheckBox>(form, "cbUseNativeSdrCapture");
            Assert.True(native.Checked);
            native.Checked = false;
            Assert.False(settings.CaptureSettings.HdrSettings.UseNativeSdrCapture);
            native.Checked = true;
            SavePanel(hdrPanel, "hdr-output-controls.png");
        }
        finally
        {
            settingsProperty.SetValue(null, previousSettings);
            taskProperty.SetValue(null, previousTask);
        }
    }

    private static T Get<T>(Form form, string field) where T : class =>
        (T)typeof(TaskSettingsForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private sealed class OffscreenSettingsForm : TaskSettingsForm
    {
        protected override bool ShowWithoutActivation => true;
        public OffscreenSettingsForm(TaskSettings settings) : base(settings, isDefault: true)
        {
            ShowInTaskbar = false;
            Opacity = 0;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
        }
    }

    private static void SavePanel(Panel panel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("SHAREX_TASK_SETTINGS_UI_OUTPUT_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        DockStyle previousDock = panel.Dock;
        Size previousSize = panel.Size;
        try
        {
            panel.Dock = DockStyle.None;
            panel.Size = new Size(previousSize.Width, Math.Max(previousSize.Height, panel.DisplayRectangle.Height + 8));
            using var preview = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height);
            panel.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
            preview.Save(Path.Combine(directory, fileName), ImageFormat.Png);
        }
        finally
        {
            panel.Size = previousSize;
            panel.Dock = previousDock;
        }
    }
}
