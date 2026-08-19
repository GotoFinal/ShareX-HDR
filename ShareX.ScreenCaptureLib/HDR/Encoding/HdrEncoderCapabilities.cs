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
using System.IO;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    public static class HdrEncoderCapabilities
    {
        private const string UltraHdrLibraryFileName = "ShareX.UltraHdr.dll";
        private const string AvifLibraryFileName = "ShareX.Avif.dll";

        private static readonly string[] UltraHdrRequiredExports =
        [
            "sharex_uhdr_encode_rgba16f",
            "sharex_uhdr_decode_rgba16f",
            "sharex_uhdr_is_image",
            "sharex_uhdr_free"
        ];

        private static readonly string[] AvifRequiredExports =
        [
            "sharex_avif_encode_rgba10",
            "sharex_avif_decode_rgba10",
            "sharex_avif_probe_hdr",
            "sharex_avif_free"
        ];

        public static bool TryGetAvailability(HdrFileFormat format, out string unavailableReason)
        {
            unavailableReason = null;

            switch (format)
            {
                case HdrFileFormat.OpenExr:
                case HdrFileFormat.HdrPng:
                    return true;
                case HdrFileFormat.UltraHdrJpeg:
                    return TryGetUltraHdrAvailability(out unavailableReason);
                case HdrFileFormat.Avif:
                    return TryGetAvifAvailability(out unavailableReason);
                default:
                    unavailableReason = $"Unknown HDR file format: {format}.";
                    return false;
            }
        }

        public static void EnsureAvailable(HdrFileFormat format)
        {
            if (!TryGetAvailability(format, out string reason))
            {
                throw new NotSupportedException(reason);
            }
        }

        private static bool TryGetUltraHdrAvailability(out string unavailableReason)
        {
            return TryGetNativeAvailability(
                "Ultra HDR JPEG",
                UltraHdrLibraryFileName,
                UltraHdrRequiredExports,
                [Architecture.X64],
                "Use OpenEXR or HDR PNG on this architecture.",
                out unavailableReason);
        }

        private static bool TryGetAvifAvailability(out string unavailableReason)
        {
            return TryGetNativeAvailability(
                "HDR AVIF",
                AvifLibraryFileName,
                AvifRequiredExports,
                [Architecture.X64, Architecture.Arm64],
                "Use OpenEXR or HDR PNG on this architecture.",
                out unavailableReason);
        }

        private static bool TryGetNativeAvailability(
            string featureName,
            string libraryFileName,
            string[] requiredExports,
            Architecture[] supportedArchitectures,
            string architectureAlternative,
            out string unavailableReason)
        {
            if (!OperatingSystem.IsWindows())
            {
                unavailableReason = $"{featureName} support is currently available only on Windows.";
                return false;
            }

            if (Array.IndexOf(supportedArchitectures, RuntimeInformation.ProcessArchitecture) < 0)
            {
                unavailableReason =
                    $"{featureName} support is not packaged for this architecture; ShareX is running as " +
                    $"{RuntimeInformation.ProcessArchitecture}. {architectureAlternative}";
                return false;
            }

            string libraryPath = Path.Combine(AppContext.BaseDirectory, libraryFileName);
            if (!File.Exists(libraryPath))
            {
                unavailableReason =
                    $"{featureName} support is unavailable because {libraryFileName} is missing " +
                    "from the ShareX application directory.";
                return false;
            }

            IntPtr libraryHandle = IntPtr.Zero;

            try
            {
                if (!NativeLibrary.TryLoad(libraryPath, out libraryHandle) || libraryHandle == IntPtr.Zero)
                {
                    unavailableReason =
                        $"{featureName} support is unavailable because {libraryFileName} " +
                        "or one of its dependencies could not be loaded.";
                    return false;
                }

                foreach (string exportName in requiredExports)
                {
                    if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out _))
                    {
                        unavailableReason =
                            $"{featureName} support is unavailable because {libraryFileName} " +
                            $"does not provide the required {exportName} entry point.";
                        return false;
                    }
                }

                unavailableReason = null;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or FileLoadException)
            {
                unavailableReason =
                    $"{featureName} support is unavailable because {libraryFileName} " +
                    $"could not be loaded: {e.Message}";
                return false;
            }
            finally
            {
                if (libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(libraryHandle);
                }
            }
        }
    }
}
