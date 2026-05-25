using System.IO;
using System.Text.Json;

namespace CaravanCMS.Admin.Services;

/// <summary>
/// Persists application settings to a JSON file in %LOCALAPPDATA%\CaravanCMS\.
/// Settings survive app restarts and Windows updates.
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CaravanCMS",
        "admin-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}

/// <summary>User-configurable settings for the Admin application.</summary>
public class AppSettings
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = "caravanland-internal-api-key-2024";
    public string CaravanHistoryPath { get; set; } = @"C:\Users\info\OneDrive - Caravanland\Documents\Caravan History";
    public string ApiExePath { get; set; } = @"CaravanCMS.Api.exe";
    public DateTime? LastImportAt { get; set; }
}
