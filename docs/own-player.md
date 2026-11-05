# Как показать свой плеер в острове

Остров видит музыку через системные медиа-сессии Windows: это та же плашка, что появляется над регулятором громкости. Чтобы твой плеер туда попал, он должен сообщать Windows, что играет. Это делается через `SystemMediaTransportControls` (SMTC).

## C# / WPF (.NET 6+)

В `.csproj` плеера поставь Windows-TFM:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
```

Код (самый простой способ в десктопном приложении: завести `MediaPlayer` только ради его SMTC):

```csharp
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

public sealed class SystemMedia
{
    private readonly MediaPlayer _dummy = new();          // ничего не играет, нужен только ради SMTC
    private readonly SystemMediaTransportControls _smtc;

    public event Action? PlayPause, Next, Previous;

    public SystemMedia()
    {
        _dummy.CommandManager.IsEnabled = false;          // кнопками управляем сами
        _smtc = _dummy.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;

        _smtc.ButtonPressed += (_, e) =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                case SystemMediaTransportControlsButton.Pause: PlayPause?.Invoke(); break;
                case SystemMediaTransportControlsButton.Next: Next?.Invoke(); break;
                case SystemMediaTransportControlsButton.Previous: Previous?.Invoke(); break;
            }
        };
    }

    /// <summary>Вызывай при смене трека.</summary>
    public void SetTrack(string title, string artist, string? coverUrl)
    {
        var u = _smtc.DisplayUpdater;
        u.Type = MediaPlaybackType.Music;
        u.MusicProperties.Title = title;
        u.MusicProperties.Artist = artist;
        u.Thumbnail = coverUrl == null ? null : RandomAccessStreamReference.CreateFromUri(new Uri(coverUrl));
        u.Update();
    }

    /// <summary>Вызывай при паузе/плее.</summary>
    public void SetPlaying(bool playing) =>
        _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

    /// <summary>Раз в секунду — для полоски прогресса в острове.</summary>
    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = duration,
            Position = position,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = duration,
        });
    }
}
```

После этого плеер появится и в острове, и в системной плашке громкости, а кнопки на клавиатуре (⏯ ⏭ ⏮) тоже начнут им управлять.

Если обложка лежит локальным файлом, вместо `CreateFromUri` используй `RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(path))`.
