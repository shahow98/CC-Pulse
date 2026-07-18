using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Services;

/// <summary>
/// Manages the system tray icon. Updates icon color based on aggregate session status.
/// Provides right-click context menu with Show/Hide and Exit options.
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
            Text = "CC-Pulse: No sessions",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();

        var toggleItem = new ToolStripMenuItem("Show/Hide Window");
        toggleItem.Click += (_, _) => ToggleWindowRequested?.Invoke(this, EventArgs.Empty);
        contextMenu.Items.Add(toggleItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;

        // Double-click toggles window visibility
        _notifyIcon.DoubleClick += (_, _) => ToggleWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
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

        _notifyIcon.Text = count switch
        {
            0 => "CC-Pulse: No sessions",
            1 => $"CC-Pulse: {status} (1 session)",
            _ => $"CC-Pulse: {status} ({count} sessions)"
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

        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
