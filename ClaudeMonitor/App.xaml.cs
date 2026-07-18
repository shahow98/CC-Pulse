using System;
using System.Windows;
using ClaudeMonitor.Services;
using ClaudeMonitor.UI;

namespace ClaudeMonitor;

/// <summary>
/// Main application class. Manages the lifecycle of all components:
/// SessionManager, HookServer, TrayManager, and StatusWindow.
/// </summary>
public partial class App : System.Windows.Application
{
    private SessionManager? _sessionManager;
    private HookServer? _hookServer;
    private TrayManager? _trayManager;
    private StatusWindow? _statusWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize core services
        _sessionManager = new SessionManager();
        _hookServer = new HookServer(_sessionManager);
        _trayManager = new TrayManager(_sessionManager);
        _statusWindow = new StatusWindow(_sessionManager);

        // Wire up tray events
        _trayManager.ExitRequested += OnExitRequested;
        _trayManager.ToggleWindowRequested += OnToggleWindow;

        // Start the HTTP hook server
        _hookServer.Start();

        // Show the status window
        _statusWindow.Show();
    }

    private void OnToggleWindow(object? sender, EventArgs e)
    {
        _statusWindow?.ToggleVisibility();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Clean up resources in reverse order
        _hookServer?.Dispose();
        _trayManager?.Dispose();
        _sessionManager?.Dispose();

        base.OnExit(e);
    }
}
