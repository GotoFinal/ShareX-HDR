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
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Opens an already-published OBS hook frame without creating a keepalive,
    /// changing hook_info, or signaling any lifecycle event owned by OBS.
    /// </summary>
    internal sealed class ObsGameCapturePassiveSession : IDisposable
    {
        private const uint MaximumDimension = 32_768;
        private const uint MaximumMappingSize = 1024 * 1024 * 1024;

        private MemoryMappedFile hookInfoMapping;
        private MemoryMappedViewAccessor hookInfoView;
        private MemoryMappedFile textureMapping;
        private MemoryMappedViewAccessor textureView;

        public ObsGameCapturePublication Publication { get; private set; }

        private ObsGameCapturePassiveSession()
        {
        }

        public static bool IsHostPresent(uint processId)
        {
            if (Mutex.TryOpenExisting(ObsHookProtocol.KeepAliveName(processId), out Mutex mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }

        public static bool TryOpen(
            uint processId,
            ulong rootWindow,
            out ObsGameCapturePassiveSession session,
            out string reason)
        {
            session = null;
            var candidate = new ObsGameCapturePassiveSession();

            try
            {
                candidate.hookInfoMapping = MemoryMappedFile.OpenExisting(
                    ObsHookProtocol.HookInfoName(processId),
                    MemoryMappedFileRights.Read);
                candidate.hookInfoView = candidate.hookInfoMapping.CreateViewAccessor(
                    0,
                    ObsHookProtocol.HookInfoSize,
                    MemoryMappedFileAccess.Read);
                candidate.hookInfoView.Read(0, out ObsHookInfo info);
                ValidatePublication(info);

                string mappingName = ObsHookProtocol.TextureMappingName(rootWindow, info.MapId);

                try
                {
                    candidate.textureMapping = MemoryMappedFile.OpenExisting(
                        mappingName,
                        MemoryMappedFileRights.Read);
                }
                catch (FileNotFoundException) when (info.Window != 0 && info.Window != rootWindow)
                {
                    mappingName = ObsHookProtocol.TextureMappingName(info.Window, info.MapId);
                    candidate.textureMapping = MemoryMappedFile.OpenExisting(
                        mappingName,
                        MemoryMappedFileRights.Read);
                }

                candidate.textureView = candidate.textureMapping.CreateViewAccessor(
                    0,
                    info.MapSize,
                    MemoryMappedFileAccess.Read);
                uint sharedTextureHandle = candidate.textureView.ReadUInt32(0);

                if (sharedTextureHandle == 0)
                {
                    throw new InvalidDataException("The existing OBS hook published an empty texture handle.");
                }

                candidate.Publication = new ObsGameCapturePublication(info, sharedTextureHandle, mappingName);
                session = candidate;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or
                IOException or InvalidDataException or ArgumentException)
            {
                candidate.Dispose();
                reason = ex.Message;
                return false;
            }
        }

        private static void ValidatePublication(ObsHookInfo info)
        {
            if (!ObsHookProtocol.IsSupportedVersion(info.HookVersionMajor, info.HookVersionMinor))
            {
                throw new InvalidDataException(
                    $"Existing OBS hook protocol {info.HookVersionMajor}.{info.HookVersionMinor} is unsupported.");
            }

            if (info.CaptureType != ObsHookCaptureType.Texture)
            {
                throw new InvalidDataException("Existing OBS hook is not publishing a shared texture.");
            }

            if (info.Width == 0 || info.Height == 0 || info.Width > MaximumDimension || info.Height > MaximumDimension)
            {
                throw new InvalidDataException("Existing OBS hook published invalid frame dimensions.");
            }

            if (info.MapSize < 4 || info.MapSize > MaximumMappingSize)
            {
                throw new InvalidDataException("Existing OBS hook published an invalid mapping size.");
            }
        }

        public void Dispose()
        {
            Publication = null;
            textureView?.Dispose();
            textureView = null;
            textureMapping?.Dispose();
            textureMapping = null;
            hookInfoView?.Dispose();
            hookInfoView = null;
            hookInfoMapping?.Dispose();
            hookInfoMapping = null;
        }
    }
}
