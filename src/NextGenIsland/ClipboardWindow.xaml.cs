using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NextGenIsland.Services;

namespace NextGenIsland;

public partial class ClipboardWindow : Window
{
    private readonly ClipboardStore _store;

    public ClipboardWindow(ClipboardStore store)
    {
        InitializeComponent();
        _store = store;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task ReloadAsync()
    {
        var rows = await _store.SearchAsync(SearchBox.Text);
        ItemsList.ItemsSource = rows;
        CountText.Text = $"{rows.Count} entries shown · stored on this device";
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) await ReloadAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ClipboardItem i)
            Clipboard.SetText(i.Text);
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ClipboardItem i)
        {
            await _store.TogglePinAsync(i.Id, !i.Pinned);
            await ReloadAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ClipboardItem i)
        {
            await _store.DeleteAsync(i.Id);
            await ReloadAsync();
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete all unpinned entries?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await _store.ClearUnpinnedAsync();
            await ReloadAsync();
        }
    }
}
