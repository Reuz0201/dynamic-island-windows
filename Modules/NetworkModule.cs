using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using DynamicIsland.Core;
using DynamicIsland.Shared;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Трафик по приложениям: «Telegram · отправляет 3 МБ/с», «Steam · качает 20 МБ/с».
    ///
    /// Сами данные собирает помощник DynamicIsland.NetHelper (ему нужны права админа для ETW),
    /// а остров читает их через pipe — поэтому остров работает без админа.
    /// VPN-клиенты (kittend и т.п.) и системные процессы игнорируются.
    /// </summary>
    public sealed class NetworkModule : IStartable, IDisposable
    {
        // Пороги на одно приложение
        private const double UploadStartBps = 400 * 1024;
        private const double DownloadStartBps = 1.5 * 1024 * 1024;
        private const int UploadStartAfter = 3;
        private const int DownloadStartAfter = 4;
        private const double StopBelowFactor = 0.15;
        private const int StopAfterSeconds = 4;

        // Системное и служебное — не показываем
        private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "svchost", "lsass", "services", "wininit", "csrss", "smss",
            "msmpeng", "nissrv", "searchindexer", "searchhost", "searchapp", "wudfhost", "dashost",
            "spoolsv", "dllhost", "backgroundtaskhost", "runtimebroker", "dynamicisland",
        };

        // Браузеры и мессенджеры постоянно что-то подкачивают (видео, превью) —
        // для них показываем только ОТПРАВКУ, а скачивание файлов ловит папка «Загрузки»
        private static readonly string[] NoDownloadFlow =
        {
            "chrome", "msedge", "firefox", "opera", "browser", "brave", "vivaldi", "arc",
            "telegram", "discord", "whatsapp", "slack", "teams", "zoom", "spotify", "yandex",
        };

        private sealed class Flow
        {
            public string Id = "";
            public string App = "";
            public string Friendly = "";
            public bool Upload;
            public int AboveSeconds;
            public int BelowSeconds;
            public bool Active;
            public bool Hidden;
            public double Speed;
            public double Total;
            public DateTime StartedAt;
            public int SeenTick;
        }

        private readonly IIslandHost _host;
        private readonly Func<bool> _folderDownloadActive;
        private readonly object _lock = new();

        // приложение → (имя для показа, отправлено, получено) за текущую секунду
        private Dictionary<string, (string Friendly, long Up, long Down)> _bytes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Flow> _flows = new();
        private readonly HashSet<string> _hiddenApps;
        private readonly string _hiddenFile;
        private Thread? _thread;
        private Timer? _timer;
        private int _tick;
        private volatile bool _stopped;
        private NamedPipeClientStream? _pipe;

        public NetworkModule(IIslandHost host, Func<bool> folderDownloadActive)
        {
            _host = host;
            _folderDownloadActive = folderDownloadActive;

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
            _hiddenFile = Path.Combine(dir, "network-hidden-apps.txt");
            _hiddenApps = LoadHidden();
        }

        public static bool IsAdmin() => Elevation.IsAdmin();

        public void Start()
        {
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "NetHelper pipe" };
            _thread.Start();
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
        }

        // ------------------------------------------------------------ связь с помощником

        private void ReadLoop()
        {
            try
            {
                if (!Elevation.IsHelperInstalled())
                {
                    Log.Info("Помощник трафика не установлен — спрашиваем разрешение");
                    AskForHelper();
                    // ждём, пока поставят (или навсегда, если отказались — это фоновый поток)
                    while (!_stopped && !Elevation.IsHelperInstalled()) Thread.Sleep(15000);
                }

                bool launched = false;
                while (!_stopped)
                {
                    try
                    {
                        using var pipe = new NamedPipeClientStream(".", "DynamicIsland.Net", PipeDirection.In);
                        _pipe = pipe;
                        try
                        {
                            pipe.Connect(1500);
                        }
                        catch (TimeoutException)
                        {
                            if (!launched)
                            {
                                launched = true;
                                Elevation.RunHelper();
                            }
                            continue;
                        }

                        Log.Info("Подключились к помощнику трафика");
                        using var reader = new StreamReader(pipe);
                        string? line;
                        while (!_stopped && (line = reader.ReadLine()) != null) Consume(line);
                    }
                    catch (Exception ex) when (!_stopped)
                    {
                        Log.Warn("Связь с помощником: " + ex.Message);
                    }

                    // помощник закрылся — перезапустим
                    launched = false;
                    Thread.Sleep(2000);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Модуль трафика", ex);
            }
        }

        private void AskForHelper()
        {
            _host.ShowAlert(new IslandAlert
            {
                Key = "nethelper",
                Glyph = Glyphs.Upload,
                Accent = Palette.Purple,
                Title = "Трафик приложений",
                Subtitle = "Нужно разрешение администратора один раз",
                Detail = "дальше помощник запускается сам, без окон",
                Duration = TimeSpan.FromSeconds(20),
                Actions =
                {
                    new IslandAction
                    {
                        Glyph = "\uE72E",
                        Text = "Разрешить",
                        OnClick = () => ThreadPool.QueueUserWorkItem(_ =>
                        {
                            if (Elevation.RequestHelperInstall()) Elevation.RunHelper();
                        }),
                    },
                    new IslandAction { Glyph = Glyphs.Close, Tooltip = "Не сейчас" },
                },
            });
        }

        private sealed class Item
        {
            public string n { get; set; } = "";
            public string f { get; set; } = "";
            public long u { get; set; }
            public long d { get; set; }
        }

        private void Consume(string line)
        {
            List<Item>? items;
            try { items = JsonSerializer.Deserialize<List<Item>>(line); }
            catch { return; }
            if (items == null) return;

            lock (_lock)
            {
                foreach (var it in items)
                {
                    _bytes.TryGetValue(it.n, out var cur);
                    _bytes[it.n] = (it.f, cur.Up + it.u, cur.Down + it.d);
                }
            }
        }

        // ------------------------------------------------------------ раз в секунду

        private readonly object _tickLock = new();

        private void Tick()
        {
            // если прошлая секунда ещё считается (медленно резолвились процессы) — пропускаем
            if (!Monitor.TryEnter(_tickLock)) return;
            try { TickCore(); }
            catch { }
            finally { Monitor.Exit(_tickLock); }
        }

        private void TickCore()
        {
            Dictionary<string, (string Friendly, long Up, long Down)> snapshot;
            lock (_lock)
            {
                snapshot = _bytes;
                _bytes = new Dictionary<string, (string Friendly, long Up, long Down)>(StringComparer.OrdinalIgnoreCase);
            }

            _tick++;

            var perApp = new Dictionary<string, (string Friendly, double Up, double Down)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, b) in snapshot)
            {
                if (ShouldIgnore(name)) continue;
                perApp[name] = (b.Friendly, b.Up, b.Down);
            }

            bool folderDownload = _folderDownloadActive();

            foreach (var (app, t) in perApp)
            {
                UpdateFlow(app, t.Friendly, upload: true, t.Up);

                bool noDownload = folderDownload || NoDownloadFlow.Any(n => app.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (!noDownload) UpdateFlow(app, t.Friendly, upload: false, t.Down);
            }

            // Приложения, которые в эту секунду молчали, тоже надо «прокрутить» (0 байт)
            foreach (var flow in _flows.Values.Where(f => f.SeenTick != _tick).ToList())
                UpdateFlow(flow.App, flow.Friendly, flow.Upload, 0);
        }

        private void UpdateFlow(string app, string friendly, bool upload, double bps)
        {
            string id = $"net:{(upload ? "up" : "down")}:{app.ToLowerInvariant()}";
            double threshold = upload ? UploadStartBps : DownloadStartBps;
            int startAfter = upload ? UploadStartAfter : DownloadStartAfter;

            if (!_flows.TryGetValue(id, out var flow))
            {
                // новые потоки заводим, только если есть хоть какой-то заметный трафик
                if (bps < threshold) return;
                flow = new Flow { Id = id, App = app, Friendly = friendly, Upload = upload };
                _flows[id] = flow;
            }

            flow.SeenTick = _tick;
            flow.Speed = flow.Speed <= 0 ? bps : flow.Speed * 0.5 + bps * 0.5;

            if (!flow.Active)
            {
                flow.AboveSeconds = bps >= threshold ? flow.AboveSeconds + 1 : 0;
                if (flow.AboveSeconds == 0)
                {
                    _flows.Remove(id);
                    return;
                }
                if (flow.AboveSeconds < startAfter) return;

                flow.Active = true;
                flow.BelowSeconds = 0;
                flow.Total = bps * startAfter;
                flow.StartedAt = DateTime.UtcNow;
            }
            else
            {
                flow.Total += bps;
                flow.BelowSeconds = bps < threshold * StopBelowFactor ? flow.BelowSeconds + 1 : 0;
                if (flow.BelowSeconds >= StopAfterSeconds)
                {
                    Finish(flow);
                    return;
                }
            }

            if (flow.Hidden) return;

            string verb = upload ? "отправляет" : "качает";
            string done = upload ? "отправлено" : "скачано";

            _host.SetActivity(new LiveActivity(flow.Id)
            {
                Order = upload ? 2 : 3,
                Glyph = upload ? Glyphs.Upload : Glyphs.Download,
                Accent = upload ? Palette.Purple : Palette.Blue,
                Title = flow.Friendly,
                Subtitle = $"{verb} · {Format.Speed(flow.Speed)} · {done} {Format.Size(flow.Total)}",
                Badge = (upload ? "↑ " : "↓ ") + Format.Speed(flow.Speed),
                Progress = double.NaN,
                Actions = new List<IslandAction>
                {
                    new() { Glyph = Glyphs.Close, Tooltip = "Скрыть сейчас", OnClick = () => HideNow(flow) },
                    new() { Glyph = "", Tooltip = $"Никогда не показывать {flow.Friendly}", OnClick = () => HideForever(flow) },
                },
            });
        }

        private void Finish(Flow flow)
        {
            _flows.Remove(flow.Id);
            _host.RemoveActivity(flow.Id);

            if (flow.Hidden || flow.Total < 5 * 1024 * 1024) return;

            var took = DateTime.UtcNow - flow.StartedAt - TimeSpan.FromSeconds(StopAfterSeconds);
            if (took < TimeSpan.FromSeconds(1)) took = TimeSpan.FromSeconds(1);

            _host.ShowAlert(new IslandAlert
            {
                Key = flow.Id + ":done",
                Glyph = flow.Upload ? Glyphs.Upload : Glyphs.Download,
                Accent = Palette.Green,
                Title = flow.Upload ? "Отправлено" : "Скачано",
                Subtitle = flow.Friendly,
                Detail = $"{Format.Size(flow.Total)} за {Format.HumanMinutes(TimeSpan.FromSeconds(Math.Round(took.TotalSeconds)))}",
                Duration = TimeSpan.FromSeconds(4),
            });
        }

        // ------------------------------------------------------------ скрытие

        private void HideNow(Flow flow)
        {
            flow.Hidden = true;
            _host.RemoveActivity(flow.Id);
        }

        private void HideForever(Flow flow)
        {
            HideNow(flow);
            lock (_hiddenApps)
            {
                _hiddenApps.Add(flow.App);
                SaveHidden();
            }
            _host.ShowAlert(new IslandAlert
            {
                Glyph = "",
                Accent = Palette.Gray,
                Title = "Больше не показываю",
                Subtitle = flow.Friendly,
                Detail = "Вернуть: удали строку из network-hidden-apps.txt в %AppData%\\DynamicIsland",
                Duration = TimeSpan.FromSeconds(5),
            });
        }

        private HashSet<string> LoadHidden()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(_hiddenFile))
                    foreach (var line in File.ReadAllLines(_hiddenFile))
                        if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
            }
            catch { }
            return set;
        }

        private void SaveHidden()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_hiddenFile)!);
                File.WriteAllLines(_hiddenFile, _hiddenApps.OrderBy(x => x));
            }
            catch { }
        }

        // ------------------------------------------------------------ процессы

        private bool ShouldIgnore(string name)
        {
            if (IgnoredProcesses.Contains(name)) return true;
            if (VpnModule.LooksLikeVpnName(name)) return true; // kittend, wireguard, sing-box...
            lock (_hiddenApps) return _hiddenApps.Contains(name);
        }

        public void Dispose()
        {
            _stopped = true;
            _timer?.Dispose();
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
