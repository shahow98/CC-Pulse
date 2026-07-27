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
    private double _opacity = 0.9;

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

    /// <summary>Window opacity: 0.1 (nearly invisible) to 1.0 (fully opaque). Default 0.9.</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 1.0);
            if (Math.Abs(_opacity - clamped) < 0.001) return;
            _opacity = clamped;
            Save();
            OpacityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the opacity setting changes.</summary>
    public event EventHandler? OpacityChanged;

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
                }
                if (data?.Opacity.HasValue == true)
                {
                    _opacity = Math.Clamp(data.Opacity.Value, 0.1, 1.0);
                }
                if (data?.Language is not null || data?.Opacity.HasValue == true)
                {
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
            var data = new SettingsData { Language = _language, Opacity = _opacity };
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
        OpacityChanged = null;
    }

    private class SettingsData
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("opacity")]
        public double? Opacity { get; set; }
    }
}
