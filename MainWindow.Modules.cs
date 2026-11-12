using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NextGenIsland;

// Navigation, theming and the Shelf / Reminders / Agents / Widgets / Web / Calls modules.
public partial class MainWindow
{
    // Module key -> card id in IslandCardCatalog. Media can never be hidden (it is the fallback tab).
    private static readonly Dictionary<string, string> ModuleCards = new()
    {
        ["Media"] = "media.player",
        ["Notifications"] = "system.notifications",
        ["Clipboard"] = "productivity.clipboard",
        ["System"] = "system.monitor",
        ["Shelf"] = "productivity.file-shelf",
        ["Reminders"] = "productivity.reminders",
        ["Agents"] = "developer.ai-agents",
        ["Widgets"] = "widgets.tools",
        ["Web"] = "web.drawer",
        ["Calls"] = "calls.tools",
    };

    private Dictionary<string, Grid> _views = new();
    private Dictionary<string, RadioButton> _navButtons = new();
    private string _activeKey = "Media";
    private bool _suppressNav;

    private readonly List<ReminderItem> _reminders = new();
    private readonly HashSet<string> _alerted = new();
    private int _whenIndex, _repeatIndex;
    private readonly List<string> _shelfFiles = new();
    private readonly string _extraDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland");

    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private const int FocusMinutes = 25;
    private int _focusSeconds = FocusMinutes * 60;
    private bool _focusRunning;

    private bool _dragOver, _dragOut;
    private int _tick;
    private IslandPalette _albumPalette = AlbumPalette.Fallback;

    // True while something inside the island needs it to stay open.
    private bool ModuleBusy => _dragOver || _dragOut || ReminderInput.IsKeyboardFocusWithin || WebUrlInput.IsKeyboardFocusWithin || _spkTesting || _meter != null;

    private void InitModules()
    {
        _views = new Dictionary<string, Grid>
        {
            ["Media"] = ViewMedia, ["Notifications"] = ViewNotifications, ["Clipboard"] = ViewClipboard,
            ["System"] = ViewSystem, ["Shelf"] = ViewShelf, ["Reminders"] = ViewReminders,
            ["Agents"] = ViewAgents, ["Widgets"] = ViewWidgets, ["Web"] = ViewWeb, ["Calls"] = ViewCalls,
        };
        _navButtons = new Dictionary<string, RadioButton>
        {
            ["Media"] = NavMedia, ["Notifications"] = NavNotifications, ["Clipboard"] = NavClipboard,
            ["System"] = NavSystem, ["Shelf"] = NavShelf, ["Reminders"] = NavReminders,
            ["Agents"] = NavAgents, ["Widgets"] = NavWidgets, ["Web"] = NavWeb, ["Calls"] = NavCalls,
        };

        LoadExtraData();
        LoadSites();
        _focusTimer.Tick += FocusTimer_Tick;
        UpdateFocusUi();
        ApplyCardVisibility();
        ApplyPalette(EffectivePalette());
        ApplyShape();
        UpdateLyricsIcon();
    }

    private bool IsModuleEnabled(string key)
        => key == "Media" || !_settings.DisabledCardIds.Contains(ModuleCards[key]);

    // ---------------------------------------------------------------- navigation

