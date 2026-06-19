using System.Drawing;
using System.Windows;

namespace OwTranslateLite.Core;

public static class ScreenBoundsService
{
    public const double MinimumCaptureWidth = 80;
    public const double MinimumCaptureHeight = 30;

    public static Rect GetVirtualScreenRect()
    {
        Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    public static bool TryClipToVirtualScreen(Rect requested, out Rect clipped)
    {
        clipped = Rect.Empty;
        if (!IsFinite(requested.Left) ||
            !IsFinite(requested.Top) ||
            !IsFinite(requested.Width) ||
            !IsFinite(requested.Height) ||
            requested.Width < 2 ||
            requested.Height < 2)
        {
            return false;
        }

        Rect screen = GetVirtualScreenRect();
        Rect intersection = Rect.Intersect(screen, requested);
        if (intersection.IsEmpty ||
            intersection.Width < MinimumCaptureWidth ||
            intersection.Height < MinimumCaptureHeight)
        {
            return false;
        }

        clipped = intersection;
        return true;
    }

    public static void ValidateCaptureRegion(Rect region)
    {
        if (!IsFinite(region.Left) ||
            !IsFinite(region.Top) ||
            !IsFinite(region.Width) ||
            !IsFinite(region.Height) ||
            region.Width < 2 ||
            region.Height < 2)
        {
            throw new InvalidCaptureRegionException("聊天区域无效，请重新选择聊天区域。");
        }

        Rect virtualScreen = GetVirtualScreenRect();
        if (!virtualScreen.Contains(region))
        {
            throw new InvalidCaptureRegionException(
                $"聊天区域不在当前屏幕范围内，请重新选择聊天区域。屏幕范围：{Format(virtualScreen)}，当前区域：{Format(region)}。");
        }
    }

    public static string Format(Rect rect) =>
        $"{rect.Left:0},{rect.Top:0} {rect.Width:0}x{rect.Height:0}";

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class InvalidCaptureRegionException : InvalidOperationException
{
    public InvalidCaptureRegionException(string message)
        : base(message)
    {
    }
}
