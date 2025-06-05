using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using DirectXTexNet;
using Vortice.Direct3D11;
using Buffer = System.Buffer;

namespace ShareX.ScreenCaptureLib.HDR
{
    // heavily inspired by https://github.com/SpecialKO/SKIV/blob/ed2a4a9de93ebba9661f9e8ed31c5d67ab490d2d/src/utility/image.cpp#L1300C1-L1300C25
    // MIT License Copyright (c) 2024 Aemony
    public class SDRHelper
    {
        private static readonly string defaultSDRFileExt = ".png";


        public static void SKIV_Image_SaveToDisk_SDR(ID3D11Texture2D image, ID3D11Device device, string wszFileName, bool force_sRGB)
        {
            var stopwatchTotal = Stopwatch.StartNew();
            int width = (int)image.Description.Width;
            int height = (int)image.Description.Height;
            var pixelSpan = image.GetPixelSpan(device, out var pixelData);
            Vector4[] scrgb = pixelData;
            var outPixels = new Vector4[scrgb.Length]; // TODO: optmizr??

            var log = Console.Out;

            const int E_INVALIDARG = unchecked((int)0x80070057);
            const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

            // 2. Get the file extension from the provided filename
            string wszExtension = GetExtension(wszFileName);

            // 3. Prepare an “implicit” filename in case the user did not supply an extension
            string wszImplicitFileName = wszFileName;
            if (string.IsNullOrEmpty(wszExtension))
            {
                wszImplicitFileName += defaultSDRFileExt;
                wszExtension = GetExtension(wszImplicitFileName);
            }

            // 5. Initialize luminance‐tracking vectors (from DirectXMath)
            Vector4 maxLum = Vector4.Zero;
            Vector4 minLum = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);

            bool is_hdr = true;

            // 6. Prepare scratch buffers for intermediate conversions
            // ScratchImage final_sdr = new ScratchImage();

            var xPixelFormat = image.Description.Format;

            // 8. Flags for preferring higher‐bit‐depth WIC pixel formats
            bool bPrefer10bpcAs48bpp = false;
            bool bPrefer10bpcAs32bpp = false;

            // 9. Prepare WIC codec GUID and WIC flags
            DirectXTexNet.WICCodecs wic_codec;
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
                bPrefer10bpcAs48bpp = false;
                bPrefer10bpcAs32bpp = false;
            }
            // 14. Extension: “hdp” or “jxr” (WMP)
            else if ((HasExtension(wszExtension, "hdp") != null) ||
                     (HasExtension(wszExtension, "jxr") != null))
            {
                wic_codec = WICCodecs.WMP;
                bPrefer10bpcAs32bpp = is_hdr;
            }
            // 15. Unsupported extension
            else
            {
                throw new Exception("Unsupported file extension");
            }

            // 16. If it’s HDR, apply tonemapping
            if (bPrefer10bpcAs48bpp || bPrefer10bpcAs32bpp)
            {
                wic_flags |= WIC_FLAGS.FORCE_SRGB;
            }

            // ScratchImage tonemapped_hdr = new ScratchImage();
            // ScratchImage tonemapped_copy = new ScratchImage();

            // 16a. Compute min/max luminance in scrgb
            log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImageBegin");

            var stopwatchCore = Stopwatch.StartNew();
            for (var i = 0; i < scrgb.Length; i++)
            {
                Vector4 v = scrgb[i];
                v = Vector4.Transform(v, from709ToXYZ);


                maxLum = Vector4.Create(MathF.Max(v.Y, maxLum.Y));

                minLum = Vector4.Create(
                    MathF.Min(v.Y, minLum.Y)
                );
            }

            // Ensure minLum ≥ 0
            minLum = Vector4.Max(Vector4.Zero, minLum);


            log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, min/max calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

            // 16b. Build a luminance frequency histogram
            uint[] luminance_freq = new uint[65536];
            // (C# arrays are zero-initialized by default; no need to call ZeroMemory)

            float fLumRange = maxLum.Y - minLum.Y;

