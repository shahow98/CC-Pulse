using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeMonitor.Services;

/// <summary>
/// Manages persistent application settings stored in ~/.cc-pulse/settings.json.
/// Supports language preference (en / zh-CN) with system locale auto-detection.
/// </summary>
public class AppSettings : IDisposable
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cc-pulse");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static AppSettings? _instance;
    private static readonly object _lock = new();

    private string _language = "en";

    /// <summary>Singleton instance.</summary>
    public static AppSettings Instance
    {
        get
        {
            lock (_lock)
            {
                _instance ??= new AppSettings();
                return _instance;
            }
        }
    }

    /// <summary>Current language code: "en" or "zh-CN".</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            Save();
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the language setting changes.</summary>
    public event EventHandler? LanguageChanged;

    private AppSettings()
    {
        Load();
    }

    /// <summary>Load settings from disk, or auto-detect from system locale.</summary>
    private void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions);
                if (data?.Language is not null)
                {
                    _language = data.Language;
                    return;
                }
            }
        }
        catch
        {
            // If settings file is corrupt, fall through to auto-detect
        }

        // Auto-detect from system locale
        _language = IsChineseSystem() ? "zh-CN" : "en";
    }

    /// <summary>Save current settings to disk.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var data = new SettingsData { Language = _language };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Silently fail — settings are best-effort
        }
    }

    /// <summary>Detect whether the system is running a Chinese locale.</summary>
    private static bool IsChineseSystem()
    {
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        LanguageChanged = null;
    }

    private class SettingsData
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
