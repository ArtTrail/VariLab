using System;
using System.IO;
using System.Text.Json;

namespace VariLab.Services;

/// <summary>Loads and saves config.json in %AppData%\VariLab\ — currently just the
/// last-used AAVSO Observer Code, so it doesn't need re-typing every session.</summary>
public static class ConfigService
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VariLab");

    private static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Config] Load failed: {ex.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Config] Save failed: {ex.Message}");
        }
    }
}

public class AppConfig
{
    public string AavsoObserverCode { get; set; } = "";
    public string LastTargetDirectory { get; set; } = "";
}
