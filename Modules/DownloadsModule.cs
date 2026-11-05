using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Следит за папкой «Загрузки» (и подпапками, например Telegram Desktop):
    /// пока файл качается — живая активность с размером и скоростью,
    /// когда докачался — алерт «Скачано» с кнопками «Открыть» и «Папка».
    /// </summary>
    public sealed class DownloadsModule : IStartable, IDisposable
    {
        // Временные файлы браузеров и торрент-клиентов
        private static readonly string[] TempExtensions =
        {
            ".crdownload", // Chrome, Edge, Яндекс, Opera
            ".part",       // Firefox
            ".partial",    // старый Edge
            ".download",   // Safari
            ".opdownload", // старая Opera
            ".!qb",        // qBittorrent
            ".!ut",        // uTorrent
            ".tmp",
        };

        private sealed class Tracked
        {
            public string Path = "";
            public long Size;
            public long SizeAtLastTick;
            public double Speed;
            public DateTime FirstSeen = DateTime.UtcNow;
            public DateTime LastGrow = DateTime.UtcNow;
            public DateTime? CompleteAfter;
            public bool ShownAsActivity;
            public long PeakSize;
            /// <summary>Файл не создан, а просто изменился (старый файл открыли/пересохранили).</summary>
            public bool FromChange;
        }

        private readonly IIslandHost _host;
        private readonly object _lock = new();
        private readonly Dictionary<string, Tracked> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _recentlyCompleted = new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? _watcher;
        private Timer? _timer;
        private string _root = "";

        public DownloadsModule(IIslandHost host) => _host = host;

        public bool HasActiveDownloads
        {
            get { lock (_lock) return _files.Values.Any(f => f.ShownAsActivity); }
        }

        public void Start()
        {
            _root = GetDownloadsFolder();
            if (!Directory.Exists(_root)) return;

            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Touch(e.FullPath, created: true);
            _watcher.Changed += (_, e) => Touch(e.FullPath, created: false);
            _watcher.Renamed += OnRenamed;
            _watcher.EnableRaisingEvents = true;

            _timer = new Timer(_ => Poll(), null, 1000, 1000);
        }

        // ------------------------------------------------------------ события ФС

        private static bool IsTemp(string path) =>
            TempExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        private static bool IsIgnored(string path)
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("~$") || name.StartsWith(".")) return true;
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void Touch(string path, bool created)
        {
            if (IsIgnored(path)) return;
            try
            {
                if (Directory.Exists(path)) return;
            }
            catch { return; }

            lock (_lock)
            {
                if (_recentlyCompleted.TryGetValue(path, out var done) && DateTime.UtcNow - done < TimeSpan.FromSeconds(10))
                    return;

                if (_files.ContainsKey(path)) return;

                var tracked = new Tracked { Path = path };
                if (!created)
                {
                    // старый файл: считаем загрузкой, только если он реально растёт
                    tracked.FromChange = true;
                    tracked.LastGrow = DateTime.MinValue;
                    try { tracked.SizeAtLastTick = new FileInfo(path).Length; } catch { }
                }
                _files[path] = tracked;
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            lock (_lock)
            {
                if (_files.Remove(e.OldFullPath, out var old))
                {
                    // переносим статистику: «file.zip.crdownload» → «file.zip»
                    if (old.ShownAsActivity) _host.RemoveActivity(ActivityId(old.Path));
                    old.Path = e.FullPath;
                    old.ShownAsActivity = false;

                    // Временный файл стал нормальным — значит, докачался
                    if (IsTemp(e.OldFullPath) && !IsTemp(e.FullPath))
                        old.CompleteAfter = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);

                    _files[e.FullPath] = old;
                    return;
                }
            }

            // переименовали обычный файл (не загрузку) — это не скачивание
            if (IsTemp(e.FullPath)) Touch(e.FullPath, created: true);
        }

        // ------------------------------------------------------------ опрос раз в секунду

        private void Poll()
        {
            var now = DateTime.UtcNow;
            var completed = new List<Tracked>();

            lock (_lock)
            {
                foreach (var f in _files.Values.ToList())
                {
                    long size;
                    try
                    {
                        var info = new FileInfo(f.Path);
                        if (!info.Exists)
                        {
                            Drop(f);
                            continue;
                        }
                        size = info.Length;
                    }
                    catch
                    {
                        continue;
                    }

                    long delta = size - f.SizeAtLastTick;
                    f.SizeAtLastTick = size;
                    f.Size = size;
                    f.PeakSize = Math.Max(f.PeakSize, size);
                    // сглаженная скорость
                    f.Speed = f.Speed <= 0 ? Math.Max(0, delta) : f.Speed * 0.6 + Math.Max(0, delta) * 0.4;
                    if (delta > 0) f.LastGrow = now;

                    if (f.FromChange && f.LastGrow == DateTime.MinValue)
                    {
                        if (now - f.FirstSeen > TimeSpan.FromSeconds(5)) Drop(f);
                        continue;
                    }

                    bool temp = IsTemp(f.Path);
                    bool growing = now - f.LastGrow < TimeSpan.FromSeconds(3);

                    if (f.CompleteAfter is DateTime after)
                    {
                        if (now >= after)
                        {
                            completed.Add(f);
                            Drop(f);
                        }
                        continue;
                    }

                    if (!temp && size > 0 && !growing && now - f.FirstSeen > TimeSpan.FromSeconds(2))
                    {
                        completed.Add(f);
                        Drop(f);
                        continue;
                    }

                    // Пустой файл-заглушка (Firefox так делает) — ждём
                    if (!temp && size == 0)
                    {
                        if (now - f.FirstSeen > TimeSpan.FromMinutes(10)) Drop(f);
                        continue;
                    }

                    // В процессе: показываем, если качается дольше секунды
                    if ((temp || growing) && now - f.FirstSeen > TimeSpan.FromSeconds(1))
                    {
                        f.ShownAsActivity = true;
                        PushActivity(f, temp && size == 0);
                    }

                    // зависший временный файл (браузер поставил на паузу) — через 30 мин забываем
                    if (temp && now - f.LastGrow > TimeSpan.FromMinutes(30)) Drop(f);
                }

                foreach (var key in _recentlyCompleted.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
                    _recentlyCompleted.Remove(key);
            }

            foreach (var f in completed) AnnounceCompleted(f);
        }

        private void Drop(Tracked f)
        {
            _files.Remove(f.Path);
            if (f.ShownAsActivity) _host.RemoveActivity(ActivityId(f.Path));
        }

        private static string ActivityId(string path) => "dl:" + path.ToLowerInvariant();

        private static string DisplayName(string path)
        {
            string name = Path.GetFileName(path);
            foreach (var ext in TempExtensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^ext.Length];
                    break;
                }
            }
            if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Не подтверждено", StringComparison.OrdinalIgnoreCase))
                return "Новый файл";
            return name;
        }

        private void PushActivity(Tracked f, bool starting)
        {
            bool stalled = DateTime.UtcNow - f.LastGrow > TimeSpan.FromSeconds(3);
            string speed = stalled ? "на паузе" : Format.Speed(f.Speed);

            string folder = _root;
            string path = f.Path;

            _host.SetActivity(new LiveActivity(ActivityId(f.Path))
            {
                Order = 1,
                Glyph = Glyphs.Download,
                Accent = Palette.Blue,
                Title = DisplayName(f.Path),
                Subtitle = starting ? "начинается…" : $"{Format.Size(f.Size)} · {speed}",
                Badge = stalled ? Format.Size(f.Size) : Format.Speed(f.Speed),
                Progress = double.NaN, // размер всего файла браузеры не сообщают
                Actions = new List<IslandAction>
                {
                    new() { Glyph = Glyphs.Folder, Tooltip = "Открыть папку", OnClick = () => ShowInFolder(path, folder) },
                },
            });
        }

        private void AnnounceCompleted(Tracked f)
        {
            string path = f.Path;
            lock (_lock) _recentlyCompleted[path] = DateTime.UtcNow;

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            if (size == 0) return;

            string? sub = Path.GetDirectoryName(path);
            string where = sub != null && !sub.Equals(_root, StringComparison.OrdinalIgnoreCase)
                ? $"в {Path.GetFileName(sub)}"
                : "в Загрузках";

            _host.ShowAlert(new IslandAlert
            {
                Key = "dl-done:" + path,
                Glyph = Glyphs.Download,
                Accent = Palette.Green,
                Title = "Скачано",
                Subtitle = Path.GetFileName(path),
                Detail = $"{Format.Size(size)} · {where}",
                Duration = TimeSpan.FromSeconds(6),
                Actions = new List<IslandAction>
                {
                    new() { Glyph = Glyphs.OpenFile, Text = "Открыть", OnClick = () => Open(path) },
                    new() { Glyph = Glyphs.Folder, Tooltip = "Показать в папке", OnClick = () => ShowInFolder(path, _root) },
                },
            });
        }

        // ------------------------------------------------------------ действия

        private static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        private static void ShowInFolder(string path, string fallbackFolder)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else
                    Process.Start(new ProcessStartInfo(fallbackFolder) { UseShellExecute = true });
            }
            catch { }
        }

        // ------------------------------------------------------------ папка «Загрузки»

        private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        private static string GetDownloadsFolder()
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    string path = Marshal.PtrToStringUni(ptr) ?? "";
                    Marshal.FreeCoTaskMem(ptr);
                    if (path.Length > 0) return path;
                }
            }
            catch { }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _watcher?.Dispose();
        }
    }
}
