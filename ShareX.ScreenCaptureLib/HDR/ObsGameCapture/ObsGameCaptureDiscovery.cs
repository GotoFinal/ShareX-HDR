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

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShareX.ScreenCaptureLib
{
    public sealed record ObsGameCaptureBinaryPaths(
        string InstallationRoot,
        ObsBinaryArchitecture TargetArchitecture,
        string GraphicsHookPath,
        string InjectHelperPath,
        string GraphicsOffsetsHelperPath);

    public static class ObsGameCaptureDiscovery
    {
        private const string WinCaptureRelativePath = @"data\obs-plugins\win-capture";

        public static IReadOnlyList<string> FindInstallationRoots(string explicitPath = null)
        {
            var roots = new List<string>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidate(roots, seenRoots, explicitPath);

            if (OperatingSystem.IsWindows())
            {
                foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                {
                    foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        AddRegistryCandidates(roots, seenRoots, hive, view);
                    }
                }
            }

            AddCandidate(roots, seenRoots, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio"));
            AddCandidate(roots, seenRoots, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio"));

            return roots;
        }

        public static bool TryResolveBinaryPaths(
            string installationRoot,
            ObsBinaryArchitecture targetArchitecture,
            out ObsGameCaptureBinaryPaths paths,
            out string error)
        {
            paths = null;
            error = string.Empty;

            if (targetArchitecture is not (ObsBinaryArchitecture.X86 or
                ObsBinaryArchitecture.X64 or ObsBinaryArchitecture.Arm64))
            {
                error = $"OBS Game Capture target architecture {targetArchitecture} is not supported yet.";
                return false;
            }

            string root = NormalizeRoot(installationRoot);

            if (string.IsNullOrEmpty(root))
            {
                error = "OBS installation root is invalid.";
                return false;
            }

            string suffix = targetArchitecture == ObsBinaryArchitecture.X86 ? "32" : "64";
            string captureDirectory = Path.Combine(root, WinCaptureRelativePath);
            string hook = Path.Combine(captureDirectory, $"graphics-hook{suffix}.dll");
            string injector = Path.Combine(captureDirectory, $"inject-helper{suffix}.exe");
            string offsets = Path.Combine(captureDirectory, $"get-graphics-offsets{suffix}.exe");

            if (!IsContainedBy(root, hook) || !IsContainedBy(root, injector) || !IsContainedBy(root, offsets))
            {
                error = "Resolved OBS binary path escapes the selected installation root.";
                return false;
            }

            string[] missing = new[] { hook, injector, offsets }
                .Where(x => !File.Exists(x))
                .Select(Path.GetFileName)
                .ToArray();

            if (missing.Length > 0)
            {
                error = $"OBS installation is missing: {string.Join(", ", missing)}.";
                return false;
            }

            paths = new ObsGameCaptureBinaryPaths(root, targetArchitecture, hook, injector, offsets);
            return true;
        }

        private static void AddRegistryCandidates(
            List<string> roots,
            HashSet<string> seenRoots,
            RegistryHive hive,
            RegistryView view)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

                if (uninstall == null)
                {
                    return;
                }

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using RegistryKey product = uninstall.OpenSubKey(subKeyName);
                    string displayName = product?.GetValue("DisplayName") as string;

                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.StartsWith("OBS Studio", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddCandidate(roots, seenRoots, product.GetValue("InstallLocation") as string);
                    AddCandidate(roots, seenRoots, ExtractExecutablePath(product.GetValue("DisplayIcon") as string));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        private static string ExtractExecutablePath(string displayIcon)
        {
            if (string.IsNullOrWhiteSpace(displayIcon))
            {
                return null;
            }

            string value = displayIcon.Trim();

            if (value.StartsWith('"'))
            {
                int closingQuote = value.IndexOf('"', 1);
                return closingQuote > 1 ? value[1..closingQuote] : null;
            }

            int comma = value.LastIndexOf(',');
            return comma > 0 ? value[..comma].Trim() : value;
        }

        private static void AddCandidate(List<string> roots, HashSet<string> seenRoots, string path)
        {
            string root = NormalizeRoot(path);

            if (!string.IsNullOrEmpty(root) && Directory.Exists(root) &&
                Directory.Exists(Path.Combine(root, WinCaptureRelativePath)) &&
                seenRoots.Add(root))
            {
                roots.Add(root);
            }
        }

        private static string NormalizeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                string fullPath = Path.GetFullPath(path.Trim().Trim('"'));

                if (File.Exists(fullPath))
                {
                    DirectoryInfo executableDirectory = Directory.GetParent(fullPath);

                    if (executableDirectory?.Parent?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return executableDirectory.Parent.Parent?.FullName;
                    }

                    fullPath = executableDirectory?.FullName;
                }

                var directory = new DirectoryInfo(fullPath);

                if (directory.Name.Equals("win-capture", StringComparison.OrdinalIgnoreCase) &&
                    directory.Parent?.Name.Equals("obs-plugins", StringComparison.OrdinalIgnoreCase) == true &&
                    directory.Parent.Parent?.Name.Equals("data", StringComparison.OrdinalIgnoreCase) == true)
                {
                    directory = directory.Parent.Parent.Parent;
                }

                return directory?.FullName;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static bool IsContainedBy(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path);

            return !Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }
}
