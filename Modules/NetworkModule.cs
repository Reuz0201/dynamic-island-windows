using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using DynamicIsland.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Трафик по процессам через ETW (то же, чем пользуется «Монитор ресурсов»):
    /// «Telegram · отправляет 3 МБ/с», «Steam · качает 20 МБ/с».
    ///
    /// VPN-клиенты (kittend и т.п.) и системные процессы игнорируются — их трафик
    /// это «обёртка» над трафиком настоящих приложений, которые и так видны.
    /// Нужен запуск от администратора.
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

        private sealed class ProcInfo
        {
            public string Name = "";
            public string Friendly = "";
            public DateTime ResolvedAt;
        }

        private readonly IIslandHost _host;
        private readonly Func<bool> _folderDownloadActive;
        private readonly object _lock = new();

        // pid → (отправлено, получено) за текущую секунду
        private Dictionary<int, (long Up, long Down)> _bytes = new();
        private readonly Dictionary<int, ProcInfo> _procs = new();
        private readonly Dictionary<string, Flow> _flows = new();
        private readonly HashSet<string> _hiddenApps;
        private readonly string _hiddenFile;
        private readonly int _ownPid = Environment.ProcessId;

        private TraceEventSession? _session;
        private Thread? _thread;
        private Timer? _timer;
        private int _tick;

        public NetworkModule(IIslandHost host, Func<bool> folderDownloadActive)
        {
            _host = host;
            _folderDownloadActive = folderDownloadActive;

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
            _hiddenFile = Path.Combine(dir, "network-hidden-apps.txt");
            _hiddenApps = LoadHidden();
        }

        public static bool IsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public void Start()
        {
            if (!IsAdmin())
            {
                _host.ShowAlert(new IslandAlert
                {
                    Glyph = Glyphs.Upload,
                    Accent = Palette.Orange,
                    Title = "Трафик по приложениям выключен",
                    Subtitle = "Запусти через start.bat — нужны права администратора",
                    Duration = TimeSpan.FromSeconds(6),
                });
                return;
            }

            _thread = new Thread(RunSession) { IsBackground = true, Name = "ETW network" };
            _thread.Start();

            _timer = new Timer(_ => Tick(), null, 1000, 1000);
        }

        // ------------------------------------------------------------ ETW

        private void RunSession()
        {
            try
            {
                // Своё имя сессии (Win8+), чтобы не драться с другими программами за «NT Kernel Logger»
                _session = new TraceEventSession("DynamicIsland-Network") { StopOnDispose = true };
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                var k = _session.Source.Kernel;
                k.TcpIpSend += d => Add(d.ProcessID, d.size, upload: true);
                k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size, upload: true);
                k.UdpIpSend += d => Add(d.ProcessID, d.size, upload: true);
                k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size, upload: true);
                k.TcpIpRecv += d => Add(d.ProcessID, d.size, upload: false);
                k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size, upload: false);
                k.UdpIpRecv += d => Add(d.ProcessID, d.size, upload: false);
                k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size, upload: false);

                _session.Source.Process(); // блокирует поток, пока сессию не остановят
            }
            catch (Exception ex)
            {
                _host.ShowAlert(new IslandAlert
                {
                    Glyph = Glyphs.Upload,
                    Accent = Palette.Red,
                    Title = "Не удалось следить за трафиком",
                    Subtitle = ex.Message,
                    Duration = TimeSpan.FromSeconds(8),
                });
            }
        }

        private void Add(int pid, int size, bool upload)
        {
            if (pid <= 4 || pid == _ownPid || size <= 0) return;

            lock (_lock)
            {
                _bytes.TryGetValue(pid, out var cur);
                _bytes[pid] = upload ? (cur.Up + size, cur.Down) : (cur.Up, cur.Down + size);
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
            Dictionary<int, (long Up, long Down)> snapshot;
            lock (_lock)
            {
                snapshot = _bytes;
                _bytes = new Dictionary<int, (long Up, long Down)>();
            }

            _tick++;

            // Склеиваем процессы одного приложения (у Chrome их десятки)
            var perApp = new Dictionary<string, (string Friendly, double Up, double Down)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, b) in snapshot)
            {
                var info = Resolve(pid);
                if (info == null || ShouldIgnore(info.Name)) continue;

                perApp.TryGetValue(info.Name, out var cur);
                perApp[info.Name] = (info.Friendly, cur.Up + b.Up, cur.Down + b.Down);
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

            // Забываем процессы, которых давно нет
            if (_tick % 60 == 0)
            {
                foreach (var pid in _procs.Keys.Where(p => !snapshot.ContainsKey(p)).ToList())
                    _procs.Remove(pid);
            }
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

        private ProcInfo? Resolve(int pid)
        {
            if (_procs.TryGetValue(pid, out var cached) && DateTime.UtcNow - cached.ResolvedAt < TimeSpan.FromMinutes(2))
                return cached;

            try
            {
                using var p = Process.GetProcessById(pid);
                string name = p.ProcessName;
                string friendly = name;

                try
                {
                    string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) friendly = desc.Trim();
                }
                catch
                {
                    // к некоторым процессам доступа нет даже у админа — хватит имени exe
                }

                var info = new ProcInfo { Name = name, Friendly = friendly, ResolvedAt = DateTime.UtcNow };
                _procs[pid] = info;
                return info;
            }
            catch
            {
                return null; // процесс уже завершился
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            try { _session?.Dispose(); } catch { }
        }
    }
}
