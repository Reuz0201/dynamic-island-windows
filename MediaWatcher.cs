using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Smtc = global::Windows.Media.Control;

namespace DynamicIsland
{
    /// <summary>Снимок того, что сейчас играет.</summary>
    public sealed record MediaState(
        string Title,
        string Artist,
        string AppName,
        byte[]? Thumbnail,
        bool IsPlaying,
        TimeSpan Position,
        TimeSpan Duration,
        DateTimeOffset PositionUpdatedAt,
        bool CanPrev,
        bool CanNext,
        bool CanSeek)
    {
        public string TrackKey => $"{AppName}|{Title}|{Artist}";
    }

    /// <summary>
    /// Следит за системными медиа-сессиями Windows (то, что показывается
    /// в плашке громкости): Spotify, Яндекс Музыка, браузеры, плееры и т.д.
    /// </summary>
    public sealed class MediaWatcher
    {
        private Smtc.GlobalSystemMediaTransportControlsSessionManager? _manager;
        private Smtc.GlobalSystemMediaTransportControlsSession? _session;

        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        private string? _thumbKey;
        private byte[]? _thumbCache;

        /// <summary>Вызывается на фоновом потоке. null — ничего не играет.</summary>
        public event Action<MediaState?>? StateChanged;

        public async Task StartAsync()
        {
            _manager = await Smtc.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => { PickSession(); _ = RefreshAsync(); };
            _manager.SessionsChanged += (_, _) => { PickSession(); _ = RefreshAsync(); };
            PickSession();
            await RefreshAsync();
        }

        private void PickSession()
        {
            if (_manager == null) return;

            Smtc.GlobalSystemMediaTransportControlsSession? next = null;

            try
            {
                next = _manager.GetCurrentSession();

                // Если «текущая» сессия на паузе, а где-то другое реально играет — берём то
                var playing = _manager.GetSessions().FirstOrDefault(s =>
                    s.GetPlaybackInfo()?.PlaybackStatus ==
                    Smtc.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

                if (playing != null && (next == null ||
                    next.GetPlaybackInfo()?.PlaybackStatus !=
                    Smtc.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
                {
                    next = playing;
                }
            }
            catch
            {
                // сессия могла закрыться прямо во время опроса
            }

            if (ReferenceEquals(next, _session)) return;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            _session = next;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }

        private void OnMediaChanged(Smtc.GlobalSystemMediaTransportControlsSession s, Smtc.MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();

        private void OnPlaybackChanged(Smtc.GlobalSystemMediaTransportControlsSession s, Smtc.PlaybackInfoChangedEventArgs e)
        {
            // при паузе/плее может смениться, какая сессия главная
            PickSession();
            _ = RefreshAsync();
        }

        private void OnTimelineChanged(Smtc.GlobalSystemMediaTransportControlsSession s, Smtc.TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

        public async Task RefreshAsync()
        {
            await _refreshLock.WaitAsync();
            try
            {
                StateChanged?.Invoke(await ReadStateAsync());
            }
            catch
            {
                StateChanged?.Invoke(null);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<MediaState?> ReadStateAsync()
        {
            var session = _session;
            if (session == null) return null;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            string appName = PrettyAppName(session.SourceAppUserModelId);
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            string key = $"{appName}|{props.Title}|{artist}";

            // Обложку читаем только при смене трека — это самое дорогое
            if (key != _thumbKey || _thumbCache == null)
            {
                _thumbCache = await ReadThumbnailAsync(props.Thumbnail);
                _thumbKey = key;
            }

            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            var controls = playback?.Controls;

            return new MediaState(
                Title: props.Title,
                Artist: artist ?? "",
                AppName: appName,
                Thumbnail: _thumbCache,
                IsPlaying: playback?.PlaybackStatus ==
                           Smtc.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Position: position < TimeSpan.Zero ? TimeSpan.Zero : position,
                Duration: duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                PositionUpdatedAt: timeline.LastUpdatedTime,
                CanPrev: controls?.IsPreviousEnabled ?? false,
                CanNext: controls?.IsNextEnabled ?? false,
                CanSeek: controls?.IsPlaybackPositionEnabled ?? false);
        }

        private static async Task<byte[]?> ReadThumbnailAsync(global::Windows.Storage.Streams.IRandomAccessStreamReference? reference)
        {
            if (reference == null) return null;

            try
            {
                using var ras = await reference.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string PrettyAppName(string? aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";

            string id = aumid.ToLowerInvariant();

            if (id.Contains("spotify")) return "Spotify";
            if (id.Contains("yandex") && id.Contains("music")) return "Яндекс Музыка";
            if (id.Contains("chrome")) return "Chrome";
            if (id.Contains("msedge")) return "Edge";
            if (id.Contains("firefox") || id == "308046b0af4a39cb") return "Firefox";
            if (id.Contains("opera")) return "Opera";
            if (id.Contains("yandex")) return "Яндекс Браузер";
            if (id.Contains("zunemusic")) return "Медиаплеер";
            if (id.Contains("telegram")) return "Telegram";
            if (id.Contains("vlc")) return "VLC";
            if (id.Contains("aimp")) return "AIMP";

            // "Something.exe" → "Something", "Pkg_hash!App" → "Pkg"
            string name = aumid.Split('!')[0];
            name = name.Split('_')[0];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
            return name;
        }

        // -------------------------------------------------------------- управление

        public async Task TogglePlayPauseAsync()
        {
            if (_session != null) await _session.TryTogglePlayPauseAsync();
        }

        public async Task NextAsync()
        {
            if (_session != null) await _session.TrySkipNextAsync();
        }

        public async Task PreviousAsync()
        {
            if (_session != null) await _session.TrySkipPreviousAsync();
        }

        public async Task SeekAsync(TimeSpan position)
        {
            var session = _session;
            if (session == null) return;
            var start = session.GetTimelineProperties().StartTime;
            await session.TryChangePlaybackPositionAsync((start + position).Ticks);
        }
    }
}
