using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using OwTranslateLite.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinLanguage = Windows.Globalization.Language;

namespace OwTranslateLite.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en-US",
        ["ja"] = "ja-JP",
        ["ko"] = "ko-KR",
        ["ru"] = "ru-RU",
        ["zh"] = "zh-Hans-CN"
    };

    public string Name => "Windows OCR";

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(Bitmap bitmap, string languageCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Bitmap prepared = PrepareForOcr(bitmap);
        using MemoryStream stream = new();
        prepared.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;

        using IRandomAccessStream randomAccessStream = stream.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        if (IsAutoLanguage(languageCode))
        {
            List<OcrTextLine> merged = [];
            foreach (string code in new[] { "en", "ja", "ko", "ru", "zh" })
            {
                IReadOnlyList<OcrTextLine> lines = await RecognizeWithLanguageAsync(softwareBitmap, code);
                foreach (OcrTextLine line in lines)
                {
                    if (!merged.Any(existing => IsSameLine(existing, line)))
                    {
                        merged.Add(line);
                    }
                }
            }

            return merged.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToList();
        }

        return await RecognizeWithLanguageAsync(softwareBitmap, languageCode);
    }

    private static async Task<IReadOnlyList<OcrTextLine>> RecognizeWithLanguageAsync(SoftwareBitmap softwareBitmap, string languageCode)
    {
        OcrEngine? engine = CreateEngine(languageCode);
        if (engine is null)
        {
            return Array.Empty<OcrTextLine>();
        }

        OcrResult result = await engine.RecognizeAsync(softwareBitmap);
        return result.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Words.Count > 0)
            .Select(line =>
            {
                Windows.Foundation.Rect box = MergeWords(line);
                return new OcrTextLine(CleanupSpacing(line.Text, languageCode), new Rect(box.X, box.Y, box.Width, box.Height));
            })
            .ToList();
    }

    private static bool IsAutoLanguage(string languageCode)
    {
        return string.IsNullOrWhiteSpace(languageCode) ||
               languageCode.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               languageCode.Equals("mixed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameLine(OcrTextLine left, OcrTextLine right)
    {
        string leftText = NormalizeForCompare(left.Text);
        string rightText = NormalizeForCompare(right.Text);
        if (leftText.Length > 0 && leftText == rightText)
        {
            return true;
        }

        double centerDistance = Math.Abs(left.Bounds.Top + left.Bounds.Height / 2 - (right.Bounds.Top + right.Bounds.Height / 2));
        double leftEdgeDistance = Math.Abs(left.Bounds.Left - right.Bounds.Left);
        return centerDistance < 8 && leftEdgeDistance < 24;
    }

    private static string NormalizeForCompare(string text)
    {
        string lower = text.ToLowerInvariant();
        lower = System.Text.RegularExpressions.Regex.Replace(lower, @"[^\p{L}\p{N}]+", "");
        return lower.Trim();
    }

    private static OcrEngine? CreateEngine(string languageCode)
    {
        try
        {
            if (LanguageMap.TryGetValue(languageCode, out string? tag))
            {
                WinLanguage language = new(tag);
                if (OcrEngine.IsLanguageSupported(language))
                {
                    return OcrEngine.TryCreateFromLanguage(language);
                }
            }

            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            return null;
        }
    }

    private static Windows.Foundation.Rect MergeWords(OcrLine line)
    {
        double left = line.Words.Min(word => word.BoundingRect.X);
        double top = line.Words.Min(word => word.BoundingRect.Y);
        double right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        double bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
        return new Windows.Foundation.Rect(left, top, right - left, bottom - top);
    }

    private static Bitmap PrepareForOcr(Bitmap source)
    {
        Bitmap result = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(result);
        using ImageAttributes attributes = new();
        float[][] matrix =
        [
            [1.25f, 0, 0, 0, 0],
            [0, 1.25f, 0, 0, 0],
            [0, 0, 1.25f, 0, 0],
            [0, 0, 0, 1, 0],
            [0.02f, 0.02f, 0.02f, 0, 1]
        ];
        attributes.SetColorMatrix(new ColorMatrix(matrix));
        attributes.SetGamma(1.08f);
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }

    private static string CleanupSpacing(string text, string languageCode)
    {
        if (languageCode.Equals("ja", StringComparison.OrdinalIgnoreCase) || languageCode.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(text, @"([\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])\s+([\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])", "$1$2");
        }

        return text.Trim();
    }
}
