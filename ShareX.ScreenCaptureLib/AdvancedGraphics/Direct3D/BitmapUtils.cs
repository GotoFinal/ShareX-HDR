using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class BitmapUtils
{
    public static Bitmap BuildBitmapFromMappedPointer(
        IntPtr dataPtr,
        int rowPitch,
        int width,
        int height)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        // Lock the entire bitmap's bits
        var rect = new Rectangle(0, 0, width, height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

        unsafe
        {
            byte* srcRow = (byte*)dataPtr;
            byte* dstRow = (byte*)bmpData.Scan0.ToPointer();

            for (int y = 0; y < height; y++)
            {
                // Copy exactly width * 4 bytes from srcRow to dstRow
                Buffer.MemoryCopy(
                    srcRow + y * rowPitch,
                    dstRow + y * bmpData.Stride,
                    bmpData.Stride,
                    width * 4
                );
            }
        }

        bmp.UnlockBits(bmpData);
        return bmp;
    }

    public static Bitmap BuildBitmapFromByteArray(
        byte[] pixelBytes, // length = (width*4) * height
        int width,
        int height)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var rect = new Rectangle(0, 0, width, height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

        int rowPitchDst = bmpData.Stride; // usually >= width*4
        int rowPitchSrc = width * 4; // exactly width * 4

        unsafe
        {
            byte* dstBase = (byte*)bmpData.Scan0.ToPointer();
            fixed (byte* srcBase = pixelBytes)
            {
                for (int y = 0; y < height; y++)
                {
                    // IntPtr srcRowPtr = new IntPtr(srcBase + y * rowPitchSrc);
                    // IntPtr dstRowPtr = new IntPtr(dstBase + y * rowPitchDst);

                    // Marshal.Copy(
                    //     srcRowPtr,
                    //     new byte[rowPitchSrc],
                    //     0,
                    //     rowPitchSrc
                    // );
                    // Or use a single Buffer.MemoryCopy if you want to stay in unsafe:
                    Buffer.MemoryCopy(
                        srcBase + y * rowPitchSrc,
                        dstBase + y * rowPitchDst,
                        rowPitchDst,
                        rowPitchSrc
                    );
                    //
                    // But Marshal.Copy is simpler here (no pin/unpin issues).
                }
            }
        }

        bmp.UnlockBits(bmpData);
        return bmp;
    }
}