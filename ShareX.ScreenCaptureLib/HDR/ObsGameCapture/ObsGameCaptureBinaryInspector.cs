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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ShareX.ScreenCaptureLib
{
    public enum ObsBinaryArchitecture
    {
        Unsupported,
        X86,
        X64,
        Arm64
    }

    public enum ObsGameCaptureArtifactKind
    {
        GraphicsHook,
        InjectHelper,
        GraphicsOffsetsHelper
    }

    public sealed record ObsAuthenticodeTrustResult(
        bool IsTrusted,
        int NativeStatus,
        string SignerSubject,
        string SignerThumbprint)
    {
        public string NativeStatusHex => $"0x{unchecked((uint)NativeStatus):X8}";
    }

    public sealed record ObsGameCaptureBinaryIdentity(
        ObsGameCaptureArtifactKind Kind,
        string Path,
        ObsBinaryArchitecture Architecture,
        string FileVersion,
        string ProductVersion,
        string Sha256,
        long Length,
        ObsAuthenticodeTrustResult Trust);

    public static class ObsGameCaptureBinaryInspector
    {
        public static ObsGameCaptureBinaryIdentity Inspect(string path, ObsGameCaptureArtifactKind kind)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = System.IO.Path.GetFullPath(path);
            var file = new FileInfo(fullPath);

            if (!file.Exists)
            {
                throw new FileNotFoundException("OBS Game Capture binary was not found.", fullPath);
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(fullPath);

            return new ObsGameCaptureBinaryIdentity(
                kind,
                fullPath,
                ReadArchitecture(fullPath),
                version.FileVersion ?? string.Empty,
                version.ProductVersion ?? string.Empty,
                ComputeSha256(fullPath),
                file.Length,
                VerifyAuthenticode(fullPath));
        }

        public static ObsBinaryArchitecture ReadArchitecture(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            {
                return ObsBinaryArchitecture.Unsupported;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();

            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                return ObsBinaryArchitecture.Unsupported;
            }

            stream.Position = peOffset;

            if (reader.ReadUInt32() != 0x00004550)
            {
                return ObsBinaryArchitecture.Unsupported;
            }

            return reader.ReadUInt16() switch
            {
                0x014C => ObsBinaryArchitecture.X86,
                0x8664 => ObsBinaryArchitecture.X64,
                0xAA64 => ObsBinaryArchitecture.Arm64,
                _ => ObsBinaryArchitecture.Unsupported
            };
        }

        private static string ComputeSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static ObsAuthenticodeTrustResult VerifyAuthenticode(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new ObsAuthenticodeTrustResult(false, unchecked((int)0x800B0100), string.Empty, string.Empty);
            }

            IntPtr filePath = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            WinTrustData trustData = default;
            Guid action = WinTrustActionGenericVerifyV2;
            int status;

            try
            {
                filePath = Marshal.StringToCoTaskMemUni(path);
                var fileInfo = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePath
                };

                fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                trustData = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = WinTrustDataUiChoice.None,
                    RevocationChecks = WinTrustDataRevocationChecks.WholeChain,
                    UnionChoice = WinTrustDataChoice.File,
                    FileInfo = fileInfoPointer,
                    StateAction = WinTrustDataStateAction.Verify,
                    ProviderFlags = WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
                        WinTrustProviderFlags.CacheOnlyUrlRetrieval
                };

                status = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
            finally
            {
                if (trustData.StructSize != 0)
                {
                    trustData.StateAction = WinTrustDataStateAction.Close;
                    _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
                }

                if (fileInfoPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(fileInfoPointer);
                }

                if (filePath != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(filePath);
                }
            }

            string signerSubject = string.Empty;
            string signerThumbprint = string.Empty;

            if (status == 0)
            {
                TryReadSigner(path, out signerSubject, out signerThumbprint);
            }

            return new ObsAuthenticodeTrustResult(status == 0, status, signerSubject, signerThumbprint);
        }

        private static void TryReadSigner(string path, out string subject, out string thumbprint)
        {
            subject = string.Empty;
            thumbprint = string.Empty;

            try
            {
#pragma warning disable SYSLIB0057 // There is no modern managed API that extracts an embedded PE Authenticode signer.
                using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using X509Certificate2 certificate2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                subject = certificate2.Subject;
                thumbprint = certificate2.Thumbprint;
            }
            catch (CryptographicException)
            {
            }
        }

        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        [Flags]
        private enum WinTrustProviderFlags : uint
        {
            RevocationCheckChainExcludeRoot = 0x00000080,
            CacheOnlyUrlRetrieval = 0x00001000
        }

        private enum WinTrustDataUiChoice : uint
        {
            None = 2
        }

        private enum WinTrustDataRevocationChecks : uint
        {
            WholeChain = 1
        }

        private enum WinTrustDataChoice : uint
        {
            File = 1
        }

        private enum WinTrustDataStateAction : uint
        {
            Verify = 1,
            Close = 2
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public WinTrustDataUiChoice UiChoice;
            public WinTrustDataRevocationChecks RevocationChecks;
            public WinTrustDataChoice UnionChoice;
            public IntPtr FileInfo;
            public WinTrustDataStateAction StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public WinTrustProviderFlags ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr window,
            [In] ref Guid action,
            [In, Out] ref WinTrustData trustData);
    }
}
