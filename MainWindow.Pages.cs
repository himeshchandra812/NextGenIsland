using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NextGenIsland.Services;

namespace NextGenIsland;

public sealed class SiteItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

// Per-tab island height, plus the Notifications, Clipboard, Shelf and Web pages.
public partial class MainWindow
{
    private static readonly Dictionary<string, double> ViewHeights = new()
    {
        ["Media"] = 178, ["Notifications"] = 214, ["Clipboard"] = 190, ["System"] = 140, ["Shelf"] = 176,
        ["Reminders"] = 196, ["Agents"] = 188, ["Widgets"] = 232, ["Web"] = 196, ["Calls"] = 190,
    };

    private double HeightFor(string key)
    {
        double h = ViewHeights.TryGetValue(key, out var v) ? v : 190;
        if (key == "Web")
        {
            int rows = Math.Min(3, (Math.Min(_sites.Count, 8) + 1 + 2) / 3);   // site tiles + the add tile, 3 per row
            h = 34 + rows * 56 + 30 + 14 + (WebAddRow.Visibility == Visibility.Visible ? 40 : 0);
        }
        return h;
    }

    // Tabs have different content, so the island grows or shrinks to fit the active one.
    private void ResizeForView()
    {
        if (!_expanded) return;
        SyncExpandedHeight();
        AnimateIsland(ExpandedWidth, HeightFor(_activeKey));
    }

    private static SolidColorBrush Rgba(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return (int)d.TotalMinutes + "m";
        if (d < TimeSpan.FromDays(1)) return (int)d.TotalHours + "h";
        return t.ToString("d MMM");
    }

    // ---------------------------------------------------------------- notifications

    private void RenderNotifications()
    {
        var all = _notificationService.History;
        NotifList.Children.Clear();
        foreach (var n in all.Take(3)) NotifList.Children.Add(MakeNotifRow(n));
        NotifEmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationCountText.Text = all.Count == 1 ? "1 notice" : all.Count + " notices";
    }

