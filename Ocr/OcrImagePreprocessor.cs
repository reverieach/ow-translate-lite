using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;

namespace OwTranslateLite.Ocr;

public static class OcrImagePreprocessor
{
    public const int ScaleFactor = 2;

    public static Bitmap Prepare(Bitmap source)
    {
        Bitmap scaled = ScaleColorPreserving(source);
        ApplyLightSharpen(scaled);
        return scaled;
    }

    public static Rect ScaleBoundsBack(Rect bounds) =>
        new(
            bounds.Left / ScaleFactor,
            bounds.Top / ScaleFactor,
            bounds.Width / ScaleFactor,
            bounds.Height / ScaleFactor);

    private static ImageAttributes CreateColorPreservingAttributes()
    {
        ImageAttributes attributes = new();
        const float contrast = 1.18f;
        const float offset = 0.018f;
        float[][] matrix =
        [
            [contrast, 0, 0, 0, 0],
            [0, contrast, 0, 0, 0],
            [0, 0, contrast, 0, 0],
            [0, 0, 0, 1, 0],
            [offset, offset, offset, 0, 1]
        ];
        attributes.SetColorMatrix(new ColorMatrix(matrix));
        attributes.SetGamma(0.96f);
        return attributes;
    }

    private static Bitmap ScaleColorPreserving(Bitmap source)
    {
        int width = Math.Max(1, source.Width * ScaleFactor);
        int height = Math.Max(1, source.Height * ScaleFactor);
        Bitmap scaled = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        using ImageAttributes attributes = CreateColorPreservingAttributes();
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return scaled;
    }

    private static void ApplyLightSharpen(Bitmap bitmap)
    {
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int bytes = stride * data.Height;
            byte[] source = new byte[bytes];
            byte[] output = new byte[bytes];
            Marshal.Copy(data.Scan0, source, 0, bytes);
            Array.Copy(source, output, bytes);

            for (int y = 1; y < bitmap.Height - 1; y++)
            {
                for (int x = 1; x < bitmap.Width - 1; x++)
                {
                    int index = y * stride + x * 4;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int value =
                            source[index + channel] * 5 -
                            source[index - 4 + channel] -
                            source[index + 4 + channel] -
                            source[index - stride + channel] -
                            source[index + stride + channel];
                        output[index + channel] = ClampToByte(value);
                    }

                    output[index + 3] = source[index + 3];
                }
            }

            Marshal.Copy(output, 0, data.Scan0, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte ClampToByte(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= 255 ? (byte)255 : (byte)value;
    }
}
