using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Manages the system tray icon. Updates icon color based on aggregate session status.
/// Provides right-click context menu with Show/Hide, Language, and Exit options.
/// </summary>
public class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SessionManager _sessionManager;
    private bool _disposed;

    /// <summary>Raised when the user clicks "Exit" in the context menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user clicks "Show/Hide Window" in the context menu.</summary>
    public event EventHandler? ToggleWindowRequested;

    public TrayManager(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _sessionManager.SessionsChanged += OnSessionsChanged;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon("green.ico"),
            Text = Lang.Get("TrayNoSessions"),
            Visible = true
        };

        BuildContextMenu();

        // Double-click toggles window visibility
        _notifyIcon.DoubleClick += (_, _) => ToggleWindowRequested?.Invoke(this, EventArgs.Empty);

        // Listen for language changes to rebuild menu
        AppSettings.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Build (or rebuild) the context menu with localized strings.</summary>
    private void BuildContextMenu()
    {
        var contextMenu = new ContextMenuStrip();

        var toggleItem = new ToolStripMenuItem(Lang.Get("TrayShowHide"));
        toggleItem.Click += (_, _) => ToggleWindowRequested?.Invoke(this, EventArgs.Empty);
        contextMenu.Items.Add(toggleItem);

        // Language submenu
        var languageItem = new ToolStripMenuItem(Lang.Get("TrayLanguage"));

        var englishItem = new ToolStripMenuItem("English");
        englishItem.Click += (_, _) => AppSettings.Instance.Language = "en";
        languageItem.DropDownItems.Add(englishItem);

        var chineseItem = new ToolStripMenuItem("简体中文");
        chineseItem.Click += (_, _) => AppSettings.Instance.Language = "zh-CN";
        languageItem.DropDownItems.Add(chineseItem);

        contextMenu.Items.Add(languageItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem(Lang.Get("TrayExit"));
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        contextMenu.Items.Add(exitItem);

        // Update check marks for current language
        UpdateLanguageCheckMarks(languageItem);

        // Replace the old menu
        var oldMenu = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = contextMenu;
        oldMenu?.Dispose();
    }

    /// <summary>Set the check mark on the current language item.</summary>
    private void UpdateLanguageCheckMarks(ToolStripMenuItem languageItem)
    {
        var currentLang = AppSettings.Instance.Language;
        foreach (ToolStripMenuItem item in languageItem.DropDownItems)
        {
            item.Checked = (item.Text == "English" && currentLang == "en")
                        || (item.Text == "简体中文" && currentLang == "zh-CN");
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        UpdateIcon();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Rebuild the context menu with new language strings
        BuildContextMenu();
        // Update tooltip text
        UpdateIcon();
    }

    /// <summary>Update the tray icon and tooltip based on aggregate status.</summary>
    public void UpdateIcon()
    {
        var status = _sessionManager.AggregateStatus;
        var count = _sessionManager.SessionCount;

        _notifyIcon.Icon = status switch
        {
            SessionStatus.Interactive => LoadIcon("red.ico"),
            SessionStatus.Busy => LoadIcon("yellow.ico"),
            _ => LoadIcon("green.ico")
        };

        var statusText = status switch
        {
            SessionStatus.Busy => Lang.Get("StatusBusy"),
            SessionStatus.Interactive => Lang.Get("StatusInteractive"),
            _ => Lang.Get("StatusIdle")
        };

        _notifyIcon.Text = count switch
        {
            0 => Lang.Get("TrayNoSessions"),
            1 => Lang.Get("TraySessionStatus", statusText, 1),
            _ => Lang.Get("TraySessionStatusPlural", statusText, count)
        };
    }

    /// <summary>Load an icon from embedded resources or the Assets/Icons directory.</summary>
    private static System.Drawing.Icon LoadIcon(string iconName)
    {
        // Try loading from the Assets/Icons directory next to the executable
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var filePath = Path.Combine(exeDir, "Assets", "Icons", iconName);

        if (File.Exists(filePath))
        {
            return new System.Drawing.Icon(filePath);
        }

        // Fallback: try loading from embedded resources
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"ClaudeMonitor.Assets.Icons.{iconName.Replace('.', '_')}";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            return new System.Drawing.Icon(stream);
        }

        // Last resort: use SystemIcons.Information
        return System.Drawing.SystemIcons.Information;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.SessionsChanged -= OnSessionsChanged;
        AppSettings.Instance.LanguageChanged -= OnLanguageChanged;

        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
