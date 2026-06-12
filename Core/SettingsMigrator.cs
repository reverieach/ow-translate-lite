namespace OwTranslateLite.Core;

public static class SettingsMigrator
{
    public static SettingsMigrationResult MigrateAfterLoad(AppSettings settings)
    {
        bool changed = false;
        bool migratedSecret = false;

        if (!string.IsNullOrWhiteSpace(settings.LegacyPlainTextApiKey))
        {
            settings.ApiKey = settings.LegacyPlainTextApiKey.Trim();
            settings.ApiKeyProtected = SecretStore.ProtectIfChanged(settings.ApiKey, settings.ApiKeyProtected);
            settings.LegacyPlainTextApiKey = null;
            changed = true;
            migratedSecret = true;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ApiKeyProtected) &&
                 SecretStore.TryUnprotect(settings.ApiKeyProtected, out string apiKey))
        {
            settings.ApiKey = apiKey;
        }

        changed |= Normalize(settings);
        return new SettingsMigrationResult(changed, migratedSecret);
    }

    public static void PrepareForSave(AppSettings settings)
    {
        settings.ApiKey = settings.ApiKey.Trim();
        settings.ApiKeyProtected = SecretStore.ProtectIfChanged(settings.ApiKey, settings.ApiKeyProtected);
        settings.LegacyPlainTextApiKey = null;
        _ = Normalize(settings);
    }

    private static bool Normalize(AppSettings settings)
    {
        bool changed = false;
        if (!string.Equals(settings.OcrEngine, "OneOCR", StringComparison.Ordinal))
        {
            settings.OcrEngine = "OneOCR";
            changed = true;
        }

        if (!string.Equals(settings.OcrLanguage, "auto", StringComparison.Ordinal))
        {
            settings.OcrLanguage = "auto";
            changed = true;
        }

        if (settings.TranslationProvider is not ("DeepSeek" or "OpenAI Compatible"))
        {
            settings.TranslationProvider = "DeepSeek";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
        {
            settings.ApiUrl = "https://api.deepseek.com";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            settings.Model = "deepseek-v4-flash";
            changed = true;
        }

        string replyTarget = settings.ReplyTargetLanguage is "en" or "ja" or "ko" ? settings.ReplyTargetLanguage : "auto";
        if (!string.Equals(settings.ReplyTargetLanguage, replyTarget, StringComparison.Ordinal))
        {
            settings.ReplyTargetLanguage = replyTarget;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.ReplyHotkey))
        {
            settings.ReplyHotkey = "Ctrl+Shift+Enter";
            changed = true;
        }

        settings.CaptureIntervalMs = Clamp(settings.CaptureIntervalMs, 250, 3000, ref changed);
        settings.RequestTimeoutSeconds = Clamp(settings.RequestTimeoutSeconds, 5, 90, ref changed);
        settings.OverlayOpacity = Clamp(settings.OverlayOpacity, 0, 1, ref changed);
        settings.OverlayFontSize = Clamp(settings.OverlayFontSize, 12, 36, ref changed);
        settings.OverlayLeft = NormalizeNullableFinite(settings.OverlayLeft, ref changed);
        settings.OverlayTop = NormalizeNullableFinite(settings.OverlayTop, ref changed);
        settings.OverlayWidth = NormalizeNullableFinite(settings.OverlayWidth, ref changed, minimum: 260);
        settings.OverlayHeight = NormalizeNullableFinite(settings.OverlayHeight, ref changed, minimum: 100);

        if (settings.CaptureRegion is not null)
        {
            changed |= NormalizeCaptureRegion(settings.CaptureRegion);
        }

        return changed;
    }

    private static bool NormalizeCaptureRegion(CaptureRegion region)
    {
        bool changed = false;
        region.Left = NormalizeFinite(region.Left, 0, ref changed);
        region.Top = NormalizeFinite(region.Top, 0, ref changed);
        region.Width = NormalizeFinite(region.Width, 320, ref changed, minimum: 80);
        region.Height = NormalizeFinite(region.Height, 180, ref changed, minimum: 60);
        return changed;
    }

    private static int Clamp(int value, int minimum, int maximum, ref bool changed)
    {
        int clamped = Math.Clamp(value, minimum, maximum);
        if (clamped == value)
        {
            return value;
        }

        changed = true;
        return clamped;
    }

    private static double Clamp(double value, double minimum, double maximum, ref bool changed)
    {
        double clamped = Math.Clamp(IsFinite(value) ? value : minimum, minimum, maximum);
        if (Math.Abs(clamped - value) < 0.0001)
        {
            return value;
        }

        changed = true;
        return clamped;
    }

    private static double? NormalizeNullableFinite(
        double? value,
        ref bool changed,
        double minimum = double.NegativeInfinity)
    {
        if (value is null)
        {
            return null;
        }

        double current = value.Value;
        if (!IsFinite(current))
        {
            changed = true;
            return null;
        }

        if (current < minimum)
        {
            changed = true;
            return minimum;
        }

        return value;
    }

    private static double NormalizeFinite(
        double value,
        double fallback,
        ref bool changed,
        double minimum = double.NegativeInfinity)
    {
        double next = IsFinite(value) ? value : fallback;
        next = Math.Max(next, minimum);
        if (Math.Abs(next - value) < 0.0001)
        {
            return value;
        }

        changed = true;
        return next;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed record SettingsMigrationResult(bool Changed, bool MigratedSecret);
