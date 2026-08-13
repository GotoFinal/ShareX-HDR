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

using SharpGen.Runtime;
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ShareX.ScreenCaptureLib
{
    public static class ObsGameCaptureTextureReader
    {
        public static HdrRgba16FloatBuffer CopyRgba16FloatToOwnedBuffer(ObsGameCapturePublication publication)
        {
            return CopyRgba16FloatToOwnedBuffer(publication, null);
        }

        public static HdrRgba16FloatBuffer CopyRgba16FloatToOwnedBuffer(
            ObsGameCapturePublication publication,
            ObsGameCaptureRgb10A2ColorSpace rgb10A2ColorSpace)
        {
            return CopyRgba16FloatToOwnedBuffer(publication, (ObsGameCaptureRgb10A2ColorSpace?)rgb10A2ColorSpace);
        }

        private static HdrRgba16FloatBuffer CopyRgba16FloatToOwnedBuffer(
            ObsGameCapturePublication publication,
            ObsGameCaptureRgb10A2ColorSpace? rgb10A2ColorSpace)
        {
            ArgumentNullException.ThrowIfNull(publication);

            ObsHookInfo info = publication.HookInfo;

            if (info.CaptureType != ObsHookCaptureType.Texture || publication.SharedTextureHandle == 0)
            {
                throw new NotSupportedException("The OBS publication is not a shared-texture capture.");
            }

            Format format = (Format)info.Format;

            if (format != Format.R16G16B16A16_Float && format != Format.R10G10B10A2_UNorm)
            {
                throw new NotSupportedException($"OBS shared texture format {format} is not supported.");
            }

            if (format == Format.R10G10B10A2_UNorm && rgb10A2ColorSpace == null)
            {
                throw new NotSupportedException(
                    "OBS does not publish whether RGB10A2 is sRGB or Rec.2100 PQ. " +
                    "Call the overload with an explicit RGB10A2 color space.");
            }

            using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
            Exception lastError = null;

            for (uint adapterIndex = 0;
                factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success;
                adapterIndex++)
            {
                using (adapter)
                {
                    try
                    {
                        using ID3D11Device device = CreateDevice(adapter, out ID3D11DeviceContext context);
                        using (context)
                        using (ID3D11Texture2D source = device.OpenSharedResource<ID3D11Texture2D>(
                            new IntPtr(publication.SharedTextureHandle)))
                        {
                            return CopyToOwnedBuffer(device, context, source, info, rgb10A2ColorSpace);
                        }
                    }
                    catch (Exception ex) when (ex is SharpGenException or COMException)
                    {
                        lastError = ex;
                    }
                }
            }

            throw new InvalidOperationException(
                "No DXGI adapter could open the OBS shared texture.",
                lastError);
        }

        private static ID3D11Device CreateDevice(IDXGIAdapter adapter, out ID3D11DeviceContext context)
        {
            D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                null,
                out ID3D11Device device,
                out _,
                out context).CheckError();

            return device;
        }

        private static HdrRgba16FloatBuffer CopyToOwnedBuffer(
            ID3D11Device device,
            ID3D11DeviceContext context,
            ID3D11Texture2D source,
            ObsHookInfo info,
            ObsGameCaptureRgb10A2ColorSpace? rgb10A2ColorSpace)
        {
            Texture2DDescription sourceDescription = source.Description;

            if ((sourceDescription.Format != Format.R16G16B16A16_Float &&
                sourceDescription.Format != Format.R10G10B10A2_UNorm) ||
                sourceDescription.Format != (Format)info.Format ||
                sourceDescription.Width != info.Width || sourceDescription.Height != info.Height ||
                sourceDescription.SampleDescription.Count != 1)
            {
                throw new InvalidOperationException("OBS shared texture description does not match hook_info.");
            }

            var stagingDescription = new Texture2DDescription
            {
                Width = sourceDescription.Width,
                Height = sourceDescription.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            using ID3D11Texture2D staging = device.CreateTexture2D(stagingDescription);
            context.CopyResource(staging, source);
            MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                int rowPitch = checked((int)mapped.RowPitch);
                int width = checked((int)info.Width);
                int height = checked((int)info.Height);

                return sourceDescription.Format == Format.R16G16B16A16_Float
                    ? HdrRgba16FloatBuffer.CopyFrom(mapped.DataPointer, rowPitch, width, height)
                    : ObsGameCaptureRgb10A2Converter.ConvertToRgba16Float(
                        mapped.DataPointer,
                        rowPitch,
                        width,
                        height,
                        rgb10A2ColorSpace.Value);
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }
}
