using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;

namespace OwTranslateLite.Core;

public static class ScreenCaptureService
{
    public static Bitmap Capture(Rect region)
    {
        ScreenBoundsService.ValidateCaptureRegion(region);
        int left = (int)Math.Round(region.Left);
        int top = (int)Math.Round(region.Top);
        int width = Math.Max(1, (int)Math.Round(region.Width));
        int height = Math.Max(1, (int)Math.Round(region.Height));

        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
