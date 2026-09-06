using System;
using System.IO;
using System.Text.Json;

namespace VariLab.Services;

/// <summary>Loads and saves config.json in %AppData%\VariLab\ — the last-used AAVSO Observer
/// Code, and each directory field's own last-used value (Data tab's Target/Output
/// Directories, Batch window's Input/Results Directories), so none of them need re-picking
/// every session.</summary>
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

    // Each directory field remembers its own last-used value independently (not a single
    // shared "last directory") — LastTargetDirectory kept under its original name for
    // backward compatibility with config.json files that already exist on disk.
    public string LastTargetDirectory { get; set; } = "";
    public string LastOutputDirectory { get; set; } = "";
    public string LastBatchInputDirectory { get; set; } = "";
    public string LastBatchResultsDirectory { get; set; } = "";
    public string LastImportFilePath { get; set; } = "";
    public string SkippedUpdateVersion { get; set; } = "";
}
