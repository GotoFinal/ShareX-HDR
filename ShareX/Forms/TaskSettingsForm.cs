#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.Properties;
using ShareX.ScreenCaptureLib;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public partial class TaskSettingsForm : Form
    {
        public TaskSettings TaskSettings { get; private set; }
        public bool IsDefault { get; private set; }

        private ToolStripDropDownItem tsmiImageFileUploaders, tsmiTextFileUploaders;
        private bool loaded;
        private Panel pObsGameCapture;
        private CheckBox cbObsGameCaptureEnabled;
        private CheckBox cbObsReuseExistingHook;
        private CheckBox cbObsCaptureThirdPartyOverlays;
        private TextBox txtObsGameProcesses;
        private TextBox txtObsInstallationPath;
        private ComboBox cbObsAlphaMode;
        private ComboBox cbObsCaptureFrameRate;
        private ComboBox cbObsCursorMode;
        private ComboBox cbHdrGameWindowPromotion;
        private ComboBox cbObsRgb10A2Interpretation;
        private NumericUpDown nudObsSessionIdleTimeout;
        private Panel pHdrFileOutput;
        private ComboBox cbHdrOutputMode;
        private ComboBox cbHdrClipboardOutputMode;
        private ComboBox cbHdrClipboardFileFormat;
        private ComboBox cbHdrFileFormat;
        private ComboBox cbHdrOpenExrExposure;
        private Label lblHdrEncoderAvailability;
        private CheckBox cbHdrUploadWithFileUploader;
        private CheckBox cbHdrFlattenTransparencyForUltraHdr;
        private NumericUpDown nudHdrMasteringMaximumNits;
        private NumericUpDown nudHdrMasteringMinimumNits;
        private NumericUpDown nudHdrJpegQuality;
        private NumericUpDown nudHdrGainMapQuality;
        private Label lblHdrJpegQuality;
        private Label lblHdrGainMapQuality;
        private Label lblHdrAvifQuality;
        private Label lblHdrAvifSpeed;
        private NumericUpDown nudHdrAvifQuality;
        private NumericUpDown nudHdrAvifSpeed;
        private ComboBox cbHdrMixedMonitorBrightnessMode;
        private NumericUpDown nudHdrMixedMonitorWhiteNits;
        private Panel pHdrToneMapping;
        private ComboBox cbHdrPaperWhiteMode;
        private NumericUpDown nudHdrPaperWhiteNits;
        private Label lblHdrPaperWhiteNits;
        private Label lblScreenRecordingHdrMode;
        private ComboBox cbScreenRecordingHdrMode;
        private Label lblScreenRecordingHdrInfo;

        private sealed class HdrFileFormatItem
        {
            public HdrFileFormat Format { get; }
            private string Description { get; }

            public HdrFileFormatItem(HdrFileFormat format)
            {
                Format = format;
                Description = format.GetDescription();
            }

            public override string ToString() => Description;
        }

        private sealed class HdrToneMappingModeItem
        {
            public HdrToneMappingMode Mode { get; }
            private string Description { get; }

            public HdrToneMappingModeItem(HdrToneMappingMode mode)
            {
                Mode = mode;
                Description = mode.GetDescription();
            }

            public override string ToString() => Description;
        }

        private sealed class ObsEnumItem<T> where T : struct, Enum
        {
            public T Value { get; }
            public string Description { get; }

            public ObsEnumItem(T value)
            {
                Value = value;
                Description = value.GetDescription();
            }

            public override string ToString() => Description;
        }

        public TaskSettingsForm(TaskSettings hotkeySetting, bool isDefault = false)
        {
            InitializeComponent();
            TaskSettings = hotkeySetting;
            IsDefault = isDefault;
            InitializeHdrScreenRecordingControls();
            InitializeHdrToneMappingControls();
            InitializeObsGameCaptureControls();
            InitializeHdrFileOutputControls();
            ShareXResources.ApplyTheme(this, true);

            tsmiURLShorteners.Image = ShareXResources.IsDarkTheme ? Resources.edit_scale_white : Resources.edit_scale;

            UpdateWindowTitle();

            if (IsDefault)
            {
                tcTaskSettings.TabPages.Remove(tpTask);
                cbOverrideGeneralSettings.Visible = cbOverrideImageSettings.Visible = cbOverrideCaptureSettings.Visible = cbOverrideActions.Visible =
                    cbOverrideUploadSettings.Visible = cbOverrideToolsSettings.Visible = cbOverrideAdvancedSettings.Visible = false;
            }
            else
            {
                #region Task

                AddEnumItemsContextMenu<HotkeyType>(x =>
                {
                    TaskSettings.Job = x;
                    UpdateWindowTitle();
                }, cmsTask);
                SetEnumCheckedContextMenu(TaskSettings.Job, cmsTask);

                tbDescription.Text = TaskSettings.Description;

                cbOverrideAfterCaptureSettings.Checked = !TaskSettings.UseDefaultAfterCaptureJob;
                btnAfterCapture.Enabled = !TaskSettings.UseDefaultAfterCaptureJob;
                AddMultiEnumItemsContextMenu<AfterCaptureTasks>(x => TaskSettings.AfterCaptureJob = TaskSettings.AfterCaptureJob.Swap(x), cmsAfterCapture);
                SetMultiEnumCheckedContextMenu(TaskSettings.AfterCaptureJob, cmsAfterCapture);

                cbOverrideAfterUploadSettings.Checked = !TaskSettings.UseDefaultAfterUploadJob;
                btnAfterUpload.Enabled = !TaskSettings.UseDefaultAfterUploadJob;
                AddMultiEnumItemsContextMenu<AfterUploadTasks>(x => TaskSettings.AfterUploadJob = TaskSettings.AfterUploadJob.Swap(x), cmsAfterUpload);
                SetMultiEnumCheckedContextMenu(TaskSettings.AfterUploadJob, cmsAfterUpload);

                cbOverrideDestinationSettings.Checked = !TaskSettings.UseDefaultDestinations;
                btnDestinations.Enabled = !TaskSettings.UseDefaultDestinations;
                AddEnumItems<ImageDestination>(x =>
                {
                    TaskSettings.ImageDestination = x;

                    if (x == ImageDestination.FileUploader)
                    {
                        SetEnumChecked(TaskSettings.ImageFileDestination, tsmiImageFileUploaders);
                    }
                    else
                    {
                        MainForm.Uncheck(tsmiImageFileUploaders);
                    }
                }, tsmiImageUploaders);
                tsmiImageFileUploaders = (ToolStripDropDownItem)tsmiImageUploaders.DropDownItems[tsmiImageUploaders.DropDownItems.Count - 1];
                AddEnumItems<FileDestination>(x =>
                {
                    TaskSettings.ImageFileDestination = x;
                    tsmiImageFileUploaders.PerformClick();
                }, tsmiImageFileUploaders);
                SetEnumChecked(TaskSettings.ImageDestination, tsmiImageUploaders);
                MainForm.SetImageFileDestinationChecked(TaskSettings.ImageDestination, TaskSettings.ImageFileDestination, tsmiImageFileUploaders);
                AddEnumItems<TextDestination>(x =>
                {
                    TaskSettings.TextDestination = x;

                    if (x == TextDestination.FileUploader)
                    {
                        SetEnumChecked(TaskSettings.TextFileDestination, tsmiTextFileUploaders);
                    }
                    else
                    {
                        MainForm.Uncheck(tsmiTextFileUploaders);
                    }
                }, tsmiTextUploaders);
                tsmiTextFileUploaders = (ToolStripDropDownItem)tsmiTextUploaders.DropDownItems[tsmiTextUploaders.DropDownItems.Count - 1];
                AddEnumItems<FileDestination>(x =>
                {
                    TaskSettings.TextFileDestination = x;
                    tsmiTextFileUploaders.PerformClick();
                }, tsmiTextFileUploaders);
                SetEnumChecked(TaskSettings.TextDestination, tsmiTextUploaders);
                MainForm.SetTextFileDestinationChecked(TaskSettings.TextDestination, TaskSettings.TextFileDestination, tsmiTextFileUploaders);
                AddEnumItems<FileDestination>(x => TaskSettings.FileDestination = x, tsmiFileUploaders);
                SetEnumChecked(TaskSettings.FileDestination, tsmiFileUploaders);
                AddEnumItems<UrlShortenerType>(x => TaskSettings.URLShortenerDestination = x, tsmiURLShorteners);
                SetEnumChecked(TaskSettings.URLShortenerDestination, tsmiURLShorteners);
                AddEnumItems<URLSharingServices>(x => TaskSettings.URLSharingServiceDestination = x, tsmiURLSharingServices);
                SetEnumChecked(TaskSettings.URLSharingServiceDestination, tsmiURLSharingServices);
                UpdateDestinationStates();

                if (Program.UploadersConfig != null)
                {
                    cbOverrideFTPAccount.Enabled = cbFTPAccounts.Enabled = Program.UploadersConfig.FTPAccountList.Count > 0;

                    if (Program.UploadersConfig.FTPAccountList.Count > 0)
                    {
                        cbOverrideFTPAccount.Checked = TaskSettings.OverrideFTP;
                        cbFTPAccounts.Enabled = TaskSettings.OverrideFTP;
                        cbFTPAccounts.Items.Clear();
                        cbFTPAccounts.Items.AddRange(Program.UploadersConfig.FTPAccountList.ToArray());
                        cbFTPAccounts.SelectedIndex = TaskSettings.FTPIndex.BetweenOrDefault(0, Program.UploadersConfig.FTPAccountList.Count - 1);
                    }

                    cbOverrideCustomUploader.Enabled = cbCustomUploaders.Enabled = Program.UploadersConfig.CustomUploadersList.Count > 0;

                    if (Program.UploadersConfig.CustomUploadersList.Count > 0)
                    {
                        cbOverrideCustomUploader.Checked = TaskSettings.OverrideCustomUploader;
                        cbCustomUploaders.Enabled = TaskSettings.OverrideCustomUploader;
                        cbCustomUploaders.Items.Clear();
                        cbCustomUploaders.Items.AddRange(Program.UploadersConfig.CustomUploadersList.ToArray());
                        cbCustomUploaders.SelectedIndex = TaskSettings.CustomUploaderIndex.BetweenOrDefault(0, Program.UploadersConfig.CustomUploadersList.Count - 1);
                    }
                }

                cbOverrideScreenshotsFolder.Checked = TaskSettings.OverrideScreenshotsFolder;
                CodeMenu screenshotsFolderMenu = CodeMenu.Create<CodeMenuEntryFilename>(txtScreenshotsFolder, CodeMenuEntryFilename.t, CodeMenuEntryFilename.pn,
                    CodeMenuEntryFilename.i, CodeMenuEntryFilename.width, CodeMenuEntryFilename.height, CodeMenuEntryFilename.n);
                screenshotsFolderMenu.MenuLocationBottom = true;
                txtScreenshotsFolder.Text = TaskSettings.ScreenshotsFolder;
                txtScreenshotsFolder.Enabled = btnScreenshotsFolderBrowse.Enabled = TaskSettings.OverrideScreenshotsFolder;

                UpdateTaskTabMenuNames();

                #endregion Task

                cbOverrideGeneralSettings.Checked = !TaskSettings.UseDefaultGeneralSettings;
                cbOverrideImageSettings.Checked = !TaskSettings.UseDefaultImageSettings;
                cbOverrideCaptureSettings.Checked = !TaskSettings.UseDefaultCaptureSettings;
                cbOverrideActions.Checked = !TaskSettings.UseDefaultActions;
                cbOverrideUploadSettings.Checked = !TaskSettings.UseDefaultUploadSettings;
                cbOverrideToolsSettings.Checked = !TaskSettings.UseDefaultToolsSettings;
                cbOverrideAdvancedSettings.Checked = !TaskSettings.UseDefaultAdvancedSettings;
            }

            UpdateDefaultSettingVisibility();

            tttvMain.MainTabControl = tcTaskSettings;

            #region General

            #region Notifications

            cbPlaySoundAfterCapture.Checked = TaskSettings.GeneralSettings.PlaySoundAfterCapture;
            cbPlaySoundAfterUpload.Checked = TaskSettings.GeneralSettings.PlaySoundAfterUpload;
            cbPlaySoundAfterAction.Checked = TaskSettings.GeneralSettings.PlaySoundAfterAction;
            cbShowToastNotificationAfterTaskCompleted.Checked = TaskSettings.GeneralSettings.ShowToastNotificationAfterTaskCompleted;
            gbToastWindow.Enabled = TaskSettings.GeneralSettings.ShowToastNotificationAfterTaskCompleted;
            nudToastWindowDuration.SetValue((decimal)TaskSettings.GeneralSettings.ToastWindowDuration);
            nudToastWindowFadeDuration.SetValue((decimal)TaskSettings.GeneralSettings.ToastWindowFadeDuration);
            cbToastWindowPlacement.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<ContentAlignment>());
            cbToastWindowPlacement.SelectedIndex = TaskSettings.GeneralSettings.ToastWindowPlacement.GetIndex();
            nudToastWindowSizeWidth.SetValue(TaskSettings.GeneralSettings.ToastWindowSize.Width);
            nudToastWindowSizeHeight.SetValue(TaskSettings.GeneralSettings.ToastWindowSize.Height);
            cbToastWindowLeftClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<ToastClickAction>());
            cbToastWindowLeftClickAction.SelectedIndex = (int)TaskSettings.GeneralSettings.ToastWindowLeftClickAction;
            cbToastWindowRightClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<ToastClickAction>());
            cbToastWindowRightClickAction.SelectedIndex = (int)TaskSettings.GeneralSettings.ToastWindowRightClickAction;
            cbToastWindowMiddleClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<ToastClickAction>());
            cbToastWindowMiddleClickAction.SelectedIndex = (int)TaskSettings.GeneralSettings.ToastWindowMiddleClickAction;
            cbToastWindowAutoHide.Checked = TaskSettings.GeneralSettings.ToastWindowAutoHide;
            cbDisableNotificationsOnFullscreen.Checked = TaskSettings.GeneralSettings.DisableNotificationsOnFullscreen;
            cbUseCustomCaptureSound.Checked = TaskSettings.GeneralSettings.UseCustomCaptureSound;
            txtCustomCaptureSoundPath.Enabled = btnCustomCaptureSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomCaptureSound;
            txtCustomCaptureSoundPath.Text = TaskSettings.GeneralSettings.CustomCaptureSoundPath;
            cbUseCustomTaskCompletedSound.Checked = TaskSettings.GeneralSettings.UseCustomTaskCompletedSound;
            txtCustomTaskCompletedSoundPath.Enabled = btnCustomTaskCompletedSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomTaskCompletedSound;
            txtCustomTaskCompletedSoundPath.Text = TaskSettings.GeneralSettings.CustomTaskCompletedSoundPath;
            cbUseCustomActionCompletedSound.Checked = TaskSettings.GeneralSettings.UseCustomActionCompletedSound;
            txtCustomActionCompletedSoundPath.Enabled = btnCustomActionCompletedSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomActionCompletedSound;
            txtCustomActionCompletedSoundPath.Text = TaskSettings.GeneralSettings.CustomActionCompletedSoundPath;
            cbUseCustomErrorSound.Checked = TaskSettings.GeneralSettings.UseCustomErrorSound;
            txtCustomErrorSoundPath.Enabled = btnCustomErrorSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomErrorSound;
            txtCustomErrorSoundPath.Text = TaskSettings.GeneralSettings.CustomErrorSoundPath;

            #endregion

            #endregion General

            #region Image

            #region General

            cbImageFormat.Items.AddRange(Enum.GetNames(typeof(EImageFormat)));
            cbImageFormat.SelectedIndex = (int)TaskSettings.ImageSettings.ImageFormat;
            cbImagePNGBitDepth.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<PNGBitDepth>());
            cbImagePNGBitDepth.SelectedIndex = (int)TaskSettings.ImageSettings.ImagePNGBitDepth;
            nudImageJPEGQuality.SetValue(TaskSettings.ImageSettings.ImageJPEGQuality);
            cbImageGIFQuality.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<GIFQuality>());
            cbImageGIFQuality.SelectedIndex = (int)TaskSettings.ImageSettings.ImageGIFQuality;
            cbImageAutoUseJPEG.Checked = TaskSettings.ImageSettings.ImageAutoUseJPEG;
            nudImageAutoUseJPEGSize.Enabled = TaskSettings.ImageSettings.ImageAutoUseJPEG;
            cbImageAutoJPEGQuality.Enabled = TaskSettings.ImageSettings.ImageAutoUseJPEG;
            nudImageAutoUseJPEGSize.SetValue(TaskSettings.ImageSettings.ImageAutoUseJPEGSize);
            cbImageAutoJPEGQuality.Checked = TaskSettings.ImageSettings.ImageAutoJPEGQuality;
            cbImageFileExist.Items.Clear();
            cbImageFileExist.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<FileExistAction>());
            cbImageFileExist.SelectedIndex = (int)TaskSettings.ImageSettings.FileExistAction;

            #endregion General

            #region Effects

            cbShowImageEffectsWindowAfterCapture.Checked = TaskSettings.ImageSettings.ShowImageEffectsWindowAfterCapture;
            cbImageEffectOnlyRegionCapture.Checked = TaskSettings.ImageSettings.ImageEffectOnlyRegionCapture;
            cbUseRandomImageEffect.Checked = TaskSettings.ImageSettings.UseRandomImageEffect;

            #endregion Effects

            #region Thumbnail

            nudThumbnailWidth.SetValue(TaskSettings.ImageSettings.ThumbnailWidth);
            nudThumbnailHeight.SetValue(TaskSettings.ImageSettings.ThumbnailHeight);
            txtThumbnailName.Text = TaskSettings.ImageSettings.ThumbnailName;
            lblThumbnailNamePreview.Text = "ImageName" + TaskSettings.ImageSettings.ThumbnailName + ".jpg";
            cbThumbnailIfSmaller.Checked = TaskSettings.ImageSettings.ThumbnailCheckSize;

            #endregion Thumbnail

            #endregion Image

            #region Capture

            #region General

            cbShowCursor.Checked = TaskSettings.CaptureSettings.ShowCursor;
            nudScreenshotDelay.SetValue(TaskSettings.CaptureSettings.ScreenshotDelay);
            cbCaptureTransparent.Checked = TaskSettings.CaptureSettings.CaptureTransparent;
            cbCaptureShadow.Enabled = TaskSettings.CaptureSettings.CaptureTransparent;
            cbCaptureShadow.Checked = TaskSettings.CaptureSettings.CaptureShadow;
            nudCaptureShadowOffset.SetValue(TaskSettings.CaptureSettings.CaptureShadowOffset);
            cbCaptureClientArea.Checked = TaskSettings.CaptureSettings.CaptureClientArea;
            cbCaptureAutoHideDesktopIcons.Checked = TaskSettings.CaptureSettings.CaptureAutoHideDesktopIcons;
            cbCaptureAutoHideTaskbar.Checked = TaskSettings.CaptureSettings.CaptureAutoHideTaskbar;
            TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
            TaskSettings.CaptureSettings.HdrSettings.ObsGameCapture ??= new ObsGameCaptureSettings();
            TaskSettings.CaptureSettings.HdrSettings.FileOutput ??= new HdrFileOutputSettings();
            cbUseHDRSupport.Checked = TaskSettings.CaptureSettings.UseHDRSupport;
            cbHDRProcessingBackend.Items.AddRange(Helpers.GetEnumDescriptions<HdrProcessingBackend>());
            cbHDRProcessingBackend.SelectedIndex = (int)TaskSettings.CaptureSettings.HdrSettings.ProcessingBackend;
            cbHDRPeakBrightnessMode.Items.AddRange(Helpers.GetEnumDescriptions<HdrPeakBrightnessMode>());
            cbHDRPeakBrightnessMode.SelectedIndex = (int)TaskSettings.CaptureSettings.HdrSettings.PeakBrightnessMode;
            cbHDRToneMappingMode.Items.AddRange(new object[]
            {
                new HdrToneMappingModeItem(HdrToneMappingMode.ContentAware),
                new HdrToneMappingModeItem(HdrToneMappingMode.PerWindow),
                new HdrToneMappingModeItem(HdrToneMappingMode.Uniform)
            });
            cbHDRToneMappingMode.SelectedItem = cbHDRToneMappingMode.Items
                .OfType<HdrToneMappingModeItem>()
                .FirstOrDefault(x =>
                    x.Mode == TaskSettings.CaptureSettings.HdrSettings.ToneMappingMode);
            SetHdrControlsEnabled(TaskSettings.CaptureSettings.UseHDRSupport);
            nudHDRBrightnessNits.SetValue((decimal)TaskSettings.CaptureSettings.HdrSettings.HdrBrightnessNits);
            LoadHdrToneMappingSettings();
            LoadObsGameCaptureSettings();
            LoadHdrFileOutputSettings();
            nudCaptureCustomRegionX.SetValue(TaskSettings.CaptureSettings.CaptureCustomRegion.X);
            nudCaptureCustomRegionY.SetValue(TaskSettings.CaptureSettings.CaptureCustomRegion.Y);
            nudCaptureCustomRegionWidth.SetValue(TaskSettings.CaptureSettings.CaptureCustomRegion.Width);
            nudCaptureCustomRegionHeight.SetValue(TaskSettings.CaptureSettings.CaptureCustomRegion.Height);
            txtCaptureCustomWindow.Text = TaskSettings.CaptureSettings.CaptureCustomWindow;

            #endregion General

            #region Region capture

            cbRegionCaptureMultiRegionMode.Checked = !TaskSettings.CaptureSettings.SurfaceOptions.QuickCrop;
            cbRegionCaptureMouseRightClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<RegionCaptureAction>());
            cbRegionCaptureMouseRightClickAction.SelectedIndex = (int)TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionRightClick;
            cbRegionCaptureMouseMiddleClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<RegionCaptureAction>());
            cbRegionCaptureMouseMiddleClickAction.SelectedIndex = (int)TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionMiddleClick;
            cbRegionCaptureMouse4ClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<RegionCaptureAction>());
            cbRegionCaptureMouse4ClickAction.SelectedIndex = (int)TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionX1Click;
            cbRegionCaptureMouse5ClickAction.Items.AddRange(Helpers.GetLocalizedEnumDescriptions<RegionCaptureAction>());
            cbRegionCaptureMouse5ClickAction.SelectedIndex = (int)TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionX2Click;
            cbRegionCaptureDetectWindows.Checked = TaskSettings.CaptureSettings.SurfaceOptions.DetectWindows;
            cbRegionCaptureDetectControls.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.DetectWindows;
            cbRegionCaptureDetectControls.Checked = TaskSettings.CaptureSettings.SurfaceOptions.DetectControls;
            nudRegionCaptureBackgroundDimStrength.SetValue(TaskSettings.CaptureSettings.SurfaceOptions.BackgroundDimStrength);
            cbRegionCaptureUseCustomInfoText.Checked = TaskSettings.CaptureSettings.SurfaceOptions.UseCustomInfoText;
            txtRegionCaptureCustomInfoText.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.UseCustomInfoText;
            TaskSettings.CaptureSettings.SurfaceOptions.CustomInfoText = TaskSettings.CaptureSettings.SurfaceOptions.CustomInfoText.Replace("\r\n", "$n").Replace("\n", "$n");
            CodeMenu.Create<CodeMenuEntryPixelInfo>(txtRegionCaptureCustomInfoText);
            txtRegionCaptureCustomInfoText.Text = TaskSettings.CaptureSettings.SurfaceOptions.CustomInfoText;
            cbRegionCaptureSnapSizes.Items.AddRange(TaskSettings.CaptureSettings.SurfaceOptions.SnapSizes.ToArray());
            cbRegionCaptureShowInfo.Checked = TaskSettings.CaptureSettings.SurfaceOptions.ShowInfo;
            cbRegionCaptureShowMagnifier.Checked = TaskSettings.CaptureSettings.SurfaceOptions.ShowMagnifier;
            cbRegionCaptureUseSquareMagnifier.Enabled = nudRegionCaptureMagnifierPixelCount.Enabled = nudRegionCaptureMagnifierPixelSize.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.ShowMagnifier;
            cbRegionCaptureUseSquareMagnifier.Checked = TaskSettings.CaptureSettings.SurfaceOptions.UseSquareMagnifier;
            nudRegionCaptureMagnifierPixelCount.Minimum = RegionCaptureOptions.MagnifierPixelCountMinimum;
            nudRegionCaptureMagnifierPixelCount.Maximum = RegionCaptureOptions.MagnifierPixelCountMaximum;
            nudRegionCaptureMagnifierPixelCount.SetValue(TaskSettings.CaptureSettings.SurfaceOptions.MagnifierPixelCount);
            nudRegionCaptureMagnifierPixelSize.Minimum = RegionCaptureOptions.MagnifierPixelSizeMinimum;
            nudRegionCaptureMagnifierPixelSize.Maximum = RegionCaptureOptions.MagnifierPixelSizeMaximum;
            nudRegionCaptureMagnifierPixelSize.SetValue(TaskSettings.CaptureSettings.SurfaceOptions.MagnifierPixelSize);
            cbRegionCaptureShowCenterCrosshair.Checked = TaskSettings.CaptureSettings.SurfaceOptions.ShowCenterCrosshair;
            cbRegionCaptureShowCrosshair.Checked = TaskSettings.CaptureSettings.SurfaceOptions.ShowCrosshair;
            cbRegionCaptureIsFixedSize.Checked = TaskSettings.CaptureSettings.SurfaceOptions.IsFixedSize;
            nudRegionCaptureFixedSizeWidth.Enabled = nudRegionCaptureFixedSizeHeight.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.IsFixedSize;
            nudRegionCaptureFixedSizeWidth.SetValue(TaskSettings.CaptureSettings.SurfaceOptions.FixedSize.Width);
            nudRegionCaptureFixedSizeHeight.SetValue(TaskSettings.CaptureSettings.SurfaceOptions.FixedSize.Height);
            cbRegionCaptureShowFPS.Checked = TaskSettings.CaptureSettings.SurfaceOptions.ShowFPS;
            nudRegionCaptureFPSLimit.SetValue(TaskSettings.CaptureSettings.SurfaceOptions.FPSLimit);
            cbRegionCaptureActiveMonitorMode.Checked = TaskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            #endregion Region capture

            #region Screen recorder

            if (HelpersOptions.DevMode)
            {
                nudScreenRecordFPS.Maximum = 300;
                nudGIFFPS.Maximum = 60;
            }

            nudScreenRecordFPS.SetValue(TaskSettings.CaptureSettings.ScreenRecordFPS);
            nudGIFFPS.SetValue(TaskSettings.CaptureSettings.GIFFPS);
            cbScreenRecorderFixedDuration.Checked = nudScreenRecorderDuration.Enabled = TaskSettings.CaptureSettings.ScreenRecordFixedDuration;
            nudScreenRecorderDuration.SetValue((decimal)TaskSettings.CaptureSettings.ScreenRecordDuration);
            cbScreenRecordAutoStart.Checked = nudScreenRecorderStartDelay.Enabled = TaskSettings.CaptureSettings.ScreenRecordAutoStart;
            nudScreenRecorderStartDelay.SetValue((decimal)TaskSettings.CaptureSettings.ScreenRecordStartDelay);
            cbScreenRecorderShowCursor.Checked = TaskSettings.CaptureSettings.ScreenRecordShowCursor;
            cbScreenRecordingHdrMode.SelectedIndex = (int)GetScreenRecordingHdrMode();
            cbScreenRecordTwoPassEncoding.Checked = TaskSettings.CaptureSettings.ScreenRecordTwoPassEncoding;
            cbScreenRecordTransparentRegion.Checked = TaskSettings.CaptureSettings.ScreenRecordTransparentRegion;
            cbScreenRecordConfirmAbort.Checked = TaskSettings.CaptureSettings.ScreenRecordAskConfirmationOnAbort;

            #endregion Screen recorder

            #region OCR

            OCROptions ocrOptions = TaskSettings.CaptureSettings.OCROptions;

            try
            {
                OCRLanguage[] languages = OCRHelper.AvailableLanguages.OrderBy(x => x.DisplayName).ToArray();

                if (languages.Length > 0)
                {
                    cbCaptureOCRDefaultLanguage.Items.AddRange(languages);

                    if (ocrOptions.Language == null)
                    {
                        cbCaptureOCRDefaultLanguage.SelectedIndex = 0;
                        ocrOptions.Language = languages[0].LanguageTag;
                    }
                    else
                    {
                        int index = Array.FindIndex(languages, x => x.LanguageTag.Equals(ocrOptions.Language, StringComparison.OrdinalIgnoreCase));

                        if (index >= 0)
                        {
                            cbCaptureOCRDefaultLanguage.SelectedIndex = index;
                        }
                        else
                        {
                            cbCaptureOCRDefaultLanguage.SelectedIndex = 0;
                            ocrOptions.Language = languages[0].LanguageTag;
                        }
                    }
                }
            }
            catch
            {
                cbCaptureOCRDefaultLanguage.Enabled = false;
            }

            cbCaptureOCRSilent.Checked = ocrOptions.Silent;
            cbCaptureOCRAutoCopy.Enabled = !ocrOptions.Silent;
            cbCaptureOCRAutoCopy.Checked = ocrOptions.AutoCopy;
            cbCloseWindowAfterOpenServiceLink.Checked = ocrOptions.CloseWindowAfterOpeningServiceLink;

            #endregion OCR

            #endregion Capture

            #region Upload

            #region File naming

            txtNameFormatPattern.Text = TaskSettings.UploadSettings.NameFormatPattern;
            txtNameFormatPatternActiveWindow.Text = TaskSettings.UploadSettings.NameFormatPatternActiveWindow;
            CodeMenu.Create<CodeMenuEntryFilename>(txtNameFormatPattern, CodeMenuEntryFilename.n, CodeMenuEntryFilename.t, CodeMenuEntryFilename.pn);
            CodeMenu.Create<CodeMenuEntryFilename>(txtNameFormatPatternActiveWindow, CodeMenuEntryFilename.n);
            cbFileUploadUseNamePattern.Checked = TaskSettings.UploadSettings.FileUploadUseNamePattern;
            nudAutoIncrementNumber.SetValue(Program.Settings.NameParserAutoIncrementNumber);
            UpdateNameFormatPreviews();
            cbNameFormatCustomTimeZone.Checked = cbNameFormatTimeZone.Enabled = TaskSettings.UploadSettings.UseCustomTimeZone;
            cbNameFormatTimeZone.Items.AddRange(TimeZoneInfo.GetSystemTimeZones().ToArray());
            for (int i = 0; i < cbNameFormatTimeZone.Items.Count; i++)
            {
                if (cbNameFormatTimeZone.Items[i].Equals(TaskSettings.UploadSettings.CustomTimeZone))
                {
                    cbNameFormatTimeZone.SelectedIndex = i;
                    break;
                }
            }
            cbFileUploadReplaceProblematicCharacters.Checked = TaskSettings.UploadSettings.FileUploadReplaceProblematicCharacters;
            cbURLRegexReplace.Checked = TaskSettings.UploadSettings.URLRegexReplace;
            lblURLRegexReplacePattern.Enabled = txtURLRegexReplacePattern.Enabled =
                lblURLRegexReplaceReplacement.Enabled = txtURLRegexReplaceReplacement.Enabled = TaskSettings.UploadSettings.URLRegexReplace;
            txtURLRegexReplacePattern.Text = TaskSettings.UploadSettings.URLRegexReplacePattern;
            txtURLRegexReplaceReplacement.Text = TaskSettings.UploadSettings.URLRegexReplaceReplacement;

            #endregion File naming

            #region Clipboard upload

            cbClipboardUploadURLContents.Checked = TaskSettings.UploadSettings.ClipboardUploadURLContents;
            cbClipboardUploadShortenURL.Checked = TaskSettings.UploadSettings.ClipboardUploadShortenURL;
            cbClipboardUploadShareURL.Checked = TaskSettings.UploadSettings.ClipboardUploadShareURL;
            cbClipboardUploadAutoIndexFolder.Checked = TaskSettings.UploadSettings.ClipboardUploadAutoIndexFolder;

            #endregion Clipboard upload

            #region Uploader filters

            cbUploaderFiltersDestination.Items.AddRange(UploaderFactory.AllGenericUploaderServices.OrderBy(x => x.ServiceName).ToArray());

            if (TaskSettings.UploadSettings.UploaderFilters == null) TaskSettings.UploadSettings.UploaderFilters = new List<UploaderFilter>();

            foreach (UploaderFilter filter in TaskSettings.UploadSettings.UploaderFilters)
            {
                AddUploaderFilterToList(filter);
            }

            #endregion Uploader filters

            #endregion Upload

            #region Actions

            TaskHelpers.AddDefaultExternalPrograms(TaskSettings);
            TaskSettings.ExternalPrograms.ForEach(AddFileAction);

            #endregion Actions

            #region Watch folders

            cbWatchFolderEnabled.Checked = TaskSettings.WatchFolderEnabled;

            if (TaskSettings.WatchFolderList == null)
            {
                TaskSettings.WatchFolderList = new List<WatchFolderSettings>();
            }
            else
            {
                foreach (WatchFolderSettings watchFolder in TaskSettings.WatchFolderList)
                {
                    WatchFolderAdd(watchFolder);
                }
            }

            #endregion Watch folders

            #region Tools

            #region General

            cbImageEditorUseLegacyImageEditor.Checked = TaskSettings.ToolsSettings.UseLegacyImageEditor;

            CodeMenu.Create<CodeMenuEntryPixelInfo>(txtToolsScreenColorPickerFormat);
            txtToolsScreenColorPickerFormat.Text = TaskSettings.ToolsSettings.ScreenColorPickerFormat;

            CodeMenu.Create<CodeMenuEntryPixelInfo>(txtToolsScreenColorPickerFormatCtrl);
            txtToolsScreenColorPickerFormatCtrl.Text = TaskSettings.ToolsSettings.ScreenColorPickerFormatCtrl;

            CodeMenu.Create<CodeMenuEntryPixelInfo>(txtToolsScreenColorPickerInfoText);
            txtToolsScreenColorPickerInfoText.Text = TaskSettings.ToolsSettings.ScreenColorPickerInfoText;

            #endregion

            #endregion Tools

            #region Advanced

            pgTaskSettings.SelectedObject = TaskSettings.AdvancedSettings;

            #endregion Advanced

            loaded = true;
        }

        private void TaskSettingsForm_Resize(object sender, EventArgs e)
        {
            Refresh();
        }

        private void tttvMain_TabChanged(TabPage tabPage)
        {
            if (IsDefault && (tabPage == tpGeneralMain || tabPage == tpUploadMain))
            {
                tttvMain.SelectChildNode();
            }
        }

        private void InitializeHdrScreenRecordingControls()
        {
            lblScreenRecordingHdrMode = new Label
            {
                AutoSize = true,
                Location = new Point(8, 252),
                Text = "HDR desktop recording:",
                Enabled = TaskSettings.CaptureSettings.UseHDRSupport
            };
            cbScreenRecordingHdrMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(170, 247),
                Size = new Size(360, 23),
                Enabled = TaskSettings.CaptureSettings.UseHDRSupport
            };
            cbScreenRecordingHdrMode.Items.AddRange(Helpers.GetEnumDescriptions<ScreenRecordingHdrMode>());
            cbScreenRecordingHdrMode.SelectedIndex = (int)GetScreenRecordingHdrMode();
            cbScreenRecordingHdrMode.SelectedIndexChanged += (_, _) =>
            {
                if (cbScreenRecordingHdrMode.SelectedIndex >= 0)
                {
                    ScreenRecordingHdrMode mode =
                        (ScreenRecordingHdrMode)cbScreenRecordingHdrMode.SelectedIndex;
                    TaskSettings.CaptureSettings.ScreenRecordHdrMode = mode;
                    TaskSettings.CaptureSettings.ScreenRecordUseHdrCapture =
                        mode != ScreenRecordingHdrMode.Disabled;
                }
            };
            lblScreenRecordingHdrInfo = new Label
            {
                AutoSize = false,
                Location = new Point(8, 278),
                Size = new Size(522, 45),
                Text = "Native HDR10 records HEVC Main10 MP4 using the HDR output mastering luminance. " +
                    "H.264 NVENC/QSV selections are promoted to HEVC; other incompatible codecs use x265.",
                Enabled = TaskSettings.CaptureSettings.UseHDRSupport
            };
            tpScreenRecorder.Controls.Add(lblScreenRecordingHdrMode);
            tpScreenRecorder.Controls.Add(cbScreenRecordingHdrMode);
            tpScreenRecorder.Controls.Add(lblScreenRecordingHdrInfo);
        }

        private ScreenRecordingHdrMode GetScreenRecordingHdrMode()
        {
            if (!TaskSettings.CaptureSettings.ScreenRecordUseHdrCapture)
            {
                return ScreenRecordingHdrMode.Disabled;
            }

            ScreenRecordingHdrMode mode = TaskSettings.CaptureSettings.ScreenRecordHdrMode;
            return Enum.IsDefined(mode) ? mode : ScreenRecordingHdrMode.ToneMapToSdr;
        }

        private void UpdateWindowTitle()
        {
            if (IsDefault)
            {
                Text = "ShareX - " + Resources.TaskSettingsForm_UpdateWindowTitle_Task_settings;
            }
            else
            {
                Text = "ShareX - " + string.Format(Resources.TaskSettingsForm_UpdateWindowTitle_Task_settings_for__0_, TaskSettings);
            }
        }

        private void UpdateDefaultSettingVisibility()
        {
            if (!IsDefault)
            {
                tpNotifications.Enabled = !TaskSettings.UseDefaultGeneralSettings;
                pImage.Enabled = tpEffects.Enabled = tpThumbnail.Enabled = !TaskSettings.UseDefaultImageSettings;
                pCapture.Enabled = tpRegionCapture.Enabled = tpScreenRecorder.Enabled = tpOCR.Enabled = !TaskSettings.UseDefaultCaptureSettings;
                pActions.Enabled = !TaskSettings.UseDefaultActions;
                tpFileNaming.Enabled = tpUploadClipboard.Enabled = tpUploaderFilters.Enabled = !TaskSettings.UseDefaultUploadSettings;
                pTools.Enabled = !TaskSettings.UseDefaultToolsSettings;
                pgTaskSettings.Enabled = !TaskSettings.UseDefaultAdvancedSettings;
            }
        }

        #region Task

        private void UpdateDestinationStates()
        {
            if (Program.UploadersConfig != null)
            {
                EnableDisableToolStripMenuItems<ImageDestination>(tsmiImageUploaders);
                EnableDisableToolStripMenuItems<FileDestination>(tsmiImageFileUploaders);
                EnableDisableToolStripMenuItems<TextDestination>(tsmiTextUploaders);
                EnableDisableToolStripMenuItems<FileDestination>(tsmiTextFileUploaders);
                EnableDisableToolStripMenuItems<FileDestination>(tsmiFileUploaders);
                EnableDisableToolStripMenuItems<UrlShortenerType>(tsmiURLShorteners);
                EnableDisableToolStripMenuItems<URLSharingServices>(tsmiURLSharingServices);
            }
        }

        private void AddEnumItemsContextMenu<T>(Action<T> selectedEnum, params ToolStripDropDown[] parents) where T : Enum
        {
            EnumInfo[] enums = Helpers.GetEnums<T>().OfType<Enum>().Select(x => new EnumInfo(x)).ToArray();

            foreach (ToolStripDropDown parent in parents)
            {
                foreach (EnumInfo enumInfo in enums)
                {
                    ToolStripMenuItem tsmi = new ToolStripMenuItem(enumInfo.Description.Replace("&", "&&"));
                    tsmi.Image = TaskHelpers.FindMenuIcon(enumInfo.Value);
                    tsmi.Tag = enumInfo;

                    tsmi.Click += (sender, e) =>
                    {
                        SetEnumCheckedContextMenu(enumInfo, parents);

                        selectedEnum((T)enumInfo.Value);

                        UpdateTaskTabMenuNames();
                    };

                    if (!string.IsNullOrEmpty(enumInfo.Category))
                    {
                        ToolStripMenuItem tsmiParent = parent.Items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text == enumInfo.Category);

                        if (tsmiParent == null)
                        {
                            tsmiParent = new ToolStripMenuItem(enumInfo.Category);
                            parent.Items.Add(tsmiParent);
                        }

                        tsmiParent.DropDownItems.Add(tsmi);
                    }
                    else
                    {
                        parent.Items.Add(tsmi);
                    }
                }
            }
        }

        private void SetEnumCheckedContextMenu(Enum value, params ToolStripDropDown[] parents)
        {
            SetEnumCheckedContextMenu(new EnumInfo(value), parents);
        }

        private void SetEnumCheckedContextMenu(EnumInfo enumInfo, params ToolStripDropDown[] parents)
        {
            foreach (ToolStripDropDown parent in parents)
            {
                foreach (ToolStripMenuItem tsmiParent in parent.Items)
                {
                    EnumInfo currentEnumInfo;

                    if (tsmiParent.DropDownItems.Count > 0)
                    {
                        foreach (ToolStripMenuItem tsmiCategoryParent in tsmiParent.DropDownItems)
                        {
                            currentEnumInfo = (EnumInfo)tsmiCategoryParent.Tag;
                            tsmiCategoryParent.Checked = currentEnumInfo.Value.Equals(enumInfo.Value);
                        }
                    }
                    else
                    {
                        currentEnumInfo = (EnumInfo)tsmiParent.Tag;
                        tsmiParent.Checked = currentEnumInfo.Value.Equals(enumInfo.Value);
                    }
                }
            }
        }

        private void AddMultiEnumItemsContextMenu<T>(Action<T> selectedEnum, params ToolStripDropDown[] parents) where T : Enum
        {
            string[] enums = Helpers.GetLocalizedEnumDescriptions<T>().Skip(1).Select(x => x.Replace("&", "&&")).ToArray();

            foreach (ToolStripDropDown parent in parents)
            {
                for (int i = 0; i < enums.Length; i++)
                {
                    ToolStripMenuItem tsmi = new ToolStripMenuItem(enums[i]);
                    tsmi.Image = TaskHelpers.FindMenuIcon<T>(i + 1);

                    int index = i;

                    tsmi.Click += (sender, e) =>
                    {
                        foreach (ToolStripDropDown parent2 in parents)
                        {
                            ToolStripMenuItem tsmi2 = (ToolStripMenuItem)parent2.Items[index];
                            tsmi2.Checked = !tsmi2.Checked;
                        }

                        selectedEnum((T)Enum.ToObject(typeof(T), 1 << index));

                        UpdateTaskTabMenuNames();
                    };

                    parent.Items.Add(tsmi);
                }
            }
        }

        private void SetMultiEnumCheckedContextMenu(Enum value, params ToolStripDropDown[] parents)
        {
            for (int i = 0; i < parents[0].Items.Count; i++)
            {
                foreach (ToolStripDropDown parent in parents)
                {
                    ToolStripMenuItem tsmi = (ToolStripMenuItem)parent.Items[i];
                    tsmi.Checked = value.HasFlag(1 << i);
                }
            }
        }

        private void AddEnumItems<T>(Action<T> selectedEnum, params ToolStripDropDownItem[] parents)
        {
            string[] enums = Helpers.GetLocalizedEnumDescriptions<T>();

            foreach (ToolStripDropDownItem parent in parents)
            {
                for (int i = 0; i < enums.Length; i++)
                {
                    ToolStripMenuItem tsmi = new ToolStripMenuItem(enums[i]);

                    int index = i;

                    tsmi.Click += (sender, e) =>
                    {
                        foreach (ToolStripDropDownItem parent2 in parents)
                        {
                            for (int i2 = 0; i2 < enums.Length; i2++)
                            {
                                ToolStripMenuItem tsmi2 = (ToolStripMenuItem)parent2.DropDownItems[i2];
                                tsmi2.Checked = index == i2;
                            }
                        }

                        selectedEnum((T)Enum.ToObject(typeof(T), index));

                        UpdateTaskTabMenuNames();
                    };

                    parent.DropDownItems.Add(tsmi);
                }
            }
        }

        private void SetEnumChecked(Enum value, params ToolStripDropDownItem[] parents)
        {
            int index = value.GetIndex();

            foreach (ToolStripDropDownItem parent in parents)
            {
                ((ToolStripMenuItem)parent.DropDownItems[index]).Checked = true;
            }
        }

        private void EnableDisableToolStripMenuItems<T>(params ToolStripDropDownItem[] parents)
        {
            foreach (ToolStripDropDownItem parent in parents)
            {
                for (int i = 0; i < parent.DropDownItems.Count; i++)
                {
                    parent.DropDownItems[i].Enabled = UploadersConfigValidator.Validate<T>(i, Program.UploadersConfig);
                }
            }
        }

        private void UpdateTaskTabMenuNames()
        {
            btnTask.Text = TaskSettings.Job.GetLocalizedDescription();
            btnTask.Image = TaskHelpers.FindMenuIcon(TaskSettings.Job);

            btnAfterCapture.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_After_capture___0_,
                string.Join(", ", TaskSettings.AfterCaptureJob.GetFlags().Select(x => x.GetLocalizedDescription())));

            btnAfterUpload.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_After_upload___0_,
                string.Join(", ", TaskSettings.AfterUploadJob.GetFlags().Select(x => x.GetLocalizedDescription())));

            string imageUploader = TaskSettings.ImageDestination == ImageDestination.FileUploader ?
                TaskSettings.ImageFileDestination.GetLocalizedDescription() : TaskSettings.ImageDestination.GetLocalizedDescription();
            tsmiImageUploaders.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_Image_uploader___0_, imageUploader);

            string textUploader = TaskSettings.TextDestination == TextDestination.FileUploader ?
                TaskSettings.TextFileDestination.GetLocalizedDescription() : TaskSettings.TextDestination.GetLocalizedDescription();
            tsmiTextUploaders.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_Text_uploader___0_, textUploader);

            tsmiFileUploaders.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_File_uploader___0_, TaskSettings.FileDestination.GetLocalizedDescription());

            tsmiURLShorteners.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_URL_shortener___0_, TaskSettings.URLShortenerDestination.GetLocalizedDescription());

            tsmiURLSharingServices.Text = string.Format(Resources.TaskSettingsForm_UpdateUploaderMenuNames_URL_sharing_service___0_, TaskSettings.URLSharingServiceDestination.GetLocalizedDescription());
        }

        private void tbDescription_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.Description = tbDescription.Text;
            UpdateWindowTitle();
        }

        private void cbUseDefaultAfterCaptureSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultAfterCaptureJob = !cbOverrideAfterCaptureSettings.Checked;
            btnAfterCapture.Enabled = !TaskSettings.UseDefaultAfterCaptureJob;
        }

        private void cbUseDefaultAfterUploadSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultAfterUploadJob = !cbOverrideAfterUploadSettings.Checked;
            btnAfterUpload.Enabled = !TaskSettings.UseDefaultAfterUploadJob;
        }

        private void cbUseDefaultDestinationSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultDestinations = !cbOverrideDestinationSettings.Checked;
            btnDestinations.Enabled = !TaskSettings.UseDefaultDestinations;
        }

        private void cbOverrideFTPAccount_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.OverrideFTP = cbOverrideFTPAccount.Checked;
            cbFTPAccounts.Enabled = TaskSettings.OverrideFTP;
        }

        private void cbFTPAccounts_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.FTPIndex = cbFTPAccounts.SelectedIndex;
        }

        private void cbOverrideCustomUploader_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.OverrideCustomUploader = cbOverrideCustomUploader.Checked;
            cbCustomUploaders.Enabled = TaskSettings.OverrideCustomUploader;
        }

        private void cbCustomUploaders_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.CustomUploaderIndex = cbCustomUploaders.SelectedIndex;
        }

        private void cbOverrideScreenshotsFolder_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.OverrideScreenshotsFolder = cbOverrideScreenshotsFolder.Checked;
            txtScreenshotsFolder.Enabled = btnScreenshotsFolderBrowse.Enabled = TaskSettings.OverrideScreenshotsFolder;
        }

        private void txtScreenshotsFolder_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.ScreenshotsFolder = txtScreenshotsFolder.Text;
        }

        private void btnScreenshotsFolderBrowse_Click(object sender, EventArgs e)
        {
            FileHelpers.BrowseFolder(Resources.ApplicationSettingsForm_btnBrowseCustomScreenshotsPath_Click_Choose_screenshots_folder_path,
                txtScreenshotsFolder, TaskSettings.ScreenshotsFolder, true);
        }

        #endregion Task

        #region General

        private void cbUseDefaultGeneralSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultGeneralSettings = !cbOverrideGeneralSettings.Checked;
            UpdateDefaultSettingVisibility();
        }

        private void cbPlaySoundAfterCapture_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.PlaySoundAfterCapture = cbPlaySoundAfterCapture.Checked;
        }

        private void cbPlaySoundAfterUpload_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.PlaySoundAfterUpload = cbPlaySoundAfterUpload.Checked;
        }

        private void cbPlaySoundAfterAction_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.PlaySoundAfterAction = cbPlaySoundAfterAction.Checked;
        }

        private void cbShowToastNotificationAfterTaskCompleted_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ShowToastNotificationAfterTaskCompleted = cbShowToastNotificationAfterTaskCompleted.Checked;
            gbToastWindow.Enabled = TaskSettings.GeneralSettings.ShowToastNotificationAfterTaskCompleted;
        }

        private void nudToastWindowDuration_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowDuration = (float)nudToastWindowDuration.Value;
        }

        private void nudToastWindowFadeDuration_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowFadeDuration = (float)nudToastWindowFadeDuration.Value;
        }

        private void cbToastWindowPlacement_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowPlacement = Helpers.GetEnumFromIndex<ContentAlignment>(cbToastWindowPlacement.SelectedIndex);
        }

        private void nudToastWindowSizeWidth_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowSize = new Size((int)nudToastWindowSizeWidth.Value, TaskSettings.GeneralSettings.ToastWindowSize.Height);
        }

        private void nudToastWindowSizeHeight_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowSize = new Size(TaskSettings.GeneralSettings.ToastWindowSize.Width, (int)nudToastWindowSizeHeight.Value);
        }

        private void cbToastWindowLeftClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowLeftClickAction = (ToastClickAction)cbToastWindowLeftClickAction.SelectedIndex;
        }

        private void cbToastWindowRightClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowRightClickAction = (ToastClickAction)cbToastWindowRightClickAction.SelectedIndex;
        }

        private void cbToastWindowMiddleClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowMiddleClickAction = (ToastClickAction)cbToastWindowMiddleClickAction.SelectedIndex;
        }

        private void cbToastWindowAutoHide_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.ToastWindowAutoHide = cbToastWindowAutoHide.Checked;
        }

        private void cbDisableNotificationsOnFullscreen_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.DisableNotificationsOnFullscreen = cbDisableNotificationsOnFullscreen.Checked;
        }

        private void cbUseCustomCaptureSound_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.UseCustomCaptureSound = cbUseCustomCaptureSound.Checked;
            txtCustomCaptureSoundPath.Enabled = btnCustomCaptureSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomCaptureSound;
        }

        private void txtCustomCaptureSoundPath_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.CustomCaptureSoundPath = txtCustomCaptureSoundPath.Text;
        }

        private void btnCustomCaptureSoundPath_Click(object sender, EventArgs e)
        {
            FileHelpers.BrowseFile(txtCustomCaptureSoundPath, filter: "Audio file (*.wav)|*.wav");
        }

        private void cbUseCustomTaskCompletedSound_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.UseCustomTaskCompletedSound = cbUseCustomTaskCompletedSound.Checked;
            txtCustomTaskCompletedSoundPath.Enabled = btnCustomTaskCompletedSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomTaskCompletedSound;
        }

        private void txtCustomTaskCompletedSoundPath_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.CustomTaskCompletedSoundPath = txtCustomTaskCompletedSoundPath.Text;
        }

        private void btnCustomTaskCompletedSoundPath_Click(object sender, EventArgs e)
        {
            FileHelpers.BrowseFile(txtCustomTaskCompletedSoundPath, filter: "Audio file (*.wav)|*.wav");
        }

        private void cbUseCustomActionCompletedSound_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.UseCustomActionCompletedSound = cbUseCustomActionCompletedSound.Checked;
            txtCustomActionCompletedSoundPath.Enabled = btnCustomActionCompletedSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomActionCompletedSound;
        }

        private void txtCustomActionCompletedSoundPath_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.CustomActionCompletedSoundPath = txtCustomActionCompletedSoundPath.Text;
        }

        private void btnCustomActionCompletedSoundPath_Click(object sender, EventArgs e)
        {
            FileHelpers.BrowseFile(txtCustomActionCompletedSoundPath, filter: "Audio file (*.wav)|*.wav");
        }

        private void cbUseCustomErrorSound_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.UseCustomErrorSound = cbUseCustomErrorSound.Checked;
            txtCustomErrorSoundPath.Enabled = btnCustomErrorSoundPath.Enabled = TaskSettings.GeneralSettings.UseCustomErrorSound;
        }

        private void txtCustomErrorSoundPath_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.GeneralSettings.CustomErrorSoundPath = txtCustomErrorSoundPath.Text;
        }

        private void btnCustomErrorSoundPath_Click(object sender, EventArgs e)
        {
            FileHelpers.BrowseFile(txtCustomErrorSoundPath, filter: "Audio file (*.wav)|*.wav");
        }

        #endregion General

        #region Image

        private void cbUseDefaultImageSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultImageSettings = !cbOverrideImageSettings.Checked;
            UpdateDefaultSettingVisibility();
        }

        private void cbImageFormat_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageFormat = (EImageFormat)cbImageFormat.SelectedIndex;
        }

        private void cbImagePNGBitDepth_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImagePNGBitDepth = (PNGBitDepth)cbImagePNGBitDepth.SelectedIndex;
        }

        private void nudImageJPEGQuality_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageJPEGQuality = (int)nudImageJPEGQuality.Value;
        }

        private void cbImageGIFQuality_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageGIFQuality = (GIFQuality)cbImageGIFQuality.SelectedIndex;
        }

        private void cbImageAutoUseJPEG_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageAutoUseJPEG = cbImageAutoUseJPEG.Checked;
            nudImageAutoUseJPEGSize.Enabled = TaskSettings.ImageSettings.ImageAutoUseJPEG;
            cbImageAutoJPEGQuality.Enabled = TaskSettings.ImageSettings.ImageAutoUseJPEG;
        }

        private void nudImageAutoUseJPEGSize_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageAutoUseJPEGSize = (int)nudImageAutoUseJPEGSize.Value;
        }

        private void cbImageAutoJPEGQuality_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageAutoJPEGQuality = cbImageAutoJPEGQuality.Checked;
        }

        private void cbImageFileExist_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.FileExistAction = (FileExistAction)cbImageFileExist.SelectedIndex;
        }

        private void cbShowImageEffectsWindowAfterCapture_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ShowImageEffectsWindowAfterCapture = cbShowImageEffectsWindowAfterCapture.Checked;
        }

        private void cbImageEffectOnlyRegionCapture_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ImageEffectOnlyRegionCapture = cbImageEffectOnlyRegionCapture.Checked;
        }

        private void cbUseRandomImageEffect_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.UseRandomImageEffect = cbUseRandomImageEffect.Checked;
        }

        private void btnImageEffects_Click(object sender, EventArgs e)
        {
            TaskHelpers.OpenImageEffectsSingleton(TaskSettings);
        }

        private void nudThumbnailWidth_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ThumbnailWidth = (int)nudThumbnailWidth.Value;
        }

        private void nudThumbnailHeight_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ThumbnailHeight = (int)nudThumbnailHeight.Value;
        }

        private void txtThumbnailName_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ThumbnailName = txtThumbnailName.Text;
            lblThumbnailNamePreview.Text = "ImageName" + TaskSettings.ImageSettings.ThumbnailName + ".jpg";
        }

        private void cbThumbnailIfSmaller_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ImageSettings.ThumbnailCheckSize = cbThumbnailIfSmaller.Checked;
        }

        #endregion Image

        #region Capture

        #region General

        private void cbUseDefaultCaptureSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultCaptureSettings = !cbOverrideCaptureSettings.Checked;
            UpdateDefaultSettingVisibility();
        }

        private void cbShowCursor_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ShowCursor = cbShowCursor.Checked;
        }

        private void nudScreenshotDelay_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenshotDelay = nudScreenshotDelay.Value;
        }

        private void cbCaptureTransparent_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureTransparent = cbCaptureTransparent.Checked;
            cbCaptureShadow.Enabled = TaskSettings.CaptureSettings.CaptureTransparent;
        }

        private void cbCaptureShadow_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureShadow = cbCaptureShadow.Checked;
        }

        private void nudCaptureShadowOffset_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureShadowOffset = (int)nudCaptureShadowOffset.Value;
        }

        private void cbCaptureClientArea_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureClientArea = cbCaptureClientArea.Checked;
        }

        private void cbCaptureAutoHideDesktopIcons_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureAutoHideDesktopIcons = cbCaptureAutoHideDesktopIcons.Checked;
        }

        private void cbCaptureAutoHideTaskbar_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureAutoHideTaskbar = cbCaptureAutoHideTaskbar.Checked;
        }

        private void InitializeObsGameCaptureControls()
        {
            var page = new TabPage("Game capture (experimental)")
            {
                BackColor = SystemColors.Window,
                Padding = new Padding(8)
            };
            pObsGameCapture = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            page.Controls.Add(pObsGameCapture);
            tcCapture.TabPages.Add(page);

            cbObsGameCaptureEnabled = new CheckBox
            {
                AutoSize = true,
                Location = new Point(4, 4),
                Text = "Automatically use the exact OBS hook for configured games"
            };
            cbObsGameCaptureEnabled.CheckedChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetObsGameCaptureSettings().Enabled = cbObsGameCaptureEnabled.Checked;
                }
            };
            pObsGameCapture.Controls.Add(cbObsGameCaptureEnabled);

            var processLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 36),
                Text = "Game process names (one per line, .exe optional):"
            };
            pObsGameCapture.Controls.Add(processLabel);

            txtObsGameProcesses = new TextBox
            {
                AcceptsReturn = true,
                Location = new Point(4, 58),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Size = new Size(514, 88)
            };
            txtObsGameProcesses.TextChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetObsGameCaptureSettings().SetProcessNames(txtObsGameProcesses.Text);
                }
            };
            pObsGameCapture.Controls.Add(txtObsGameProcesses);

            var addRunningProcess = new Button
            {
                AutoSize = true,
                Location = new Point(4, 153),
                Text = "Add running process..."
            };
            addRunningProcess.Click += (_, _) => ShowObsRunningProcesses(addRunningProcess);
            pObsGameCapture.Controls.Add(addRunningProcess);

            cbObsReuseExistingHook = new CheckBox
            {
                AutoSize = true,
                Location = new Point(168, 158),
                Text = "Reuse an existing OBS hook read-only"
            };
            cbObsReuseExistingHook.CheckedChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetObsGameCaptureSettings().ReuseExistingHook = cbObsReuseExistingHook.Checked;
                }
            };
            pObsGameCapture.Controls.Add(cbObsReuseExistingHook);

            var defaultsLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 190),
                Text = "Default options for configured games:"
            };
            pObsGameCapture.Controls.Add(defaultsLabel);

            var alphaLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 215),
                Text = "Alpha interpretation:"
            };
            pObsGameCapture.Controls.Add(alphaLabel);

            cbObsAlphaMode = CreateObsEnumComboBox<ObsGameCaptureAlphaMode>(168, 211, 242);
            cbObsAlphaMode.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && TryGetObsEnumValue(cbObsAlphaMode.SelectedItem, out ObsGameCaptureAlphaMode value))
                {
                    GetObsGameCaptureSettings().AlphaMode = value;
                }
            };
            pObsGameCapture.Controls.Add(cbObsAlphaMode);

            cbObsCaptureThirdPartyOverlays = new CheckBox
            {
                AutoSize = true,
                Location = new Point(4, 245),
                Text = "Capture third-party overlays"
            };
            cbObsCaptureThirdPartyOverlays.CheckedChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetObsGameCaptureSettings().CaptureThirdPartyOverlays =
                        cbObsCaptureThirdPartyOverlays.Checked;
                }
            };
            pObsGameCapture.Controls.Add(cbObsCaptureThirdPartyOverlays);

            var frameRateLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 280),
                Text = "Hook rate while capturing:"
            };
            pObsGameCapture.Controls.Add(frameRateLabel);

            cbObsCaptureFrameRate = CreateObsEnumComboBox<ObsGameCaptureFrameRate>(168, 276, 242);
            cbObsCaptureFrameRate.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && TryGetObsEnumValue(cbObsCaptureFrameRate.SelectedItem, out ObsGameCaptureFrameRate value))
                {
                    GetObsGameCaptureSettings().CaptureFrameRate = value;
                }
            };
            pObsGameCapture.Controls.Add(cbObsCaptureFrameRate);

            var cursorLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 314),
                Text = "Cursor:"
            };
            pObsGameCapture.Controls.Add(cursorLabel);

            cbObsCursorMode = CreateObsEnumComboBox<ObsGameCaptureCursorMode>(168, 310, 242);
            cbObsCursorMode.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && TryGetObsEnumValue(cbObsCursorMode.SelectedItem, out ObsGameCaptureCursorMode value))
                {
                    GetObsGameCaptureSettings().CursorMode = value;
                }
            };
            pObsGameCapture.Controls.Add(cbObsCursorMode);

            var promotionLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 348),
                Text = "Precise-mode game promotion:"
            };
            pObsGameCapture.Controls.Add(promotionLabel);

            cbHdrGameWindowPromotion = CreateObsEnumComboBox<HdrGameWindowPromotionMode>(190, 344, 320);
            cbHdrGameWindowPromotion.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && TryGetObsEnumValue(
                    cbHdrGameWindowPromotion.SelectedItem,
                    out HdrGameWindowPromotionMode value))
                {
                    TaskSettings.CaptureSettings.HdrSettings.GameWindowPromotionMode = value;
                }
            };
            pObsGameCapture.Controls.Add(cbHdrGameWindowPromotion);

            var perGameOptions = new Button
            {
                AutoSize = true,
                Location = new Point(4, 378),
                Text = "Per-game options..."
            };
            perGameOptions.Click += (_, _) => ShowObsPerGameOptions();
            pObsGameCapture.Controls.Add(perGameOptions);

            var testConfiguration = new Button
            {
                AutoSize = true,
                Location = new Point(150, 378),
                Text = "Test configuration..."
            };
            testConfiguration.Click += async (_, _) =>
                await TestObsGameCaptureConfigurationAsync(testConfiguration);
            pObsGameCapture.Controls.Add(testConfiguration);

            var colorLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 420),
                Text = "RGB10A2 interpretation:"
            };
            pObsGameCapture.Controls.Add(colorLabel);

            cbObsRgb10A2Interpretation = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(168, 416),
                Size = new Size(242, 23)
            };
            cbObsRgb10A2Interpretation.Items.AddRange(
                Helpers.GetEnumDescriptions<ObsGameCaptureRgb10A2Interpretation>());
            cbObsRgb10A2Interpretation.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && cbObsRgb10A2Interpretation.SelectedIndex >= 0)
                {
                    GetObsGameCaptureSettings().Rgb10A2Interpretation =
                        (ObsGameCaptureRgb10A2Interpretation)cbObsRgb10A2Interpretation.SelectedIndex;
                }
            };
            pObsGameCapture.Controls.Add(cbObsRgb10A2Interpretation);

            var pathLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 454),
                Text = "OBS installation path (blank = discover installed OBS):"
            };
            pObsGameCapture.Controls.Add(pathLabel);

            txtObsInstallationPath = new TextBox
            {
                Location = new Point(4, 476),
                Size = new Size(430, 23)
            };
            txtObsInstallationPath.TextChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetObsGameCaptureSettings().ObsInstallationPath = txtObsInstallationPath.Text.Trim();
                }
            };
            pObsGameCapture.Controls.Add(txtObsInstallationPath);

            var browsePath = new Button
            {
                Location = new Point(440, 475),
                Size = new Size(78, 25),
                Text = "Browse..."
            };
            browsePath.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Select the OBS Studio installation directory",
                    SelectedPath = txtObsInstallationPath.Text
                };

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    txtObsInstallationPath.Text = dialog.SelectedPath;
                }
            };
            pObsGameCapture.Controls.Add(browsePath);

            var idleLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 516),
                Text = "Inactive-session safety timeout (seconds):"
            };
            pObsGameCapture.Controls.Add(idleLabel);

            nudObsSessionIdleTimeout = new NumericUpDown
            {
                Location = new Point(310, 512),
                Minimum = 5,
                Maximum = 600,
                Value = 15,
                Size = new Size(100, 23)
            };
            nudObsSessionIdleTimeout.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetObsGameCaptureSettings().SessionIdleTimeoutSeconds =
                        (int)nudObsSessionIdleTimeout.Value;
                }
            };
            pObsGameCapture.Controls.Add(nudObsSessionIdleTimeout);

            var warning = new Label
            {
                AutoSize = false,
                Location = new Point(4, 551),
                Size = new Size(514, 145),
                Text = "Experimental. ShareX only considers configured visible processes whose client area " +
                    "intersects the screenshot; pixels outside the game client use normal desktop capture. " +
                    "Precise-mode promotion always maps known game clients; automatic mode learns only foreground " +
                    "fullscreen windows with coherent HDR highlights. " +
                    "Normal screenshots stop ShareX-owned hooks after copying one frame; multi-frame capture reuses " +
                    "the hook only for that capture session. " +
                    "Existing OBS publications are opened read-only and retain the owner's hook rate/overlay setting. " +
                    "Cursor exclusion prevents ShareX from adding the desktop cursor; it cannot remove a cursor rendered by the game. " +
                    "A valid OBS signature does not guarantee acceptance by every game or anti-cheat."
            };
            pObsGameCapture.Controls.Add(warning);
        }

        private void InitializeHdrFileOutputControls()
        {
            var page = new TabPage("HDR output")
            {
                BackColor = SystemColors.Window,
                Padding = new Padding(8)
            };
            pHdrFileOutput = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            page.Controls.Add(pHdrFileOutput);
            tcCapture.TabPages.Add(page);

            AddHdrOutputLabel("Output:", 4, 9);
            cbHdrOutputMode = CreateHdrOutputComboBox(190, 5);
            cbHdrOutputMode.Items.AddRange(Helpers.GetEnumDescriptions<HdrOutputMode>());
            cbHdrOutputMode.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && cbHdrOutputMode.SelectedIndex >= 0)
                {
                    GetHdrFileOutputSettings().OutputMode = (HdrOutputMode)cbHdrOutputMode.SelectedIndex;
                    UpdateHdrFileOutputControlsEnabled();
                }
            };

            AddHdrOutputLabel("HDR format:", 4, 43);
            cbHdrFileFormat = CreateHdrOutputComboBox(190, 39);
            foreach (HdrFileFormat format in Enum.GetValues<HdrFileFormat>())
            {
                if (HdrEncoderCapabilities.TryGetAvailability(format, out _))
                {
                    cbHdrFileFormat.Items.Add(new HdrFileFormatItem(format));
                }
            }

            cbHdrFileFormat.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && cbHdrFileFormat.SelectedItem is HdrFileFormatItem selectedFormat)
                {
                    GetHdrFileOutputSettings().FileFormat = selectedFormat.Format;
                    UpdateHdrFileOutputControlsEnabled();
                }
            };

            AddHdrOutputLabel("Mastering peak (nits):", 4, 77);
            nudHdrMasteringMaximumNits = CreateHdrOutputNumeric(190, 73, 80, 10000, 1000);
            nudHdrMasteringMaximumNits.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().MasteringDisplayMaximumNits =
                        (float)nudHdrMasteringMaximumNits.Value;
                }
            };

            AddHdrOutputLabel("Mastering black (nits):", 4, 111);
            nudHdrMasteringMinimumNits = CreateHdrOutputNumeric(190, 107, 0, 1, 0.0005m);
            nudHdrMasteringMinimumNits.DecimalPlaces = 4;
            nudHdrMasteringMinimumNits.Increment = 0.0001m;
            nudHdrMasteringMinimumNits.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().MasteringDisplayMinimumNits =
                        (float)nudHdrMasteringMinimumNits.Value;
                }
            };

            AddHdrOutputLabel("EXR exposure:", 4, 145);
            cbHdrOpenExrExposure = CreateHdrOutputComboBox(190, 141);
            cbHdrOpenExrExposure.Items.AddRange(Helpers.GetEnumDescriptions<OpenExrExposureMode>());
            cbHdrOpenExrExposure.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && cbHdrOpenExrExposure.SelectedIndex >= 0)
                {
                    GetHdrFileOutputSettings().OpenExrExposureMode =
                        (OpenExrExposureMode)cbHdrOpenExrExposure.SelectedIndex;
                }
            };

            lblHdrJpegQuality = AddHdrOutputLabel("JPEG base quality:", 4, 179);
            nudHdrJpegQuality = CreateHdrOutputNumeric(190, 175, 1, 100, 95);
            nudHdrJpegQuality.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().JpegQuality = (int)nudHdrJpegQuality.Value;
                }
            };

            lblHdrGainMapQuality = AddHdrOutputLabel("Gain-map quality:", 4, 213);
            nudHdrGainMapQuality = CreateHdrOutputNumeric(190, 209, 1, 100, 90);
            nudHdrGainMapQuality.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().GainMapQuality = (int)nudHdrGainMapQuality.Value;
                }
            };

            lblHdrAvifQuality = AddHdrOutputLabel("AVIF quality:", 4, 247);
            nudHdrAvifQuality = CreateHdrOutputNumeric(190, 243, 1, 100, 90);
            nudHdrAvifQuality.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().AvifQuality = (int)nudHdrAvifQuality.Value;
                }
            };

            lblHdrAvifSpeed = AddHdrOutputLabel("AVIF speed (0 slow–10 fast):", 4, 281);
            nudHdrAvifSpeed = CreateHdrOutputNumeric(190, 277, 0, 10, 6);
            nudHdrAvifSpeed.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().AvifSpeed = (int)nudHdrAvifSpeed.Value;
                }
            };

            cbHdrFlattenTransparencyForUltraHdr = new CheckBox
            {
                AutoSize = false,
                Location = new Point(4, 312),
                Size = new Size(514, 24),
                Text = "Flatten transparent pixels to black for Ultra HDR JPEG"
            };
            cbHdrFlattenTransparencyForUltraHdr.CheckedChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().FlattenTransparencyForUltraHdr =
                        cbHdrFlattenTransparencyForUltraHdr.Checked;
                }
            };
            pHdrFileOutput.Controls.Add(cbHdrFlattenTransparencyForUltraHdr);

            cbHdrUploadWithFileUploader = new CheckBox
            {
                AutoSize = false,
                Location = new Point(4, 341),
                Size = new Size(514, 24),
                Text = "Upload HDR through the image file uploader (preserves encoded bytes)"
            };
            cbHdrUploadWithFileUploader.CheckedChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrFileOutputSettings().UploadWithFileUploader =
                        cbHdrUploadWithFileUploader.Checked;
                }
            };
            pHdrFileOutput.Controls.Add(cbHdrUploadWithFileUploader);

            AddHdrOutputLabel("Clipboard output:", 4, 375);
            cbHdrClipboardOutputMode = CreateHdrOutputComboBox(190, 371);
            cbHdrClipboardOutputMode.Items.AddRange(
                Helpers.GetEnumDescriptions<HdrClipboardOutputMode>());
            cbHdrClipboardOutputMode.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && cbHdrClipboardOutputMode.SelectedIndex >= 0)
                {
                    GetHdrFileOutputSettings().ClipboardOutputMode =
                        (HdrClipboardOutputMode)cbHdrClipboardOutputMode.SelectedIndex;
                    UpdateHdrFileOutputControlsEnabled();
                }
            };

            AddHdrOutputLabel("Clipboard HDR format:", 4, 409);
            cbHdrClipboardFileFormat = CreateHdrOutputComboBox(190, 405);
            foreach (HdrFileFormat format in Enum.GetValues<HdrFileFormat>())
            {
                if (HdrEncoderCapabilities.TryGetAvailability(format, out _))
                {
                    cbHdrClipboardFileFormat.Items.Add(new HdrFileFormatItem(format));
                }
            }

            cbHdrClipboardFileFormat.SelectedIndexChanged += (_, _) =>
            {
                if (loaded &&
                    cbHdrClipboardFileFormat.SelectedItem is HdrFileFormatItem selectedFormat)
                {
                    GetHdrFileOutputSettings().ClipboardFileFormat = selectedFormat.Format;
                    UpdateHdrFileOutputControlsEnabled();
                }
            };

            AddHdrOutputLabel("Mixed-monitor SDR brightness:", 4, 443);
            cbHdrMixedMonitorBrightnessMode =
                CreateObsEnumComboBox<HdrMixedMonitorBrightnessMode>(190, 439, 320);
            cbHdrMixedMonitorBrightnessMode.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && TryGetObsEnumValue(
                    cbHdrMixedMonitorBrightnessMode.SelectedItem,
                    out HdrMixedMonitorBrightnessMode value))
                {
                    GetHdrCaptureSettings().MixedMonitorBrightnessMode = value;
                    UpdateHdrFileOutputControlsEnabled();
                }
            };
            pHdrFileOutput.Controls.Add(cbHdrMixedMonitorBrightnessMode);

            AddHdrOutputLabel("Custom SDR white (nits):", 4, 477);
            nudHdrMixedMonitorWhiteNits = CreateHdrOutputNumeric(
                190,
                473,
                (decimal)HdrCaptureSettings.MinimumBrightnessNits,
                (decimal)HdrCaptureSettings.MaximumPaperWhiteNits,
                (decimal)HdrCaptureSettings.DefaultBrightnessNits);
            nudHdrMixedMonitorWhiteNits.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrCaptureSettings().MixedMonitorCustomSdrWhiteNits =
                        (float)nudHdrMixedMonitorWhiteNits.Value;
                }
            };

            lblHdrEncoderAvailability = new Label
            {
                AutoSize = false,
                Location = new Point(4, 508),
                Size = new Size(514, 42)
            };
            pHdrFileOutput.Controls.Add(lblHdrEncoderAvailability);
            UpdateHdrEncoderAvailabilityLabel();

            var details = new Label
            {
                AutoSize = false,
                Location = new Point(4, 555),
                Size = new Size(514, 285),
                Text =
                    "Ultra HDR JPEG is the recommended shareable format: HDR-aware viewers use its gain map, " +
                    "and other viewers show the embedded SDR JPEG. HDR AVIF stores a compact 10-bit BT.2020/PQ " +
                    "4:4:4 image with alpha, CICP, CLLI, and mastering metadata; viewers without HDR AVIF support " +
                    "do not get an embedded SDR fallback. OpenEXR can normalize captured display white " +
                    "to 1.0 for conventional viewers, or preserve raw scRGB HALF samples losslessly. " +
                    "HDR PNG stores 16-bit BT.2020/PQ and is experimental because viewer support " +
                    "is still uneven. HDR and SDR writes a separate -SDR file for EXR/PNG/AVIF; Ultra HDR needs " +
                    "only its single dual-representation JPEG. Transparent pixels require OpenEXR/HDR PNG/AVIF " +
                    "unless the explicit Ultra HDR flatten-to-black option is enabled; JPEG cannot preserve alpha. " +
                    "The file-uploader option avoids image hosts that may decode or recompress the upload and " +
                    "discard HDR metadata. Clipboard output has its own independent format selector. HDR-only " +
                    "publishes the selected encoded file under its native registered name plus MIME and ShareX " +
                    "formats. The recommended HDR + SDR mode places ordinary SDR Bitmap, DIB, and PNG data in " +
                    "the standard compatibility slots, while retaining the selected HDR bytes in explicit MIME " +
                    "and ShareX formats. Ultra HDR JPEG can also remain a normal JPEG because it contains its own " +
                    "SDR base. Windows has no universal negotiated HDR clipboard bitmap format, so applications " +
                    "must understand the selected encoded format to paste the HDR representation. In mixed-monitor " +
                    "captures, Match nearest HDR display raises SDR-monitor paper white to the nearest HDR display's " +
                    "Windows SDR brightness while respecting foreground HDR/game boundaries. Preserve keeps the " +
                    "captured absolute luminance; Custom uses the entered paper-white value."
            };
            pHdrFileOutput.Controls.Add(details);
        }

        private Label AddHdrOutputLabel(string text, int x, int y)
        {
            var label = new Label
            {
                AutoSize = true,
                Location = new Point(x, y),
                Text = text
            };
            pHdrFileOutput.Controls.Add(label);
            return label;
        }

        private ComboBox CreateHdrOutputComboBox(int x, int y)
        {
            var comboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(x, y),
                Size = new Size(250, 23)
            };
            pHdrFileOutput.Controls.Add(comboBox);
            return comboBox;
        }

        private NumericUpDown CreateHdrOutputNumeric(
            int x,
            int y,
            decimal minimum,
            decimal maximum,
            decimal value)
        {
            var numeric = new NumericUpDown
            {
                Location = new Point(x, y),
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Size = new Size(120, 23)
            };
            pHdrFileOutput.Controls.Add(numeric);
            return numeric;
        }

        private void LoadHdrFileOutputSettings()
        {
            HdrFileOutputSettings settings = GetHdrFileOutputSettings();
            HdrFileFormatItem selectedFormat = cbHdrFileFormat.Items
                .Cast<HdrFileFormatItem>()
                .FirstOrDefault(x => x.Format == settings.FileFormat);
            HdrFileFormatItem selectedClipboardFormat = cbHdrClipboardFileFormat.Items
                .Cast<HdrFileFormatItem>()
                .FirstOrDefault(x => x.Format == settings.ClipboardFileFormat);

            if (selectedFormat == null)
            {
                selectedFormat = cbHdrFileFormat.Items.Cast<HdrFileFormatItem>().First();
                settings.FileFormat = selectedFormat.Format;
            }

            if (selectedClipboardFormat == null)
            {
                selectedClipboardFormat = cbHdrClipboardFileFormat.Items
                    .Cast<HdrFileFormatItem>()
                    .First();
                settings.ClipboardFileFormat = selectedClipboardFormat.Format;
            }

            cbHdrOutputMode.SelectedIndex = (int)settings.OutputMode;
            cbHdrFileFormat.SelectedItem = selectedFormat;
            nudHdrMasteringMaximumNits.SetValue((decimal)settings.MasteringDisplayMaximumNits);
            nudHdrMasteringMinimumNits.SetValue((decimal)settings.MasteringDisplayMinimumNits);
            cbHdrOpenExrExposure.SelectedIndex = (int)settings.OpenExrExposureMode;
            nudHdrJpegQuality.SetValue(settings.JpegQuality);
            nudHdrGainMapQuality.SetValue(settings.GainMapQuality);
            nudHdrAvifQuality.SetValue(settings.AvifQuality);
            nudHdrAvifSpeed.SetValue(settings.AvifSpeed);
            cbHdrFlattenTransparencyForUltraHdr.Checked = settings.FlattenTransparencyForUltraHdr;
            cbHdrUploadWithFileUploader.Checked = settings.UploadWithFileUploader;
            cbHdrClipboardOutputMode.SelectedIndex = (int)settings.ClipboardOutputMode;
            cbHdrClipboardFileFormat.SelectedItem = selectedClipboardFormat;
            HdrCaptureSettings captureSettings = GetHdrCaptureSettings();
            SelectObsEnumValue(
                cbHdrMixedMonitorBrightnessMode,
                captureSettings.MixedMonitorBrightnessMode);
            nudHdrMixedMonitorWhiteNits.SetValue(
                (decimal)captureSettings.MixedMonitorCustomSdrWhiteNits);
            UpdateHdrFileOutputControlsEnabled();
        }

        private HdrFileOutputSettings GetHdrFileOutputSettings()
        {
            TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
            return TaskSettings.CaptureSettings.HdrSettings.FileOutput ??= new HdrFileOutputSettings();
        }

        private void UpdateHdrFileOutputControlsEnabled()
        {
            bool hdrEnabled = cbUseHDRSupport.Checked;
            bool nativeOutputEnabled = hdrEnabled &&
                cbHdrOutputMode.SelectedIndex != (int)HdrOutputMode.SdrOnly;
            bool hdrClipboardEnabled = hdrEnabled &&
                cbHdrClipboardOutputMode.SelectedIndex != (int)HdrClipboardOutputMode.SdrOnly;
            HdrFileFormat? diskFormat =
                cbHdrFileFormat.SelectedItem is HdrFileFormatItem selectedDiskFormat
                    ? selectedDiskFormat.Format
                    : null;
            HdrFileFormat? clipboardFormat =
                cbHdrClipboardFileFormat.SelectedItem is HdrFileFormatItem selectedClipboardFormat
                    ? selectedClipboardFormat.Format
                    : null;
            bool ultraHdr =
                (nativeOutputEnabled && diskFormat == HdrFileFormat.UltraHdrJpeg) ||
                (hdrClipboardEnabled && clipboardFormat == HdrFileFormat.UltraHdrJpeg);
            bool openExr =
                (nativeOutputEnabled && diskFormat == HdrFileFormat.OpenExr) ||
                (hdrClipboardEnabled && clipboardFormat == HdrFileFormat.OpenExr);
            bool avif =
                (nativeOutputEnabled && diskFormat == HdrFileFormat.Avif) ||
                (hdrClipboardEnabled && clipboardFormat == HdrFileFormat.Avif);
            bool hdrEncodingEnabled = nativeOutputEnabled || hdrClipboardEnabled;

            pHdrFileOutput.Enabled = hdrEnabled;
            cbHdrClipboardOutputMode.Enabled = hdrEnabled;
            cbHdrClipboardFileFormat.Enabled = hdrClipboardEnabled;
            cbHdrMixedMonitorBrightnessMode.Enabled = hdrEnabled;
            nudHdrMixedMonitorWhiteNits.Enabled = hdrEnabled &&
                GetHdrCaptureSettings().MixedMonitorBrightnessMode ==
                    HdrMixedMonitorBrightnessMode.Custom;
            cbHdrFileFormat.Enabled = nativeOutputEnabled;
            nudHdrMasteringMaximumNits.Enabled = hdrEncodingEnabled;
            nudHdrMasteringMinimumNits.Enabled = hdrEncodingEnabled;
            cbHdrOpenExrExposure.Enabled = openExr;
            nudHdrJpegQuality.Enabled = ultraHdr;
            nudHdrGainMapQuality.Enabled = ultraHdr;
            lblHdrJpegQuality.Visible = ultraHdr;
            nudHdrJpegQuality.Visible = ultraHdr;
            lblHdrGainMapQuality.Visible = ultraHdr;
            nudHdrGainMapQuality.Visible = ultraHdr;
            lblHdrAvifQuality.Visible = avif;
            nudHdrAvifQuality.Visible = avif;
            nudHdrAvifQuality.Enabled = avif;
            lblHdrAvifSpeed.Visible = avif;
            nudHdrAvifSpeed.Visible = avif;
            nudHdrAvifSpeed.Enabled = avif;
            cbHdrFlattenTransparencyForUltraHdr.Enabled = ultraHdr;
            cbHdrUploadWithFileUploader.Enabled = nativeOutputEnabled;
        }

        private void UpdateHdrEncoderAvailabilityLabel()
        {
            string ultraHdrStatus;
            if (HdrEncoderCapabilities.TryGetAvailability(
                HdrFileFormat.UltraHdrJpeg,
                out string unavailableReason))
            {
                ultraHdrStatus = "Ultra HDR JPEG: available";
            }
            else
            {
                ultraHdrStatus = unavailableReason;
            }

            string avifStatus = HdrEncoderCapabilities.TryGetAvailability(
                HdrFileFormat.Avif,
                out string avifUnavailableReason)
                ? "HDR AVIF: available"
                : avifUnavailableReason;
            lblHdrEncoderAvailability.Text =
                $"{ultraHdrStatus}. {avifStatus}. OpenEXR and HDR PNG are managed and architecture-independent.";
        }

        private void InitializeHdrToneMappingControls()
        {
            var page = new TabPage("HDR tone mapping")
            {
                BackColor = SystemColors.Window,
                Padding = new Padding(8)
            };
            pHdrToneMapping = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            page.Controls.Add(pHdrToneMapping);
            tcCapture.TabPages.Add(page);

            pCapture.Controls.Remove(lblHDRProcessingBackend);
            pCapture.Controls.Remove(cbHDRProcessingBackend);
            pCapture.Controls.Remove(lblHDRToneMappingMode);
            pCapture.Controls.Remove(cbHDRToneMappingMode);
            pCapture.Controls.Remove(lblHDRBrightnessNits);
            pCapture.Controls.Remove(cbHDRPeakBrightnessMode);
            pCapture.Controls.Remove(nudHDRBrightnessNits);

            lblHDRProcessingBackend.Location = new Point(4, 13);
            lblHDRProcessingBackend.Text = "Processing backend:";
            cbHDRProcessingBackend.Location = new Point(190, 9);
            cbHDRProcessingBackend.Size = new Size(320, 23);
            pHdrToneMapping.Controls.Add(lblHDRProcessingBackend);
            pHdrToneMapping.Controls.Add(cbHDRProcessingBackend);

            lblHDRToneMappingMode.Location = new Point(4, 47);
            cbHDRToneMappingMode.Location = new Point(190, 43);
            cbHDRToneMappingMode.Size = new Size(320, 23);
            pHdrToneMapping.Controls.Add(lblHDRToneMappingMode);
            pHdrToneMapping.Controls.Add(cbHDRToneMappingMode);

            lblHDRBrightnessNits.Location = new Point(4, 81);
            lblHDRBrightnessNits.Text = "HDR content peak:";
            cbHDRPeakBrightnessMode.Location = new Point(190, 77);
            cbHDRPeakBrightnessMode.Size = new Size(230, 23);
            nudHDRBrightnessNits.Location = new Point(426, 77);
            nudHDRBrightnessNits.Size = new Size(84, 23);
            pHdrToneMapping.Controls.Add(lblHDRBrightnessNits);
            pHdrToneMapping.Controls.Add(cbHDRPeakBrightnessMode);
            pHdrToneMapping.Controls.Add(nudHDRBrightnessNits);

            var paperWhiteLabel = new Label
            {
                AutoSize = true,
                Location = new Point(4, 115),
                Text = "SDR paper white:"
            };
            pHdrToneMapping.Controls.Add(paperWhiteLabel);

            cbHdrPaperWhiteMode = CreateObsEnumComboBox<HdrPaperWhiteMode>(190, 111, 230);
            cbHdrPaperWhiteMode.SelectedIndexChanged += (_, _) =>
            {
                if (loaded && TryGetObsEnumValue(cbHdrPaperWhiteMode.SelectedItem, out HdrPaperWhiteMode value))
                {
                    GetHdrCaptureSettings().PaperWhiteMode = value;
                    UpdateHdrToneMappingControlsEnabled();
                }
            };
            pHdrToneMapping.Controls.Add(cbHdrPaperWhiteMode);

            nudHdrPaperWhiteNits = new NumericUpDown
            {
                Location = new Point(426, 111),
                Minimum = (decimal)HdrCaptureSettings.MinimumBrightnessNits,
                Maximum = (decimal)HdrCaptureSettings.MaximumPaperWhiteNits,
                Value = (decimal)HdrCaptureSettings.DefaultBrightnessNits,
                Size = new Size(84, 23)
            };
            nudHdrPaperWhiteNits.ValueChanged += (_, _) =>
            {
                if (loaded)
                {
                    GetHdrCaptureSettings().PaperWhiteNits = (float)nudHdrPaperWhiteNits.Value;
                }
            };
            pHdrToneMapping.Controls.Add(nudHdrPaperWhiteNits);
            lblHdrPaperWhiteNits = paperWhiteLabel;

            var explanation = new Label
            {
                AutoSize = false,
                Location = new Point(4, 154),
                Size = new Size(514, 190),
                Text = "Automatic HDR content peak analyzes the FP16 pixels selected by the current content-aware " +
                    "mask, rejects sparse outliers, and builds the tone curve from a high luminance percentile. " +
                    "Custom uses the exact source peak entered above. Automatic SDR paper white uses Windows' " +
                    "per-display SDR brightness calibration; Custom overrides it for advanced calibration or " +
                    "incorrect display metadata. The reported display maximum remains diagnostic metadata because " +
                    "panel capability is not the same as content brightness. Values are measured in nits."
            };
            pHdrToneMapping.Controls.Add(explanation);
        }

        private HdrCaptureSettings GetHdrCaptureSettings()
        {
            return TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
        }

        private void LoadHdrToneMappingSettings()
        {
            HdrCaptureSettings settings = GetHdrCaptureSettings();
            SelectObsEnumValue(cbHdrPaperWhiteMode, settings.PaperWhiteMode);
            nudHdrPaperWhiteNits.SetValue((decimal)settings.PaperWhiteNits);
            UpdateHdrToneMappingControlsEnabled();
        }

        private void UpdateHdrToneMappingControlsEnabled()
        {
            bool enabled = cbUseHDRSupport.Checked;
            cbHdrPaperWhiteMode.Enabled = enabled;
            lblHdrPaperWhiteNits.Enabled = enabled;
            nudHdrPaperWhiteNits.Enabled = enabled &&
                GetHdrCaptureSettings().PaperWhiteMode == HdrPaperWhiteMode.Custom;
        }

        private void LoadObsGameCaptureSettings()
        {
            ObsGameCaptureSettings settings = GetObsGameCaptureSettings();
            cbObsGameCaptureEnabled.Checked = settings.Enabled;
            cbObsReuseExistingHook.Checked = settings.ReuseExistingHook;
            cbObsCaptureThirdPartyOverlays.Checked = settings.CaptureThirdPartyOverlays;
            SelectObsEnumValue(cbObsAlphaMode, settings.AlphaMode);
            SelectObsEnumValue(cbObsCaptureFrameRate, settings.CaptureFrameRate);
            SelectObsEnumValue(cbObsCursorMode, settings.CursorMode);
            SelectObsEnumValue(
                cbHdrGameWindowPromotion,
                TaskSettings.CaptureSettings.HdrSettings.GameWindowPromotionMode);
            txtObsGameProcesses.Text = settings.GetProcessNamesText();
            txtObsInstallationPath.Text = settings.ObsInstallationPath;
            cbObsRgb10A2Interpretation.SelectedIndex = (int)settings.Rgb10A2Interpretation;
            nudObsSessionIdleTimeout.SetValue(settings.SessionIdleTimeoutSeconds);
        }

        private ObsGameCaptureSettings GetObsGameCaptureSettings()
        {
            TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
            return TaskSettings.CaptureSettings.HdrSettings.ObsGameCapture;
        }

        private void ShowObsRunningProcesses(Control owner)
        {
            List<string> processNames = new List<string>();

            foreach (WindowInfo window in new WindowsList().GetVisibleWindowsList())
            {
                try
                {
                    string processName = window.ProcessName;

                    if (!string.IsNullOrWhiteSpace(processName) &&
                        !processName.Equals("ShareX", StringComparison.OrdinalIgnoreCase) &&
                        !processName.StartsWith("obs", StringComparison.OrdinalIgnoreCase))
                    {
                        processNames.Add(processName);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }

            processNames = processNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (processNames.Count == 0)
            {
                MessageBox.Show(this, "No visible application processes were found.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var menu = new ContextMenuStrip();

            foreach (string processName in processNames)
            {
                var item = new ToolStripMenuItem(processName);
                item.Click += (_, _) =>
                {
                    ObsGameCaptureSettings settings = GetObsGameCaptureSettings();
                    settings.ProcessNames = settings.ProcessNames.Concat(new[] { processName }).ToList();
                    txtObsGameProcesses.Text = settings.GetProcessNamesText();
                };
                menu.Items.Add(item);
            }

            // ToolStripDropDown still accesses its native handle after raising Closed.
            // Disposing from the event itself races that cleanup on current WinForms.
            menu.Closed += (_, _) =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)(() => menu.Dispose()));
                    }
                    catch (InvalidOperationException)
                    {
                        // The settings form is already closing; no further UI work is safe.
                    }
                }
            };
            menu.Show(owner, new Point(0, owner.Height));
        }

        private void ShowObsPerGameOptions()
        {
            ObsGameCaptureSettings settings = GetObsGameCaptureSettings();

            if (settings.ProcessNames.Count == 0)
            {
                MessageBox.Show(this,
                    "Add at least one game process before configuring per-game options.",
                    "OBS Game Capture options",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using var dialog = new Form
            {
                Text = "OBS Game Capture per-game options",
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(1120, 440),
                MinimumSize = new Size(800, 330),
                ShowIcon = false,
                ShowInTaskbar = false
            };
            var explanation = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 72,
                Padding = new Padding(8),
                Text = "Each row inherits the global defaults unless overridden. Render process maps a launcher/profile " +
                    "to a different executable; title and class accept case-insensitive * and ? globs. Hook rate and " +
                    "overlay changes apply to ShareX-owned capture sessions. Existing foreign hooks keep their owner's options."
            };
            var grid = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                Dock = DockStyle.Fill,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                ScrollBars = ScrollBars.Both
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Game process",
                ReadOnly = true,
                Width = 120
            });
            grid.Columns.Add(CreateObsTextColumn("Render process", 125));
            grid.Columns.Add(CreateObsTextColumn("Window title glob", 150));
            grid.Columns.Add(CreateObsTextColumn("Window class glob", 130));
            grid.Columns.Add(CreateObsEnumColumn<ObsGameCaptureAlphaModeOverride>("Alpha", 145));
            grid.Columns.Add(CreateObsEnumColumn<ObsGameCaptureOptionOverride>("Overlays", 105));
            grid.Columns.Add(CreateObsEnumColumn<ObsGameCaptureRgb10A2Override>("RGB10A2", 135));
            grid.Columns.Add(CreateObsEnumColumn<ObsGameCaptureFrameRateOverride>("Hook rate", 105));
            grid.Columns.Add(CreateObsEnumColumn<ObsGameCaptureOptionOverride>("Reuse hook", 105));
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Idle seconds (0=default)",
                ValueType = typeof(int),
                Width = 135
            });
            grid.Columns.Add(CreateObsEnumColumn<ObsGameCaptureCursorModeOverride>("Cursor", 145));

            foreach (string processName in settings.ProcessNames)
            {
                ObsGameCaptureProcessOptions processOptions = settings.GetProcessOptions(processName);
                grid.Rows.Add(
                    processName,
                    processOptions?.TargetProcessName ?? string.Empty,
                    processOptions?.WindowTitlePattern ?? string.Empty,
                    processOptions?.WindowClassPattern ?? string.Empty,
                    processOptions?.AlphaMode ?? ObsGameCaptureAlphaModeOverride.UseDefault,
                    processOptions?.CaptureThirdPartyOverlays ?? ObsGameCaptureOptionOverride.UseDefault,
                    processOptions?.Rgb10A2Interpretation ?? ObsGameCaptureRgb10A2Override.UseDefault,
                    processOptions?.CaptureFrameRate ?? ObsGameCaptureFrameRateOverride.UseDefault,
                    processOptions?.ReuseExistingHook ?? ObsGameCaptureOptionOverride.UseDefault,
                    processOptions?.SessionIdleTimeoutSeconds ?? 0,
                    processOptions?.CursorMode ?? ObsGameCaptureCursorModeOverride.UseDefault);
            }

            var ok = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Text = "OK"
            };
            var cancel = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                Text = "Cancel"
            };
            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            dialog.Controls.Add(grid);
            dialog.Controls.Add(explanation);
            dialog.Controls.Add(buttons);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            ShareXResources.ApplyTheme(dialog, true);

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    settings.SetProcessOptions(new ObsGameCaptureProcessOptions
                    {
                        ProcessName = row.Cells[0].Value as string,
                        TargetProcessName = row.Cells[1].Value as string,
                        WindowTitlePattern = row.Cells[2].Value as string,
                        WindowClassPattern = row.Cells[3].Value as string,
                        AlphaMode = GetObsEnumValue(
                            row.Cells[4].Value,
                            ObsGameCaptureAlphaModeOverride.UseDefault),
                        CaptureThirdPartyOverlays = GetObsEnumValue(
                            row.Cells[5].Value,
                            ObsGameCaptureOptionOverride.UseDefault),
                        Rgb10A2Interpretation = GetObsEnumValue(
                            row.Cells[6].Value,
                            ObsGameCaptureRgb10A2Override.UseDefault),
                        CaptureFrameRate = GetObsEnumValue(
                            row.Cells[7].Value,
                            ObsGameCaptureFrameRateOverride.UseDefault),
                        ReuseExistingHook = GetObsEnumValue(
                            row.Cells[8].Value,
                            ObsGameCaptureOptionOverride.UseDefault),
                        SessionIdleTimeoutSeconds = GetObsInteger(row.Cells[9].Value),
                        CursorMode = GetObsEnumValue(
                            row.Cells[10].Value,
                            ObsGameCaptureCursorModeOverride.UseDefault)
                    });
                }
            }
        }

        private static DataGridViewTextBoxColumn CreateObsTextColumn(string headerText, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = headerText,
                Width = width
            };
        }

        private static DataGridViewComboBoxColumn CreateObsEnumColumn<T>(string headerText, int width)
            where T : struct, Enum
        {
            return new DataGridViewComboBoxColumn
            {
                HeaderText = headerText,
                DataSource = Enum.GetValues<T>()
                    .Select(x => new ObsEnumItem<T>(x))
                    .ToList(),
                DisplayMember = nameof(ObsEnumItem<T>.Description),
                ValueMember = nameof(ObsEnumItem<T>.Value),
                ValueType = typeof(T),
                Width = width
            };
        }

        private static ComboBox CreateObsEnumComboBox<T>(int x, int y, int width)
            where T : struct, Enum
        {
            var comboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(x, y),
                Size = new Size(width, 23)
            };

            comboBox.Items.AddRange(Enum.GetValues<T>()
                .Select(x => (object)new ObsEnumItem<T>(x))
                .ToArray());
            return comboBox;
        }

        private static void SelectObsEnumValue<T>(ComboBox comboBox, T value)
            where T : struct, Enum
        {
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ObsEnumItem<T> item && EqualityComparer<T>.Default.Equals(item.Value, value))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }

            comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
        }

        private static bool TryGetObsEnumValue<T>(object value, out T result)
            where T : struct, Enum
        {
            if (value is ObsEnumItem<T> item && Enum.IsDefined(item.Value))
            {
                result = item.Value;
                return true;
            }

            result = default;
            return false;
        }

        private static T GetObsEnumValue<T>(object value, T defaultValue)
            where T : struct, Enum
        {
            return value is T result && Enum.IsDefined(result) ? result : defaultValue;
        }

        private static int GetObsInteger(object value)
        {
            return int.TryParse(Convert.ToString(value), out int result) ? result : 0;
        }

        private async Task TestObsGameCaptureConfigurationAsync(Button button)
        {
            const string originalText = "Test configuration...";
            button.Enabled = false;
            button.Text = "Testing...";

            try
            {
                ObsGameCaptureSettings settings = GetObsGameCaptureSettings();
                var visibleMatches = new Dictionary<int,
                    (string Name, string Path, string Title, string ClassName, ObsBinaryArchitecture Architecture)>();

                foreach (WindowInfo window in new WindowsList().GetVisibleWindowsList())
                {
                    try
                    {
                        string processName = window.ProcessName;
                        string processPath = window.ProcessFilePath ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(processName) || !settings.MatchesWindow(
                            processName,
                            processPath,
                            window.Text,
                            window.ClassName))
                        {
                            continue;
                        }

                        int processId = window.ProcessId;
                        ObsBinaryArchitecture architecture = !string.IsNullOrWhiteSpace(processPath)
                            ? ObsGameCaptureBinaryInspector.ReadArchitecture(processPath)
                            : ObsBinaryArchitecture.Unsupported;
                        visibleMatches.TryAdd(processId,
                            (processName, processPath, window.Text, window.ClassName, architecture));
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e);
                    }
                }

                ObsBinaryArchitecture[] architectures = visibleMatches.Values
                    .Select(x => x.Architecture)
                    .Where(x => x is ObsBinaryArchitecture.X86 or ObsBinaryArchitecture.X64)
                    .Distinct()
                    .ToArray();

                if (architectures.Length == 0)
                {
                    architectures = new[] { ObsBinaryArchitecture.X64 };
                }

                var reports = new Dictionary<ObsBinaryArchitecture, ObsGameCaptureCompatibilityReport>();

                foreach (ObsBinaryArchitecture architecture in architectures)
                {
                    reports[architecture] = await ObsGameCaptureCompatibilityProbe.ProbeAsync(
                        architecture,
                        settings.ObsInstallationPath);
                }

                if (!IsDisposed)
                {
                    ShowObsGameCaptureTestReport(BuildObsGameCaptureTestReport(
                        settings,
                        TaskSettings.CaptureSettings.HdrSettings.GameWindowPromotionMode,
                        visibleMatches,
                        reports));
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);

                if (!IsDisposed)
                {
                    MessageBox.Show(this, $"OBS Game Capture test failed safely:\r\n\r\n{e.Message}",
                        "OBS Game Capture test", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (!button.IsDisposed)
                {
                    button.Enabled = true;
                    button.Text = originalText;
                }
            }
        }

        private static string BuildObsGameCaptureTestReport(
            ObsGameCaptureSettings settings,
            HdrGameWindowPromotionMode gameWindowPromotionMode,
            IReadOnlyDictionary<int,
                (string Name, string Path, string Title, string ClassName, ObsBinaryArchitecture Architecture)> visibleMatches,
            IReadOnlyDictionary<ObsBinaryArchitecture, ObsGameCaptureCompatibilityReport> reports)
        {
            var result = new StringBuilder();
            result.AppendLine("OBS Game Capture configuration test");
            result.AppendLine();
            result.AppendLine($"Automatic game capture: {(settings.Enabled ? "Enabled" : "Disabled")}");
            result.AppendLine($"Reuse existing hook: {(settings.ReuseExistingHook ? "Enabled" : "Disabled")}");
            result.AppendLine($"Default alpha interpretation: {settings.AlphaMode.GetDescription()}");
            result.AppendLine($"Default capture third-party overlays: {(settings.CaptureThirdPartyOverlays ? "Enabled" : "Disabled")}");
            result.AppendLine($"Default RGB10A2 interpretation: {settings.Rgb10A2Interpretation.GetDescription()}");
            result.AppendLine($"Default hook rate while ShareX is capturing: {settings.CaptureFrameRate.GetDescription()}");
            result.AppendLine($"Default cursor: {settings.CursorMode.GetDescription()}");
            result.AppendLine($"Inactive-session safety timeout: {settings.SessionIdleTimeoutSeconds} seconds");
            result.AppendLine("Lifecycle: normal screenshots stop ShareX-owned hooks after one copied frame; multi-frame capture retains them only for its active session.");
            result.AppendLine($"Precise-mode game promotion: {gameWindowPromotionMode.GetDescription()}");
            result.AppendLine($"Configured processes: {(settings.ProcessNames.Count > 0 ? string.Join(", ", settings.ProcessNames) : "None")}");
            result.AppendLine($"OBS path: {(string.IsNullOrWhiteSpace(settings.ObsInstallationPath) ? "Automatic discovery" : settings.ObsInstallationPath)}");
            result.AppendLine();

            if (visibleMatches.Count == 0)
            {
                result.AppendLine("Visible configured games: None");
                result.AppendLine("The OBS binary check below uses x64 because no configured running game architecture could be detected.");
            }
            else
            {
                result.AppendLine("Visible configured games:");

                foreach (KeyValuePair<int,
                    (string Name, string Path, string Title, string ClassName, ObsBinaryArchitecture Architecture)> match in visibleMatches)
                {
                    ObsGameCaptureEffectiveOptions options = settings.ResolveOptions(
                        match.Value.Name,
                        match.Value.Path);
                    result.AppendLine(
                        $"- {match.Value.Name} (PID {match.Key}, {match.Value.Architecture}); " +
                        $"window=\"{match.Value.Title}\" class=\"{match.Value.ClassName}\"; " +
                        $"alpha={options.AlphaMode}, overlays={(options.CaptureThirdPartyOverlays ? "on" : "off")}, " +
                        $"RGB10A2={options.Rgb10A2Interpretation}, rate={options.CaptureFrameRate.GetDescription()}, " +
                        $"reuse={(options.ReuseExistingHook ? "on" : "off")}, " +
                        $"idle={options.SessionIdleTimeoutSeconds}s, cursor={options.CursorMode}");
                }
            }

            foreach (KeyValuePair<ObsBinaryArchitecture, ObsGameCaptureCompatibilityReport> entry in reports)
            {
                ObsGameCaptureCompatibilityReport report = entry.Value;
                result.AppendLine();
                result.AppendLine($"{entry.Key} OBS hook: {report.Status}");
                result.AppendLine(report.Message);

                if (report.BinaryPaths != null)
                {
                    result.AppendLine($"Installation: {report.BinaryPaths.InstallationRoot}");
                }

                foreach (ObsGameCaptureBinaryIdentity binary in report.Binaries)
                {
                    result.AppendLine(
                        $"- {binary.Kind}: {binary.Architecture}, version {binary.FileVersion}, " +
                        $"signature {(binary.Trust.IsTrusted ? "trusted" : $"invalid ({binary.Trust.NativeStatusHex})")}");
                }

                if (report.OffsetHelper != null)
                {
                    result.AppendLine(
                        $"Graphics offsets helper: exit {report.OffsetHelper.ExitCode}, " +
                        $"{report.OffsetHelper.Duration.TotalMilliseconds:F0} ms");
                }

                foreach (string diagnostic in report.Diagnostics)
                {
                    result.AppendLine($"- {diagnostic}");
                }
            }

            return result.ToString().TrimEnd();
        }

        private void ShowObsGameCaptureTestReport(string report)
        {
            using var dialog = new Form
            {
                Text = "OBS Game Capture test",
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(720, 480),
                MinimumSize = new Size(560, 360),
                ShowIcon = false,
                ShowInTaskbar = false
            };
            var output = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = report
            };
            var close = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Text = "Close"
            };
            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            buttons.Controls.Add(close);
            dialog.Controls.Add(output);
            dialog.Controls.Add(buttons);
            dialog.AcceptButton = close;
            dialog.CancelButton = close;
            ShareXResources.ApplyTheme(dialog, true);
            dialog.ShowDialog(this);
        }

        private void cbUseHDRSupport_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.UseHDRSupport = cbUseHDRSupport.Checked;
            SetHdrControlsEnabled(cbUseHDRSupport.Checked);
        }

        private void SetHdrControlsEnabled(bool enabled)
        {
            cbHDRProcessingBackend.Enabled = lblHDRProcessingBackend.Enabled = enabled;
            cbHDRPeakBrightnessMode.Enabled = enabled;
            cbHDRToneMappingMode.Enabled = lblHDRToneMappingMode.Enabled = enabled;
            lblHDRBrightnessNits.Enabled = enabled;
            nudHDRBrightnessNits.Enabled = enabled &&
                TaskSettings.CaptureSettings.HdrSettings?.PeakBrightnessMode == HdrPeakBrightnessMode.Custom;
            UpdateHdrToneMappingControlsEnabled();
            pObsGameCapture.Enabled = enabled;
            pHdrFileOutput.Enabled = enabled;
            lblScreenRecordingHdrMode.Enabled = enabled;
            cbScreenRecordingHdrMode.Enabled = enabled;
            lblScreenRecordingHdrInfo.Enabled = enabled;
            UpdateHdrFileOutputControlsEnabled();
        }

        private void cbHDRProcessingBackend_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbHDRProcessingBackend.SelectedIndex >= 0)
            {
                TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
                TaskSettings.CaptureSettings.HdrSettings.ProcessingBackend =
                    (HdrProcessingBackend)cbHDRProcessingBackend.SelectedIndex;
            }
        }

        private void cbHDRToneMappingMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbHDRToneMappingMode.SelectedItem is HdrToneMappingModeItem item)
            {
                TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
                TaskSettings.CaptureSettings.HdrSettings.ToneMappingMode = item.Mode;
            }
        }

        private void cbHDRPeakBrightnessMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbHDRPeakBrightnessMode.SelectedIndex >= 0)
            {
                HdrCaptureSettings settings = GetHdrCaptureSettings();
                HdrPeakBrightnessMode previousMode = settings.PeakBrightnessMode;
                HdrPeakBrightnessMode selectedMode =
                    (HdrPeakBrightnessMode)cbHDRPeakBrightnessMode.SelectedIndex;
                settings.PeakBrightnessMode = selectedMode;

                // The legacy 203-nit value represented Automatic, so seed a
                // useful manual value the first time a migrated user selects
                // Custom. Existing custom values remain untouched.
                if (loaded &&
                    previousMode == HdrPeakBrightnessMode.Automatic &&
                    selectedMode == HdrPeakBrightnessMode.Custom &&
                    Math.Abs(settings.HdrBrightnessNits - HdrCaptureSettings.DefaultBrightnessNits) < 0.01f)
                {
                    settings.HdrBrightnessNits = HdrCaptureSettings.DefaultCustomPeakBrightnessNits;
                    nudHDRBrightnessNits.SetValue(
                        (decimal)HdrCaptureSettings.DefaultCustomPeakBrightnessNits);
                }

                nudHDRBrightnessNits.Enabled = cbUseHDRSupport.Checked &&
                    settings.PeakBrightnessMode == HdrPeakBrightnessMode.Custom;
            }
        }

        private void nudHDRBrightnessNits_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.HdrSettings ??= new HdrCaptureSettings();
            TaskSettings.CaptureSettings.HdrSettings.HdrBrightnessNits = (float)nudHDRBrightnessNits.Value;
        }

        private void nudScreenRegionX_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureCustomRegion.X = (int)nudCaptureCustomRegionX.Value;
        }

        private void nudScreenRegionY_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureCustomRegion.Y = (int)nudCaptureCustomRegionY.Value;
        }

        private void nudScreenRegionWidth_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureCustomRegion.Width = (int)nudCaptureCustomRegionWidth.Value;
        }

        private void nudScreenRegionHeight_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureCustomRegion.Height = (int)nudCaptureCustomRegionHeight.Value;
        }

        private void btnCaptureCustomRegionSelectRectangle_Click(object sender, EventArgs e)
        {
            if (RegionCaptureTasks.GetRectangleRegion(out Rectangle rect, TaskSettings.CaptureSettings.SurfaceOptions,
                TaskHelpers.GetScreenshotWithoutCursor(TaskSettings)))
            {
                nudCaptureCustomRegionX.SetValue(rect.X);
                nudCaptureCustomRegionY.SetValue(rect.Y);
                nudCaptureCustomRegionWidth.SetValue(rect.Width);
                nudCaptureCustomRegionHeight.SetValue(rect.Height);
            }
        }

        private void txtCaptureCustomWindow_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.CaptureCustomWindow = txtCaptureCustomWindow.Text;
        }

        #endregion General

        #region Region capture

        private void cbRegionCaptureMultiRegionMode_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.QuickCrop = !cbRegionCaptureMultiRegionMode.Checked;
        }

        private void cbRegionCaptureMouseRightClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionRightClick = (RegionCaptureAction)cbRegionCaptureMouseRightClickAction.SelectedIndex;
        }

        private void cbRegionCaptureMouseMiddleClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionMiddleClick = (RegionCaptureAction)cbRegionCaptureMouseMiddleClickAction.SelectedIndex;
        }

        private void cbRegionCaptureMouse4ClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionX1Click = (RegionCaptureAction)cbRegionCaptureMouse4ClickAction.SelectedIndex;
        }

        private void cbRegionCaptureMouse5ClickAction_SelectedIndexChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.RegionCaptureActionX2Click = (RegionCaptureAction)cbRegionCaptureMouse5ClickAction.SelectedIndex;
        }

        private void cbRegionCaptureDetectWindows_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.DetectWindows = cbRegionCaptureDetectWindows.Checked;
            cbRegionCaptureDetectControls.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.DetectWindows;
        }

        private void cbRegionCaptureDetectControls_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.DetectControls = cbRegionCaptureDetectControls.Checked;
        }

        private void nudRegionCaptureBackgroundDimStrength_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.BackgroundDimStrength = (int)nudRegionCaptureBackgroundDimStrength.Value;
        }

        private void cbRegionCaptureUseCustomInfoText_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.UseCustomInfoText = cbRegionCaptureUseCustomInfoText.Checked;
            txtRegionCaptureCustomInfoText.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.UseCustomInfoText;
        }

        private void txtRegionCaptureCustomInfoText_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.CustomInfoText = txtRegionCaptureCustomInfoText.Text;
        }

        private void btnRegionCaptureSnapSizesAdd_Click(object sender, EventArgs e)
        {
            pRegionCaptureSnapSizes.Visible = true;
        }

        private void btnRegionCaptureSnapSizesRemove_Click(object sender, EventArgs e)
        {
            int index = cbRegionCaptureSnapSizes.SelectedIndex;

            if (index > -1)
            {
                TaskSettings.CaptureSettings.SurfaceOptions.SnapSizes.RemoveAt(index);
                cbRegionCaptureSnapSizes.Items.RemoveAt(index);
                cbRegionCaptureSnapSizes.SelectedIndex = cbRegionCaptureSnapSizes.Items.Count - 1;
            }
        }

        private void btnRegionCaptureSnapSizesDialogAdd_Click(object sender, EventArgs e)
        {
            pRegionCaptureSnapSizes.Visible = false;
            SnapSize size = new SnapSize((int)nudRegionCaptureSnapSizesWidth.Value, (int)nudRegionCaptureSnapSizesHeight.Value);
            TaskSettings.CaptureSettings.SurfaceOptions.SnapSizes.Add(size);
            cbRegionCaptureSnapSizes.Items.Add(size);
            cbRegionCaptureSnapSizes.SelectedIndex = cbRegionCaptureSnapSizes.Items.Count - 1;
        }

        private void btnRegionCaptureSnapSizesDialogCancel_Click(object sender, EventArgs e)
        {
            pRegionCaptureSnapSizes.Visible = false;
        }

        private void cbRegionCaptureShowInfo_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.ShowInfo = cbRegionCaptureShowInfo.Checked;
        }

        private void cbRegionCaptureShowMagnifier_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.ShowMagnifier = cbRegionCaptureShowMagnifier.Checked;
            cbRegionCaptureUseSquareMagnifier.Enabled = nudRegionCaptureMagnifierPixelCount.Enabled = nudRegionCaptureMagnifierPixelSize.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.ShowMagnifier;
        }

        private void cbRegionCaptureUseSquareMagnifier_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.UseSquareMagnifier = cbRegionCaptureUseSquareMagnifier.Checked;
        }

        private void nudRegionCaptureMagnifierPixelCount_ValueChanged(object sender, EventArgs e)
        {
            if (loaded)
            {
                TaskSettings.CaptureSettings.SurfaceOptions.MagnifierPixelCount = (int)nudRegionCaptureMagnifierPixelCount.Value;
            }
        }

        private void nudRegionCaptureMagnifierPixelSize_ValueChanged(object sender, EventArgs e)
        {
            if (loaded)
            {
                TaskSettings.CaptureSettings.SurfaceOptions.MagnifierPixelSize = (int)nudRegionCaptureMagnifierPixelSize.Value;
            }
        }

        private void cbRegionCaptureShowCenterCrosshair_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.ShowCenterCrosshair = cbRegionCaptureShowCenterCrosshair.Checked;
        }

        private void cbRegionCaptureShowCrosshair_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.ShowCrosshair = cbRegionCaptureShowCrosshair.Checked;
        }

        private void cbRegionCaptureIsFixedSize_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.IsFixedSize = cbRegionCaptureIsFixedSize.Checked;
            nudRegionCaptureFixedSizeWidth.Enabled = nudRegionCaptureFixedSizeHeight.Enabled = TaskSettings.CaptureSettings.SurfaceOptions.IsFixedSize;
        }

        private void nudRegionCaptureFixedSizeWidth_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.FixedSize = new Size((int)nudRegionCaptureFixedSizeWidth.Value, TaskSettings.CaptureSettings.SurfaceOptions.FixedSize.Height);
        }

        private void nudRegionCaptureFixedSizeHeight_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.FixedSize = new Size(TaskSettings.CaptureSettings.SurfaceOptions.FixedSize.Width, (int)nudRegionCaptureFixedSizeHeight.Value);
        }

        private void cbRegionCaptureShowFPS_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.ShowFPS = cbRegionCaptureShowFPS.Checked;
        }

        private void nudRegionCaptureFPSLimit_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.FPSLimit = (int)nudRegionCaptureFPSLimit.Value;
        }

        private void cbRegionCaptureActiveMonitorMode_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode = cbRegionCaptureActiveMonitorMode.Checked;
        }

        #endregion Region capture

        #region Screen recorder

        private void btnScreenRecorderFFmpegOptions_Click(object sender, EventArgs e)
        {
            ScreenRecordingOptions options = new ScreenRecordingOptions
            {
                IsRecording = true,
                FFmpeg = TaskSettings.CaptureSettings.FFmpegOptions,
                FPS = TaskSettings.CaptureSettings.ScreenRecordFPS,
                Duration = TaskSettings.CaptureSettings.ScreenRecordFixedDuration ? TaskSettings.CaptureSettings.ScreenRecordDuration : 0,
                OutputPath = "output.mp4",
                CaptureArea = Screen.PrimaryScreen.Bounds,
                DrawCursor = TaskSettings.CaptureSettings.ScreenRecordShowCursor
            };

            using (FFmpegOptionsForm form = new FFmpegOptionsForm(options))
            {
                form.ShowDialog();

                TaskSettings.CaptureSettings.FFmpegOptions = form.Options.FFmpeg;
            }
        }

        private void nudScreenRecordFPS_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordFPS = (int)nudScreenRecordFPS.Value;
        }

        private void nudGIFFPS_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.GIFFPS = (int)nudGIFFPS.Value;
        }

        private void cbScreenRecorderFixedDuration_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordFixedDuration = cbScreenRecorderFixedDuration.Checked;
            nudScreenRecorderDuration.Enabled = TaskSettings.CaptureSettings.ScreenRecordFixedDuration;
        }

        private void nudScreenRecorderDuration_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordDuration = (float)nudScreenRecorderDuration.Value;
        }

        private void cbScreenRecordAutoStart_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordAutoStart = cbScreenRecordAutoStart.Checked;
            nudScreenRecorderStartDelay.Enabled = cbScreenRecordAutoStart.Checked;
        }

        private void nudScreenRecorderStartDelay_ValueChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordStartDelay = (float)nudScreenRecorderStartDelay.Value;
        }

        private void cbScreenRecorderShowCursor_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordShowCursor = cbScreenRecorderShowCursor.Checked;
        }

        private void cbScreenRecordTwoPassEncoding_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordTwoPassEncoding = cbScreenRecordTwoPassEncoding.Checked;
        }

        private void cbScreenRecordTransparentRegion_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordTransparentRegion = cbScreenRecordTransparentRegion.Checked;
        }

        private void cbScreenRecordConfirmAbort_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.ScreenRecordAskConfirmationOnAbort = cbScreenRecordConfirmAbort.Checked;
        }

        #endregion Screen recorder

        #region OCR

        private void cbCaptureOCRDefaultLanguage_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (loaded)
            {
                TaskSettings.CaptureSettings.OCROptions.Language = ((OCRLanguage)cbCaptureOCRDefaultLanguage.SelectedItem).LanguageTag;
            }
        }

        private void btnCaptureOCRHelp_Click(object sender, EventArgs e)
        {
            URLHelpers.OpenURL(Links.DocsOCR);
        }

        private void cbCaptureOCRSilent_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.OCROptions.Silent = cbCaptureOCRSilent.Checked;
            cbCaptureOCRAutoCopy.Enabled = !TaskSettings.CaptureSettings.OCROptions.Silent;
        }

        private void cbCaptureOCRAutoCopy_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.OCROptions.AutoCopy = cbCaptureOCRAutoCopy.Checked;
        }

        private void cbCloseWindowAfterOpenServiceLink_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.CaptureSettings.OCROptions.CloseWindowAfterOpeningServiceLink = cbCloseWindowAfterOpenServiceLink.Checked;
        }

        #endregion OCR

        #endregion Capture

        #region Upload

        private void UpdateNameFormatPreviews()
        {
            NameParser nameParser = new NameParser(NameParserType.FileName)
            {
                AutoIncrementNumber = Program.Settings.NameParserAutoIncrementNumber,
                ImageWidth = 1920,
                ImageHeight = 1080,
                MaxNameLength = TaskSettings.AdvancedSettings.NamePatternMaxLength,
                MaxTitleLength = TaskSettings.AdvancedSettings.NamePatternMaxTitleLength,
                CustomTimeZone = TaskSettings.UploadSettings.UseCustomTimeZone ? TaskSettings.UploadSettings.CustomTimeZone : null,
                IsPreviewMode = true
            };

            lblNameFormatPatternPreview.Text = Resources.TaskSettingsForm_txtNameFormatPatternActiveWindow_TextChanged_Preview_ + " " +
                nameParser.Parse(TaskSettings.UploadSettings.NameFormatPattern);

            nameParser.WindowText = Text;
            nameParser.ProcessName = "ShareX";

            lblNameFormatPatternPreviewActiveWindow.Text = Resources.TaskSettingsForm_txtNameFormatPatternActiveWindow_TextChanged_Preview_ + " " +
                nameParser.Parse(TaskSettings.UploadSettings.NameFormatPatternActiveWindow);
        }

        private void cbUseDefaultUploadSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultUploadSettings = !cbOverrideUploadSettings.Checked;
            UpdateDefaultSettingVisibility();
        }

        private void txtNameFormatPattern_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.NameFormatPattern = txtNameFormatPattern.Text;
            UpdateNameFormatPreviews();
        }

        private void txtNameFormatPatternActiveWindow_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.NameFormatPatternActiveWindow = txtNameFormatPatternActiveWindow.Text;
            UpdateNameFormatPreviews();
        }

        private void cbFileUploadUseNamePattern_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.FileUploadUseNamePattern = cbFileUploadUseNamePattern.Checked;
        }

        private void btnAutoIncrementNumber_Click(object sender, EventArgs e)
        {
            Program.Settings.NameParserAutoIncrementNumber = (int)nudAutoIncrementNumber.Value;
            UpdateNameFormatPreviews();
        }

        private void cbNameFormatCustomTimeZone_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.UseCustomTimeZone = cbNameFormatCustomTimeZone.Checked;
            cbNameFormatTimeZone.Enabled = TaskSettings.UploadSettings.UseCustomTimeZone;
            UpdateNameFormatPreviews();
        }

        private void cbNameFormatTimeZone_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbNameFormatTimeZone.SelectedItem is TimeZoneInfo timeZoneInfo)
            {
                TaskSettings.UploadSettings.CustomTimeZone = timeZoneInfo;
            }

            UpdateNameFormatPreviews();
        }

        private void cbFileUploadReplaceProblematicCharacters_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.FileUploadReplaceProblematicCharacters = cbFileUploadReplaceProblematicCharacters.Checked;
        }

        private void cbURLRegexReplace_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.URLRegexReplace = cbURLRegexReplace.Checked;
            lblURLRegexReplacePattern.Enabled = txtURLRegexReplacePattern.Enabled =
                lblURLRegexReplaceReplacement.Enabled = txtURLRegexReplaceReplacement.Enabled = TaskSettings.UploadSettings.URLRegexReplace;
        }

        private void txtURLRegexReplacePattern_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.URLRegexReplacePattern = txtURLRegexReplacePattern.Text;
        }

        private void txtURLRegexReplaceReplacement_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.URLRegexReplaceReplacement = txtURLRegexReplaceReplacement.Text;
        }

        private void cbClipboardUploadContents_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.ClipboardUploadURLContents = cbClipboardUploadURLContents.Checked;
        }

        private void cbClipboardUploadAutoDetectURL_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.ClipboardUploadShortenURL = cbClipboardUploadShortenURL.Checked;
        }

        private void cbClipboardUploadShareURL_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.ClipboardUploadShareURL = cbClipboardUploadShareURL.Checked;
        }

        private void cbClipboardUploadAutoIndexFolder_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UploadSettings.ClipboardUploadAutoIndexFolder = cbClipboardUploadAutoIndexFolder.Checked;
        }

        private UploaderFilter GetUploaderFilterFromFields()
        {
            if (cbUploaderFiltersDestination.SelectedItem is IGenericUploaderService service)
            {
                UploaderFilter filter = new UploaderFilter();
                filter.Uploader = service.ServiceIdentifier;
                filter.SetExtensions(txtUploaderFiltersExtensions.Text);
                return filter;
            }

            return null;
        }

        private void AddUploaderFilterToList(UploaderFilter filter)
        {
            if (filter != null)
            {
                ListViewItem lvi = new ListViewItem(filter.Uploader);
                lvi.SubItems.Add(filter.GetExtensions());
                lvi.Tag = filter;

                lvUploaderFiltersList.Items.Add(lvi);
            }
        }

        private void UpdateUploaderFilterFields(UploaderFilter filter)
        {
            if (filter == null)
            {
                filter = new UploaderFilter();
            }

            for (int i = 0; i < cbUploaderFiltersDestination.Items.Count; i++)
            {
                if (cbUploaderFiltersDestination.Items[i] is IGenericUploaderService service &&
                    service.ServiceIdentifier.Equals(filter.Uploader, StringComparison.OrdinalIgnoreCase))
                {
                    cbUploaderFiltersDestination.SelectedIndex = i;
                    break;
                }
            }

            txtUploaderFiltersExtensions.Text = filter.GetExtensions();
        }

        private void btnUploaderFiltersAdd_Click(object sender, EventArgs e)
        {
            UploaderFilter filter = GetUploaderFilterFromFields();

            if (filter != null)
            {
                TaskSettings.UploadSettings.UploaderFilters.Add(filter);

                AddUploaderFilterToList(filter);

                lvUploaderFiltersList.SelectedIndex = lvUploaderFiltersList.Items.Count - 1;
            }
        }

        private void btnUploaderFiltersUpdate_Click(object sender, EventArgs e)
        {
            int index = lvUploaderFiltersList.SelectedIndex;

            if (index > -1)
            {
                UploaderFilter filter = GetUploaderFilterFromFields();

                if (filter != null)
                {
                    TaskSettings.UploadSettings.UploaderFilters[index] = filter;

                    ListViewItem lvi = lvUploaderFiltersList.Items[index];
                    lvi.Text = filter.Uploader;
                    lvi.SubItems[1].Text = filter.GetExtensions();
                    lvi.Tag = filter;
                }
            }
        }

        private void btnUploaderFiltersRemove_Click(object sender, EventArgs e)
        {
            int index = lvUploaderFiltersList.SelectedIndex;

            if (index > -1)
            {
                TaskSettings.UploadSettings.UploaderFilters.RemoveAt(index);

                lvUploaderFiltersList.Items.RemoveAt(index);
            }
        }

        private void lvUploaderFiltersList_SelectedIndexChanged(object sender, EventArgs e)
        {
            UploaderFilter filter = null;

            if (lvUploaderFiltersList.SelectedItems.Count > 0)
            {
                filter = lvUploaderFiltersList.SelectedItems[0].Tag as UploaderFilter;
            }

            UpdateUploaderFilterFields(filter);
        }

        #endregion Upload

        #region Actions

        private void cbUseDefaultActions_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultActions = !cbOverrideActions.Checked;
            UpdateDefaultSettingVisibility();
        }

        private void btnActionsAdd_Click(object sender, EventArgs e)
        {
            using (ActionsForm form = new ActionsForm())
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    ExternalProgram fileAction = form.FileAction;
                    fileAction.IsActive = true;
                    TaskSettings.ExternalPrograms.Add(fileAction);
                    AddFileAction(fileAction);
                }
            }
        }

        private void AddFileAction(ExternalProgram fileAction)
        {
            ListViewItem lvi = new ListViewItem(fileAction.Name ?? "");
            lvi.Tag = fileAction;
            lvi.Checked = fileAction.IsActive;
            lvi.SubItems.Add(fileAction.Path ?? "");
            lvi.SubItems.Add(fileAction.Args ?? "");
            lvi.SubItems.Add(fileAction.Extensions ?? "");
            lvActions.Items.Add(lvi);
        }

        private void btnActionsEdit_Click(object sender, EventArgs e)
        {
            if (lvActions.SelectedItems.Count > 0)
            {
                ListViewItem lvi = lvActions.SelectedItems[0];
                ExternalProgram fileAction = lvi.Tag as ExternalProgram;

                using (ActionsForm form = new ActionsForm(fileAction))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        lvi.Text = fileAction.Name ?? "";
                        lvi.SubItems[1].Text = fileAction.Path ?? "";
                        lvi.SubItems[2].Text = fileAction.Args ?? "";
                        lvi.SubItems[3].Text = fileAction.Extensions ?? "";
                    }
                }
            }
        }

        private void btnActionsDuplicate_Click(object sender, EventArgs e)
        {
            foreach (ExternalProgram fileAction in lvActions.SelectedItems.Cast<ListViewItem>().Select(x => ((ExternalProgram)x.Tag).Copy()))
            {
                TaskSettings.ExternalPrograms.Add(fileAction);
                AddFileAction(fileAction);
            }
        }

        private void btnActionsRemove_Click(object sender, EventArgs e)
        {
            if (lvActions.SelectedItems.Count > 0)
            {
                ListViewItem lvi = lvActions.SelectedItems[0];
                ExternalProgram fileAction = lvi.Tag as ExternalProgram;

                TaskSettings.ExternalPrograms.Remove(fileAction);
                lvActions.Items.Remove(lvi);
            }
        }

        private void btnActions_Click(object sender, EventArgs e)
        {
            URLHelpers.OpenURL(Links.Actions);
        }

        private void lvActions_SelectedIndexChanged(object sender, EventArgs e)
        {
            btnActionsEdit.Enabled = btnActionsDuplicate.Enabled = btnActionsRemove.Enabled = lvActions.SelectedItems.Count > 0;
        }

        private void lvActions_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            ExternalProgram fileAction = e.Item.Tag as ExternalProgram;
            fileAction.IsActive = e.Item.Checked;
        }

        private void lvActions_ItemMoved(object sender, int oldIndex, int newIndex)
        {
            TaskSettings.ExternalPrograms.Move(oldIndex, newIndex);
        }

        #endregion Actions

        #region Watch folders

        private void WatchFolderAdd(WatchFolderSettings watchFolderSetting)
        {
            if (Program.WatchFolderManager != null && watchFolderSetting != null)
            {
                Program.WatchFolderManager.AddWatchFolder(watchFolderSetting, TaskSettings);

                ListViewItem lvi = new ListViewItem(watchFolderSetting.FolderPath ?? "");
                lvi.Tag = watchFolderSetting;
                lvi.SubItems.Add(watchFolderSetting.Filter ?? "");
                lvi.SubItems.Add(watchFolderSetting.IncludeSubdirectories.ToString());
                lvWatchFolderList.Items.Add(lvi);
            }
        }

        private void WatchFolderEditSelected()
        {
            if (lvWatchFolderList.SelectedItems.Count > 0)
            {
                ListViewItem lvi = lvWatchFolderList.SelectedItems[0];
                WatchFolderSettings watchFolder = lvi.Tag as WatchFolderSettings;

                using (WatchFolderForm form = new WatchFolderForm(watchFolder))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        lvi.Text = watchFolder.FolderPath ?? "";
                        lvi.SubItems[1].Text = watchFolder.Filter ?? "";
                        lvi.SubItems[2].Text = watchFolder.IncludeSubdirectories.ToString();

                        Program.WatchFolderManager.UpdateWatchFolderState(watchFolder);
                    }
                }
            }
        }

        private void cbWatchFolderEnabled_CheckedChanged(object sender, EventArgs e)
        {
            if (loaded)
            {
                TaskSettings.WatchFolderEnabled = cbWatchFolderEnabled.Checked;

                foreach (WatchFolderSettings watchFolderSetting in TaskSettings.WatchFolderList)
                {
                    Program.WatchFolderManager.UpdateWatchFolderState(watchFolderSetting);
                }
            }
        }

        private void btnWatchFolderAdd_Click(object sender, EventArgs e)
        {
            using (WatchFolderForm form = new WatchFolderForm())
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    WatchFolderAdd(form.WatchFolder);
                }
            }
        }

        private void btnWatchFolderEdit_Click(object sender, EventArgs e)
        {
            WatchFolderEditSelected();
        }

        private void btnWatchFolderRemove_Click(object sender, EventArgs e)
        {
            if (lvWatchFolderList.SelectedItems.Count > 0)
            {
                ListViewItem lvi = lvWatchFolderList.SelectedItems[0];
                WatchFolderSettings watchFolderSetting = lvi.Tag as WatchFolderSettings;
                Program.WatchFolderManager.RemoveWatchFolder(watchFolderSetting);
                lvWatchFolderList.Items.Remove(lvi);
            }
        }

        private void lvWatchFolderList_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            WatchFolderEditSelected();
        }

        #endregion Watch folders

        #region Tools

        #region General

        private void cbUseDefaultToolsSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultToolsSettings = !cbOverrideToolsSettings.Checked;
            UpdateDefaultSettingVisibility();
        }

        private void cbImageEditorUseLegacyImageEditor_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.ToolsSettings.UseLegacyImageEditor = cbImageEditorUseLegacyImageEditor.Checked;
        }

        private void txtToolsScreenColorPickerFormat_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.ToolsSettings.ScreenColorPickerFormat = txtToolsScreenColorPickerFormat.Text;
        }

        private void txtToolsScreenColorPickerFormatCtrl_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.ToolsSettings.ScreenColorPickerFormatCtrl = txtToolsScreenColorPickerFormatCtrl.Text;
        }

        private void txtToolsScreenColorPickerInfoText_TextChanged(object sender, EventArgs e)
        {
            TaskSettings.ToolsSettings.ScreenColorPickerInfoText = txtToolsScreenColorPickerInfoText.Text;
        }

        #endregion

        #endregion Tools

        #region Advanced

        private void cbUseDefaultAdvancedSettings_CheckedChanged(object sender, EventArgs e)
        {
            TaskSettings.UseDefaultAdvancedSettings = !cbOverrideAdvancedSettings.Checked;
            UpdateDefaultSettingVisibility();
        }

        #endregion Advanced
    }
}
