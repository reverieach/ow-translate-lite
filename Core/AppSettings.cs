using System.Windows;

namespace OwTranslateLite.Core;

public sealed class AppSettings
{
    public bool FirstRun { get; set; } = true;
    public string OcrEngine { get; set; } = "OneOCR";
    public string OcrLanguage { get; set; } = "en";
    public string TranslationProvider { get; set; } = "Local";
    public string ApiUrl { get; set; } = "https://api.deepseek.com/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-chat";
    public int CaptureIntervalMs { get; set; } = 900;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public double OverlayOpacity { get; set; } = 0.86;
    public double OverlayFontSize { get; set; } = 20;
    public bool OverlayClickThrough { get; set; } = true;
    public string OverlayMode { get; set; } = "Floating";
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double? OverlayWidth { get; set; }
    public double? OverlayHeight { get; set; }
    public CaptureRegion? CaptureRegion { get; set; }
}

public sealed class CaptureRegion
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Rect ToRect() => new(Left, Top, Width, Height);

    public static CaptureRegion FromRect(Rect rect)
    {
        return new CaptureRegion
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height
        };
    }
}
