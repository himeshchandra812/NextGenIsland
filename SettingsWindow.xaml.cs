using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NextGenIsland.Services;

namespace NextGenIsland;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = SettingsStore.Load();

        // General
        PauseClipboard.IsChecked = s.ClipboardPaused;
        StartWithWindows.IsChecked = s.StartWithWindows;

        // Notifications
        EnableNotifications.IsChecked = s.NotificationsEnabled;
        QuietModeToggle.IsChecked = s.QuietMode;

        // Duration combo
        SelectDuration(s.NotificationDurationSeconds);

        // Hardware
        BatteryWarnings.IsChecked = s.BatteryWarningsEnabled;
        NetworkMonitoring.IsChecked = s.NetworkMonitoringEnabled;

        // Populate interfaces
        PopulateInterfaces(s.SelectedNetworkInterface);
    }

    private void SelectDuration(int seconds)
    {
        int index = 1; // default 5s
        switch (seconds)
        {
            case 3: index = 0; break;
            case 5: index = 1; break;
            case 8: index = 2; break;
            case 10: index = 3; break;
        }
        DurationCombo.SelectedIndex = index;
    }

    private int GetSelectedDuration()
    {
        if (DurationCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int val))
        {
            return val;
        }
        return 5;
    }

    private void PopulateInterfaces(string currentSelected)
    {
        InterfaceCombo.Items.Clear();
        var autoItem = new ComboBoxItem { Content = "Auto-Detect Active", Tag = "" };
        InterfaceCombo.Items.Add(autoItem);

        var list = NetworkMonitorService.GetAvailableInterfaces();
        ComboBoxItem? selectedItem = autoItem;

        foreach (var name in list)
        {
            var item = new ComboBoxItem { Content = name, Tag = name };
            InterfaceCombo.Items.Add(item);
            if (string.Equals(name, currentSelected, StringComparison.OrdinalIgnoreCase))
            {
                selectedItem = item;
            }
        }

        InterfaceCombo.SelectedItem = selectedItem;
    }

    private string GetSelectedInterface()
    {
        if (InterfaceCombo.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString() ?? "";
        }
        return "";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenNotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        NotificationService.OpenWindowsNotificationSettings();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(new AppSettings
        {
            ClipboardPaused = PauseClipboard.IsChecked == true,
            StartWithWindows = StartWithWindows.IsChecked == true,
            NotificationsEnabled = EnableNotifications.IsChecked == true,
            QuietMode = QuietModeToggle.IsChecked == true,
            NotificationDurationSeconds = GetSelectedDuration(),
            BatteryWarningsEnabled = BatteryWarnings.IsChecked == true,
            NetworkMonitoringEnabled = NetworkMonitoring.IsChecked == true,
            SelectedNetworkInterface = GetSelectedInterface()
        });
        Close();
    }
}
