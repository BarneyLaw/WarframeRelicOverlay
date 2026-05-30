namespace WarframeRelicOverlay.Core;

using System.Diagnostics;
using System.IO;
using System.Text.Json;

/// <summary>
/// Represents the application settings loaded from a JSON file. 
/// Provides validation and clamping of values to ensure they are within reasonable ranges. 
/// Also handles loading defaults when the file is missing or corrupt, and saving back to disk.
/// </summary>
public sealed class AppSettings
{
    public string DetectionMode { get; set; } = "EELog";
    public string? EeLogPathOverride { get; set; } = null;
    public int DetectionIntervalMs { get; set; } = 250;
    public int DetectionStreak { get; set; } = 2;
    public int StabilizationDelayMs { get; set; } = 1200;
    public int PriceCacheTtlMinutes { get; set; } = 5;
    public double OverlayOpacity { get; set; } = 1.0;
    public int PriceFontSizeOverride { get; set; } = 0;
    public string ToggleHotkey { get; set; } = "Shift+F9";
    public bool DebugMode { get; set; } = false;
    public bool SaveDebugImages { get; set; } = false;

    private static readonly JsonSerializerOptions _loadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions _saveOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Validates the settings, ensuring that values are within expected ranges and that strings are not null or empty where required.
    /// Returns a list of warnings for any values that were out of range and have been clamped, 
    /// or for any unknown options that have been reset to defaults. 
    /// Does not throw exceptions; instead it corrects invalid values and reports them via the returned warnings list.
    /// </summary>
    /// <returns></returns>
    public List<string> Validate()
    {
        // possible future improvement: each invalid setting could be reported as a separate warning, instead of just the first one encountered

        var warnings = new List<string>();

        if (DetectionMode is not ("EELog" or "OCR" or "Manual"))
        {
            warnings.Add($"Unknown DetectionMode '{DetectionMode}', falling back to 'EELog'.");
            DetectionMode = "EELog";
        }

        int clampedInterval = Math.Clamp(DetectionIntervalMs, 100, 1000);
        if (clampedInterval != DetectionIntervalMs)
        {
            warnings.Add($"DetectionIntervalMs {DetectionIntervalMs} out of range [100,1000], clamped to {clampedInterval}.");
            DetectionIntervalMs = clampedInterval;
        }

        int clampedStreak = Math.Clamp(DetectionStreak, 1, 6);
        if (clampedStreak != DetectionStreak)
        {
            warnings.Add($"DetectionStreak {DetectionStreak} out of range [1,6], clamped to {clampedStreak}.");
            DetectionStreak = clampedStreak;
        }

        int clampedDelay = Math.Clamp(StabilizationDelayMs, 0, 2000);
        if (clampedDelay != StabilizationDelayMs)
        {
            warnings.Add($"StabilizationDelayMs {StabilizationDelayMs} out of range [0,2000], clamped to {clampedDelay}.");
            StabilizationDelayMs = clampedDelay;
        }

        int clampedTtl = Math.Clamp(PriceCacheTtlMinutes, 1, 30);
        if (clampedTtl != PriceCacheTtlMinutes)
        {
            warnings.Add($"PriceCacheTtlMinutes {PriceCacheTtlMinutes} out of range [1,30], clamped to {clampedTtl}.");
            PriceCacheTtlMinutes = clampedTtl;
        }

        double clampedOpacity = Math.Clamp(OverlayOpacity, 0.5, 1.0);
        if (clampedOpacity != OverlayOpacity)
        {
            warnings.Add($"OverlayOpacity {OverlayOpacity} out of range [0.5,1.0], clamped to {clampedOpacity}.");
            OverlayOpacity = clampedOpacity;
        }

        if (PriceFontSizeOverride < 0)
        {
            warnings.Add($"PriceFontSizeOverride {PriceFontSizeOverride} is negative, clamped to 0 (auto).");
            PriceFontSizeOverride = 0;
        }
        else if (PriceFontSizeOverride > 0)
        {
            int clampedFont = Math.Clamp(PriceFontSizeOverride, 12, 32);
            if (clampedFont != PriceFontSizeOverride)
            {
                warnings.Add($"PriceFontSizeOverride {PriceFontSizeOverride} out of range [12,32], clamped to {clampedFont}.");
                PriceFontSizeOverride = clampedFont;
            }
        }

        if (string.IsNullOrEmpty(ToggleHotkey))
        {
            warnings.Add("ToggleHotkey is null or empty, falling back to 'Shift+F9'.");
            ToggleHotkey = "Shift+F9";
        }

        return warnings;
    }

    /// <summary>
    /// Loads settings from the specified JSON file. If the file is missing, returns default settings.
    /// If the file is corrupt or contains invalid values, logs warnings and returns defaults or clamped values as appropriate.
    /// Does not throw exceptions. If the file is corrupt, it is renamed to .bak to allow recovery.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();

            string json = File.ReadAllText(path);

            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, _loadOptions);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[AppSettings] Corrupt settings file at '{path}': {ex.Message}. Renaming to .bak and using defaults.");
                try { File.Move(path, path + ".bak", overwrite: true); } catch { }
                return new AppSettings();
            }

            if (settings is null)
                return new AppSettings();

            foreach (string warning in settings.Validate())
                Debug.WriteLine($"[AppSettings] {warning}");

            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] Failed to load settings from '{path}': {ex.Message}. Using defaults.");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves the current settings to the specified JSON file. 
    /// Creates the directory if it does not exist.
    /// Writes to a temporary file first and then moves it to avoid leaving a corrupt file,
    /// if the process is interrupted during writing.
    /// </summary>
    /// <param name="path"></param>
    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        string tmpPath = path + ".tmp";
        string json = JsonSerializer.Serialize(this, _saveOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }
}
