#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Buffers.Binary;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Composites an annotation layer rendered by the existing SDR editor over a
/// retained premultiplied RGBA16F linear-scRGB image.
/// </summary>
public static class SdrAnnotationOverlayCompositor
{
    private const float ScRgbReferenceWhiteNits = 80f;
    private const float HalfMaximum = 65504f;

    // Indexed by (alpha << 8) | premultipliedChannel. The value is already
    // unpremultiplied in sRGB, decoded to linear light, and premultiplied again.
    private static readonly float[] PremultipliedSrgbToLinear = CreatePremultipliedSrgbToLinearLut();
    private static readonly float[] SrgbToLinear = CreateSrgbToLinearLut();

    /// <summary>
    /// Blends premultiplied BGRA8/sRGB overlay pixels into premultiplied
    /// RGBA16F/linear-scRGB destination pixels in place.
    /// </summary>
    public static void CompositeBgra8Premultiplied(
        Span<byte> hdrRgba16Float,
        int hdrRowBytes,
        ReadOnlySpan<byte> overlayBgra8,
        int overlayRowBytes,
        int width,
        int height,
        float annotationWhiteNits)
    {
        ValidateBuffer(hdrRgba16Float.Length, hdrRowBytes, width, height, 8, nameof(hdrRgba16Float));
        ValidateBuffer(overlayBgra8.Length, overlayRowBytes, width, height, 4, nameof(overlayBgra8));

        if (!float.IsFinite(annotationWhiteNits) || annotationWhiteNits <= 0f || annotationWhiteNits > 10000f)
        {
            throw new ArgumentOutOfRangeException(nameof(annotationWhiteNits), "Annotation white must be finite and in (0, 10000] nits.");
        }

        float whiteScale = annotationWhiteNits / ScRgbReferenceWhiteNits;

        for (int y = 0; y < height; y++)
        {
            Span<byte> hdrRow = hdrRgba16Float.Slice(y * hdrRowBytes, width * 8);
            ReadOnlySpan<byte> overlayRow = overlayBgra8.Slice(y * overlayRowBytes, width * 4);

            for (int x = 0; x < width; x++)
            {
                int overlayOffset = x * 4;
                byte alphaByte = overlayRow[overlayOffset + 3];
                if (alphaByte == 0)
                {
                    continue;
                }

                float sourceAlpha = alphaByte / 255f;
                float inverseSourceAlpha = 1f - sourceAlpha;
                int lutBase = alphaByte << 8;

                // Avalonia/Skia overlay storage is BGRA8 premultiplied.
                float sourceBlue = PremultipliedSrgbToLinear[lutBase | overlayRow[overlayOffset]] * whiteScale;
                float sourceGreen = PremultipliedSrgbToLinear[lutBase | overlayRow[overlayOffset + 1]] * whiteScale;
                float sourceRed = PremultipliedSrgbToLinear[lutBase | overlayRow[overlayOffset + 2]] * whiteScale;

                int hdrOffset = x * 8;
                float destinationRed = ReadHalf(hdrRow, hdrOffset);
                float destinationGreen = ReadHalf(hdrRow, hdrOffset + 2);
                float destinationBlue = ReadHalf(hdrRow, hdrOffset + 4);
                float destinationAlpha = Math.Clamp(ReadHalf(hdrRow, hdrOffset + 6), 0f, 1f);

                WriteHalf(hdrRow, hdrOffset, sourceRed + destinationRed * inverseSourceAlpha);
                WriteHalf(hdrRow, hdrOffset + 2, sourceGreen + destinationGreen * inverseSourceAlpha);
                WriteHalf(hdrRow, hdrOffset + 4, sourceBlue + destinationBlue * inverseSourceAlpha);
                WriteHalf(hdrRow, hdrOffset + 6, sourceAlpha + destinationAlpha * inverseSourceAlpha);
            }
        }
    }