            for (var i = 0; i < scrgb.Length; i++)
            {
                Vector4 v = scrgb[i];

                v = Vector4.Max(Vector4.Zero, Vector4.Transform(v.AsVector3(), from709ToXYZ));
                // v = Vector4.Max(Vector4.Zero, Vector3.Transform(v.AsVector3(), from709ToXYZ).AsVector4());
                luminance_freq[Math.Clamp((int)Math.Round((v.Y - minLum.Y) / (fLumRange / 65536.0f)), 0, 65535)]++;

                v = Vector4.Max(Vector4.Zero, Vector4.Transform(v, from709ToXYZ));

                int idx = Math.Clamp(
                    (int)Math.Round((v.Y - minLum.Y) / (fLumRange / 65536.0f)),
                    0,
                    65535
                );
                luminance_freq[idx]++;
            }


            log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, luminance_freq calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

            double percent = 100.0;
            double img_size = (double)width * height;

            for (int i = 65535; i >= 0; --i)
            {
                percent -= 100.0 * ((double)luminance_freq[i] / img_size);
                if (percent <= 99.94)
                {
                    float percentileLum = minLum.Y + (fLumRange * ((float)i / 65536.0f));
                    // log.WriteLine($"99.94th percentile luminance: {80.0f * percentileLum} nits");

                    maxLum = Vector4.Create(percentileLum);
                    break;
                }
            }


            log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, percentileLum calculated: " + stopwatchCore.ElapsedMilliseconds + "ms");

            // log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImageEnd");

            // 16c. After tonemapping, find peak RGB
            Vector4 maxTonemappedRGB = Vector4.Zero;
            const float _maxNitsToTonemap = 125.0f;
            float SDR_YInPQ = LinearToPQY(1.5f);
            float maxYInPQ = MathF.Max(
                SDR_YInPQ,
                LinearToPQY(MathF.Min(_maxNitsToTonemap, maxLum.Y))
            );

            bool needs_tonemapping = true; // (in C++ this was always set to true)

            if (needs_tonemapping)
            {
                maxTonemappedRGB = PerformTonemapping(scrgb, maxYInPQ, maxTonemappedRGB, outPixels);
            }

            log.WriteLine("SKIV_Image_TonemapToSDR(): EvaluateImage, tonemapped: " + stopwatchCore.ElapsedMilliseconds + "ms");

            scrgb = outPixels;

            float fMaxR = (maxTonemappedRGB.X);
            float fMaxG = (maxTonemappedRGB.Y);
            float fMaxB = (maxTonemappedRGB.Z);

            // 16d. Optionally normalize after tonemapping if any channel < 1.0
            bool normalizeAfterTonemap = false;
            if (normalizeAfterTonemap)
            {
                float fSmallestComp = MathF.Min(fMaxR, MathF.Min(fMaxG, fMaxB));
                if (fSmallestComp > 0.0f)
                {
                    float fRescale = 1.0f / fSmallestComp;
                    Vector4 vNormalizationScale = Vector4.Create(fRescale);

                    log.WriteLine("SKIV_Image_TonemapToSDR(): TransformImageBegin");

                    for (int j = 0; j < scrgb.Length; ++j)
                    {
                        Vector4 value = scrgb[j];
                        outPixels[j] = Vector4.Clamp(Vector4.Multiply(value, vNormalizationScale), Vector4.Zero, Vector4.One);
                    }
                }
            }

            // 16e. Convert to final SDR format (choose 48bpp/32bpp/24bpp based on flags)
            DXGI_FORMAT outFormat;
            if (bPrefer10bpcAs48bpp)
            {
                outFormat = DXGI_FORMAT.R16G16B16A16_UNORM;
            }
            else if (bPrefer10bpcAs32bpp)
            {
                outFormat = DXGI_FORMAT.R10G10B10A2_UNORM;
            }
            else
            {
                outFormat = DXGI_FORMAT.B8G8R8X8_UNORM_SRGB;
            }

// -----------------------------------------------------------------------------
// 17. Put the tonemapped pixel buffer (Vector4[]) in a ScratchImage
//     so DirectXTex can work with it
// -----------------------------------------------------------------------------
            stopwatchCore.Stop();
            log.WriteLine("SKIV_Image_TonemapToSDR(): ConvertToSDR: " + stopwatchCore.ElapsedMilliseconds + "ms (total: " + stopwatchTotal.ElapsedMilliseconds +
                          "ms)");
            var texHelper = TexHelper.Instance;
            using var hdrScratch = texHelper.Initialize2D(DXGI_FORMAT.R32G32B32A32_FLOAT,
                width,
                height,
                1,
                1, CP_FLAGS.NONE);

