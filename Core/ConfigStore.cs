using System.IO;
using System.Text;
using System.Text.Json;

namespace OwTranslateLite.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string AppDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OWTranslatorLite");

    public static string SettingsPath { get; } = Path.Combine(AppDirectory, "settings.json");
    public static string RuntimeLogPath { get; } = Path.Combine(AppDirectory, "runtime.log");
    public static string CrashLogPath { get; } = Path.Combine(AppDirectory, "crash.log");

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        Directory.CreateDirectory(AppDirectory);
        if (!File.Exists(SettingsPath))
        {
            Save();
            return;
        }

        try
        {
            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
            Save();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDirectory);
        string json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
    }

    public void ResetUserData()
    {
        Directory.CreateDirectory(AppDirectory);
        Settings = new AppSettings();

        DeleteIfExists(SettingsPath);
        DeleteIfExists(RuntimeLogPath);
        DeleteIfExists(CrashLogPath);

        foreach (string path in Directory.EnumerateFiles(AppDirectory, "diagnostics-*.txt"))
        {
            DeleteIfExists(path);
        }

        Save();
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; the UI reports completion after the fresh settings file is written.
        }
    }
}
