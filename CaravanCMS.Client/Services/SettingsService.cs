using System.IO;
using System.Text.Json;

namespace CaravanCMS.Client.Services;

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CaravanCMS",
        "client-settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ClientSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new ClientSettings();
            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ClientSettings>(json) ?? new ClientSettings();
        }
        catch { return new ClientSettings(); }
    }

    public void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}

public class ClientSettings
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = "caravanland-internal-api-key-2024";
}
