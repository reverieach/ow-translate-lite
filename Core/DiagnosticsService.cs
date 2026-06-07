using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace OwTranslateLite.Core;

public sealed class DiagnosticsService
{
    public void OpenAppDirectory()
    {
        Directory.CreateDirectory(ConfigStore.AppDirectory);
        OpenShellPath(ConfigStore.AppDirectory);
    }

    public void OpenRuntimeLog()
    {
        EnsureTextFile(ConfigStore.RuntimeLogPath, "OW Translator Lite runtime log\n");
        OpenShellPath(ConfigStore.RuntimeLogPath);
    }

    public void OpenDedupeLog()
    {
        EnsureTextFile(ConfigStore.DedupeLogPath, "OW Translator Lite dedupe debug log\n");
        OpenShellPath(ConfigStore.DedupeLogPath);
    }

    public string ExportDiagnostics(
        AppSettings settings,
        IEnumerable<string> uiLogLines,
        RuntimeDiagnosticsSnapshot? runtime)
    {
        Directory.CreateDirectory(ConfigStore.AppDirectory);
        string diagnosticsPath = Path.Combine(
            ConfigStore.AppDirectory,
            $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(
            diagnosticsPath,
            BuildDiagnosticsReport(settings, uiLogLines, runtime),
            new UTF8Encoding(false));

        OpenShellPath(ConfigStore.AppDirectory);
        return diagnosticsPath;
    }

    public void AppendRuntimeLog(string line)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.AppDirectory);
            File.AppendAllText(
                ConfigStore.RuntimeLogPath,
                line + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // Runtime logging is diagnostic-only and must not interrupt translation.
        }
    }

    public void AppendDedupeLog(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.AppDirectory);
            File.AppendAllText(
                ConfigStore.DedupeLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Dedupe debug logging is optional and must not affect OCR or translation.
        }
    }

    private static string BuildDiagnosticsReport(
        AppSettings settings,
        IEnumerable<string> uiLogLines,
        RuntimeDiagnosticsSnapshot? runtime)
    {
        StringBuilder builder = new();
        builder.AppendLine("OW Translator Lite Beta Diagnostics");
        builder.AppendLine($"GeneratedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Version: {Assembly.GetExecutingAssembly().GetName().Version}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($".NET: {Environment.Version}");
        builder.AppendLine();
        builder.AppendLine("== Paths ==");
        builder.AppendLine($"AppDirectory: {ConfigStore.AppDirectory}");
        builder.AppendLine($"SettingsPath: {ConfigStore.SettingsPath}");
        builder.AppendLine($"RuntimeLogPath: {ConfigStore.RuntimeLogPath}");
        builder.AppendLine($"CrashLogPath: {ConfigStore.CrashLogPath}");
        builder.AppendLine($"DedupeLogPath: {ConfigStore.DedupeLogPath}");
        builder.AppendLine();
        builder.AppendLine("== Settings ==");
        builder.AppendLine($"OcrEngine: {settings.OcrEngine}");
        builder.AppendLine($"OcrLanguage: {settings.OcrLanguage}");
        builder.AppendLine($"TranslationProvider: {settings.TranslationProvider}");
        builder.AppendLine($"ApiUrl: {settings.ApiUrl}");
        builder.AppendLine($"ApiKeyConfigured: {!string.IsNullOrWhiteSpace(settings.ApiKey)}");
        builder.AppendLine("ApiKey: [redacted]");
        builder.AppendLine($"Model: {settings.Model}");
        builder.AppendLine($"ReplyTargetLanguage: {settings.ReplyTargetLanguage}");
        builder.AppendLine($"EnableReplyHotkey: {settings.EnableReplyHotkey}");
        builder.AppendLine($"ReplyHotkey: {settings.ReplyHotkey}");
        builder.AppendLine($"CaptureIntervalMs: {settings.CaptureIntervalMs}");
        builder.AppendLine($"RequestTimeoutSeconds: {settings.RequestTimeoutSeconds}");
        builder.AppendLine($"OverlayOpacity: {settings.OverlayOpacity:0.###}");
        builder.AppendLine($"OverlayFontSize: {settings.OverlayFontSize:0.###}");
        builder.AppendLine($"OverlayClickThrough: {settings.OverlayClickThrough}");
        builder.AppendLine($"EnableDedupeDebugLog: {settings.EnableDedupeDebugLog}");
        builder.AppendLine($"OverlayBounds: {FormatBounds(settings)}");
        builder.AppendLine($"CaptureRegion: {FormatRegion(settings.CaptureRegion)}");
        if (runtime is not null)
        {
            builder.AppendLine();
            builder.AppendLine("== Runtime State ==");
            builder.AppendLine($"IsRunning: {runtime.IsRunning}");
            builder.AppendLine($"RunGeneration: {runtime.RunGeneration}");
            builder.AppendLine($"OverlayRecordCount: {runtime.OverlayRecordCount}");
            builder.AppendLine($"TranslationQueueQueued: {runtime.TranslationQueue.QueuedCount}");
            builder.AppendLine($"TranslationQueueActive: {runtime.TranslationQueue.ActiveCount}");
            builder.AppendLine($"LastApiLatencyMs: {runtime.TranslationQueue.LastApiLatencyMs}");
            builder.AppendLine($"LastTranslationFailure: {Limit(runtime.TranslationQueue.LastFailure, 180)}");
            builder.AppendLine($"TranslationQueueUpdatedAt: {FormatTimestamp(runtime.TranslationQueue.LastUpdatedAt)}");
        }

        builder.AppendLine();
        builder.AppendLine("== Current UI Log ==");
        foreach (string line in uiLogLines.TakeLast(80))
        {
            builder.AppendLine(line);
        }

        AppendFileTail(builder, ConfigStore.RuntimeLogPath, "Runtime Log Tail", 120);
        AppendFileTail(builder, ConfigStore.CrashLogPath, "Crash Log Tail", 120);
        AppendFileTail(builder, ConfigStore.DedupeLogPath, "Dedupe Debug Log Tail", 200);

        return builder.ToString();
    }

    private static string FormatBounds(AppSettings settings)
    {
        if (settings.OverlayLeft is not double left ||
            settings.OverlayTop is not double top ||
            settings.OverlayWidth is not double width ||
            settings.OverlayHeight is not double height)
        {
            return "not saved";
        }

        return $"{left:0.##},{top:0.##} {width:0.##}x{height:0.##}";
    }

    private static string FormatRegion(CaptureRegion? region) =>
        region is null
            ? "not selected"
            : $"{region.Left:0.##},{region.Top:0.##} {region.Width:0.##}x{region.Height:0.##}";

    private static string FormatTimestamp(DateTime? timestamp) =>
        timestamp is null ? "never" : timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static string Limit(string value, int maxLength)
    {
        string trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }

    private static void AppendFileTail(StringBuilder builder, string path, string title, int maxLines)
    {
        builder.AppendLine();
        builder.AppendLine($"== {title} ==");
        if (!File.Exists(path))
        {
            builder.AppendLine("not found");
            return;
        }

        try
        {
            foreach (string line in File.ReadLines(path, Encoding.UTF8).TakeLast(maxLines))
            {
                builder.AppendLine(line);
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"unavailable: {ex.Message}");
        }
    }

    private static void EnsureTextFile(string path, string initialContent)
    {
        Directory.CreateDirectory(ConfigStore.AppDirectory);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, initialContent, new UTF8Encoding(false));
        }
    }

    private static void OpenShellPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
    }
}
