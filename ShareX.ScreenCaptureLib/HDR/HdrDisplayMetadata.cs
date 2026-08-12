#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    internal sealed class HdrDisplayMetadata
    {
        private const float DefaultSdrWhiteNits = 203f;

        public bool IsHdrActive { get; }
        public float SdrWhiteNits { get; }

        private HdrDisplayMetadata(bool isHdrActive, float sdrWhiteNits)
        {
            IsHdrActive = isHdrActive;
            SdrWhiteNits = Math.Clamp(sdrWhiteNits, 80f, 1000f);
        }

        public static Dictionary<IntPtr, HdrDisplayMetadata> GetByMonitor()
        {
            Dictionary<string, float> sdrWhiteByDeviceName = GetSdrWhiteLevelsByDeviceName();
            Dictionary<IntPtr, HdrDisplayMetadata> metadataByMonitor = new Dictionary<IntPtr, HdrDisplayMetadata>();

            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0;
                factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
                adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0;
                        adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success;
                        outputIndex++)
                    {
                        using (output)
                        using (IDXGIOutput6 output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                        {
                            if (output6 == null)
                            {
                                continue;
                            }

                            OutputDescription1 description = output6.Description1;
                            bool isHdrActive = description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            float sdrWhiteNits = sdrWhiteByDeviceName.TryGetValue(description.DeviceName, out float value)
                                ? value
                                : DefaultSdrWhiteNits;

                            metadataByMonitor[description.Monitor] = new HdrDisplayMetadata(isHdrActive, sdrWhiteNits);
                        }
                    }
                }
            }

            return metadataByMonitor;
        }

        private static Dictionary<string, float> GetSdrWhiteLevelsByDeviceName()
        {
            Dictionary<string, float> result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            try
            {
                const uint queryFlags = 0x00000002; // QDC_ONLY_ACTIVE_PATHS
                const int errorSuccess = 0;
                const int errorInsufficientBuffer = 122;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    uint pathCount = 0;
                    uint modeCount = 0;
                    int error = GetDisplayConfigBufferSizes(queryFlags, ref pathCount, ref modeCount);

                    if (error != errorSuccess)
                    {
                        return result;
                    }

                    DisplayConfigPathInfo[] paths = new DisplayConfigPathInfo[pathCount];
                    DisplayConfigModeInfo[] modes = new DisplayConfigModeInfo[modeCount];
                    error = QueryDisplayConfig(
                        queryFlags,
                        ref pathCount,
                        paths,
                        ref modeCount,
                        modes,
                        IntPtr.Zero);

                    if (error == errorInsufficientBuffer)
                    {
                        continue;
                    }

                    if (error != errorSuccess)
                    {
                        return result;
                    }

                    for (int pathIndex = 0; pathIndex < pathCount; pathIndex++)
                    {
                        DisplayConfigPathInfo path = paths[pathIndex];
                        DisplayConfigSourceDeviceName sourceName = new DisplayConfigSourceDeviceName
                        {
                            Header = DisplayConfigDeviceInfoHeader.Create(
                                1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                                path.SourceInfo.AdapterId,
                                path.SourceInfo.Id,
                                Marshal.SizeOf<DisplayConfigSourceDeviceName>())
                        };

                        if (DisplayConfigGetDeviceInfo(ref sourceName) != errorSuccess ||
                            string.IsNullOrWhiteSpace(sourceName.ViewGdiDeviceName))
                        {
                            continue;
                        }

                        DisplayConfigSdrWhiteLevel whiteLevel = new DisplayConfigSdrWhiteLevel
                        {
                            Header = DisplayConfigDeviceInfoHeader.Create(
                                11, // DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL
                                path.TargetInfo.AdapterId,
                                path.TargetInfo.Id,
                                Marshal.SizeOf<DisplayConfigSdrWhiteLevel>())
                        };

                        if (DisplayConfigGetDeviceInfo(ref whiteLevel) == errorSuccess && whiteLevel.Value > 0)
                        {
                            result[sourceName.ViewGdiDeviceName] = whiteLevel.Value / 1000f * 80f;
                        }
                    }

                    return result;
                }
            }
            catch
            {
                // DXGI still provides the HDR state. If display configuration
                // changes during enumeration, use the documented fallback.
            }

            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigRational
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigPathSourceInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIndex;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigPathTargetInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIndex;
            public uint OutputTechnology;
            public uint Rotation;
            public uint Scaling;
            public DisplayConfigRational RefreshRate;
            public uint ScanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigPathInfo
        {
            public DisplayConfigPathSourceInfo SourceInfo;
            public DisplayConfigPathTargetInfo TargetInfo;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigVideoSignalInfo
        {
            public ulong PixelRate;
            public DisplayConfigRational HSyncFrequency;
            public DisplayConfigRational VSyncFrequency;
            public uint ActiveWidth;
            public uint ActiveHeight;
            public uint TotalWidth;
            public uint TotalHeight;
            public uint AdditionalSignalInfo;
            public uint ScanLineOrdering;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DisplayConfigModeUnion
        {
            [FieldOffset(0)] public DisplayConfigVideoSignalInfo TargetMode;
            [FieldOffset(0)] public DisplayConfigSourceMode SourceMode;
            [FieldOffset(0)] public DisplayConfigDesktopImageInfo DesktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigSourceMode
        {
            public uint Width;
            public uint Height;
            public uint PixelFormat;
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigDesktopImageInfo
        {
            public int PathSourceWidth;
            public int PathSourceHeight;
            public int DesktopImageLeft;
            public int DesktopImageTop;
            public int DesktopImageRight;
            public int DesktopImageBottom;
            public int DesktopImageClipLeft;
            public int DesktopImageClipTop;
            public int DesktopImageClipRight;
            public int DesktopImageClipBottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigModeInfo
        {
            public uint InfoType;
            public uint Id;
            public Luid AdapterId;
            public DisplayConfigModeUnion ModeInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigDeviceInfoHeader
        {
            public uint Type;
            public uint Size;
            public Luid AdapterId;
            public uint Id;

            public static DisplayConfigDeviceInfoHeader Create(
                uint type,
                Luid adapterId,
                uint id,
                int size)
            {
                return new DisplayConfigDeviceInfoHeader
                {
                    Type = type,
                    Size = (uint)size,
                    AdapterId = adapterId,
                    Id = id
                };
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayConfigSourceDeviceName
        {
            public DisplayConfigDeviceInfoHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ViewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigSdrWhiteLevel
        {
            public DisplayConfigDeviceInfoHeader Header;
            public uint Value;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            ref uint pathCount,
            ref uint modeCount);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint pathCount,
            [Out] DisplayConfigPathInfo[] paths,
            ref uint modeCount,
            [Out] DisplayConfigModeInfo[] modes,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName request);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSdrWhiteLevel request);
    }
}
