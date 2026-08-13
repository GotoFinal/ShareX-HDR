#region License Information (GPL v3)

/*
    ShareX - A program to capture and share images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace ShareX.ScreenCaptureLib
{
    public enum ObsGameCaptureRgb10A2Interpretation
    {
        [Description("Automatic (HDR display = PQ)")]
        Automatic,

        [Description("Rec.2100 PQ")]
        Rec2100Pq,

        [Description("sRGB / high precision SDR")]
        Srgb
    }

    public enum ObsGameCaptureOptionOverride
    {
        [Description("Use default")]
        UseDefault,

        Enabled,

        Disabled
    }

    public enum ObsGameCaptureAlphaMode
    {
        [Description("Opaque (ignore framebuffer alpha)")]
        Opaque,

        [Description("Straight alpha")]
        Straight,

        [Description("Premultiplied alpha")]
        Premultiplied
    }

    public enum ObsGameCaptureAlphaModeOverride
    {
        [Description("Use default")]
        UseDefault,

        Opaque,

        [Description("Straight alpha")]
        Straight,

        [Description("Premultiplied alpha")]
        Premultiplied
    }

    public enum ObsGameCaptureRgb10A2Override
    {
        [Description("Use default")]
        UseDefault,

        Automatic,

        [Description("Rec.2100 PQ")]
        Rec2100Pq,

        [Description("sRGB / high precision SDR")]
        Srgb
    }

    public enum ObsGameCaptureFrameRate
    {
        [Description("15 FPS")]
        Fps15 = 15,

        [Description("30 FPS")]
        Fps30 = 30,

        [Description("60 FPS")]
        Fps60 = 60,

        [Description("120 FPS")]
        Fps120 = 120
    }

    public enum ObsGameCaptureFrameRateOverride
    {
        [Description("Use default")]
        UseDefault = 0,

        [Description("15 FPS")]
        Fps15 = 15,

        [Description("30 FPS")]
        Fps30 = 30,

        [Description("60 FPS")]
        Fps60 = 60,

        [Description("120 FPS")]
        Fps120 = 120
    }

    public enum ObsGameCaptureCursorMode
    {
        [Description("Use ShareX screenshot setting")]
        UseShareXSetting,

        Include,

        Exclude
    }

    public enum ObsGameCaptureCursorModeOverride
    {
        [Description("Use default")]
        UseDefault,

        [Description("Use ShareX screenshot setting")]
        UseShareXSetting,

        Include,

        Exclude
    }

    public sealed class ObsGameCaptureProcessOptions
    {
        private ObsGameCaptureAlphaModeOverride alphaMode;
        private ObsGameCaptureOptionOverride captureThirdPartyOverlays;
        private ObsGameCaptureRgb10A2Override rgb10A2Interpretation;
        private ObsGameCaptureFrameRateOverride captureFrameRate;
        private ObsGameCaptureOptionOverride reuseExistingHook;
        private ObsGameCaptureCursorModeOverride cursorMode;
        private int sessionIdleTimeoutSeconds;

        public string ProcessName { get; set; } = string.Empty;
        public string TargetProcessName { get; set; } = string.Empty;
        public string WindowTitlePattern { get; set; } = string.Empty;
        public string WindowClassPattern { get; set; } = string.Empty;

        public ObsGameCaptureAlphaModeOverride AlphaMode
        {
            get => alphaMode;
            set => alphaMode = Enum.IsDefined(value) ? value : ObsGameCaptureAlphaModeOverride.UseDefault;
        }

        // Setter-only compatibility shim for settings written before alpha mode was explicit.
        public ObsGameCaptureOptionOverride AllowTransparency
        {
            set => AlphaMode = value switch
            {
                ObsGameCaptureOptionOverride.Enabled => ObsGameCaptureAlphaModeOverride.Straight,
                ObsGameCaptureOptionOverride.Disabled => ObsGameCaptureAlphaModeOverride.Opaque,
                _ => ObsGameCaptureAlphaModeOverride.UseDefault
            };
        }

        public ObsGameCaptureOptionOverride CaptureThirdPartyOverlays
        {
            get => captureThirdPartyOverlays;
            set => captureThirdPartyOverlays = Enum.IsDefined(value) ? value : ObsGameCaptureOptionOverride.UseDefault;
        }

        public ObsGameCaptureRgb10A2Override Rgb10A2Interpretation
        {
            get => rgb10A2Interpretation;
            set => rgb10A2Interpretation = Enum.IsDefined(value)
                ? value
                : ObsGameCaptureRgb10A2Override.UseDefault;
        }

        public ObsGameCaptureFrameRateOverride CaptureFrameRate
        {
            get => captureFrameRate;
            set => captureFrameRate = Enum.IsDefined(value)
                ? value
                : ObsGameCaptureFrameRateOverride.UseDefault;
        }

        public ObsGameCaptureOptionOverride ReuseExistingHook
        {
            get => reuseExistingHook;
            set => reuseExistingHook = Enum.IsDefined(value)
                ? value
                : ObsGameCaptureOptionOverride.UseDefault;
        }

        public ObsGameCaptureCursorModeOverride CursorMode
        {
            get => cursorMode;
            set => cursorMode = Enum.IsDefined(value)
                ? value
                : ObsGameCaptureCursorModeOverride.UseDefault;
        }

        [DefaultValue(0)]
        public int SessionIdleTimeoutSeconds
        {
            get => sessionIdleTimeoutSeconds;
            set => sessionIdleTimeoutSeconds = value == 0
                ? 0
                : Math.Clamp(value, 5, 600);
        }
    }

    public sealed record ObsGameCaptureEffectiveOptions(
        ObsGameCaptureAlphaMode AlphaMode,
        bool CaptureThirdPartyOverlays,
        ObsGameCaptureRgb10A2Interpretation Rgb10A2Interpretation,
        ObsGameCaptureFrameRate CaptureFrameRate,
        bool ReuseExistingHook,
        int SessionIdleTimeoutSeconds,
        ObsGameCaptureCursorMode CursorMode);

    public sealed class ObsGameCaptureSettings
    {
        private const int MinimumIdleTimeoutSeconds = 5;
        private const int MaximumIdleTimeoutSeconds = 600;

        private List<string> processNames = new List<string>();
        private List<ObsGameCaptureProcessOptions> processOptions = new List<ObsGameCaptureProcessOptions>();
        private int sessionIdleTimeoutSeconds = 15;
        private ObsGameCaptureRgb10A2Interpretation rgb10A2Interpretation;
        private ObsGameCaptureAlphaMode alphaMode;
        private ObsGameCaptureFrameRate captureFrameRate = ObsGameCaptureFrameRate.Fps15;
        private ObsGameCaptureCursorMode cursorMode;

        public bool Enabled { get; set; }
        public bool ReuseExistingHook { get; set; } = true;
        public bool CaptureThirdPartyOverlays { get; set; }
        public string ObsInstallationPath { get; set; } = string.Empty;

        public ObsGameCaptureAlphaMode AlphaMode
        {
            get => alphaMode;
            set => alphaMode = Enum.IsDefined(value) ? value : ObsGameCaptureAlphaMode.Opaque;
        }

        // Setter-only compatibility shim for settings written before alpha mode was explicit.
        public bool AllowTransparency
        {
            set => AlphaMode = value
                ? ObsGameCaptureAlphaMode.Straight
                : ObsGameCaptureAlphaMode.Opaque;
        }

        public ObsGameCaptureFrameRate CaptureFrameRate
        {
            get => captureFrameRate;
            set => captureFrameRate = Enum.IsDefined(value) ? value : ObsGameCaptureFrameRate.Fps15;
        }

        public ObsGameCaptureCursorMode CursorMode
        {
            get => cursorMode;
            set => cursorMode = Enum.IsDefined(value)
                ? value
                : ObsGameCaptureCursorMode.UseShareXSetting;
        }

        public List<string> ProcessNames
        {
            get => processNames;
            set => processNames = NormalizeProcessNames(value);
        }

        public List<ObsGameCaptureProcessOptions> ProcessOptions
        {
            get => processOptions;
            set => processOptions = NormalizeProcessOptions(value);
        }

        public ObsGameCaptureRgb10A2Interpretation Rgb10A2Interpretation
        {
            get => rgb10A2Interpretation;
            set => rgb10A2Interpretation = Enum.IsDefined(value)
                ? value
                : ObsGameCaptureRgb10A2Interpretation.Automatic;
        }

        public int SessionIdleTimeoutSeconds
        {
            get => sessionIdleTimeoutSeconds;
            set => sessionIdleTimeoutSeconds = Math.Clamp(
                value,
                MinimumIdleTimeoutSeconds,
                MaximumIdleTimeoutSeconds);
        }

        public bool MatchesProcess(string processName, string processPath = null)
        {
            if (!Enabled || processNames.Count == 0)
            {
                return false;
            }

            string normalizedName = NormalizeProcessName(processName);
            string normalizedFileName = NormalizeProcessName(Path.GetFileName(processPath));

            bool directlyConfigured = processNames.Any(configured =>
                configured.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                configured.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase));

            return directlyConfigured || processOptions.Any(x =>
            {
                string target = NormalizeProcessName(x.TargetProcessName);
                return IsProfileConfigured(x.ProcessName) &&
                    !string.IsNullOrWhiteSpace(target) &&
                    (target.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                    target.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase));
            });
        }

        public bool MatchesWindow(
            string processName,
            string processPath,
            string windowTitle,
            string windowClass)
        {
            if (!MatchesProcess(processName, processPath))
            {
                return false;
            }

            ObsGameCaptureProcessOptions options = GetProcessOptionsForTarget(processName, processPath);
            return options == null ||
                (MatchesGlob(windowTitle, options.WindowTitlePattern) &&
                MatchesGlob(windowClass, options.WindowClassPattern));
        }

        public void SetProcessNames(string processList)
        {
            ProcessNames = (processList ?? string.Empty)
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        public string GetProcessNamesText()
        {
            return string.Join(Environment.NewLine, processNames);
        }

        public ObsGameCaptureProcessOptions GetProcessOptions(string processName)
        {
            string normalizedName = NormalizeProcessName(processName);
            return processOptions.FirstOrDefault(x =>
                NormalizeProcessName(x.ProcessName).Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        }

        public ObsGameCaptureProcessOptions GetProcessOptionsForTarget(
            string processName,
            string processPath = null)
        {
            string normalizedName = NormalizeProcessName(processName);
            string normalizedFileName = NormalizeProcessName(Path.GetFileName(processPath));

            ObsGameCaptureProcessOptions directMatch = processOptions.FirstOrDefault(x =>
            {
                string configured = NormalizeProcessName(x.ProcessName);
                return configured.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                    configured.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase);
            });

            if (directMatch != null)
            {
                return directMatch;
            }

            return processOptions.FirstOrDefault(x =>
            {
                string target = NormalizeProcessName(x.TargetProcessName);
                return IsProfileConfigured(x.ProcessName) &&
                    !string.IsNullOrWhiteSpace(target) &&
                    (target.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                    target.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase));
            });
        }

        private bool IsProfileConfigured(string processName)
        {
            string normalizedName = NormalizeProcessName(processName);
            return processNames.Any(x => x.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        }

        public void SetProcessOptions(ObsGameCaptureProcessOptions options)
        {
            string normalizedName = NormalizeProcessName(options?.ProcessName);

            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return;
            }

            processOptions.RemoveAll(x =>
                NormalizeProcessName(x.ProcessName).Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

            if (HasOverrides(options))
            {
                options.ProcessName = normalizedName;
                processOptions.Add(options);
                processOptions = NormalizeProcessOptions(processOptions);
            }
        }

        public ObsGameCaptureEffectiveOptions ResolveOptions(string processName, string processPath = null)
        {
            ObsGameCaptureProcessOptions processSetting = GetProcessOptionsForTarget(processName, processPath);

            return new ObsGameCaptureEffectiveOptions(
                ResolveAlphaMode(processSetting?.AlphaMode, AlphaMode),
                ResolveOption(processSetting?.CaptureThirdPartyOverlays, CaptureThirdPartyOverlays),
                ResolveRgb10A2(processSetting?.Rgb10A2Interpretation, Rgb10A2Interpretation),
                ResolveFrameRate(processSetting?.CaptureFrameRate, CaptureFrameRate),
                ResolveOption(processSetting?.ReuseExistingHook, ReuseExistingHook),
                processSetting?.SessionIdleTimeoutSeconds > 0
                    ? processSetting.SessionIdleTimeoutSeconds
                    : SessionIdleTimeoutSeconds,
                ResolveCursorMode(processSetting?.CursorMode, CursorMode));
        }

        private static List<string> NormalizeProcessNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Select(NormalizeProcessName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<ObsGameCaptureProcessOptions> NormalizeProcessOptions(
            IEnumerable<ObsGameCaptureProcessOptions> options)
        {
            return (options ?? Enumerable.Empty<ObsGameCaptureProcessOptions>())
                .Where(x => x != null)
                .Select(x => new ObsGameCaptureProcessOptions
                {
                    ProcessName = NormalizeProcessName(x.ProcessName),
                    TargetProcessName = NormalizeProcessName(x.TargetProcessName),
                    WindowTitlePattern = NormalizePattern(x.WindowTitlePattern),
                    WindowClassPattern = NormalizePattern(x.WindowClassPattern),
                    AlphaMode = x.AlphaMode,
                    CaptureThirdPartyOverlays = x.CaptureThirdPartyOverlays,
                    Rgb10A2Interpretation = x.Rgb10A2Interpretation,
                    CaptureFrameRate = x.CaptureFrameRate,
                    ReuseExistingHook = x.ReuseExistingHook,
                    SessionIdleTimeoutSeconds = x.SessionIdleTimeoutSeconds,
                    CursorMode = x.CursorMode
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.ProcessName) && HasOverrides(x))
                .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool ResolveOption(ObsGameCaptureOptionOverride? value, bool defaultValue)
        {
            return value switch
            {
                ObsGameCaptureOptionOverride.Enabled => true,
                ObsGameCaptureOptionOverride.Disabled => false,
                _ => defaultValue
            };
        }

        private static ObsGameCaptureAlphaMode ResolveAlphaMode(
            ObsGameCaptureAlphaModeOverride? value,
            ObsGameCaptureAlphaMode defaultValue)
        {
            return value switch
            {
                ObsGameCaptureAlphaModeOverride.Opaque => ObsGameCaptureAlphaMode.Opaque,
                ObsGameCaptureAlphaModeOverride.Straight => ObsGameCaptureAlphaMode.Straight,
                ObsGameCaptureAlphaModeOverride.Premultiplied => ObsGameCaptureAlphaMode.Premultiplied,
                _ => defaultValue
            };
        }

        private static ObsGameCaptureRgb10A2Interpretation ResolveRgb10A2(
            ObsGameCaptureRgb10A2Override? value,
            ObsGameCaptureRgb10A2Interpretation defaultValue)
        {
            return value switch
            {
                ObsGameCaptureRgb10A2Override.Automatic => ObsGameCaptureRgb10A2Interpretation.Automatic,
                ObsGameCaptureRgb10A2Override.Rec2100Pq => ObsGameCaptureRgb10A2Interpretation.Rec2100Pq,
                ObsGameCaptureRgb10A2Override.Srgb => ObsGameCaptureRgb10A2Interpretation.Srgb,
                _ => defaultValue
            };
        }

        private static ObsGameCaptureFrameRate ResolveFrameRate(
            ObsGameCaptureFrameRateOverride? value,
            ObsGameCaptureFrameRate defaultValue)
        {
            return value is { } frameRate && frameRate != ObsGameCaptureFrameRateOverride.UseDefault
                ? (ObsGameCaptureFrameRate)(int)frameRate
                : defaultValue;
        }

        private static ObsGameCaptureCursorMode ResolveCursorMode(
            ObsGameCaptureCursorModeOverride? value,
            ObsGameCaptureCursorMode defaultValue)
        {
            return value switch
            {
                ObsGameCaptureCursorModeOverride.UseShareXSetting => ObsGameCaptureCursorMode.UseShareXSetting,
                ObsGameCaptureCursorModeOverride.Include => ObsGameCaptureCursorMode.Include,
                ObsGameCaptureCursorModeOverride.Exclude => ObsGameCaptureCursorMode.Exclude,
                _ => defaultValue
            };
        }

        private static bool HasOverrides(ObsGameCaptureProcessOptions options)
        {
            return options != null &&
                (!string.IsNullOrWhiteSpace(options.TargetProcessName) ||
                !string.IsNullOrWhiteSpace(options.WindowTitlePattern) ||
                !string.IsNullOrWhiteSpace(options.WindowClassPattern) ||
                options.AlphaMode != ObsGameCaptureAlphaModeOverride.UseDefault ||
                options.CaptureThirdPartyOverlays != ObsGameCaptureOptionOverride.UseDefault ||
                options.Rgb10A2Interpretation != ObsGameCaptureRgb10A2Override.UseDefault ||
                options.CaptureFrameRate != ObsGameCaptureFrameRateOverride.UseDefault ||
                options.ReuseExistingHook != ObsGameCaptureOptionOverride.UseDefault ||
                options.SessionIdleTimeoutSeconds != 0 ||
                options.CursorMode != ObsGameCaptureCursorModeOverride.UseDefault);
        }

        internal static bool MatchesGlob(string value, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            value ??= string.Empty;
            int valueIndex = 0;
            int patternIndex = 0;
            int starIndex = -1;
            int retryValueIndex = 0;

            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] == '?' ||
                    char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
                {
                    valueIndex++;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    retryValueIndex = valueIndex;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex = ++retryValueIndex;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == pattern.Length;
        }

        private static string NormalizePattern(string pattern)
        {
            string normalized = (pattern ?? string.Empty).Trim();
            return normalized.Length <= 512 ? normalized : normalized[..512];
        }

        private static string NormalizeProcessName(string processName)
        {
            string name = (processName ?? string.Empty).Trim().Trim('"');

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return name;
        }
    }
}
