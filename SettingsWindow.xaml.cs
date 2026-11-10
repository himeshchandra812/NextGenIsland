using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NextGenIsland.Services;
using NextGenIsland.Core;
using NextGenIsland.Models;

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

        ShapeCombo.SelectedIndex = s.IslandShape == "Pill" ? 1 : 0;
        ShowLyrics.IsChecked = s.ShowLyrics;

        // Populate interfaces
        PopulateInterfaces(s.SelectedNetworkInterface);
        PopulateCardSettings(s);
    }

    private void PopulateCardSettings(AppSettings settings)
    {
        settings.DisabledCardIds ??= new List<string>();

        CardSettingsPanel.Children.Clear();

        foreach (var card in IslandCardCatalog.All.OrderBy(c => c.DefaultOrder))
        {
            bool isPlanned = card.Status == IslandCardStatus.Planned;
            bool isDisabled = settings.DisabledCardIds.Contains(
                card.Id, StringComparer.OrdinalIgnoreCase);

            var checkBox = new CheckBox
            {
                Content = isPlanned ? $"{card.Title}  (Coming soon)" : card.Title,
                IsChecked = !isDisabled && card.EnabledByDefault,
                IsEnabled = !isPlanned,
                Tag = card.Id,
                ToolTip = card.Description,
                Margin = new Thickness(0, 5, 0, 5),
                FontSize = 11.5
            };

            CardSettingsPanel.Children.Add(checkBox);
        }
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
        var settings = SettingsStore.Load();

        settings.ClipboardPaused = PauseClipboard.IsChecked == true;
        settings.StartWithWindows = StartWithWindows.IsChecked == true;
        settings.NotificationsEnabled = EnableNotifications.IsChecked == true;
        settings.QuietMode = QuietModeToggle.IsChecked == true;
        settings.NotificationDurationSeconds = GetSelectedDuration();
        settings.BatteryWarningsEnabled = BatteryWarnings.IsChecked == true;
        settings.NetworkMonitoringEnabled = NetworkMonitoring.IsChecked == true;
        settings.SelectedNetworkInterface = GetSelectedInterface();
        settings.IslandShape = (ShapeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Pill" ? "Pill" : "Notch";
        settings.ShowLyrics = ShowLyrics.IsChecked == true;

        settings.DisabledCardIds = CardSettingsPanel.Children
            .OfType<CheckBox>()
            .Where(checkBox => checkBox.IsEnabled && checkBox.IsChecked != true)
            .Select(checkBox => checkBox.Tag?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        SettingsStore.Save(settings);
        Close();
    }
}
