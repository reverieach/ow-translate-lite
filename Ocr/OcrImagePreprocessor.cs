using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;

namespace OwTranslateLite.Ocr;

public enum OcrPreprocessingMode
{
    ColorPreserving,
    OwChatCyanMask,
    OwChatMultiColorMask,
    OwChatMultiColorMaskThickened
}

public static class OcrImagePreprocessor
{
    public const int ScaleFactor = 2;
    public const OcrPreprocessingMode DefaultMode = OcrPreprocessingMode.OwChatMultiColorMask;

    public static Bitmap Prepare(Bitmap source, OcrPreprocessingMode mode)
    {
        return mode switch
        {
            OcrPreprocessingMode.OwChatCyanMask => PrepareOwChatMask(source, includeGreenAndOrange: false, thicken: false),
            OcrPreprocessingMode.OwChatMultiColorMask => PrepareOwChatMask(source, includeGreenAndOrange: true, thicken: false),
            OcrPreprocessingMode.OwChatMultiColorMaskThickened => PrepareOwChatMask(source, includeGreenAndOrange: true, thicken: true),
            _ => PrepareColorPreserving(source)
        };
    }

    public static Bitmap PrepareColorPreserving(Bitmap source)
    {
        Bitmap scaled = ScaleColorPreserving(source);
        ApplyLightSharpen(scaled);
        return scaled;
    }

    private static Bitmap PrepareOwChatMask(Bitmap source, bool includeGreenAndOrange, bool thicken)
    {
        using Bitmap scaled = ScaleColorPreserving(source);
        Bitmap masked = CreateOwChatMask(scaled, includeGreenAndOrange);
        if (thicken)
        {
            ApplyWhiteDilation(masked);
        }

        ApplyLightSharpen(masked);
        return masked;
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

    private static Bitmap CreateOwChatMask(Bitmap source, bool includeGreenAndOrange)
    {
        Bitmap output = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        Rectangle rect = new(0, 0, source.Width, source.Height);
        BitmapData sourceData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData outputData = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int sourceStride = Math.Abs(sourceData.Stride);
            int outputStride = Math.Abs(outputData.Stride);
            int sourceBytes = sourceStride * source.Height;
            int outputBytes = outputStride * output.Height;
            byte[] sourceBuffer = new byte[sourceBytes];
            byte[] outputBuffer = new byte[outputBytes];
            Marshal.Copy(sourceData.Scan0, sourceBuffer, 0, sourceBytes);

            for (int y = 0; y < source.Height; y++)
            {
                int sourceRow = y * sourceStride;
                int outputRow = y * outputStride;
                for (int x = 0; x < source.Width; x++)
                {
                    int sourceIndex = sourceRow + x * 4;
                    byte b = sourceBuffer[sourceIndex];
                    byte g = sourceBuffer[sourceIndex + 1];
                    byte r = sourceBuffer[sourceIndex + 2];
                    bool isText = IsOwChatCyan(r, g, b) ||
                                  (includeGreenAndOrange && (IsOwChatGreen(r, g, b) || IsOwChatOrange(r, g, b)));

                    int outputIndex = outputRow + x * 4;
                    byte value = isText ? GetForegroundMaskValue(r, g, b) : (byte)0;
                    outputBuffer[outputIndex] = value;
                    outputBuffer[outputIndex + 1] = value;
                    outputBuffer[outputIndex + 2] = value;
                    outputBuffer[outputIndex + 3] = 255;
                }
            }

            Marshal.Copy(outputBuffer, 0, outputData.Scan0, outputBytes);
        }
        finally
        {
            source.UnlockBits(sourceData);
            output.UnlockBits(outputData);
        }

        return output;
    }

    private static bool IsOwChatCyan(byte r, byte g, byte b)
    {
        return b >= 118 &&
               g >= 105 &&
               r <= 140 &&
               b >= r + 42 &&
               g >= r + 26;
    }

    private static bool IsOwChatGreen(byte r, byte g, byte b)
    {
        return g >= 122 &&
               r <= 150 &&
               b <= 170 &&
               g >= r + 28 &&
               g >= b + 12;
    }

    private static bool IsOwChatOrange(byte r, byte g, byte b)
    {
        return r >= 145 &&
               g >= 74 &&
               g <= 210 &&
               b <= 150 &&
               r >= b + 52 &&
               r + 20 >= g;
    }

    private static byte GetForegroundMaskValue(byte r, byte g, byte b)
    {
        int value = Math.Max(Math.Max(r, g), b) + 35;
        return ClampToByte(value);
    }

    private static void ApplyWhiteDilation(Bitmap bitmap)
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
                    byte max =
                        Math.Max(
                            Math.Max(source[index], source[index - 4]),
                            Math.Max(
                                Math.Max(source[index + 4], source[index - stride]),
                                source[index + stride]));
                    if (max == 0)
                    {
                        continue;
                    }

                    output[index] = max;
                    output[index + 1] = max;
                    output[index + 2] = max;
                    output[index + 3] = 255;
                }
            }

            Marshal.Copy(output, 0, data.Scan0, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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
