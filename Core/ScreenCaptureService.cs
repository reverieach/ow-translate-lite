using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;

namespace OwTranslateLite.Core;

public static class ScreenCaptureService
{
    public static Bitmap Capture(Rect region)
    {
        ValidateRegion(region);
        int left = (int)Math.Round(region.Left);
        int top = (int)Math.Round(region.Top);
        int width = Math.Max(1, (int)Math.Round(region.Width));
        int height = Math.Max(1, (int)Math.Round(region.Height));

        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static void ValidateRegion(Rect region)
    {
        if (!IsFinite(region.Left) ||
            !IsFinite(region.Top) ||
            !IsFinite(region.Width) ||
            !IsFinite(region.Height) ||
            region.Width < 2 ||
            region.Height < 2)
        {
            throw new InvalidOperationException("聊天区域无效，请重新选择聊天区域。");
        }

        Rect virtualScreen = new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.Contains(region))
        {
            throw new InvalidOperationException("聊天区域不在当前屏幕范围内，请重新选择聊天区域。");
        }
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
