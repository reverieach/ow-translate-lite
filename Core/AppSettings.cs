using System.Windows;
using System.Text.Json.Serialization;

namespace OwTranslateLite.Core;

public sealed class AppSettings
{
    public bool FirstRun { get; set; } = true;
    public bool ShowQuickStart { get; set; } = true;
    public string OcrEngine { get; set; } = "OneOCR";
    public string OcrLanguage { get; set; } = "auto";
    public string TranslationProvider { get; set; } = "DeepSeek";
    public string ApiUrl { get; set; } = "https://api.deepseek.com";
    [JsonIgnore]
    public string ApiKey { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    [JsonPropertyName("apiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlainTextApiKey { get; set; }
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ReplyTargetLanguage { get; set; } = "auto";
    public bool EnableReplyHotkey { get; set; }
    public string ReplyHotkey { get; set; } = "Ctrl+Shift+Enter";
    public int CaptureIntervalMs { get; set; } = 900;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public double OverlayOpacity { get; set; } = 0.153;
    public double OverlayFontSize { get; set; } = 14.92;
    public bool OverlayClickThrough { get; set; } = true;
    public bool ShowReplyInputBar { get; set; } = true;
    public bool EnableDedupeDebugLog { get; set; }
    public double? OverlayLeft { get; set; } = 42;
    public double? OverlayTop { get; set; } = 151;
    public double? OverlayWidth { get; set; } = 454;
    public double? OverlayHeight { get; set; } = 276;
    public CaptureRegion? CaptureRegion { get; set; } = new()
    {
        Left = 45,
        Top = 398,
        Width = 447,
        Height = 276
    };
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