    private UIElement MakeNotifRow(IslandNotification n)
    {
        var icon = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8),
            Background = Rgba(0x30, 0xFF, 0xFF, 0xFF),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(n.AppName) ? "!" : n.AppName.Substring(0, 1).ToUpperInvariant(),
                Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var time = new TextBlock
        {
            Text = Ago(n.Timestamp), Foreground = Rgba(0x80, 0xFF, 0xFF, 0xFF), FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0)
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = n.Title, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = n.Message, Foreground = Rgba(0x99, 0xFF, 0xFF, 0xFF), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(time, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(time);

        return new Border
        {
            Background = Rgba(0x1A, 0xFF, 0xFF, 0xFF), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6),
            ToolTip = n.AppName + ": " + n.Title + "\n" + n.Message, Child = grid
        };
    }

    // ---------------------------------------------------------------- clipboard

    private string _clipFilter = "All";

    private void ClipFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string f }) { _clipFilter = f; _ = RenderClipboardAsync(); }
    }

    private async void ClearClipboard_Click(object sender, RoutedEventArgs e)
    {
        try { await _store.ClearUnpinnedAsync(); await RenderClipboardAsync(); }
        catch (Exception ex) { Debug.WriteLine("ClearClipboard: " + ex.Message); }
    }

    private async Task RenderClipboardAsync()
    {
        List<ClipboardItem> items;
        try { items = await _store.SearchAsync(""); }
        catch (Exception ex) { Debug.WriteLine("RenderClipboard: " + ex.Message); return; }

        items = _clipFilter switch
        {
            "Pinned" => items.Where(i => i.Pinned).ToList(),
            "Text" => items.Where(i => i.Kind == "text").ToList(),
            "Images" => items.Where(i => i.Kind == "image").ToList(),
            "Files" => items.Where(i => i.Kind == "files").ToList(),
            _ => items
        };

        ClipList.Children.Clear();
        foreach (var it in items.Take(3)) ClipList.Children.Add(MakeClipCard(it));
        ClipEmptyText.Text = _clipFilter switch
        {
            "Pinned" => "No pinned items yet.",
            "Text" => "No text copied yet.",
            "Images" => "No images yet. Copy an image or take a screenshot.",
            "Files" => "No files yet. Copy files in Explorer.",
            _ => "Copy text, an image or files and it shows up here."
        };
        ClipEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipChipAll.Opacity = _clipFilter == "All" ? 1.0 : 0.5;
        ClipChipText.Opacity = _clipFilter == "Text" ? 1.0 : 0.5;
        ClipChipImages.Opacity = _clipFilter == "Images" ? 1.0 : 0.5;
        ClipChipFiles.Opacity = _clipFilter == "Files" ? 1.0 : 0.5;
        ClipChipPinned.Opacity = _clipFilter == "Pinned" ? 1.0 : 0.5;
    }

    private TextBlock MiniAction(string text, string tip, Action act)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = 10.5, Foreground = Rgba(0x99, 0xFF, 0xFF, 0xFF),
            Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0)
        };
        t.MouseEnter += (_, _) => t.Foreground = Brushes.White;
        t.MouseLeave += (_, _) => t.Foreground = Rgba(0x99, 0xFF, 0xFF, 0xFF);
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return t;
    }

    private UIElement MakeClipCard(ClipboardItem it)
    {
        string label = it.Kind switch { "image" => "Image", "files" => "Files", _ => "Text" };
        Brush labelBrush = it.Kind switch
        {
            "image" => Rgba(0xFF, 0x64, 0xD2, 0xFF),
            "files" => Rgba(0xFF, 0xBF, 0x5A, 0xF2),
            _ => Rgba(0xFF, 0x30, 0xD1, 0x58)
        };
        if (it.Pinned) { label = "Pinned"; labelBrush = Rgba(0xFF, 0xFF, 0x9F, 0x0A); }

        var header = new DockPanel();
        var del = MiniAction("x", "Delete", () => _ = DeleteClipAsync(it));
        var pin = MiniAction(it.Pinned ? "Unpin" : "Pin", it.Pinned ? "Unpin this item" : "Pin this item",
            () => _ = TogglePinAsync(it));
        DockPanel.SetDock(del, Dock.Right);
        DockPanel.SetDock(pin, Dock.Right);
        header.Children.Add(del);
        header.Children.Add(pin);
        header.Children.Add(new TextBlock
        {
            Text = label, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = labelBrush
        });

        string footText = Ago(it.CreatedAt);
        UIElement body;
        switch (it.Kind)
        {
            case "image":
            {
                var thumb = string.IsNullOrEmpty(it.ImagePath) ? null : ClipboardAccess.LoadThumb(it.ImagePath, 220);
                if (thumb != null)
                {
                    body = new Border
                    {
                        Height = 46, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 4),
                        Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill }
                    };
                }
                else
                {
                    body = new TextBlock
                    {
                        Text = "Image file missing", Foreground = Rgba(0x99, 0xFF, 0xFF, 0xFF), FontSize = 11,
                        Margin = new Thickness(0, 6, 0, 0)
                    };
                }
                if (!string.IsNullOrEmpty(it.Meta)) footText = it.Meta + "  " + footText;
                break;
            }
            case "files":
            {
                var paths = it.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string first = paths.Length > 0 ? System.IO.Path.GetFileName(paths[0].TrimEnd('\\', '/')) : "";
                if (first.Length == 0 && paths.Length > 0) first = paths[0];
                var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                stack.Children.Add(new TextBlock
                {
                    Text = first, Foreground = Brushes.White, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (paths.Length > 1)
                    stack.Children.Add(new TextBlock
                    {
                        Text = "+" + (paths.Length - 1) + " more", FontSize = 10.5, Foreground = Rgba(0x99, 0xFF, 0xFF, 0xFF),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                body = stack;
                break;
            }
            default:
            {
                string flat = it.Text.Trim().Replace("\r", " ").Replace("\n", " ");
                if (flat.Length > 90) flat = flat.Substring(0, 90) + "...";
                body = new TextBlock
                {
                    Text = flat, Foreground = Brushes.White, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 50, Margin = new Thickness(0, 6, 0, 0)
                };
                break;
            }
        }

        var foot = new TextBlock
        {
            Text = footText, FontSize = 10, Foreground = Rgba(0x80, 0xFF, 0xFF, 0xFF),
            VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 1);
        Grid.SetRow(foot, 2);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(foot);

        var card = new Border
        {
            Width = 118, Height = 96, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = "Click to copy back",
            Background = Rgba(0x1E, 0xFF, 0xFF, 0xFF), BorderBrush = Rgba(0x14, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1), Child = grid
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            try { ClipboardStatus.Text = ClipboardAccess.CopyBack(it); }
            catch (Exception ex) { ClipboardStatus.Text = "Could not copy back"; Debug.WriteLine("Copy back: " + ex.Message); }
        };
        return card;
    }

    private async Task TogglePinAsync(ClipboardItem it)
    {
        try { await _store.TogglePinAsync(it.Id, !it.Pinned); await RenderClipboardAsync(); }
        catch (Exception ex) { Debug.WriteLine("TogglePin: " + ex.Message); }
    }

    private async Task DeleteClipAsync(ClipboardItem it)
    {
        try { await _store.DeleteAsync(it.Id); await RenderClipboardAsync(); }
        catch (Exception ex) { Debug.WriteLine("DeleteClip: " + ex.Message); }
    }

    // ---------------------------------------------------------------- shelf

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        _shelfFiles.Clear();
        SaveExtraData("shelf.json", _shelfFiles);
        RenderShelf();
    }

    // ---------------------------------------------------------------- web drawer

    private readonly List<SiteItem> _sites = new();

    private void LoadSites()
    {
        var path = Path.Combine(_extraDataDir, "sites.json");
        bool existed = File.Exists(path);
        try
        {
            if (existed)
                _sites.AddRange(JsonSerializer.Deserialize<List<SiteItem>>(File.ReadAllText(path)) ?? new());
        }
        catch (Exception ex) { Debug.WriteLine("LoadSites: " + ex.Message); }

        if (!existed)
        {
            _sites.Add(new SiteItem { Name = "YT Music", Url = "https://music.youtube.com" });
            _sites.Add(new SiteItem { Name = "YouTube", Url = "https://www.youtube.com" });
            _sites.Add(new SiteItem { Name = "GitHub", Url = "https://github.com" });
        }
    }

    private void SaveSites()
    {
        try
        {
            Directory.CreateDirectory(_extraDataDir);
            File.WriteAllText(Path.Combine(_extraDataDir, "sites.json"), JsonSerializer.Serialize(_sites));
        }
        catch (Exception ex) { Debug.WriteLine("SaveSites: " + ex.Message); }
    }

    private static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host.Substring(4) : u.Host;
    }

    private static string NameFor(string host)
    {
        switch (host.ToLowerInvariant())
        {
            case "music.youtube.com": return "YT Music";
            case "youtube.com": return "YouTube";
            case "open.spotify.com":
            case "spotify.com": return "Spotify";
            case "github.com": return "GitHub";
            case "mail.google.com": return "Gmail";
            case "notion.so": return "Notion";
        }
        var first = host.Split('.')[0];
        return first.Length == 0 ? host : char.ToUpperInvariant(first[0]) + first.Substring(1);
    }

    private static Color TintFor(string host)
    {
        string h = host.ToLowerInvariant();
        if (h.Contains("youtube")) return Color.FromRgb(229, 57, 63);
        if (h.Contains("spotify")) return Color.FromRgb(29, 185, 84);
        if (h.Contains("github")) return Color.FromRgb(110, 118, 129);
        if (h.Contains("notion")) return Color.FromRgb(120, 120, 125);
        if (h.Contains("google")) return Color.FromRgb(66, 133, 244);

        int hash = 0;
        foreach (char c in h) hash = (hash * 31 + c) & 0x7FFFFFFF;
        double hue = hash % 360, s = 0.6, v = 0.85;
        double cc = v * s, x = cc * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0)), m = v - cc;
        (double r, double g, double b) = hue < 60 ? (cc, x, 0.0) : hue < 120 ? (x, cc, 0.0) : hue < 180 ? (0.0, cc, x)
            : hue < 240 ? (0.0, x, cc) : hue < 300 ? (x, 0.0, cc) : (cc, 0.0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private void RenderWeb()
    {
        WebPanel.Children.Clear();
        foreach (var s in _sites.Take(8)) WebPanel.Children.Add(MakeSiteTile(s));
        WebPanel.Children.Add(MakeAddTile());
    }

    private UIElement MakeSiteTile(SiteItem site)
    {
        string host = HostOf(site.Url);
        var tint = TintFor(host);

        var icon = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(site.Name) ? "?" : site.Name.Substring(0, 1).ToUpperInvariant(),
                Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = site.Name, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        texts.Children.Add(new TextBlock
        {
            Text = host, Foreground = Rgba(0x99, 0xFF, 0xFF, 0xFF), FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(icon);
        grid.Children.Add(texts);

        var tile = new Border
        {
            Width = 120, Height = 48, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = site.Url,
            BorderThickness = new Thickness(1), BorderBrush = Rgba(0x1A, 0xFF, 0xFF, 0xFF),
            Background = new LinearGradientBrush(
                Color.FromArgb(0x58, tint.R, tint.G, tint.B), Color.FromArgb(0x14, tint.R, tint.G, tint.B), 0.0),
            Child = grid
        };
        tile.MouseLeftButtonUp += (_, _) => OpenUrl(site.Url);

        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) =>
        {
            _sites.Remove(site);
            SaveSites();
            RenderWeb();
            ResizeForView();
        };
        menu.Items.Add(remove);
        tile.ContextMenu = menu;
        return tile;
    }

    private UIElement MakeAddTile()
    {
        var dash = new System.Windows.Shapes.Rectangle
        {
            Stroke = Rgba(0x40, 0xFF, 0xFF, 0xFF), StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 4, 4 }, RadiusX = 13, RadiusY = 13, IsHitTestVisible = false
        };
        var label = new TextBlock
        {
            Text = "+  Add a site", Foreground = Rgba(0xB3, 0xFF, 0xFF, 0xFF), FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var g = new Grid
        {
            Width = 120, Height = 48, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };
        g.Children.Add(dash);
        g.Children.Add(label);
        g.MouseLeftButtonUp += (_, _) => ToggleWebAdd(true);
        return g;
    }

    private void ToggleWebAdd(bool show)
    {
        WebAddRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WebHintText.Text = "Add any page. Right-click a site to remove it.";
        if (show) { WebUrlInput.Clear(); WebUrlInput.Focus(); }
        ResizeForView();
    }

    private void WebAdd_Click(object sender, RoutedEventArgs e) => AddSite();
    private void WebCancel_Click(object sender, RoutedEventArgs e) => ToggleWebAdd(false);

    private void WebUrlInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddSite(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ToggleWebAdd(false); e.Handled = true; }
    }

    private void AddSite()
    {
        string raw = WebUrlInput.Text.Trim();
        if (raw.Length == 0) return;
        if (!raw.Contains("://")) raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.Host.Contains('.'))
        {
            WebHintText.Text = "Enter a valid web address, like example.com";
            return;
        }

        _sites.Add(new SiteItem { Name = NameFor(HostOf(uri.ToString())), Url = uri.ToString() });
        SaveSites();
        ToggleWebAdd(false);
        RenderWeb();
        ResizeForView();
    }
}
