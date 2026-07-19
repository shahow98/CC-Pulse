using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ClaudeMonitor.Services;
using ClaudeMonitor.UI;

namespace ClaudeMonitor;

/// <summary>
/// Main application class. Manages the lifecycle of all components:
/// SessionManager, HookServer, TrayManager, and StatusWindow.
///
/// Also supports CLI sub-commands for hook operations (replaces .cmd scripts):
///   ClaudeMonitor.exe hook &lt;endpoint&gt;        — send status update to HookServer
///   ClaudeMonitor.exe configure-hooks            — add CC-Pulse hooks to settings.json
///   ClaudeMonitor.exe remove-hooks               — remove CC-Pulse hooks from settings.json
///   ClaudeMonitor.exe stop-process               — stop running ClaudeMonitor process
/// </summary>
public partial class App : System.Windows.Application
{
    private SessionManager? _sessionManager;
    private HookServer? _hookServer;
    private TrayManager? _trayManager;
    private StatusWindow? _statusWindow;

    private static readonly string ClaudeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude");

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check for CLI sub-commands
        if (e.Args.Length > 0)
        {
            HandleCliCommand(e.Args);
            Shutdown();
            return;
        }

        // Check if Claude Code is installed
        if (!IsClaudeCodeInstalled())
        {
            var result = System.Windows.MessageBox.Show(
                Lang.Get("MsgClaudeNotFoundBody"),
                Lang.Get("MsgClaudeNotFoundTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }
        }

        // Auto-configure hooks on first launch (or if not yet configured)
        EnsureHooksConfigured();

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

    /// <summary>
    /// Check whether Claude Code appears to be installed by looking for
    /// the ~/.claude/ directory and settings.json.
    /// </summary>
    private static bool IsClaudeCodeInstalled()
    {
        // ~/.claude/ directory is the primary indicator
        if (Directory.Exists(ClaudeDir))
            return true;

        // Also check if claude command is in PATH
        try
        {
            var claudePath = FindInPath("claude.cmd") ?? FindInPath("claude.exe");
            if (claudePath != null)
                return true;
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>
    /// Find an executable in the system PATH.
    /// </summary>
    private static string? FindInPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in paths)
        {
            try
            {
                var fullPath = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch { /* ignore invalid paths */ }
        }

        return null;
    }

    /// <summary>
    /// Ensure CC-Pulse hooks are configured in Claude Code settings.
    /// Runs automatically on first launch or if hooks are missing.
    /// Also migrates legacy shell-form hooks to exec form with the hook proxy.
    /// </summary>
    private static void EnsureHooksConfigured()
    {
        try
        {
            // Check if hooks need migration from legacy shell form to exec form
            var needsMigration = File.Exists(SettingsPath) && HookConfigurator.AreHooksConfigured()
                && HookConfigurator.UsesLegacyFormat();

            if (needsMigration)
            {
                // Remove old hooks and reconfigure with new format
                HookConfigurator.Remove();
            }
            else if (File.Exists(SettingsPath) && HookConfigurator.AreHooksConfigured())
            {
                return; // Already configured with current format, nothing to do
            }

            // Get the exe path for hook commands
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "ClaudeMonitor.exe");

            var exitCode = HookConfigurator.Configure(exePath);

            if (exitCode == 0)
            {
                // Success — no need to bother the user
                Debug.WriteLine("CC-Pulse hooks auto-configured successfully.");
            }
            else
            {
                System.Windows.MessageBox.Show(
                    Lang.Get("MsgHookConfigFailedBody", SettingsPath),
                    Lang.Get("MsgHookConfigFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                Lang.Get("MsgHookConfigErrorBody", ex.Message),
                Lang.Get("MsgHookConfigErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Route CLI sub-commands to their handlers.
    /// </summary>
    private void HandleCliCommand(string[] args)
    {
        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "hook":
                var endpoint = args.Length > 1 ? args[1] : "idle";
                Environment.ExitCode = HookRunner.Run(endpoint);
                break;

            case "configure-hooks":
                var exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppContext.BaseDirectory, "ClaudeMonitor.exe");
                Environment.ExitCode = HookConfigurator.Configure(exePath);
                break;

            case "remove-hooks":
                Environment.ExitCode = HookConfigurator.Remove();
                break;

            case "stop-process":
                Environment.ExitCode = StopProcess();
                break;

            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                Console.Error.WriteLine("Usage: ClaudeMonitor.exe [hook <endpoint>|configure-hooks|remove-hooks|stop-process]");
                Environment.ExitCode = 1;
                break;
        }
    }

    /// <summary>
    /// Stop any running ClaudeMonitor process (excluding the current one).
    /// Replaces stop-process.cmd which used taskkill.
    /// </summary>
    private static int StopProcess()
    {
        try
        {
            var currentId = Environment.ProcessId;
            var processes = Process.GetProcessesByName("ClaudeMonitor")
                .Where(p => p.Id != currentId)
                .ToArray();

            if (processes.Length == 0)
                return 0;

            foreach (var proc in processes)
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch { /* ignore permission/already-exited errors */ }
                finally
                {
                    proc.Dispose();
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to stop process: {ex.Message}");
            return 1;
        }
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
        AppSettings.Instance.Dispose();

        base.OnExit(e);
    }
}
