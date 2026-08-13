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

    public sealed class ObsGameCaptureSettings
    {
        private const int MinimumIdleTimeoutSeconds = 5;
        private const int MaximumIdleTimeoutSeconds = 600;

        private List<string> processNames = new List<string>();
        private int sessionIdleTimeoutSeconds = 120;
        private ObsGameCaptureRgb10A2Interpretation rgb10A2Interpretation;

        public bool Enabled { get; set; }
        public bool ReuseExistingHook { get; set; } = true;
        public string ObsInstallationPath { get; set; } = string.Empty;

        public List<string> ProcessNames
        {
            get => processNames;
            set => processNames = NormalizeProcessNames(value);
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

            return processNames.Any(configured =>
                configured.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                configured.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase));
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

        private static List<string> NormalizeProcessNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Select(NormalizeProcessName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