    private void SelectModule(string key)
    {
        if (!_views.ContainsKey(key) || !IsModuleEnabled(key)) return;
        int prev = EnabledIndexOf(_activeKey);
        _activeKey = key;
        int next = EnabledIndexOf(key);

        foreach (var v in _views.Values) v.Visibility = Visibility.Collapsed;
        var view = _views[key];
        view.Visibility = Visibility.Visible;

        // Slide in from the side we are travelling towards.
        double dx = next == prev ? 0.0 : (next > prev ? 18.0 : -18.0);
        var slide = new TranslateTransform(dx, 0);
        view.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        });
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        MoveIndicator(animate: true);
        RefreshModuleData(key);
        ResizeForView();
        ApplyPalette(EffectivePalette());
    }

    private int EnabledIndexOf(string key)
    {
        int i = 0;
        foreach (var k in ModuleKeys)
        {
            if (!IsModuleEnabled(k)) continue;
            if (k == key) return i;
            i++;
        }
        return 0;
    }

    private void MoveIndicator(bool animate)
    {
        double x = EnabledIndexOf(_activeKey) * NavSlot;
        if (!animate)
        {
            NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            NavIndicatorShift.X = x;
            return;
        }
        NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            To = x,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // Settings -> Visible Cards decides which tabs exist.
    private void ApplyCardVisibility()
    {
        int count = 0;
        foreach (var key in ModuleKeys)
        {
            bool on = IsModuleEnabled(key);
            _navButtons[key].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) count++;
        }
        NavStrip.Width = count * NavSlot;

        bool notifOn = IsModuleEnabled("Notifications");
        BellBadge.Opacity = notifOn ? 1.0 : 0.0;
        BellBadge.Margin = new Thickness(EnabledIndexOf("Notifications") * NavSlot + 17, 1, 0, 0);

        if (!IsModuleEnabled(_activeKey))
        {
            _suppressNav = true;
            NavMedia.IsChecked = true;
            _suppressNav = false;
            SelectModule("Media");
        }
        else
        {
            MoveIndicator(animate: false);
        }
    }

    private void RefreshModuleData(string key)
    {
        switch (key)
        {
            case "Shelf": RenderShelf(); break;
            case "Notifications": RenderNotifications(); break;
            case "Clipboard": _ = RenderClipboardAsync(); break;
            case "Web": RenderWeb(); break;
            case "Reminders": RenderReminders(); break;
            case "Agents": _ = RefreshAgentsAsync(); break;
            case "Widgets": UpdateWidgetClock(); _ = RefreshWeatherAsync(); break;
            case "Calls": LoadCallDevices(); UpdateMicUi(); break;
        }
    }

    private void TickModules()
    {
        _tick++;
        SampleStats();
        CheckReminders();
        if (ViewNotifications.Visibility == Visibility.Visible && _tick % 15 == 0) RenderNotifications();
        if (ViewWidgets.Visibility == Visibility.Visible) UpdateWidgetClock();
        if (ViewAgents.Visibility == Visibility.Visible && _tick % 4 == 0) _ = RefreshAgentsAsync();
    }

    private void OpenNotifications()
    {
        ExpandIsland(true);
        if (IsModuleEnabled("Notifications")) NavNotifications.IsChecked = true;
    }

    // ---------------------------------------------------------------- themes

    // Expanded non-media tabs use a neutral near-black body; Media and the collapsed notch keep the album colours.
    private IslandPalette EffectivePalette()
    {
        // Wallpaper theme: the whole island (every tab, collapsed or open) is tinted from the desktop wallpaper.
        if (_settings.Theme == "Wallpaper")
        {
            var w = WallpaperTheme.Get();
            var dark = Color.FromRgb(8, 8, 10);
            return w with
            {
                BackgroundStart = MixTint(dark, w.Accent, 0.46, 0xF4),
                BackgroundEnd = MixTint(dark, w.Accent, 0.16, 0xF8),
                Border = Color.FromArgb(0x66, w.Accent.R, w.Accent.G, w.Accent.B),
                NavIndicator = Colors.White
            };
        }

        // Collapsed pill with nothing playing: neutral charcoal with a thin grey edge (NotchPixel look).
        if (!_expanded && _settings.IslandShape == "Pill" && _settings.Theme == "LiquidGlass"
            && _albumPalette.Equals(AlbumPalette.Fallback))
        {
            return _albumPalette with
            {
                BackgroundStart = Color.FromArgb(0xF4, 0x24, 0x25, 0x29),
                BackgroundEnd = Color.FromArgb(0xF4, 0x1B, 0x1C, 0x1F),
                Border = Color.FromArgb(0x58, 0x8E, 0x90, 0x98),
                NavIndicator = Colors.White
            };
        }

        var p = ThemePalette();
        bool plainTheme = _settings.Theme is "Black" or "Graphite" or "Acrylic";
        if (_expanded && _activeKey != "Media" && !plainTheme)
        {
            p = p with
            {
                BackgroundStart = Color.FromArgb(0xF4, 0x12, 0x12, 0x14),
                BackgroundEnd = Color.FromArgb(0xF8, 0x08, 0x08, 0x0A),
                Border = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            };
        }
        else if (!plainTheme && !_albumPalette.Equals(AlbumPalette.Fallback))
        {
            // The extracted background is almost black, so build a visible tint from the accent colour.
            var ink = Color.FromRgb(8, 8, 10);
            p = p with
            {
                BackgroundStart = MixTint(ink, p.Accent, 0.52, 0xF4),
                BackgroundEnd = MixTint(ink, p.Accent, 0.20, 0xF8),
                Border = Color.FromArgb(0x66, p.Accent.R, p.Accent.G, p.Accent.B)
            };
        }
        return p with { NavIndicator = Colors.White };
    }

    private static Color MixTint(Color from, Color to, double t, byte alpha)
        => Color.FromArgb(alpha,
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));

    private IslandPalette ThemePalette()
    {
        var a = _albumPalette;
        switch (_settings.Theme)
        {
            case "Black":
                return a with
                {
                    BackgroundStart = Color.FromArgb(0xF6, 0x0A, 0x0A, 0x0C),
                    BackgroundEnd = Color.FromArgb(0xFA, 0x00, 0x00, 0x00),
                    Border = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
                };
            case "Graphite":
                return a with
                {
                    BackgroundStart = Color.FromArgb(0xF2, 0x36, 0x38, 0x40),
                    BackgroundEnd = Color.FromArgb(0xF4, 0x1A, 0x1B, 0x20),
                    Border = Color.FromArgb(0x55, 0x96, 0x9C, 0xAC)
                };
            case "Acrylic":
                return a with
                {
                    BackgroundStart = Color.FromArgb(0xB8, a.BackgroundStart.R, a.BackgroundStart.G, a.BackgroundStart.B),
                    BackgroundEnd = Color.FromArgb(0xB0, a.BackgroundEnd.R, a.BackgroundEnd.G, a.BackgroundEnd.B),
                    Border = Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF)
                };
            default:
                return a;
        }
    }

    // ---------------------------------------------------------------- persistence

    private void LoadExtraData()
    {
        try
        {
            var r2 = Path.Combine(_extraDataDir, "reminders2.json");
            var r = Path.Combine(_extraDataDir, "reminders.json");
            if (File.Exists(r2))
                _reminders.AddRange(JsonSerializer.Deserialize<List<ReminderItem>>(File.ReadAllText(r2)) ?? new());
            else if (File.Exists(r))   // migrate the old plain-text reminders
                foreach (var t in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(r)) ?? new())
                    _reminders.Add(new ReminderItem { Text = t });
            var s = Path.Combine(_extraDataDir, "shelf.json");
            if (File.Exists(s))
                _shelfFiles.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(s)) ?? new());
        }
        catch (Exception ex) { Debug.WriteLine("LoadExtraData: " + ex.Message); }
    }

    private void SaveExtraData(string file, List<string> data)
    {
        try
        {
            Directory.CreateDirectory(_extraDataDir);
            File.WriteAllText(Path.Combine(_extraDataDir, file), JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { Debug.WriteLine("SaveExtraData: " + ex.Message); }
    }

    // ---------------------------------------------------------------- file shelf

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || !IsModuleEnabled("Shelf"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        _dragOver = true;
        _collapseTimer?.Stop();
        ExpandIsland(true);
        NavShelf.IsChecked = true;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && IsModuleEnabled("Shelf")
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        _dragOver = false;
        if (!_isHovering && !_isPinned && !_suspendCollapse) _collapseTimer?.Start();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        _dragOver = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && IsModuleEnabled("Shelf"))
        {
            foreach (var p in paths)
            {
                if ((File.Exists(p) || Directory.Exists(p)) &&
                    !_shelfFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
                    _shelfFiles.Add(p);
            }
            SaveExtraData("shelf.json", _shelfFiles);
            RenderShelf();
        }
        e.Handled = true;
    }

    private void RenderShelf()
    {
        // Drop entries whose file no longer exists.
        int removed = _shelfFiles.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        if (removed > 0) SaveExtraData("shelf.json", _shelfFiles);

        ShelfPanel.Children.Clear();
        const int maxShown = 8;
        foreach (var path in _shelfFiles.Take(maxShown))
            ShelfPanel.Children.Add(MakeShelfChip(path));

        ShelfCountText.Text = _shelfFiles.Count == 1 ? "1 FILE" : _shelfFiles.Count + " FILES";
        if (_shelfFiles.Count > maxShown) ShelfCountText.Text += " (SHOWING " + maxShown + ")";
        ShelfEmptyText.Visibility = _shelfFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement MakeShelfChip(string path)
    {
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        if (name.Length > 18) name = name.Substring(0, 17) + "...";

        var label = new TextBlock
        {
            Text = name,
            Foreground = Brushes.White,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var remove = new Button
        {
            Content = "x",
            Style = (Style)FindResource("IconButton"),
            ToolTip = "Remove from shelf",
            Width = 20,
            Height = 20,
            Tag = path
        };
        remove.Click += ShelfRemove_Click;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(remove);

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            ToolTip = path + "\nClick to open, drag out to copy.",
            Tag = path,
            Child = row
        };

        Point? start = null;
        chip.MouseLeftButtonDown += (_, e) => start = e.GetPosition(chip);
        chip.MouseLeftButtonUp += (_, _) =>
        {
            if (start == null) return;
            start = null;
            OpenPath(path);
        };
        chip.MouseMove += (_, e) =>
        {
            if (start == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(chip) - start.Value;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            _dragOut = true;
            try { DragDrop.DoDragDrop(chip, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); }
            catch (Exception ex) { Debug.WriteLine("Drag out: " + ex.Message); }
            finally { _dragOut = false; }
        };
        return chip;
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            _shelfFiles.Remove(path);
            SaveExtraData("shelf.json", _shelfFiles);
            RenderShelf();
        }
        e.Handled = true;
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine("OpenPath: " + ex.Message); }
    }

    // ---------------------------------------------------------------- reminders

    private static readonly (string Label, int Minutes)[] WhenOptions =
    {
        ("No time", 0), ("In 10 min", 10), ("In 30 min", 30), ("In 1 hour", 60), ("Tomorrow", -1)
    };
    private static readonly string[] RepeatOptions = { "Once", "Daily", "Weekly" };

    private void SaveReminders()
    {
        try
        {
            Directory.CreateDirectory(_extraDataDir);
            File.WriteAllText(Path.Combine(_extraDataDir, "reminders2.json"), JsonSerializer.Serialize(_reminders));
        }
        catch (Exception ex) { Debug.WriteLine("SaveReminders: " + ex.Message); }
    }

    private void WhenCycle_Click(object sender, RoutedEventArgs e)
    {
        _whenIndex = (_whenIndex + 1) % WhenOptions.Length;
        WhenButton.Content = WhenOptions[_whenIndex].Label;
    }

    private void RepeatCycle_Click(object sender, RoutedEventArgs e)
    {
        _repeatIndex = (_repeatIndex + 1) % RepeatOptions.Length;
        RepeatButton.Content = RepeatOptions[_repeatIndex];
    }

    private void ReminderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddReminder(); e.Handled = true; }
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e) => AddReminder();

    private void AddReminder()
    {
        var text = ReminderInput.Text.Trim();
        if (text.Length == 0) return;

        DateTime? due = null;
        int minutes = WhenOptions[_whenIndex].Minutes;
        if (minutes > 0) due = DateTime.Now.AddMinutes(minutes);
        else if (minutes < 0) due = DateTime.Today.AddDays(1).AddHours(9);

        _reminders.Insert(0, new ReminderItem
        {
            Text = text.Length > 120 ? text.Substring(0, 120) : text,
            Due = due,
            Repeat = due.HasValue ? RepeatOptions[_repeatIndex] : "Once"
        });
        ReminderInput.Clear();
        SaveReminders();
        RenderReminders();
    }

    // Called every second. Posts one notification per reminder when it becomes due.
    private void CheckReminders()
    {
        var now = DateTime.Now;
        bool fired = false;
        foreach (var r in _reminders)
        {
            if (r.Due.HasValue && r.Due.Value <= now && _alerted.Add(r.Id))
            {
                fired = true;
                _notificationService.PostNotification("Reminder", r.Text,
                    "Due " + r.Due.Value.ToString("HH:mm"), isSystemAlert: false);
            }
        }
        if (fired && ViewReminders.Visibility == Visibility.Visible) RenderReminders();
    }

    private static string DueLabel(ReminderItem r)
    {
        if (!r.Due.HasValue) return "";
        var d = r.Due.Value;
        string when = d.Date == DateTime.Today ? d.ToString("HH:mm") : d.ToString("ddd HH:mm");
        return r.Repeat == "Once" ? when : when + " - " + r.Repeat;
    }

    private void RenderReminders()
    {
        ReminderItems.Children.Clear();
        const int maxShown = 2;
        var now = DateTime.Now;
        foreach (var r in _reminders.Take(maxShown))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool overdue = r.Due.HasValue && r.Due.Value <= now;
            var dock = new DockPanel { LastChildFill = true };
            var due = new TextBlock
            {
                Text = DueLabel(r),
                FontSize = 10.5,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = overdue
                    ? new SolidColorBrush(Color.FromRgb(255, 159, 10))
                    : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
            };
            DockPanel.SetDock(due, Dock.Right);
            dock.Children.Add(due);
            dock.Children.Add(new TextBlock
            {
                Text = r.Text,
                Foreground = Brushes.White,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = r.Text,
                VerticalAlignment = VerticalAlignment.Center
            });

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 6, 12, 6),
                Child = dock
            };

            var snooze = new Button
            {
                Content = "Snooze",
                Style = (Style)FindResource("GlassPill"),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Remind again in 10 minutes",
                Tag = r.Id,
                Visibility = r.Due.HasValue ? Visibility.Visible : Visibility.Collapsed
            };
            snooze.Click += ReminderSnooze_Click;

            var done = new Button
            {
                Content = "Done",
                Style = (Style)FindResource("GlassPill"),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(6, 0, 0, 0),
                Tag = r.Id
            };
            done.Click += ReminderDone_Click;

            Grid.SetColumn(snooze, 1);
            Grid.SetColumn(done, 2);
            row.Children.Add(card);
            row.Children.Add(snooze);
            row.Children.Add(done);
            ReminderItems.Children.Add(row);
        }
        if (_reminders.Count > maxShown)
            ReminderItems.Children.Add(new TextBlock
            {
                Text = "+" + (_reminders.Count - maxShown) + " more",
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                FontSize = 10.5
            });
        ReminderEmptyText.Visibility = _reminders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReminderSnooze_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var r = _reminders.FirstOrDefault(x => x.Id == id);
        if (r == null) return;
        r.Due = DateTime.Now.AddMinutes(10);
        _alerted.Remove(r.Id);
        SaveReminders();
        RenderReminders();
    }

    private void ReminderDone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var r = _reminders.FirstOrDefault(x => x.Id == id);
        if (r == null) return;

        if (r.Due.HasValue && r.Repeat != "Once")
        {
            // Repeating: schedule the next occurrence in the future.
            var step = r.Repeat == "Weekly" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
            var next = r.Due.Value + step;
            while (next <= DateTime.Now) next += step;
            r.Due = next;
            _alerted.Remove(r.Id);
        }
        else
        {
            _reminders.Remove(r);
        }
        SaveReminders();
        RenderReminders();
    }

    // ---------------------------------------------------------------- AI agents

    private void RefreshAgents_Click(object sender, RoutedEventArgs e) => _ = RefreshAgentsAsync();

    private async Task RefreshAgentsAsync()
    {
        try
        {
            var names = await Task.Run(() =>
            {
                var list = new List<string>();
                foreach (var p in Process.GetProcesses())
                {
                    try { list.Add(p.ProcessName.ToLowerInvariant()); }
                    catch { }
                    finally { p.Dispose(); }
                }
                return list;
            });

            bool Has(string s) => names.Any(n => n.Contains(s));
            bool claude = Has("claude"), codex = Has("codex"), anti = Has("antigravity"), hermes = Has("hermes");
            SetAgent(AgentClaudeDot, AgentClaudeText, claude);
            SetAgent(AgentCodexDot, AgentCodexText, codex);
            SetAgent(AgentAntigravityDot, AgentAntigravityText, anti);
            SetAgent(AgentHermesDot, AgentHermesText, hermes);

            int running = (claude ? 1 : 0) + (codex ? 1 : 0) + (anti ? 1 : 0) + (hermes ? 1 : 0);
            AgentSummaryText.Text = running == 0
                ? "No agents running"
                : running == 1 ? "1 agent running" : running + " agents running";
        }
        catch (Exception ex) { Debug.WriteLine("RefreshAgents: " + ex.Message); }
    }

    private static void SetAgent(System.Windows.Shapes.Ellipse dot, TextBlock text, bool running)
    {
        dot.Fill = running
            ? new SolidColorBrush(Color.FromRgb(48, 209, 88))
            : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        text.Text = running ? "Running" : "Not running";
    }

    // ---------------------------------------------------------------- widgets

    private void UpdateWidgetClock()
    {
        var now = DateTime.Now;
        WidgetClockText.Text = now.ToString("HH:mm:ss");
        WidgetDateText.Text = now.ToString("dddd");
        WidgetMonthText.Text = now.ToString("MMMM").ToUpperInvariant();
        WidgetDayText.Text = now.Day.ToString();
        UpdateSparklines();
    }

    private void StartFocus_Click(object sender, RoutedEventArgs e)
    {
        if (_focusRunning) { _focusTimer.Stop(); _focusRunning = false; }
        else
        {
            if (_focusSeconds <= 0) _focusSeconds = FocusMinutes * 60;
            _focusTimer.Start();
            _focusRunning = true;
        }
        UpdateFocusUi();
    }

    private void ResetFocus_Click(object sender, RoutedEventArgs e)
    {
        _focusTimer.Stop();
        _focusRunning = false;
        _focusSeconds = FocusMinutes * 60;
        UpdateFocusUi();
    }

    private void FocusTimer_Tick(object? sender, EventArgs e)
    {
        _focusSeconds--;
        if (_focusSeconds <= 0)
        {
            _focusSeconds = 0;
            _focusTimer.Stop();
            _focusRunning = false;
            _notificationService.PostNotification("Focus timer", "Focus session complete",
                FocusMinutes + " minutes done. Take a break.", isSystemAlert: false);
        }
        UpdateFocusUi();
    }

    private void UpdateFocusUi()
    {
        FocusTimerText.Text = $"{_focusSeconds / 60:00}:{_focusSeconds % 60:00}";
        FocusStartButton.Content = _focusRunning ? "\uE769" : "\uE768";
        UpdateFocusRing();
    }

    // ---------------------------------------------------------------- web drawer

    private static void OpenUrl(string url) => OpenPath(url);
    private void OpenYouTube_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.youtube.com");
    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com");
    private void OpenGmail_Click(object sender, RoutedEventArgs e) => OpenUrl("https://mail.google.com");
    private void OpenNotion_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.notion.so");
}

public sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime? Due { get; set; }
    public string Repeat { get; set; } = "Once";   // Once, Daily or Weekly
}
