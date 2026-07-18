using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeMonitor.Models;
using ClaudeMonitor.Services;

namespace ClaudeMonitor.UI;

/// <summary>
/// Floating topmost window that displays the status of all Claude Code sessions.
/// </summary>
public partial class StatusWindow : Window
{
    private readonly SessionManager _sessionManager;
    private readonly ObservableCollection<SessionInfo> _sessions;
    private bool _isHidden;

    public StatusWindow(SessionManager sessionManager)
    {
        InitializeComponent();

        _sessionManager = sessionManager;
        _sessions = new ObservableCollection<SessionInfo>();

        SessionList.ItemsSource = _sessions;

        _sessionManager.StatusChanged += OnStatusChanged;
        _sessionManager.SessionsChanged += OnSessionsChanged;

        // Position at top-right corner of the primary screen
        PositionWindow();

        UpdateEmptyState();
    }

    /// <summary>Position the window at the top-right corner with some margin.</summary>
    private void PositionWindow()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 16;
        Top = screen.Top + 16;
    }

    private void OnStatusChanged(object? sender, SessionStatusChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Update existing item or add new one
            var existing = _sessions.FirstOrDefault(s => s.SessionId == e.SessionId);
            if (existing != null)
            {
                existing.Status = e.NewStatus;
                existing.LastUpdated = e.Session.LastUpdated;
            }
        });
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Sync the observable collection with the session manager's state
            var currentSessions = _sessionManager.GetAllSessions();

            // Remove sessions that no longer exist
            var currentIds = currentSessions.Select(s => s.SessionId).ToHashSet();
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                if (!currentIds.Contains(_sessions[i].SessionId))
                    _sessions.RemoveAt(i);
            }

            // Add or update sessions
            foreach (var session in currentSessions)
            {
                var existing = _sessions.FirstOrDefault(s => s.SessionId == session.SessionId);
                if (existing == null)
                {
                    _sessions.Add(session);
                }
                else
                {
                    existing.Status = session.Status;
                    existing.LastUpdated = session.LastUpdated;
                    existing.DisplayName = session.DisplayName;
                    existing.ProjectPath = session.ProjectPath;
                }
            }

            UpdateEmptyState();
        });
    }

    private void UpdateEmptyState()
    {
        EmptyMessage.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionList.Visibility = _sessions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Toggle window visibility (minimize to tray / restore).</summary>
    public void ToggleVisibility()
    {
        if (_isHidden)
        {
            Show();
            _isHidden = false;
        }
        else
        {
            Hide();
            _isHidden = true;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Allow dragging the window
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Minimize to tray instead of closing
        Hide();
        _isHidden = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Keep on top but don't steal focus aggressively
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _sessionManager.StatusChanged -= OnStatusChanged;
        _sessionManager.SessionsChanged -= OnSessionsChanged;
        base.OnClosing(e);
    }
}

/// <summary>Converts SessionStatus to a Brush color for the indicator circle.</summary>
public class StatusToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(System.Windows.Media.Color.FromRgb(46, 204, 113));
    private static readonly SolidColorBrush YellowBrush = new(System.Windows.Media.Color.FromRgb(241, 196, 15));
    private static readonly SolidColorBrush RedBrush = new(System.Windows.Media.Color.FromRgb(231, 76, 60));

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is SessionStatus status)
        {
            return status switch
            {
                SessionStatus.Busy => YellowBrush,
                SessionStatus.Interactive => RedBrush,
                _ => GreenBrush
            };
        }
        return GreenBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Converts SessionStatus to a human-readable text string.</summary>
public class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is SessionStatus status)
        {
            return status switch
            {
                SessionStatus.Idle => "Idle",
                SessionStatus.Busy => "Working…",
                SessionStatus.Interactive => "Waiting for input",
                _ => "Unknown"
            };
        }
        return "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
