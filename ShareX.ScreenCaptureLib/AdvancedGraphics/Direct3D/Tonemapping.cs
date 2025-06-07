using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DirectXTexNet;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Mathematics.PackedVector;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class Tonemapping
{

    // private static void PerformTonemapping2(Vector4[] scrgb, ImageInfo imageInfo, Vector4[] outPixels)
    // {
    //     var maxYInPQ = imageInfo.MaxYInPQ;
    //     for (int j = 0; j < scrgb.Length; ++j)
    //     {
    //
    //         Vector4 input_col = Vector4.One;
    //         Vector4 out_col = scrgb[0];

//     if (input.hdr_img)
//     {
//         if (display_max_luminance < 0)
//         {
//             return
//                 DrawMaxClipPattern(-display_max_luminance, input.uv);
//         }
//     }
//
//
//     // When sampling FP textures, special FP bit patterns like NaN or Infinity
//     //   may be returned. The same image represented using UNORM would replace
//     //     these special values with 0.0, and that is the behavior we want...
//     out_col =
//         SanitizeFP(out_col);
//
//
//     out_col.a = 1.0f;
//
//     Vector4 orig_col = out_col;
//
//     // input.lum.x        // Luminance (white point)
//     bool isHDR = input.lum.y > 0.0; // HDR (10 bpc or 16 bpc)
//     bool is10bpc = input.lum.z > 0.0; // 10 bpc
//     bool is16bpc = input.lum.w > 0.0; // 16 bpc (scRGB)
//
//     // 16 bpc scRGB (SDR/HDR)
//     // ColSpace:  DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
//     // Gamma:     1.0
//     // Primaries: BT.709
//     if (is16bpc)
//     {
//         out_col =
//             Vector4(input.hdr_img
//                        ? RemoveGammaExp(input_col.rgb, 2.2f) *
//                        out_col.rgb
//                        : RemoveGammaExp(input_col.rgb *
//                                         ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
//                    saturate(out_col.a) *
//                    saturate(input_col.a)
//             );
//
//         // sRGB (SDR) Content
//         if (input.srgb_img)
//         {
//             out_col =
//                 Vector4((input_col.rgb) *
//                        (out_col.rgb),
//                        saturate(out_col.a) *
//                        saturate(input_col.a));
//
//             out_col.rgb = RemoveSRGBCurve(out_col.rgb);
//         }
//
//         float hdr_scale = input.lum.x;
//
//         if (!input.hdr_img)
//             out_col.rgb = saturate(out_col.rgb) * hdr_scale;
//         else
//             out_col.a = 1.0f;
//     }
//
//     // 10 bpc SDR
//     // ColSpace:  DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709
//     // Gamma:     2.2
//     // Primaries: BT.709
//     else if (is10bpc)
//     {
//         // sRGB (SDR) Content
//         if (input.srgb_img)
//         {
//             out_col =
//                 Vector4((input_col.rgb) *
//                        (out_col.rgb),
//                        saturate(out_col.a) *
//                        saturate(input_col.a));
//
//             out_col.rgb = RemoveSRGBCurve(out_col.rgb);
//         }
//
//         else if (!input.hdr_img)
//         {
//             out_col =
//                 Vector4(RemoveGammaExp(input_col.rgb *
//                                       ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
//                        saturate(out_col.a) *
//                        saturate(input_col.a)
//                 );
//         }
//
//         else
//         {
//             out_col =
//                 Vector4(RemoveGammaExp(input_col.rgb, 2.2f) *
//                        out_col.rgb,
//                        saturate(out_col.a) *
//                        saturate(input_col.a)
//                 );
//
//             out_col.a = 1.0f; // Opaque
//         }
//     }
//
//     // 8 bpc SDR (sRGB)
//     else
//     {
// #ifdef _SRGB
//     out_col =
//       Vector4 (   (           input_col.rgb) *
//                  (             out_col.rgb),
//                                   saturate (  out_col.a)  *
//                                   saturate (input_col.a)
//               );
//
//     out_col.rgb = RemoveSRGBCurve (out_col.rgb);
//
//     // Manipulate the alpha channel a bit...
//     out_col.a = 1.0f - RemoveSRGBCurve (1.0f - out_col.a);
// #else
//
//         // sRGB (SDR) Content
//         if (input.srgb_img)
//         {
//             out_col =
//                 Vector4((input_col.rgb) *
//                        (out_col.rgb),
//                        saturate(out_col.a) *
//                        saturate(input_col.a));
//
//             out_col.rgb = RemoveSRGBCurve(out_col.rgb);
//         }
//
//         else if (!input.hdr_img)
//         {
//             out_col =
//                 Vector4(RemoveGammaExp(input_col.rgb *
//                                       ApplyGammaExp(out_col.rgb, 2.2f), 2.2f),
//                        saturate(out_col.a) *
//                        saturate(input_col.a)
//                 );
//         }
//
//         else
//         {
//             out_col =
//                 Vector4(RemoveGammaExp(input_col.rgb, 2.2f) *
//                        out_col.rgb,
//                        saturate(out_col.a) *
//                        saturate(input_col.a)
//                 );
//
//             out_col.a = 1.0f; // Opaque
//         }
// #endif
//     }
//
//     if (input.hdr_img)
//     {
//         uint implied_tonemap_type = tonemap_type;
//
//         out_col.rgb *=
//             isHDR
//                 ? user_brightness_scale
//                 : max(user_brightness_scale, 0.001f);
//
//
//         // If it's too bright, don't bother trying to tonemap the full range...
//         static const float _maxNitsToTonemap = 10000.0f;
//
//         float dML = LinearToPQY(display_max_luminance);
//         float cML = LinearToPQY(min(hdr_max_luminance, _maxNitsToTonemap));
//
//         if (implied_tonemap_type != SKIV_TONEMAP_TYPE_NONE && (!isHDR))
//         {
//             implied_tonemap_type = SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY;
//             dML = LinearToPQY(1.5f);
//         }
//
//         else if (implied_tonemap_type == SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY)
//         {
//             ///out_col.rgb *=
//             ///  1.0f / max (1.0f, 80.0f / (2.0f * sdr_reference_white));
//         }
//
//         float3 ICtCp = Rec709toICtCp(out_col.rgb);
//         float Y_in = max(ICtCp.x, 0.0f);
//         float Y_out = 1.0f;
//
//         switch (implied_tonemap_type)
//         {
//         // This tonemap type is not necessary, we always know content range
//         //SKIV_TONEMAP_TYPE_INFINITE_ROLLOFF
//
//         default:
//         case SKIV_TONEMAP_TYPE_NONE: Y_out = TonemapNone(Y_in);
//             break;
//         case SKIV_TONEMAP_TYPE_CLIP: Y_out = TonemapClip(Y_in, dML);
//             break;
//         case SKIV_TONEMAP_TYPE_NORMALIZE_TO_CLL: Y_out = TonemapSDR(Y_in, cML, 1.0f);
//             break;
//         case SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY: Y_out = TonemapHDR(Y_in, cML, dML);
//             break;
//         }
//
//         if (Y_out + Y_in > 0.0)
//         {
//             if (implied_tonemap_type == SKIV_TONEMAP_TYPE_MAP_CLL_TO_DISPLAY)
//             {
//                 if ((!isHDR))
//                     ICtCp.x = pow(ICtCp.x, 1.18f);
//             }
//
//             float I0 = ICtCp.x;
//             float I_scale = 0.0f;
//
//             ICtCp.x *=
//                 max((Y_out / Y_in), 0.0f);
//
//             if (ICtCp.x != 0.0f && I0 != 0.0f)
//             {
//                 I_scale =
//                     min(I0 / ICtCp.x, ICtCp.x / I0);
//             }
//
//             ICtCp.yz *= I_scale;
//         }
//
//         else
//             ICtCp.x = 0.0;
//
//         out_col.rgb =
//             ICtCptoRec709(ICtCp);
//     }
//
//     if (!is16bpc)
//     {
//         out_col.rgb =
//             ApplySRGBCurve(saturate(out_col.rgb));
//     }
//
//     if (dot(orig_col * user_brightness_scale, (1.0f).xxxx) <= FP16_MIN)
//         out_col.rgb = 0.0f;
//
//     out_col.rgb *=
//         out_col.a;
//
//     return
//         SanitizeFP(out_col);
//         }
//     }


    /*
     * Vector4 DrawMaxClipPattern(float x, float2 uv);

// TODO: as this code is from SKIV project it probably can be simplifed a lot more, as we dont need many of the features
Vector4 main(PS_INPUT input) : SV_Target
{
}
     */
    public static ID3D11Texture2D TonemapOnCpu(HdrSettings hdrSettings, ModernCaptureMonitorDescription region, DeviceAccess deviceAccess,
        ID3D11Texture2D inputHdrTex)
    {
        ConvertToSDRPixelsInPlace(deviceAccess.Device.ImmediateContext, inputHdrTex, out var sdrPixels, out var imageInfo);
        return inputHdrTex; // TODO
    }

    public static void ConvertToSDRPixelsInPlace(
        ID3D11DeviceContext context,
        ID3D11Texture2D image,
        out Vector4[] scrgb,
        out ImageInfo imageInfo)
    {
        int width = (int)image.Description.Width;
        int height = (int)image.Description.Height;
        var fmt = image.Description.Format;
        scrgb = image.GetPixelSpan();
        imageInfo = CalculateImageInfo(scrgb);
        PerformTonemapping(scrgb, imageInfo, scrgb);

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
        ID3D11Texture2D inputHdrTex)
    {
        ID3D11Device device = deviceAccess.Device;
        ID3D11DeviceContext ctx = device.ImmediateContext;
        ImageInfo imageInfo = CalculateImageInfo(inputHdrTex, out _);
        ShaderConstantHelper.GetShaderConstants(region.MonitorInfo, hdrSettings, imageInfo, out var vertexShaderConstants, out var pixelShaderConstants);
        var quadVerts = defaultVerts; // Direct3DUtils.ConstructForScreen(region);

        var vertexBuffer = device.CreateBuffer(quadVerts, BindFlags.VertexBuffer);

        PixelShaderConstants[] pixelShaderConstantsArray = [pixelShaderConstants];
        var psConstantBuffer = device.CreateBuffer(pixelShaderConstantsArray, BindFlags.ConstantBuffer);

        VertexShaderConstants[] vertexShaderConstantsArray = [vertexShaderConstants];
        var vsConstantBuffer = device.CreateBuffer(vertexShaderConstantsArray, BindFlags.ConstantBuffer);

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
            BindFlags = BindFlags.RenderTarget,
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

        ctx.RSSetViewport(new Viewport(0, 0, inDesc.Width, inDesc.Height, 0, 1));

        ctx.Draw(vertexCount: 6, startVertexLocation: 0);

        hdrSrv.Dispose();
        psConstantBuffer.Dispose();
        vertexBuffer.Dispose();
        ldrRtv.Dispose();
        sampler.Dispose();



        return ldrTex;
    }

    // heavily inspired by https://github.com/SpecialKO/SKIV/blob/ed2a4a9de93ebba9661f9e8ed31c5d67ab490d2d/src/utility/image.cpp#L1300C1-L1300C25
    // MIT License Copyright (c) 2024 Aemony

    private static readonly string defaultSDRFileExt = ".png";

    // TODO: consider threads?
    public static ImageInfo CalculateImageInfo(Vector4[] scrgb)
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

        for (var i = 0; i < scrgb.Length; i++)
        {
            Vector4 v = scrgb[i];
            maxCLLVector = Vector4.Max(v, maxCLLVector);
            Vector4 vXyz = Vector4.Transform(v, ColorspaceUtils.from709ToXYZ);

            maxLum = MathF.Max(vXyz.Y, maxLum);
            minLum = MathF.Min(vXyz.Y, minLum);

            totalLum += MathF.Max(0, maxLum);
        }

        float maxCll = MathF.Max(maxCLLVector.X, maxCLLVector.Y);
        maxCll = MathF.Max(maxCll, maxCLLVector.Z);
        float avgLum = (float)(totalLum / scrgb.Length);
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

        for (var i = 0; i < scrgb.Length; i++)
        {
            Vector4 v = scrgb[i];
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
        double img_size = scrgb.LongLength;

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

    public static ImageInfo CalculateImageInfo(ID3D11Texture2D image, out Vector4[] pixelData)
    {
        pixelData = image.GetPixelSpan();
        return CalculateImageInfo(pixelData);
    }

    public static ScratchImage ConvertToSDRPixels(ID3D11Texture2D image, out Vector4[] scrgb, out ImageInfo imageInfo)
    {
        var log = Console.Out;

        var stopwatchTotal = Stopwatch.StartNew();
        int width = (int)image.Description.Width;
        int height = (int)image.Description.Height;
        scrgb = image.GetPixelSpan();

        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImageBegin");

        var stopwatchCore = Stopwatch.StartNew();
        imageInfo = CalculateImageInfo(scrgb);
        log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, percentileLum calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

        PerformTonemapping(scrgb, imageInfo, scrgb);

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

    private static void PerformTonemapping(Vector4[] scrgb, ImageInfo imageInfo, Vector4[] outPixels)
    {
        var maxYInPQ = imageInfo.MaxYInPQ;
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