    /// <summary>
    /// Blends a straight-alpha BGRA8/sRGB asset into part of a premultiplied
    /// RGBA16F/linear-scRGB destination. Content outside the canvas is clipped.
    /// </summary>
    public static void CompositeBgra8Straight(
        Span<byte> hdrRgba16Float,
        int hdrRowBytes,
        int hdrWidth,
        int hdrHeight,
        ReadOnlySpan<byte> sourceBgra8,
        int sourceRowBytes,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY,
        float sourceWhiteNits)
    {
        ValidateBuffer(hdrRgba16Float.Length, hdrRowBytes, hdrWidth, hdrHeight, 8, nameof(hdrRgba16Float));
        ValidateBuffer(sourceBgra8.Length, sourceRowBytes, sourceWidth, sourceHeight, 4, nameof(sourceBgra8));

        if (!float.IsFinite(sourceWhiteNits) || sourceWhiteNits <= 0f || sourceWhiteNits > 10000f)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWhiteNits));
        }

        int sourceStartX = Math.Max(0, -destinationX);
        int sourceStartY = Math.Max(0, -destinationY);
        int destinationStartX = Math.Max(0, destinationX);
        int destinationStartY = Math.Max(0, destinationY);
        int copyWidth = Math.Min(sourceWidth - sourceStartX, hdrWidth - destinationStartX);
        int copyHeight = Math.Min(sourceHeight - sourceStartY, hdrHeight - destinationStartY);

        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        float whiteScale = sourceWhiteNits / ScRgbReferenceWhiteNits;

        for (int y = 0; y < copyHeight; y++)
        {
            Span<byte> hdrRow = hdrRgba16Float.Slice((destinationStartY + y) * hdrRowBytes, hdrWidth * 8);
            ReadOnlySpan<byte> sourceRow = sourceBgra8.Slice((sourceStartY + y) * sourceRowBytes, sourceWidth * 4);

            for (int x = 0; x < copyWidth; x++)
            {
                int sourceOffset = (sourceStartX + x) * 4;
                byte alphaByte = sourceRow[sourceOffset + 3];
                if (alphaByte == 0)
                {
                    continue;
                }

                float sourceAlpha = alphaByte / 255f;
                float inverseSourceAlpha = 1f - sourceAlpha;
                float sourceBlue = SrgbToLinear[sourceRow[sourceOffset]] * sourceAlpha * whiteScale;
                float sourceGreen = SrgbToLinear[sourceRow[sourceOffset + 1]] * sourceAlpha * whiteScale;
                float sourceRed = SrgbToLinear[sourceRow[sourceOffset + 2]] * sourceAlpha * whiteScale;

                int hdrOffset = (destinationStartX + x) * 8;
                float destinationRed = ReadHalf(hdrRow, hdrOffset);
                float destinationGreen = ReadHalf(hdrRow, hdrOffset + 2);
                float destinationBlue = ReadHalf(hdrRow, hdrOffset + 4);
                float destinationAlpha = Math.Clamp(ReadHalf(hdrRow, hdrOffset + 6), 0f, 1f);

                WriteHalf(hdrRow, hdrOffset, sourceRed + destinationRed * inverseSourceAlpha);
                WriteHalf(hdrRow, hdrOffset + 2, sourceGreen + destinationGreen * inverseSourceAlpha);
                WriteHalf(hdrRow, hdrOffset + 4, sourceBlue + destinationBlue * inverseSourceAlpha);
                WriteHalf(hdrRow, hdrOffset + 6, sourceAlpha + destinationAlpha * inverseSourceAlpha);
            }
        }
    }

    private static float[] CreatePremultipliedSrgbToLinearLut()
    {
        float[] lut = new float[ushort.MaxValue + 1];

        for (int alpha = 1; alpha <= byte.MaxValue; alpha++)
        {
            float normalizedAlpha = alpha / 255f;
            int baseIndex = alpha << 8;

            for (int channel = 0; channel <= byte.MaxValue; channel++)
            {
                float encoded = Math.Min(channel / (float)alpha, 1f);
                float linear = encoded <= 0.04045f
                    ? encoded / 12.92f
                    : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
                lut[baseIndex | channel] = linear * normalizedAlpha;
            }
        }

        return lut;
    }

    private static float[] CreateSrgbToLinearLut()
    {
        float[] lut = new float[byte.MaxValue + 1];

        for (int channel = 0; channel <= byte.MaxValue; channel++)
        {
            float encoded = channel / 255f;
            lut[channel] = encoded <= 0.04045f
                ? encoded / 12.92f
                : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        return lut;
    }

    private static float ReadHalf(ReadOnlySpan<byte> row, int offset)
    {
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset, 2));
        float value = (float)BitConverter.UInt16BitsToHalf(bits);

        if (float.IsNaN(value))
        {
            return 0f;
        }

        if (float.IsPositiveInfinity(value))
        {
            return HalfMaximum;
        }

        if (float.IsNegativeInfinity(value))
        {
            return -HalfMaximum;
        }

        return value;
    }

    private static void WriteHalf(Span<byte> row, int offset, float value)
    {
        value = float.IsNaN(value) ? 0f : Math.Clamp(value, -HalfMaximum, HalfMaximum);
        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(offset, 2), bits);
    }

    private static void ValidateBuffer(int bufferLength, int rowBytes, int width, int height, int bytesPerPixel, string paramName)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        int minimumRowBytes = checked(width * bytesPerPixel);
        if (rowBytes < minimumRowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(rowBytes), $"Row stride for {paramName} is smaller than one pixel row.");
        }

        int requiredLength = checked((height - 1) * rowBytes + minimumRowBytes);
        if (bufferLength < requiredLength)
        {
            throw new ArgumentException($"The {paramName} buffer is smaller than its dimensions and stride require.", paramName);
        }
    }
}