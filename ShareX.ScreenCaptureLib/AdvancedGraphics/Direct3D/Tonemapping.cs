using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class Tonemapping
{
// TODO: as this code is from SKIV project it probably can be simplifed a lot more, as we dont need many of the features
    public static ID3D11Texture2D TonemapOnCpu(HdrSettings hdrSettings, ModernCaptureMonitorDescription region, DeviceAccess deviceAccess,
        ID3D11Texture2D inputHdrTex)
    {
        throw new Exception("The method or operation is not implemented.");
    }

    static readonly Vertex[] defaultVerts =
    [
        new(position: new Vector2(-1f, +1f), textureCoord: new Vector2(0f, 0f)),
        new(position: new Vector2(+1f, +1f), textureCoord: new Vector2(1f, 0f)),
        new(position: new Vector2(-1f, -1f), textureCoord: new Vector2(0f, 1f)),
        new(position: new Vector2(+1f, +1f), textureCoord: new Vector2(1f, 0f)),
        new(position: new Vector2(+1f, -1f), textureCoord: new Vector2(1f, 1f)),
        new(position: new Vector2(-1f, -1f), textureCoord: new Vector2(0f, 1f))
    ];

    public static ID3D11Texture2D TonemapOnGpu(HdrSettings hdrSettings, ModernCaptureMonitorDescription region, DeviceAccess deviceAccess,
        ID3D11Texture2D cpuStaging, ID3D11Texture2D gpuRawTexture, ID3D11Texture2D canvasGpu,     Box                                  destBox,
        Box                                  srcBox)
    {
        ID3D11Device device = deviceAccess.Device;
        ID3D11DeviceContext ctx = device.ImmediateContext;
        ImageInfo imageInfo = CalculateImageInfo(hdrSettings, cpuStaging);
        ShaderConstantHelper.GetShaderConstants(region.MonitorInfo, hdrSettings, imageInfo, out var vertexShaderConstants, out var pixelShaderConstants);
        // var quadVerts = defaultVerts; // Direct3DUtils.ConstructForScreen(region);

        var rawDesc = gpuRawTexture.Description;
        float u0 = srcBox.Left   / (float)rawDesc.Width;
        float v0 = srcBox.Top    / (float)rawDesc.Height;
        float u1 = u0 + (srcBox.Width / (float)rawDesc.Width);
        float v1 = v0 + (srcBox.Height / (float)rawDesc.Height);

        float left = -1.0f;
        float right = 1.0f;
        float bottom = -1.0f;
        float top = 1.0f;
        var quadVerts = new[]
        {
            new Vertex(new Vector2(left, top),  new Vector2(u0, v0)),
            new Vertex(new Vector2(right, top),  new Vector2(u1, v0)),
            new Vertex(new Vector2(left, bottom),  new Vector2(u0, v1)),
            new Vertex(new Vector2(left, bottom),  new Vector2(u0, v1)),
            new Vertex(new Vector2(right, top),  new Vector2(u1, v0)),
            new Vertex(new Vector2(right, bottom),  new Vector2(u1, v1)),
        };


        var vertexBuffer = device.CreateBuffer(quadVerts, BindFlags.VertexBuffer);

        PixelShaderConstants[] pixelShaderConstantsArray = [pixelShaderConstants];
        var psConstantBuffer = device.CreateBuffer(pixelShaderConstantsArray, BindFlags.ConstantBuffer);

        VertexShaderConstants[] vertexShaderConstantsArray = [vertexShaderConstants];
        var vsConstantBuffer = device.CreateBuffer(vertexShaderConstantsArray, BindFlags.ConstantBuffer);

        var inDesc = gpuRawTexture.Description;
        var ldrRtv = device.CreateRenderTargetView(canvasGpu);

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
        var hdrSrv = device.CreateShaderResourceView(gpuRawTexture, hdrSrvDesc);

        ctx.OMSetRenderTargets(ldrRtv);

        var vp = new Viewport {
            X = destBox.Left,
            Y = destBox.Top,
            Width    = destBox.Width,
            Height   = destBox.Height,
            MinDepth = 0,
            MaxDepth = 1
        };
        ctx.RSSetViewport(vp);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(deviceAccess.inputLayout);
        ctx.IASetVertexBuffer(0, vertexBuffer, Vertex.SizeInBytes);

        var sampler = device.CreateSamplerState(new SamplerDescription()
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = 0
        });
        ctx.PSSetSampler(0, sampler);

        ctx.VSSetShader(deviceAccess.vxShader);
        ctx.VSSetConstantBuffer(0, vsConstantBuffer);
        ctx.PSSetShader(deviceAccess.pxShader);
        ctx.PSSetConstantBuffer(0, psConstantBuffer);
        ctx.PSSetShaderResource(0, hdrSrv);


        ctx.Draw(vertexCount: 6, startVertexLocation: 0);

        hdrSrv.Dispose();
        psConstantBuffer.Dispose();
        vertexBuffer.Dispose();
        ldrRtv.Dispose();
        sampler.Dispose();


        return canvasGpu;
    }

    // heavily inspired by https://github.com/SpecialKO/SKIV/blob/ed2a4a9de93ebba9661f9e8ed31c5d67ab490d2d/src/utility/image.cpp#L1300C1-L1300C25
    // MIT License Copyright (c) 2024 Aemony

    private static readonly string defaultSDRFileExt = ".png";

    // TODO: consider threads?
    public static ImageInfo CalculateImageInfo(Direct3DUtils.PixelReader pixelReader)
    {
        ImageInfo result = new ImageInfo();
        var log = Console.Out;

        Vector4 maxCLLVector = Vector4.Zero;
        float maxLum = 0;
        float minLum = float.MaxValue;
        double totalLum = 0;

        log.WriteLine("CalculateLightInfo(): EvaluateImageBegin");

        var stopwatchCore = Stopwatch.StartNew();
        uint[] luminance_freq = new uint[65536];
        float fLumRange = maxLum - minLum;

        var pixels = pixelReader.Pixels;
        for (uint i = 0; i < pixels; i++)
        {
            Vector4 v = pixelReader.GetPixel(i);
            maxCLLVector = Vector4.Max(v, maxCLLVector);
            Vector4 vXyz = Vector4.Transform(v, ColorspaceUtils.from709ToXYZ);

            maxLum = MathF.Max(vXyz.Y, maxLum);
            minLum = MathF.Min(vXyz.Y, minLum);

            totalLum += MathF.Max(0, maxLum);
        }

        float maxCll = MathF.Max(maxCLLVector.X, maxCLLVector.Y);
        maxCll = MathF.Max(maxCll, maxCLLVector.Z);
        float avgLum = (float)(totalLum / pixels);
        minLum = MathF.Max(0, minLum);
        result.MaxNits = MathF.Max(0, maxLum * 80);
        result.MinNits = MathF.Max(0, minLum * 80);
        result.AvgNits = avgLum * 80;
        result.MaxCLL = maxCll;

        if (maxCll == maxCLLVector.X) result.MaxCLLChannel = 'R';
        else if (maxCll == maxCLLVector.Y) result.MaxCLLChannel = 'G';
        else if (maxCll == maxCLLVector.Z) result.MaxCLLChannel = 'B';
        else result.MaxCLLChannel = 'X';

        log.WriteLine("CalculateLightInfo(): EvaluateImage, min/max calculated (max: " + maxLum + "): " + stopwatchCore.ElapsedMilliseconds + "ms");

        for (uint i = 0; i < pixels; i++)
        {
            Vector4 v = pixelReader.GetPixel(i);
            v = Vector4.Max(Vector4.Zero, Vector4.Transform(v, ColorspaceUtils.from709ToXYZ));
            luminance_freq[Math.Clamp((int)Math.Round((v.Y - minLum) / (fLumRange / 65536.0f)), 0, 65535)]++;

            int idx = Math.Clamp(
                (int)Math.Round((v.Y - minLum) / (fLumRange / 65536.0f)),
                0,
                65535
            );
            luminance_freq[idx]++;
        }

        log.WriteLine("CalculateImageInfo(): EvaluateImage, luminance_freq calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

        double percent = 100.0;
        double img_size = pixels;

        float p99Lum = maxLum;
        for (int i = 65535; i >= 0; --i)
        {
            percent -= 100.0 * ((double)luminance_freq[i] / img_size);
            if (percent <= 99.94)
            {
                float percentileLum = minLum + (fLumRange * ((float)i / 65536.0f));
                p99Lum = percentileLum;
                break;
            }
        }

        if (p99Lum <= 0.01f)
            p99Lum = maxLum;

        result.P99Nits = MathF.Max(0, p99Lum * 80);

        log.WriteLine("CalculateImageInfo(): EvaluateImage, percentileLum calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

        const float scale = 1;
        const float _maxNitsToTonemap = 125.0f * scale;
        float SDR_YInPQ = ColorspaceUtils.LinearToPQY(1.5f);
        float maxYInPQ = MathF.Max(
            SDR_YInPQ,
            ColorspaceUtils.LinearToPQY(MathF.Min(_maxNitsToTonemap, maxLum * scale))
        );
        result.MaxYInPQ = maxYInPQ; // TODO: is this correct?
        return result;
    }

    public static ImageInfo CalculateImageInfo(HdrSettings setting, ID3D11Texture2D image)
    {
        Direct3DUtils.PixelReader reader = null;
        try
        {
            reader = image.GetPixelReader(setting);
            return CalculateImageInfo(reader);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    // public static ScratchImage ConvertToSDRPixels(ID3D11Texture2D image, out Vector4[] scrgb, out ImageInfo imageInfo)
    // {
    //     var log = Console.Out;
    //
    //     var stopwatchTotal = Stopwatch.StartNew();
    //     int width = (int)image.Description.Width;
    //     int height = (int)image.Description.Height;
    //     scrgb = image.GetPixelSpan();
    //
    //     log.WriteLine("ConvertToSDRPixels(): EvaluateImageBegin");
    //
    //     var stopwatchCore = Stopwatch.StartNew();
    //     imageInfo = CalculateImageInfo(scrgb, );
    //     log.WriteLine("ConvertToSDRPixels(): EvaluateImage, percentileLum calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");
    //
    //     PerformTonemapping(scrgb, imageInfo, scrgb);
    //
    //     log.WriteLine("ConvertToSDRPixels(): EvaluateImage, tonemapped: " + stopwatchCore.ElapsedMilliseconds + "ms");
    //
    //     stopwatchCore.Stop();
    //     log.WriteLine("ConvertToSDRPixels(): ConvertToSDR: " + stopwatchCore.ElapsedMilliseconds + "ms (total: " + stopwatchTotal.ElapsedMilliseconds +
    //                   "ms)");
    //     var texHelper = TexHelper.Instance;
    //     var hdrScratch = texHelper.Initialize2D(DXGI_FORMAT.R32G32B32A32_FLOAT,
    //         width,
    //         height,
    //         1,
    //         1, CP_FLAGS.NONE);
    //
    //     unsafe
    //     {
    //         var img = hdrScratch.GetImage(0);
    //         fixed (Vector4* pSrc = scrgb) //
    //         {
    //             // copy the whole image in one go
    //             Buffer.MemoryCopy(pSrc,
    //                 (void*)img.Pixels,
    //                 img.SlicePitch, // dest capacity
    //                 img.SlicePitch); // bytes to copy
    //         }
    //     }
    //
    //     return hdrScratch;
    // }

    // public static void SaveImageToDiskSDR(ID3D11Texture2D image, string wszFileName, bool force_sRGB)
    // {
    //     var stopwatchTotal = Stopwatch.StartNew();
    //
    //     var log = Console.Out;
    //
    //     // 2. Get the file extension from the provided filename
    //     string wszExtension = GetExtension(wszFileName);
    //
    //     // 3. Prepare an “implicit” filename in case the user did not supply an extension
    //     string wszImplicitFileName = wszFileName;
    //     if (string.IsNullOrEmpty(wszExtension))
    //     {
    //         wszImplicitFileName += defaultSDRFileExt;
    //         wszExtension = GetExtension(wszImplicitFileName);
    //     }
    //
    //     // 8. Flags for preferring higher‐bit‐depth WIC pixel formats
    //     bool bPrefer10bpcAs48bpp = false;
    //     bool bPrefer10bpcAs32bpp = false;
    //
    //     // 9. Prepare WIC codec GUID and WIC flags
    //     WICCodecs wic_codec;
    //     WIC_FLAGS wic_flags = WIC_FLAGS.DITHER_DIFFUSION |
    //                           (force_sRGB ? WIC_FLAGS.FORCE_SRGB : WIC_FLAGS.NONE);
    //
    //     // 10. Branch based on extension: “jpg” / “jpeg”
    //     if ((HasExtension(wszExtension, "jpg") != null) ||
    //         (HasExtension(wszExtension, "jpeg") != null))
    //     {
    //         wic_codec = WICCodecs.JPEG;
    //     }
    //     // 11. Extension: “png”
    //     else if (HasExtension(wszExtension, "png") != null)
    //     {
    //         wic_codec = WICCodecs.PNG;
    //         // Force sRGB for PNG
    //         wic_flags |= WIC_FLAGS.FORCE_SRGB;
    //         wic_flags |= WIC_FLAGS.DEFAULT_SRGB;
    //     }
    //     // 12. Extension: “bmp”
    //     else if (HasExtension(wszExtension, "bmp") != null)
    //     {
    //         wic_codec = WICCodecs.BMP;
    //     }
    //     // 13. Extension: “tiff”
    //     else if (HasExtension(wszExtension, "tiff") != null)
    //     {
    //         wic_codec = WICCodecs.TIFF;
    //         // bPrefer10bpcAs32bpp = false;
    //     }
    //     // 14. Extension: “hdp” or “jxr” (WMP)
    //     else if ((HasExtension(wszExtension, "hdp") != null) ||
    //              (HasExtension(wszExtension, "jxr") != null))
    //     {
    //         wic_codec = WICCodecs.WMP;
    //         bPrefer10bpcAs32bpp = true;
    //     }
    //     else throw new Exception("Unsupported file extension");
    //
    //     if (bPrefer10bpcAs32bpp)
    //     {
    //         wic_flags |= WIC_FLAGS.FORCE_SRGB;
    //     }
    //
    //     var stopwatchCore = Stopwatch.StartNew();
    //     using var tonemappedScratchImage = ConvertToSDRPixels(image, out var scrgb, out _);
    //
    //     DXGI_FORMAT outFormat = bPrefer10bpcAs32bpp ? DXGI_FORMAT.R10G10B10A2_UNORM : DXGI_FORMAT.B8G8R8X8_UNORM_SRGB;
    //
    //     stopwatchCore.Stop();
    //     log.WriteLine("SaveImageToDiskSDR(): ConvertToSDR: " + stopwatchCore.ElapsedMilliseconds + "ms (total: " + stopwatchTotal.ElapsedMilliseconds +
    //                   "ms)");
    //
    //
    //     using var sdrScratch = tonemappedScratchImage.Convert(0, outFormat, TEX_FILTER_FLAGS.DEFAULT, 1.0f);
    //
    //     log.WriteLine("SaveImageToDiskSDR(): EncodeToMemory: " + stopwatchTotal.ElapsedMilliseconds);
    //     if (wic_codec == WICCodecs.JPEG)
    //     {
    //         sdrScratch.SaveToJPGFile(0, 1.0f, wszImplicitFileName);
    //     }
    //     else
    //     {
    //         Guid guid = TexHelper.Instance.GetWICCodec(wic_codec);
    //         sdrScratch.SaveToWICFile(0, wic_flags, guid, wszImplicitFileName);
    //     }
    //
    //     log.WriteLine("SaveImageToDiskSDR(): EncodeToDisk: " + stopwatchTotal.ElapsedMilliseconds);
    // }

    // private static void PerformTonemapping(Vector4[] scrgb, ImageInfo imageInfo, Vector4[] outPixels)
    // {
    //     var maxYInPQ = imageInfo.MaxYInPQ;
    //     for (int j = 0; j < scrgb.Length; ++j)
    //     {
    //         MaxTonemappedRgb(scrgb, maxYInPQ, outPixels, j);
    //     }
    // }
    //
    // private static void MaxTonemappedRgb(Vector4[] scrgb, float maxYInPQ, Vector4[] outPixels, int j)
    // {
    //     Vector4 value = scrgb[j];
    //     Vector4 ICtCp = ColorspaceUtils.Rec709toICtCp(value);
    //     float Y_in = MathF.Max(ICtCp.X, 0.0f);
    //     float Y_out = 1.0f;
    //
    //     Y_out = ColorspaceUtils.HdrTonemap(maxYInPQ, Y_out, Y_in);
    //
    //     if (Y_out + Y_in > 0.0f)
    //     {
    //         ICtCp.X = MathF.Pow(Y_in, 1.18f);
    //         float I0 = ICtCp.X;
    //         ICtCp.X *= MathF.Max(Y_out / Y_in, 0.0f);
    //         float I1 = ICtCp.X;
    //
    //         float I_scale = 0.0f;
    //         if (I0 != 0.0f && I1 != 0.0f)
    //         {
    //             I_scale = MathF.Min(I0 / I1, I1 / I0);
    //         }
    //
    //         ICtCp.Y *= I_scale;
    //         ICtCp.Z *= I_scale;
    //     }
    //
    //     value = ColorspaceUtils.ICtCpToRec709(ICtCp);
    //     outPixels[j] = value;
    // }
    // public static void ConvertToSDRPixelsInPlace(
    //     ID3D11DeviceContext context,
    //     ID3D11Texture2D image,
    //     out Vector4[] scrgb,
    //     out ImageInfo imageInfo)
    // {
    //     int width = (int)image.Description.Width;
    //     int height = (int)image.Description.Height;
    //     var fmt = image.Description.Format;
    //     scrgb = image.GetPixelSpan();
    //     imageInfo = CalculateImageInfo(scrgb);
    //     PerformTonemapping(scrgb, imageInfo, scrgb);
    //
    //     unsafe
    //     {
    //         if (fmt == Format.R32G32B32A32_Float)
    //         {
    //             // TODO: does this work?
    //             fixed (Vector4* pSrc = scrgb)
    //             {
    //                 int strideInBytes = width * sizeof(Vector4);
    //                 var sysMem = new DataBox((IntPtr)pSrc, strideInBytes, 0);
    //                 context.UpdateSubresource(sysMem, image);
    //             }
    //         }
    //         else if (fmt == Format.R16G16B16A16_Float)
    //         {
    //             // TODO: avoid that copy?
    //             int pixelCount = width * height;
    //             ulong[] packed = new ulong[pixelCount];
    //
    //             for (int i = 0; i < pixelCount; i++)
    //             {
    //                 Half4 h = scrgb[i];
    //                 packed[i] = h.PackedValue;
    //             }
    //
    //             var handle = GCHandle.Alloc(packed, GCHandleType.Pinned);
    //             try
    //             {
    //                 IntPtr ptr = handle.AddrOfPinnedObject();
    //                 int rowPitch = width * sizeof(ulong); // 8 bytes × width
    //                 var box = new DataBox(ptr, rowPitch, 0);
    //                 context.UpdateSubresource(box, image);
    //             }
    //             finally
    //             {
    //                 handle.Free();
    //             }
    //         }
    //         else
    //         {
    //             throw new InvalidOperationException(
    //                 $"ConvertToSDRPixelsInPlace: Format {fmt} is not supported. " +
    //                 "Only R32G32B32A32_Float and R16G16B16A16_Float are implemented.");
    //         }
    //     }
    // }

    private static string GetExtension(string wszFileName)
    {
        return Path.GetExtension(wszFileName)?.ToLower() ?? "";
    }

    private static object HasExtension(string wszExtension, string extension)
    {
        return wszExtension.EndsWith(extension);
    }
}