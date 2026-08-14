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
using System.Buffers.Binary;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Converts ShareX's linear, premultiplied scRGB master into limited-range
    /// Rec.2100 PQ P010. P010 stores each 10-bit code in the most significant
    /// bits of a little-endian 16-bit word and uses 4:2:0 interleaved UV.
    /// </summary>
    internal sealed class HdrP010FrameConverter
    {
        private const int TransferLutSize = 65536;
        private const float PqMaximumNits = 10000f;
        private const float Rec2020Kr = 0.2627f;
        private const float Rec2020Kb = 0.0593f;
        private const float Rec2020Kg = 1f - Rec2020Kr - Rec2020Kb;

        private readonly float masteringMaximumNits;
        private readonly float transferLutScale;
        private readonly float[] transferLut;

        public int Width { get; }
        public int Height { get; }
        public int FrameByteCount { get; }

        public HdrP010FrameConverter(int width, int height, float masteringMaximumNits)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "P010 HDR video requires positive, even frame dimensions.");
            }

            Width = width;
            Height = height;
            this.masteringMaximumNits = Math.Clamp(
                masteringMaximumNits,
                HdrFileOutputSettings.MinimumMasteringDisplayNits,
                HdrFileOutputSettings.MaximumMasteringDisplayNits);
            FrameByteCount = checked(width * height * 3);
            transferLutScale = (TransferLutSize - 1) / this.masteringMaximumNits;
            transferLut = CreateTransferLut(this.masteringMaximumNits);
        }

        public HdrVideoFrameLightLevels Convert(HdrRgba16FloatBuffer source, byte[] destination)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);

            if (source.Width != Width || source.Height != Height)
            {
                throw new ArgumentException("The HDR source size does not match the P010 converter.", nameof(source));
            }

            if (destination.Length < FrameByteCount)
            {
                throw new ArgumentException("The P010 destination buffer is too small.", nameof(destination));
            }

            int yPlaneBytes = checked(Width * Height * sizeof(ushort));
            object aggregateSync = new object();
            float frameMaxCll = 0f;
            double frameLuminanceSum = 0d;

            Parallel.For(
                0,
                Height / 2,
                () => new LightLevelAccumulator(),
                (rowPair, _, accumulator) =>
                {
                    int y = rowPair * 2;
                    ReadOnlySpan<byte> sourceTop = source.GetRowSpan(y);
                    ReadOnlySpan<byte> sourceBottom = source.GetRowSpan(y + 1);
                    Span<byte> yTop = destination.AsSpan(y * Width * 2, Width * 2);
                    Span<byte> yBottom = destination.AsSpan((y + 1) * Width * 2, Width * 2);
                    Span<byte> uvRow = destination.AsSpan(
                        yPlaneBytes + rowPair * Width * 2,
                        Width * 2);

                    for (int x = 0; x < Width; x += 2)
                    {
                        EncodedPixel topLeft = ConvertPixel(sourceTop, x);
                        EncodedPixel topRight = ConvertPixel(sourceTop, x + 1);
                        EncodedPixel bottomLeft = ConvertPixel(sourceBottom, x);
                        EncodedPixel bottomRight = ConvertPixel(sourceBottom, x + 1);

                        WriteP010(yTop, x * 2, QuantizeLuma(topLeft.Luma));
                        WriteP010(yTop, (x + 1) * 2, QuantizeLuma(topRight.Luma));
                        WriteP010(yBottom, x * 2, QuantizeLuma(bottomLeft.Luma));
                        WriteP010(yBottom, (x + 1) * 2, QuantizeLuma(bottomRight.Luma));

                        float chromaBlue = (topLeft.ChromaBlue + topRight.ChromaBlue +
                            bottomLeft.ChromaBlue + bottomRight.ChromaBlue) * 0.25f;
                        float chromaRed = (topLeft.ChromaRed + topRight.ChromaRed +
                            bottomLeft.ChromaRed + bottomRight.ChromaRed) * 0.25f;
                        WriteP010(uvRow, x * 2, QuantizeChroma(chromaBlue));
                        WriteP010(uvRow, (x + 1) * 2, QuantizeChroma(chromaRed));

                        accumulator.MaxCll = Math.Max(
                            accumulator.MaxCll,
                            Math.Max(
                                Math.Max(topLeft.MaxChannelNits, topRight.MaxChannelNits),
                                Math.Max(bottomLeft.MaxChannelNits, bottomRight.MaxChannelNits)));
                        accumulator.LuminanceSum += topLeft.LuminanceNits + topRight.LuminanceNits +
                            bottomLeft.LuminanceNits + bottomRight.LuminanceNits;
                    }

                    return accumulator;
                },
                accumulator =>
                {
                    lock (aggregateSync)
                    {
                        frameMaxCll = Math.Max(frameMaxCll, accumulator.MaxCll);
                        frameLuminanceSum += accumulator.LuminanceSum;
                    }
                });

            float maxFall = (float)(frameLuminanceSum / checked((long)Width * Height));
            return new HdrVideoFrameLightLevels(frameMaxCll, maxFall);
        }

        public HdrVideoFrameLightLevels ConvertSdrBgra(
            byte[] source,
            int sourceStride,
            byte[] destination,
            float sdrWhiteNits = HdrCaptureSettings.DefaultBrightnessNits)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);

            int packedRowBytes = checked(Width * 4);
            if (sourceStride < packedRowBytes || source.Length < checked(sourceStride * Height))
            {
                throw new ArgumentException("The SDR source buffer is too small.", nameof(source));
            }

            if (destination.Length < FrameByteCount)
            {
                throw new ArgumentException("The P010 destination buffer is too small.", nameof(destination));
            }

            sdrWhiteNits = Math.Clamp(sdrWhiteNits, 1f, masteringMaximumNits);
            int yPlaneBytes = checked(Width * Height * sizeof(ushort));
            object aggregateSync = new object();
            float frameMaxCll = 0f;
            double frameLuminanceSum = 0d;

            Parallel.For(
                0,
                Height / 2,
                () => new LightLevelAccumulator(),
                (rowPair, _, accumulator) =>
                {
                    int y = rowPair * 2;
                    ReadOnlySpan<byte> sourceTop = source.AsSpan(y * sourceStride, packedRowBytes);
                    ReadOnlySpan<byte> sourceBottom = source.AsSpan((y + 1) * sourceStride, packedRowBytes);
                    Span<byte> yTop = destination.AsSpan(y * Width * 2, Width * 2);
                    Span<byte> yBottom = destination.AsSpan((y + 1) * Width * 2, Width * 2);
                    Span<byte> uvRow = destination.AsSpan(
                        yPlaneBytes + rowPair * Width * 2,
                        Width * 2);

                    for (int x = 0; x < Width; x += 2)
                    {
                        EncodedPixel topLeft = ConvertSdrPixel(sourceTop, x, sdrWhiteNits);
                        EncodedPixel topRight = ConvertSdrPixel(sourceTop, x + 1, sdrWhiteNits);
                        EncodedPixel bottomLeft = ConvertSdrPixel(sourceBottom, x, sdrWhiteNits);
                        EncodedPixel bottomRight = ConvertSdrPixel(sourceBottom, x + 1, sdrWhiteNits);

                        WriteP010(yTop, x * 2, QuantizeLuma(topLeft.Luma));
                        WriteP010(yTop, (x + 1) * 2, QuantizeLuma(topRight.Luma));
                        WriteP010(yBottom, x * 2, QuantizeLuma(bottomLeft.Luma));
                        WriteP010(yBottom, (x + 1) * 2, QuantizeLuma(bottomRight.Luma));

                        float chromaBlue = (topLeft.ChromaBlue + topRight.ChromaBlue +
                            bottomLeft.ChromaBlue + bottomRight.ChromaBlue) * 0.25f;
                        float chromaRed = (topLeft.ChromaRed + topRight.ChromaRed +
                            bottomLeft.ChromaRed + bottomRight.ChromaRed) * 0.25f;
                        WriteP010(uvRow, x * 2, QuantizeChroma(chromaBlue));
                        WriteP010(uvRow, (x + 1) * 2, QuantizeChroma(chromaRed));

                        accumulator.MaxCll = Math.Max(
                            accumulator.MaxCll,
                            Math.Max(
                                Math.Max(topLeft.MaxChannelNits, topRight.MaxChannelNits),
                                Math.Max(bottomLeft.MaxChannelNits, bottomRight.MaxChannelNits)));
                        accumulator.LuminanceSum += topLeft.LuminanceNits + topRight.LuminanceNits +
                            bottomLeft.LuminanceNits + bottomRight.LuminanceNits;
                    }

                    return accumulator;
                },
                accumulator =>
                {
                    lock (aggregateSync)
                    {
                        frameMaxCll = Math.Max(frameMaxCll, accumulator.MaxCll);
                        frameLuminanceSum += accumulator.LuminanceSum;
                    }
                });

            float maxFall = (float)(frameLuminanceSum / checked((long)Width * Height));
            return new HdrVideoFrameLightLevels(frameMaxCll, maxFall);
        }

        private EncodedPixel ConvertPixel(ReadOnlySpan<byte> row, int x)
        {
            int offset = x * HdrRgba16FloatBuffer.BytesPerPixel;

            // The video has no alpha channel, so retain premultiplication. This is
            // equivalent to compositing translucent desktop/game pixels over black.
            float red709 = ReadFiniteHalf(row, offset);
            float green709 = ReadFiniteHalf(row, offset + 2);
            float blue709 = ReadFiniteHalf(row, offset + 4);

            return ConvertLinear709Pixel(
                red709,
                green709,
                blue709,
                HdrRgba16FloatBuffer.ReferenceWhiteNits);
        }

        private EncodedPixel ConvertSdrPixel(ReadOnlySpan<byte> row, int x, float sdrWhiteNits)
        {
            int offset = x * 4;
            float alpha = row[offset + 3] / 255f;
            float red709 = DecodeSrgb(row[offset + 2] / 255f) * alpha;
            float green709 = DecodeSrgb(row[offset + 1] / 255f) * alpha;
            float blue709 = DecodeSrgb(row[offset] / 255f) * alpha;
            return ConvertLinear709Pixel(red709, green709, blue709, sdrWhiteNits);
        }

        private EncodedPixel ConvertLinear709Pixel(
            float red709,
            float green709,
            float blue709,
            float referenceWhiteNits)
        {
            float red2020 = 0.6274039f * red709 + 0.3292830f * green709 + 0.0433131f * blue709;
            float green2020 = 0.0690973f * red709 + 0.9195404f * green709 + 0.0113623f * blue709;
            float blue2020 = 0.0163914f * red709 + 0.0880133f * green709 + 0.8955953f * blue709;

            float redNits = Math.Clamp(
                red2020 * referenceWhiteNits,
                0f,
                masteringMaximumNits);
            float greenNits = Math.Clamp(
                green2020 * referenceWhiteNits,
                0f,
                masteringMaximumNits);
            float blueNits = Math.Clamp(
                blue2020 * referenceWhiteNits,
                0f,
                masteringMaximumNits);

            float red = EncodePqLut(redNits);
            float green = EncodePqLut(greenNits);
            float blue = EncodePqLut(blueNits);
            float luma = Rec2020Kr * red + Rec2020Kg * green + Rec2020Kb * blue;
            float chromaBlue = (blue - luma) / (2f * (1f - Rec2020Kb));
            float chromaRed = (red - luma) / (2f * (1f - Rec2020Kr));
            float luminanceNits = Rec2020Kr * redNits + Rec2020Kg * greenNits + Rec2020Kb * blueNits;

            return new EncodedPixel(
                luma,
                chromaBlue,
                chromaRed,
                luminanceNits,
                Math.Max(redNits, Math.Max(greenNits, blueNits)));
        }

        private static float DecodeSrgb(float value) =>
            value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

        private float EncodePqLut(float nits)
        {
            float position = nits * transferLutScale;
            int index = Math.Min((int)position, TransferLutSize - 2);
            float fraction = position - index;
            return transferLut[index] + (transferLut[index + 1] - transferLut[index]) * fraction;
        }

        private static ushort QuantizeLuma(float value) =>
            checked((ushort)Math.Clamp((int)MathF.Round(64f + 876f * value), 64, 940));

        private static ushort QuantizeChroma(float value) =>
            checked((ushort)Math.Clamp((int)MathF.Round(512f + 896f * value), 64, 960));

        private static void WriteP010(Span<byte> destination, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(offset, sizeof(ushort)),
                checked((ushort)(value << 6)));

        private static float ReadFiniteHalf(ReadOnlySpan<byte> source, int offset)
        {
            Half value = BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort))));
            return Half.IsFinite(value) ? (float)value : 0f;
        }

        private static float[] CreateTransferLut(float masteringMaximumNits)
        {
            var result = new float[TransferLutSize];

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = EncodePq(masteringMaximumNits * i / (result.Length - 1));
            }

            return result;
        }

        private static float EncodePq(float nits)
        {
            const double m1 = 2610d / 16384d;
            const double m2 = 2523d / 32d;
            const double c1 = 3424d / 4096d;
            const double c2 = 2413d / 128d;
            const double c3 = 2392d / 128d;

            double normalized = Math.Clamp(nits / PqMaximumNits, 0d, 1d);
            double luminancePower = Math.Pow(normalized, m1);
            return (float)Math.Pow(
                (c1 + c2 * luminancePower) / (1d + c3 * luminancePower),
                m2);
        }

        private readonly record struct EncodedPixel(
            float Luma,
            float ChromaBlue,
            float ChromaRed,
            float LuminanceNits,
            float MaxChannelNits);

        private sealed class LightLevelAccumulator
        {
            public float MaxCll;
            public double LuminanceSum;
        }
    }

    internal readonly record struct HdrVideoFrameLightLevels(float MaxCll, float MaxFall);
}
