using System.Text.Json;

namespace PureUninstall.App.Services;

/// <summary>Persisted user settings.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "Default";          // Default / Light / Dark
    public bool AggressiveScan { get; set; }
    public bool BackupBeforeDelete { get; set; } = true;
    public bool QuietUninstall { get; set; } = true;
    public string UpdateCheckUrl { get; set; } = "https://api.github.com/repos/sqdcz/PureUninstall/releases/latest";
}

public static class SettingsService
{
    // NOTE: don't name this field "File" - it shadows the System.IO.File type.
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PureUninstall", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // corrupted settings -> defaults
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // settings are best-effort
        }
    }
}