            unsafe
            {
                var img = hdrScratch.GetImage(0);
                fixed (Vector4* pSrc = scrgb) // <- your final SDR buffer (outPixels)
                {
                    // copy the whole image in one go
                    Buffer.MemoryCopy(pSrc,
                        (void*)img.Pixels,
                        img.SlicePitch, // dest capacity
                        img.SlicePitch); // bytes to copy
                }
            }

// -----------------------------------------------------------------------------
// 18. Convert the floating-point buffer to the DXGI format you chose earlier
// -----------------------------------------------------------------------------
            using var sdrScratch = hdrScratch.Convert(0, outFormat, TEX_FILTER_FLAGS.DEFAULT, 1.0f);

// -----------------------------------------------------------------------------
// 19. Encode the converted image to disk with WIC
// -----------------------------------------------------------------------------
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

        private static Vector4 PerformTonemapping(Vector4[] scrgb, float maxYInPQ, Vector4 maxTonemappedRGB, Vector4[] outPixels)
        {
            for (int j = 0; j < scrgb.Length; ++j)
            {
                var maxTonemappedRgb = MaxTonemappedRgb(scrgb, maxYInPQ, maxTonemappedRGB, outPixels, j);
                maxTonemappedRGB = maxTonemappedRgb;
            }

            return maxTonemappedRGB;
        }

        private static Vector4 MaxTonemappedRgb(Vector4[] scrgb, float maxYInPQ, Vector4 maxTonemappedRGB, Vector4[] outPixels, int j)
        {
            Vector4 value = scrgb[j];
            Vector4 ICtCp = Rec709toICtCp(value);
            float Y_in =  MathF.Max(ICtCp.X, 0.0f);
            float Y_out = 1.0f;

            Y_out = HdrTonemap(maxYInPQ, Y_out, Y_in);

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

            value = ICtCpToRec709(ICtCp);
            outPixels[j] = value;
            return Vector4.Max(maxTonemappedRGB, value);
        }

        private static string GetExtension(string wszFileName)
        {
            return Path.GetExtension(wszFileName)?.ToLower() ?? "";
        }

        private static object HasExtension(string wszExtension, string extension)
        {
            return wszExtension.EndsWith(extension);
        }

        static readonly Matrix4x4 Rec709toICtCpConvMat = new Matrix4x4
        (
            0.5000f, 1.6137f, 4.3780f, 0.0f,
            0.5000f, -3.3234f, -4.2455f, 0.0f,
            0.0000f, 1.7097f, -0.1325f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        );


        private static readonly Matrix4x4 ICtCpToRec709ConvMat = new(
            1.0f, 1.0f, 1.0f, 0.0f,
            0.0086051457f, -0.0086051457f, 0.5600488596f, 0.0f,
            0.1110356045f, -0.1110356045f, -0.3206374702f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);


        static readonly Matrix4x4 from709ToXYZ = new
        (
            0.4123907983303070068359375f, 0.2126390039920806884765625f, 0.0193308182060718536376953125f, 0.0f,
            0.3575843274593353271484375f, 0.715168654918670654296875f, 0.119194783270359039306640625f, 0.0f,
            0.18048079311847686767578125f, 0.072192318737506866455078125f, 0.950532138347625732421875f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        );

        static readonly Matrix4x4 fromXYZtoLMS = new
        (
            0.3592f, -0.1922f, 0.0070f, 0.0f,
            0.6976f, 1.1004f, 0.0749f, 0.0f,
            -0.0358f, 0.0755f, 0.8434f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        );

        static readonly Matrix4x4 fromLMStoXYZ = new
        (
            2.070180056695613509600f, 0.364988250032657479740f, -0.049595542238932107896f, 0.0f,
            -1.326456876103021025500f, 0.680467362852235141020f, -0.049421161186757487412f, 0.0f,
            0.206616006847855170810f, -0.045421753075853231409f, 1.187995941732803439400f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        );

