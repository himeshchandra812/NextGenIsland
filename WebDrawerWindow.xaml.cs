using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace NextGenIsland;

// A real browser drawer under the island. WebView2 cannot render inside a transparent (layered) WPF
// window, so the drawer is its own normal window that sits just below the island.
public partial class WebDrawerWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NextGenIsland", "webview");

    private string _url;
    private string _name;
    private readonly double _top;
    private bool _ready;

    public WebDrawerWindow(string url, string name, double top)
    {
        InitializeComponent();
        _url = url;
        _name = name;
        _top = top;
        TitleText.Text = name;
        HostText.Text = HostOf(url);
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    public void Navigate(string url, string name)
    {
        _url = url;
        _name = name;
        TitleText.Text = name;
        HostText.Text = HostOf(url);
        if (_ready) Web.CoreWebView2.Navigate(url);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            int round = 2;   // DWMWCP_ROUND (Windows 11)
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
        }
        catch { /* older Windows: square corners */ }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = _top;
        if (Top + Height > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height - 8);

        try
        {
            Directory.CreateDirectory(DataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            _ready = true;

            // Pop-ups and target=_blank links stay inside the drawer.
            core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };
            core.SourceChanged += (_, _) => UpdateBar();
            core.HistoryChanged += (_, _) => UpdateBar();
            core.DocumentTitleChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(core.DocumentTitle)) TitleText.Text = core.DocumentTitle;
            };

            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("WebDrawer init: " + ex);
            Web.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = ex is WebView2RuntimeNotFoundException
                ? "The Microsoft WebView2 Runtime is not installed, so pages cannot be shown here."
                : "The page could not be loaded here.";
        }
    }

    private void UpdateBar()
    {
        if (!_ready) return;
        BackButton.IsEnabled = Web.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = Web.CoreWebView2.CanGoForward;
        _url = Web.CoreWebView2.Source;
        HostText.Text = HostOf(_url);
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (_ready && Web.CoreWebView2.CanGoBack) Web.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (_ready && Web.CoreWebView2.CanGoForward) Web.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { if (_ready) Web.CoreWebView2.Reload(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine("OpenExternal: " + ex.Message); }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { Web.Dispose(); } catch { }
    }
}
