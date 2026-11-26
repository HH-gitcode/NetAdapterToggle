using System.IO;
using System.Text.Json;

namespace NetAdapterToggle.Services;

public sealed class AppSettings
{
    public List<string> PinnedAdapters { get; set; } = [];
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool OnlyPhysicalByDefault { get; set; } = false;
}

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "NetAdapterToggle");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Ignore persistence errors
        }
    }
}