        static readonly Matrix4x4 fromXYZto709 = new
        (3.2409698963165283203125f, -0.96924364566802978515625f, 0.055630080401897430419921875f, 0.0f,
            -1.53738319873809814453125f, 1.875967502593994140625f, -0.2039769589900970458984375f, 0.0f,
            -0.4986107647418975830078125f, 0.0415550582110881805419921875f, 1.05697154998779296875f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f
        );

        static readonly Vector4 PQ_N = new(2610.0f / 4096.0f / 4.0f);
        static readonly Vector4 PQ_M = new(2523.0f / 4096.0f * 128.0f);
        static readonly Vector4 PQ_C1 = new(3424.0f / 4096.0f);
        static readonly Vector4 PQ_C2 = new(2413.0f / 4096.0f * 32.0f);
        static readonly Vector4 PQ_C3 = new(2392.0f / 4096.0f * 32.0f);
        static readonly Vector4 PQ_MaxPQ = new(125.0f);
        static readonly Vector4 RcpM = new(2610.0f / 4096.0f / 4.0f);
        static readonly Vector4 RcpN = new(2523.0f / 4096.0f * 128.0f);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector4 Rec709toICtCp(Vector4 N)
        {
            Vector4 ret = N;

            ret = Vector4.Transform(ret.AsVector3(), from709ToXYZ);
            ret = Vector4.Transform(ret.AsVector3(), fromXYZtoLMS);

            ret = LinearToPQ(Vector4.Max(ret, Vector4.Zero), PQ_MaxPQ);


            return Vector4.Transform(ret, Rec709toICtCpConvMat);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 PQToLinear(Vector4 pq, Vector4 maxPQ)
        {
            // ret = (max(pq, 0))^(1/m)

            var ret = VectorPow(Vector4.Max(pq, Vector4.Zero), RcpM);

            // nd  = max(ret - C1, 0) / (C2 - C3·ret)
            var numerator = Vector4.Max(ret - PQ_C1, Vector4.Zero);
            var denominator = PQ_C2 - PQ_C3 * ret;
            var nd = numerator / denominator;

            // ret = nd^(1/n) · maxPQ
            ret = VectorPow(nd, RcpN) * maxPQ;
            return ret;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 ICtCpToRec709(Vector4 ictcp)
        {
            var v = Vector4.Transform(ictcp.AsVector3(), ICtCpToRec709ConvMat);

            v = PQToLinear(v, PQ_MaxPQ);

            v = Vector4.Transform(v.AsVector3(), fromLMStoXYZ);
            return Vector4.Transform(v.AsVector3(), fromXYZto709);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector4 LinearToPQ(Vector4 N, Vector4 maxPQValue)
        {
            Vector4 ret = VectorPow(Vector4.Max(N, Vector4.Zero) / maxPQValue, PQ_N);
            Vector4 nd = (PQ_C1 + (PQ_C2 * ret)) / (Vector4.One + (PQ_C3 * ret));

            return VectorPow(nd, PQ_M);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float LinearToPQY(float N) // 1.5
        {
            float fScaledN = Math.Abs(N * 0.008f); // 0.008 = 1/125.0

            float ret = MathF.Pow(fScaledN, 0.1593017578125f);

            float nd = Math.Abs((0.8359375f + (18.8515625f * ret)) /
                                (1.0f + (18.6875f * ret)));

            return MathF.Pow(nd, 78.84375f);
        }
        static float LinearToPQY() // 1.5
        {

            float nd = Math.Abs(0.9918963f);

            return MathF.Pow(nd, 78.84375f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float HdrTonemap(float maxYInPQ, float Y_out, float Y_in)
        {
            float a = (Y_out / MathF.Pow(maxYInPQ, 2.0f));
            float b = (1.0f / Y_out);
            Y_out = (Y_in * (1 + a * Y_in)) / (1 + b * Y_in);
            return Y_out;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 VectorPow(Vector4 v, Vector4 p)
        {
            return new Vector4(
                (float)MathF.Pow(v.X, p.X),
                (float)MathF.Pow(v.Y, p.Y),
                (float)MathF.Pow(v.Z, p.Z),
                (float)MathF.Pow(v.W, p.W)
            );
        }
    }
}