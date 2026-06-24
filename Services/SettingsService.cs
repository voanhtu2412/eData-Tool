using System;
using System.IO;
using System.Text.Json;

namespace TaxCodeCollector.Services;

public class ScraperSettings
{
    public string CaptchaProvider { get; set; } = "Không sử dụng";
    public string CaptchaApiKey { get; set; } = string.Empty;
}

public class SettingsService
{
    private static string SettingsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaxCodeCollector");

    private static string SettingsPath => Path.Combine(SettingsFolder, "scraper_settings.json");

    public static ScraperSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<ScraperSettings>(json) ?? new ScraperSettings();
            }
        }
        catch
        {
            // Ignore error and return default
        }
        return new ScraperSettings();
    }

    public static void SaveSettings(ScraperSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore error
        }
    }
}
