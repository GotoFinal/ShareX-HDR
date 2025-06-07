using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DirectXTexNet;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Mathematics.PackedVector;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class Tonemapping
{
    public static ID3D11Texture2D TonemapOnCpu(ModernCaptureMonitorDescription stateRegion, DeviceAccess deviceAccess, ID3D11Texture2D dupStateStaging,
        ID3D11Device device, ID3D11DeviceContext ctx, ShaderHdrMetadata stateHdrMetadata)
    {
        ConvertToSDRPixelsInPlace(device, ctx, dupStateStaging, out var sdrPixels, out var sdrMaxYInPQ);
        return dupStateStaging; // TODO
    }

    public static void ConvertToSDRPixelsInPlace(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11Texture2D image,
        out Vector4[] scrgb,
        out float maxYInPQ)
    {
        int width = (int)image.Description.Width;
        int height = (int)image.Description.Height;
        var fmt = image.Description.Format;
        scrgb = image.GetPixelSpan();
        maxYInPQ = CalculateMaxYInPQ(scrgb);
        PerformTonemapping(scrgb, maxYInPQ, scrgb);

        unsafe
        {
            if (fmt == Format.R32G32B32A32_Float)
            {
                // TODO: does this work?
                fixed (Vector4* pSrc = scrgb)
                {
                    int strideInBytes = width * sizeof(Vector4);
                    var sysMem = new DataBox((IntPtr)pSrc, strideInBytes, 0);
                    context.UpdateSubresource(sysMem, image);
                }
            }
            else if (fmt == Format.R16G16B16A16_Float)
            {
                // TODO: avoid that copy?
                int pixelCount = width * height;
                ulong[] packed = new ulong[pixelCount];

                for (int i = 0; i < pixelCount; i++)
                {
                    Half4 h = scrgb[i];
                    packed[i] = h.PackedValue;
                }

                var handle = GCHandle.Alloc(packed, GCHandleType.Pinned);
                try
                {
                    IntPtr ptr = handle.AddrOfPinnedObject();
                    int rowPitch = width * sizeof(ulong); // 8 bytes × width
                    var box = new DataBox(ptr, rowPitch, 0);
                    context.UpdateSubresource(box, image);
                }
                finally
                {
                    handle.Free();
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"ConvertToSDRPixelsInPlace: Format {fmt} is not supported. " +
                    "Only R32G32B32A32_Float and R16G16B16A16_Float are implemented.");
            }
        }
    }


    public static ID3D11Texture2D TonemapOnGpu(ModernCaptureMonitorDescription region, DeviceAccess deviceAccess,
        ID3D11Texture2D inputHdrTex,
        ID3D11Device device,
        ID3D11DeviceContext ctx, ShaderHdrMetadata metadata)
    {
        metadata.MaxYInPQ = Tonemapping.CalculateMaxYInPQ(inputHdrTex, out var pixelData);
        var structure = new ShaderInputStructure();
        var quadVerts = Direct3DUtils.ConstructForScreen(region);
        // var quadVerts = new[]
        // {
        //     new ShaderInputStructure(position: new Vector2(-1f, +1f), textureCoord: new Vector2(0f, 0f)),
        //     new ShaderInputStructure(position: new Vector2(+1f, +1f), textureCoord: new Vector2(1f, 0f)),
        //     new ShaderInputStructure(position: new Vector2(-1f, -1f), textureCoord: new Vector2(0f, 1f)),
        //     new ShaderInputStructure(position: new Vector2(+1f, +1f), textureCoord: new Vector2(1f, 0f)),
        //     new ShaderInputStructure(position: new Vector2(+1f, -1f), textureCoord: new Vector2(1f, 1f)),
        //     new ShaderInputStructure(position: new Vector2(-1f, -1f), textureCoord: new Vector2(0f, 1f)),
        // };


        var vertexBuffer = device.CreateBuffer(quadVerts, BindFlags.VertexBuffer);

        ShaderHdrMetadata[] metadataArray = [metadata];
        var hdrCBuffer = device.CreateBuffer(metadataArray, BindFlags.ConstantBuffer);

        var inDesc = inputHdrTex.Description;
        var ldrDesc = new Texture2DDescription
        {
            Width = inDesc.Width,
            Height = inDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        var ldrTex = device.CreateTexture2D(ldrDesc);
        var ldrRtv = device.CreateRenderTargetView(ldrTex);

        var hdrSrvDesc = new ShaderResourceViewDescription
        {
            Format = inDesc.Format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1
            }
        };
        var hdrSrv = device.CreateShaderResourceView(inputHdrTex, hdrSrvDesc);

        ctx.OMSetRenderTargets(ldrRtv);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(deviceAccess.inputLayout);
        ctx.IASetVertexBuffer(0, vertexBuffer, ShaderInputStructure.SizeInBytes);

        ctx.VSSetShader(deviceAccess.vxShader);
        ctx.PSSetShader(deviceAccess.pxShader);
        ctx.PSSetConstantBuffer(0, hdrCBuffer);
        ctx.PSSetSampler(0, deviceAccess.samplerState);
        ctx.PSSetShaderResource(0, hdrSrv);

        ctx.RSSetViewport(new Viewport(0, 0, inDesc.Width, inDesc.Height, 0, 1));

        ctx.Draw(vertexCount: 6, startVertexLocation: 0);

        hdrSrv.Dispose();
        hdrCBuffer.Dispose();
        vertexBuffer.Dispose();
        ldrRtv.Dispose();

        return ldrTex;
    }

    // heavily inspired by https://github.com/SpecialKO/SKIV/blob/ed2a4a9de93ebba9661f9e8ed31c5d67ab490d2d/src/utility/image.cpp#L1300C1-L1300C25
    // MIT License Copyright (c) 2024 Aemony

    private static readonly string defaultSDRFileExt = ".png";

    public static float CalculateMaxYInPQ(Vector4[] scrgb)
    {
        var log = Console.Out;

        // 5. Initialize luminance‐tracking vectors (from DirectXMath)
        Vector4 maxLum = Vector4.Zero;
        Vector4 minLum = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);

        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImageBegin");

        var stopwatchCore = Stopwatch.StartNew();
        for (var i = 0; i < scrgb.Length; i++)
        {
            Vector4 v = scrgb[i];
            v = Vector4.Transform(v, ColorspaceUtils.from709ToXYZ);
            maxLum = Vector4.Create(MathF.Max(v.Y, maxLum.Y));
            minLum = Vector4.Create(MathF.Min(v.Y, minLum.Y));
        }

        minLum = Vector4.Max(Vector4.Zero, minLum);


        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, min/max calculated (max: " + maxLum.Y + "): " + stopwatchCore.ElapsedMilliseconds + "ms");

        uint[] luminance_freq = new uint[65536];
        float fLumRange = maxLum.Y - minLum.Y;
        for (var i = 0; i < scrgb.Length; i++)
        {
            Vector4 v = scrgb[i];

            v = Vector4.Max(Vector4.Zero, Vector4.Transform(v.AsVector3(), ColorspaceUtils.from709ToXYZ));
            // v = Vector4.Max(Vector4.Zero, Vector3.Transform(v.AsVector3(), from709ToXYZ).AsVector4());
            luminance_freq[Math.Clamp((int)Math.Round((v.Y - minLum.Y) / (fLumRange / 65536.0f)), 0, 65535)]++;

            v = Vector4.Max(Vector4.Zero, Vector4.Transform(v, ColorspaceUtils.from709ToXYZ));

            int idx = Math.Clamp(
                (int)Math.Round((v.Y - minLum.Y) / (fLumRange / 65536.0f)),
                0,
                65535
            );
            luminance_freq[idx]++;
        }

        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, luminance_freq calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

        double percent = 100.0;
        double img_size = scrgb.LongLength;

        for (int i = 65535; i >= 0; --i)
        {
            percent -= 100.0 * ((double)luminance_freq[i] / img_size);
            if (percent <= 99.94)
            {
                float percentileLum = minLum.Y + (fLumRange * ((float)i / 65536.0f));
                maxLum = Vector4.Create(percentileLum);
                break;
            }
        }

        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, percentileLum calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

        const float scale = 1;
        const float _maxNitsToTonemap = 125.0f * scale;
        float SDR_YInPQ = ColorspaceUtils.LinearToPQY(1.5f);
        float maxYInPQ = MathF.Max(
            SDR_YInPQ,
            ColorspaceUtils.LinearToPQY(MathF.Min(_maxNitsToTonemap, maxLum.Y * scale))
        );
        return maxYInPQ;
    }

    public static float CalculateMaxYInPQ(ID3D11Texture2D image, out Vector4[] pixelData)
    {
        pixelData = image.GetPixelSpan();
        return CalculateMaxYInPQ(pixelData);
    }

    public static ScratchImage ConvertToSDRPixels(ID3D11Texture2D image, out Vector4[] scrgb, out float maxYInPQ)
    {
        var log = Console.Out;

        var stopwatchTotal = Stopwatch.StartNew();
        int width = (int)image.Description.Width;
        int height = (int)image.Description.Height;
        scrgb = image.GetPixelSpan();

        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImageBegin");

        var stopwatchCore = Stopwatch.StartNew();
        maxYInPQ = CalculateMaxYInPQ(scrgb);
        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, percentileLum calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

        PerformTonemapping(scrgb, maxYInPQ, scrgb);

        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, tonemapped: " + stopwatchCore.ElapsedMilliseconds + "ms");

        stopwatchCore.Stop();
        log.WriteLine("SKIV_Image_TonemapToSDR(): ConvertToSDR: " + stopwatchCore.ElapsedMilliseconds + "ms (total: " + stopwatchTotal.ElapsedMilliseconds +
                      "ms)");
        var texHelper = TexHelper.Instance;
        var hdrScratch = texHelper.Initialize2D(DXGI_FORMAT.R32G32B32A32_FLOAT,
            width,
            height,
            1,
            1, CP_FLAGS.NONE);

        unsafe
        {
            var img = hdrScratch.GetImage(0);
            fixed (Vector4* pSrc = scrgb) //
            {
                // copy the whole image in one go
                Buffer.MemoryCopy(pSrc,
                    (void*)img.Pixels,
                    img.SlicePitch, // dest capacity
                    img.SlicePitch); // bytes to copy
            }
        }

        return hdrScratch;
    }

    public static void SaveImageToDiskSDR(ID3D11Texture2D image, string wszFileName, bool force_sRGB)
    {
        var stopwatchTotal = Stopwatch.StartNew();

        var log = Console.Out;

        // 2. Get the file extension from the provided filename
        string wszExtension = GetExtension(wszFileName);

        // 3. Prepare an “implicit” filename in case the user did not supply an extension
        string wszImplicitFileName = wszFileName;
        if (string.IsNullOrEmpty(wszExtension))
        {
            wszImplicitFileName += defaultSDRFileExt;
            wszExtension = GetExtension(wszImplicitFileName);
        }

        // 8. Flags for preferring higher‐bit‐depth WIC pixel formats
        bool bPrefer10bpcAs48bpp = false;
        bool bPrefer10bpcAs32bpp = false;

        // 9. Prepare WIC codec GUID and WIC flags
        WICCodecs wic_codec;
        WIC_FLAGS wic_flags = WIC_FLAGS.DITHER_DIFFUSION |
                              (force_sRGB ? WIC_FLAGS.FORCE_SRGB : WIC_FLAGS.NONE);

        // 10. Branch based on extension: “jpg” / “jpeg”
        if ((HasExtension(wszExtension, "jpg") != null) ||
            (HasExtension(wszExtension, "jpeg") != null))
        {
            wic_codec = WICCodecs.JPEG;
        }
        // 11. Extension: “png”
        else if (HasExtension(wszExtension, "png") != null)
        {
            wic_codec = WICCodecs.PNG;
            // Force sRGB for PNG
            wic_flags |= WIC_FLAGS.FORCE_SRGB;
            wic_flags |= WIC_FLAGS.DEFAULT_SRGB;
        }
        // 12. Extension: “bmp”
        else if (HasExtension(wszExtension, "bmp") != null)
        {
            wic_codec = WICCodecs.BMP;
        }
        // 13. Extension: “tiff”
        else if (HasExtension(wszExtension, "tiff") != null)
        {
            wic_codec = WICCodecs.TIFF;
            // bPrefer10bpcAs32bpp = false;
        }
        // 14. Extension: “hdp” or “jxr” (WMP)
        else if ((HasExtension(wszExtension, "hdp") != null) ||
                 (HasExtension(wszExtension, "jxr") != null))
        {
            wic_codec = WICCodecs.WMP;
            bPrefer10bpcAs32bpp = true;
        }
        else throw new Exception("Unsupported file extension");

        if (bPrefer10bpcAs32bpp)
        {
            wic_flags |= WIC_FLAGS.FORCE_SRGB;
        }

        var stopwatchCore = Stopwatch.StartNew();
        using var tonemappedScratchImage = ConvertToSDRPixels(image, out var scrgb, out _);

        DXGI_FORMAT outFormat = bPrefer10bpcAs32bpp ? DXGI_FORMAT.R10G10B10A2_UNORM : DXGI_FORMAT.B8G8R8X8_UNORM_SRGB;

        stopwatchCore.Stop();
        log.WriteLine("SKIV_Image_TonemapToSDR(): ConvertToSDR: " + stopwatchCore.ElapsedMilliseconds + "ms (total: " + stopwatchTotal.ElapsedMilliseconds +
                      "ms)");


        using var sdrScratch = tonemappedScratchImage.Convert(0, outFormat, TEX_FILTER_FLAGS.DEFAULT, 1.0f);

        log.WriteLine("SKIV_Image_TonemapToSDR(): EncodeToMemory: " + stopwatchTotal.ElapsedMilliseconds);
        if (wic_codec == WICCodecs.JPEG)
        {
            sdrScratch.SaveToJPGFile(0, 1.0f, wszImplicitFileName);
        }
        else
        {
            Guid guid = TexHelper.Instance.GetWICCodec(wic_codec);
            sdrScratch.SaveToWICFile(0, wic_flags, guid, wszImplicitFileName);
        }

        log.WriteLine("SKIV_Image_TonemapToSDR(): EncodeToDisk: " + stopwatchTotal.ElapsedMilliseconds);
    }

    private static void PerformTonemapping(Vector4[] scrgb, float maxYInPQ, Vector4[] outPixels)
    {
        for (int j = 0; j < scrgb.Length; ++j)
        {
            MaxTonemappedRgb(scrgb, maxYInPQ, outPixels, j);
        }
    }

    private static void MaxTonemappedRgb(Vector4[] scrgb, float maxYInPQ, Vector4[] outPixels, int j)
    {
        Vector4 value = scrgb[j];
        Vector4 ICtCp = ColorspaceUtils.Rec709toICtCp(value);
        float Y_in = MathF.Max(ICtCp.X, 0.0f);
        float Y_out = 1.0f;

        Y_out = ColorspaceUtils.HdrTonemap(maxYInPQ, Y_out, Y_in);

        if (Y_out + Y_in > 0.0f)
        {
            ICtCp.X = MathF.Pow(Y_in, 1.18f);
            float I0 = ICtCp.X;
            ICtCp.X *= MathF.Max(Y_out / Y_in, 0.0f);
            float I1 = ICtCp.X;

            float I_scale = 0.0f;
            if (I0 != 0.0f && I1 != 0.0f)
            {
                I_scale = MathF.Min(I0 / I1, I1 / I0);
            }

            ICtCp.Y *= I_scale;
            ICtCp.Z *= I_scale;
        }

        value = ColorspaceUtils.ICtCpToRec709(ICtCp);
        outPixels[j] = value;
    }

    private static string GetExtension(string wszFileName)
    {
        return Path.GetExtension(wszFileName)?.ToLower() ?? "";
    }

    private static object HasExtension(string wszExtension, string extension)
    {
        return wszExtension.EndsWith(extension);
    }
}