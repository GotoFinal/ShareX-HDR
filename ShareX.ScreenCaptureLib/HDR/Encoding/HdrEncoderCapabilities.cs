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

        private static readonly string[] UltraHdrRequiredExports =
        [
            "sharex_uhdr_encode_rgba16f",
            "sharex_uhdr_decode_rgba16f",
            "sharex_uhdr_is_image",
            "sharex_uhdr_free"
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
            if (!OperatingSystem.IsWindows())
            {
                unavailableReason = "Ultra HDR JPEG support is currently available only on Windows x64.";
                return false;
            }

            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                unavailableReason =
                    $"Ultra HDR JPEG support requires an x64 process; ShareX is running as " +
                    $"{RuntimeInformation.ProcessArchitecture}. Use OpenEXR or HDR PNG on this architecture.";
                return false;
            }

            string libraryPath = Path.Combine(AppContext.BaseDirectory, UltraHdrLibraryFileName);
            if (!File.Exists(libraryPath))
            {
                unavailableReason =
                    $"Ultra HDR JPEG support is unavailable because {UltraHdrLibraryFileName} is missing " +
                    "from the ShareX application directory.";
                return false;
            }

            IntPtr libraryHandle = IntPtr.Zero;

            try
            {
                if (!NativeLibrary.TryLoad(libraryPath, out libraryHandle) || libraryHandle == IntPtr.Zero)
                {
                    unavailableReason =
                        $"Ultra HDR JPEG support is unavailable because {UltraHdrLibraryFileName} " +
                        "or one of its dependencies could not be loaded.";
                    return false;
                }

                foreach (string exportName in UltraHdrRequiredExports)
                {
                    if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out _))
                    {
                        unavailableReason =
                            $"Ultra HDR JPEG support is unavailable because {UltraHdrLibraryFileName} " +
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
                    $"Ultra HDR JPEG support is unavailable because {UltraHdrLibraryFileName} " +
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
