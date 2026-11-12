using System.Net.Http;
using System.Text.Json;
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
        ShrinkInFullscreen.IsChecked = s.ShrinkInFullscreen;

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
        SelectTheme(s.Theme);
        WeatherCityBox.Text = s.WeatherCity;

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
                Margin = new Thickness(0, 6, 16, 6),
                FontSize = 11.5
            };

            CardSettingsPanel.Children.Add(checkBox);
        }
    }
    private void SelectTheme(string theme)
    {
        switch (theme)
        {
            case "Wallpaper": ThemeWallpaper.IsChecked = true; break;
            case "Black": ThemeBlack.IsChecked = true; break;
            case "Graphite": ThemeGraphite.IsChecked = true; break;
            case "Acrylic": ThemeAcrylic.IsChecked = true; break;
            default: ThemeLiquidGlass.IsChecked = true; break;
        }

        // The Wallpaper swatch previews the colours taken from your current wallpaper.
        var w = WallpaperTheme.Get();
        var dark = System.Windows.Media.Color.FromRgb(8, 8, 10);
        ThemeWallpaperPreview.Background = new System.Windows.Media.LinearGradientBrush(
            Tint(dark, w.Accent, 0.70), Tint(dark, w.Accent, 0.22), 45.0);
    }

    private static System.Windows.Media.Color Tint(System.Windows.Media.Color from, System.Windows.Media.Color to, double t)
        => System.Windows.Media.Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
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

    private static readonly HttpClient GeoClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    // Looks the city up on Open-Meteo's geocoding service. Returns null if not found or offline.
    private static async Task<(string Name, double Lat, double Lon)?> FindCityAsync(string name)
    {
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name="
                         + Uri.EscapeDataString(name);
            string json = await GeoClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;
            var r = results[0];
            return (r.GetProperty("name").GetString() ?? name,
                    r.GetProperty("latitude").GetDouble(),
                    r.GetProperty("longitude").GetDouble());
        }
        catch
        {
            return null;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Load();

        // Weather city: only look it up if the text changed.
        string typedCity = (WeatherCityBox.Text ?? "").Trim();
        if (typedCity.Length > 0 &&
            !string.Equals(typedCity, settings.WeatherCity, StringComparison.OrdinalIgnoreCase))
        {
            if (sender is Button saveButton) saveButton.IsEnabled = false;
            WeatherStatus.Text = "Looking up " + typedCity + "...";
            WeatherStatus.Visibility = Visibility.Visible;

            var found = await FindCityAsync(typedCity);
            if (found == null)
            {
                WeatherStatus.Text = "Could not find \"" + typedCity + "\" (or you are offline). Check the spelling, or clear the box to keep the current city.";
                if (sender is Button b2) b2.IsEnabled = true;
                return;   // stay open so nothing is silently lost
            }
            settings.WeatherCity = found.Value.Name;
            settings.WeatherLatitude = found.Value.Lat;
            settings.WeatherLongitude = found.Value.Lon;
        }

        settings.ClipboardPaused = PauseClipboard.IsChecked == true;
        settings.StartWithWindows = StartWithWindows.IsChecked == true;
        settings.ShrinkInFullscreen = ShrinkInFullscreen.IsChecked == true;
        settings.NotificationsEnabled = EnableNotifications.IsChecked == true;
        settings.QuietMode = QuietModeToggle.IsChecked == true;
        settings.NotificationDurationSeconds = GetSelectedDuration();
        settings.BatteryWarningsEnabled = BatteryWarnings.IsChecked == true;
        settings.NetworkMonitoringEnabled = NetworkMonitoring.IsChecked == true;
        settings.SelectedNetworkInterface = GetSelectedInterface();
        settings.IslandShape = (ShapeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Pill" ? "Pill" : "Notch";
        settings.ShowLyrics = ShowLyrics.IsChecked == true;
        settings.Theme = ThemeWallpaper.IsChecked == true ? "Wallpaper"
                       : ThemeBlack.IsChecked == true ? "Black"
                       : ThemeGraphite.IsChecked == true ? "Graphite"
                       : ThemeAcrylic.IsChecked == true ? "Acrylic"
                       : "LiquidGlass";

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
