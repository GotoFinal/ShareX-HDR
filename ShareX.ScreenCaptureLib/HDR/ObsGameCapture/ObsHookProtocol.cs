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
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    public static class ObsHookProtocol
    {
        public const uint SupportedMajorVersion = 1;
        public const uint SupportedMinorVersion = 8;
        public const int HookInfoSize = 648;

        public static string KeepAliveName(uint processId) => $"CaptureHook_KeepAlive{processId}";
        public static string PipeName(uint processId) => $"CaptureHook_Pipe{processId}";
        public static string PipePath(uint processId) => $@"\\.\pipe\{PipeName(processId)}";
        public static string HookInfoName(uint processId) => $"CaptureHook_HookInfo{processId}";
        public static string RestartEventName(uint processId) => $"CaptureHook_Restart{processId}";
        public static string StopEventName(uint processId) => $"CaptureHook_Stop{processId}";
        public static string InitializeEventName(uint processId) => $"CaptureHook_Initialize{processId}";
        public static string HookReadyEventName(uint processId) => $"CaptureHook_HookReady{processId}";
        public static string HookExitEventName(uint processId) => $"CaptureHook_Exit{processId}";
        public static string TextureMutexName(uint processId, int index) => index switch
        {
            1 => $"CaptureHook_TextureMutex1{processId}",
            2 => $"CaptureHook_TextureMutex2{processId}",
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        public static string TextureMappingName(ulong rootWindow, uint mapId) => $"CaptureHook_Texture_{rootWindow}_{mapId}";

        public static bool IsSupportedVersion(uint major, uint minor) =>
            major == SupportedMajorVersion && minor == SupportedMinorVersion;
    }

    public enum ObsHookCaptureType : uint
    {
        Memory,
        Texture
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsD3D8Offsets
    {
        public uint Present;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsD3D9Offsets
    {
        public uint Present;
        public uint PresentEx;
        public uint PresentSwap;
        public uint D3D9ClassOffset;
        public uint IsD3D9ExClassOffset;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsDxgiOffsets
    {
        public uint Present;
        public uint Resize;
        public uint Present1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsDirectDrawOffsets
    {
        public uint SurfaceCreate;
        public uint SurfaceRestore;
        public uint SurfaceRelease;
        public uint SurfaceUnlock;
        public uint SurfaceBlt;
        public uint SurfaceFlip;
        public uint SurfaceSetPalette;
        public uint PaletteSetEntries;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsDxgiOffsets2
    {
        public uint Release;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsD3D12Offsets
    {
        public uint ExecuteCommandLists;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ObsGraphicsOffsets
    {
        public ObsD3D8Offsets D3D8;
        public ObsD3D9Offsets D3D9;
        public ObsDxgiOffsets Dxgi;
        public ObsDirectDrawOffsets DirectDraw;
        public ObsDxgiOffsets2 Dxgi2;
        public ObsD3D12Offsets D3D12;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public unsafe struct ObsHookInfo
    {
        public uint HookVersionMajor;
        public uint HookVersionMinor;
        public ObsHookCaptureType CaptureType;
        public uint Window;
        public uint Format;
        public uint Width;
        public uint Height;
        public uint UnusedBaseWidth;
        public uint UnusedBaseHeight;
        public uint Pitch;
        public uint MapId;
        public uint MapSize;

        [MarshalAs(UnmanagedType.I1)]
        public bool Flip;

        public ulong FrameInterval;

        [MarshalAs(UnmanagedType.I1)]
        public bool UnusedUseScale;

        [MarshalAs(UnmanagedType.I1)]
        public bool ForceSharedMemory;

        [MarshalAs(UnmanagedType.I1)]
        public bool CaptureOverlay;

        [MarshalAs(UnmanagedType.I1)]
        public bool AllowSrgbAlias;

        public ObsGraphicsOffsets Offsets;
        public fixed uint Reserved[126];
    }
}
