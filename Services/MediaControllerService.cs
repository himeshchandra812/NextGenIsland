using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NextGenIsland.Services;

public sealed record MediaState(
    bool HasMedia,
    string Title,
    string Artist,
    string AppName,
    bool IsPlaying,
    BitmapImage? Thumbnail
);

public sealed class MediaControllerService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _disposed;

    public event Action<MediaState>? MediaStateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager != null)
            {
                _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                HookCurrentSession();
                await RefreshMediaStateAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("MediaControllerService.InitializeAsync error: " + ex);
        }
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        HookCurrentSession();
        _ = RefreshMediaStateAsync();
    }

    private void HookCurrentSession()
    {
        try
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            _currentSession = _manager?.GetCurrentSession();
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("HookCurrentSession error: " + ex);
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshMediaStateAsync();
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshMediaStateAsync();
    }

    public async Task<MediaState> RefreshMediaStateAsync()
    {
        try
        {
            var session = _currentSession ?? _manager?.GetCurrentSession();
            if (session == null)
            {
                var idle = new MediaState(false, "No Media Playing", "No active media session", "", false, null);
                MediaStateChanged?.Invoke(idle);
                return idle;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            var title = !string.IsNullOrWhiteSpace(props?.Title) ? props.Title : "No Media Playing";
            var artist = !string.IsNullOrWhiteSpace(props?.Artist)
                ? props.Artist
                : (!string.IsNullOrWhiteSpace(props?.AlbumTitle) ? props.AlbumTitle : "Unknown Artist");
            
            var appName = CleanAppName(session.SourceAppUserModelId);
            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            BitmapImage? thumbnail = null;
            if (props?.Thumbnail != null)
            {
                try
                {
                    using var streamRef = await props.Thumbnail.OpenReadAsync();
                    using var netStream = streamRef.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms);
                    ms.Position = 0;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    thumbnail = bmp;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Thumbnail decode error: " + ex);
                }
            }

            bool hasValidMedia = !string.IsNullOrWhiteSpace(props?.Title) || isPlaying;
            var state = new MediaState(
                hasValidMedia,
                hasValidMedia ? title : "No Media Playing",
                hasValidMedia ? artist : "No active media session",
                appName,
                isPlaying,
                thumbnail
            );

            MediaStateChanged?.Invoke(state);
            return state;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("RefreshMediaStateAsync error: " + ex);
            var fallback = new MediaState(false, "No Media Playing", "No active media session", "", false, null);
            MediaStateChanged?.Invoke(fallback);
            return fallback;
        }
    }

    private static string CleanAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        try
        {
            var name = Path.GetFileNameWithoutExtension(appId);
            if (name.Contains('!'))
            {
                var parts = name.Split('!');
                name = parts[parts.Length - 1];
            }

            if (name.Equals("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
            if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
            if (name.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
            if (name.Equals("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
            if (name.Equals("brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
            if (name.Equals("applemusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
            if (name.Equals("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC Media Player";
            return name;
        }
        catch
        {
            return "Media Player";
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        try
        {
            var session = _currentSession ?? _manager?.GetCurrentSession();
            if (session != null)
            {
                var success = await session.TryTogglePlayPauseAsync();
                await Task.Delay(150);
                await RefreshMediaStateAsync();
                return success;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("TogglePlayPause error: " + ex);
        }
        return false;
    }

    public async Task<bool> SkipNextAsync()
    {
        try
        {
            var session = _currentSession ?? _manager?.GetCurrentSession();
            if (session != null)
            {
                var success = await session.TrySkipNextAsync();
                await Task.Delay(200);
                await RefreshMediaStateAsync();
                return success;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("SkipNext error: " + ex);
        }
        return false;
    }

    public async Task<bool> SkipPreviousAsync()
    {
        try
        {
            var session = _currentSession ?? _manager?.GetCurrentSession();
            if (session != null)
            {
                var success = await session.TrySkipPreviousAsync();
                await Task.Delay(200);
                await RefreshMediaStateAsync();
                return success;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("SkipPrevious error: " + ex);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_manager != null)
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }
        }
        catch { }
    }
}
