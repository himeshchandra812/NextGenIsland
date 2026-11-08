using System.Diagnostics;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NextGenIsland.Services;

public sealed record IslandNotification(
    string Id,
    string AppName,
    string Title,
    string Message,
    DateTime Timestamp,
    string? AppLogoUri = null,
    bool IsSystemAlert = false
);

public sealed class NotificationService : IDisposable
{
    private readonly List<IslandNotification> _history = new();
    private readonly Queue<IslandNotification> _queue = new();
    private readonly Dictionary<string, DateTime> _recentDeduplication = new();
    private readonly DispatcherTimer _dismissTimer = new();
    private UserNotificationListener? _listener;
    private bool _isListening;
    private bool _disposed;

    public event Action<IslandNotification?>? ActiveNotificationChanged;
    public event Action? HistoryChanged;
    public event Action<bool>? WindowsListenerStatusChanged;

    public bool IsWindowsListenerAllowed { get; private set; }
    public bool NotificationsEnabled { get; set; } = true;
    public bool QuietMode { get; set; }
    public int DisplayDurationSeconds { get; set; } = 5;
    public IslandNotification? CurrentNotification { get; private set; }
    public IReadOnlyList<IslandNotification> History => _history;
    public int QueueCount => _queue.Count;

    public NotificationService()
    {
        _dismissTimer.Tick += OnDismissTimerTick;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = _listener.GetAccessStatus();

            if (status == UserNotificationListenerAccessStatus.Unspecified)
            {
                status = await _listener.RequestAccessAsync();
            }

            IsWindowsListenerAllowed = (status == UserNotificationListenerAccessStatus.Allowed);
            WindowsListenerStatusChanged?.Invoke(IsWindowsListenerAllowed);

            if (IsWindowsListenerAllowed)
            {
                _listener.NotificationChanged += Listener_NotificationChanged;
                _isListening = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("UserNotificationListener initialization error: " + ex.Message);
            IsWindowsListenerAllowed = false;
            WindowsListenerStatusChanged?.Invoke(false);
        }
    }

    public static void OpenWindowsNotificationSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Failed to open Windows Notification settings: " + ex.Message);
        }
    }

    private async void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (!NotificationsEnabled) return;

        try
        {
            if (args.ChangeKind == UserNotificationChangedKind.Added)
            {
                var notification = sender.GetNotification(args.UserNotificationId);
                if (notification != null)
                {
                    ProcessToastNotification(notification);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error reading toast notification: " + ex.Message);
        }
    }

    private void ProcessToastNotification(UserNotification notification)
    {
        try
        {
            var appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "Windows App";
            var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding == null) return;

            var textElements = binding.GetTextElements();
            string title = textElements.Count > 0 ? textElements[0].Text : "Notification";
            string body = textElements.Count > 1 ? textElements[1].Text : "";

            PostNotification(appName, title, body, isSystemAlert: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("ProcessToastNotification error: " + ex);
        }
    }

    public void PostNotification(string appName, string title, string message, bool isSystemAlert = false)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message)) return;

        // Deduplication within 8 seconds
        string key = $"{appName.Trim().ToLowerInvariant()}:{title.Trim().ToLowerInvariant()}:{message.Trim().ToLowerInvariant()}";
        var now = DateTime.UtcNow;
        if (_recentDeduplication.TryGetValue(key, out var lastTime) && (now - lastTime).TotalSeconds < 8)
        {
            return;
        }
        _recentDeduplication[key] = now;

        // Clean up old deduplication entries
        if (_recentDeduplication.Count > 100)
        {
            var cutoff = now.AddSeconds(-20);
            var toRemove = _recentDeduplication.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
            foreach (var k in toRemove) _recentDeduplication.Remove(k);
        }

        var item = new IslandNotification(
            Id: Guid.NewGuid().ToString("N")[..8],
            AppName: appName,
            Title: title,
            Message: message,
            Timestamp: DateTime.Now,
            IsSystemAlert: isSystemAlert
        );

        // Always add to history
        _history.Insert(0, item);
        if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
        HistoryChanged?.Invoke();

        // If Quiet Mode is active or notifications are disabled, don't interrupt screen
        if (QuietMode || !NotificationsEnabled)
        {
            return;
        }

        // Enqueue and display
        _queue.Enqueue(item);
        if (CurrentNotification == null)
        {
            ShowNextNotification();
        }
    }

    private void ShowNextNotification()
    {
        if (_queue.Count > 0)
        {
            CurrentNotification = _queue.Dequeue();
            ActiveNotificationChanged?.Invoke(CurrentNotification);

            _dismissTimer.Stop();
            _dismissTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, DisplayDurationSeconds));
            _dismissTimer.Start();
        }
        else
        {
            CurrentNotification = null;
            ActiveNotificationChanged?.Invoke(null);
            _dismissTimer.Stop();
        }
    }

    private void OnDismissTimerTick(object? sender, EventArgs e)
    {
        _dismissTimer.Stop();
        ShowNextNotification();
    }

    public void DismissCurrent()
    {
        _dismissTimer.Stop();
        ShowNextNotification();
    }

    public void Dismiss(string id)
    {
        var item = _history.FirstOrDefault(n => n.Id == id);
        if (item != null)
        {
            _history.Remove(item);
            HistoryChanged?.Invoke();
        }
        if (CurrentNotification?.Id == id)
        {
            DismissCurrent();
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
        HistoryChanged?.Invoke();
        if (CurrentNotification != null)
        {
            DismissCurrent();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dismissTimer.Stop();
        if (_isListening && _listener != null)
        {
            try { _listener.NotificationChanged -= Listener_NotificationChanged; } catch { }
        }
    }
}
