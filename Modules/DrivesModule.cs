using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicIsland.Core;
using DynamicIsland.Shared;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Флешки и внешние диски: «Флешка подключена · свободно 12 ГБ из 64» с кнопками
    /// «Открыть» и «Извлечь», а после безопасного извлечения — «Можно вынимать».
    /// Пока флешка вставлена, она висит строкой в раскрытом острове.
    /// </summary>
    public sealed class DrivesModule : IStartable, IDisposable
    {
        private const string UsbGlyph = "";

        private sealed class DriveState
        {
            public string Root = "";
            public string Letter = "";
            public string Label = "";
            public bool Removable;

            // --- копирование на диск: следим, как убывает свободное место
            public long LastFree = -1;
            public int WriteTicks;
            public int IdleTicks;
            public bool Copying;
            public long Written;
            public long Pending;
            public double Speed;
            public DateTime CopyStart;
        }

        private const double PollSeconds = 1.5;
        private const double CopyStartBps = 512 * 1024;
        private const double CopyIdleBps = 64 * 1024;

        private readonly IIslandHost _host;
        private readonly object _lock = new();
        private readonly Dictionary<string, DriveState> _drives = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _ejecting = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _internal = new(StringComparer.OrdinalIgnoreCase);
        private Timer? _timer;
        private bool _first = true;
        private int _tick;

        public DrivesModule(IIslandHost host) => _host = host;

        public void Start() => _timer = new Timer(_ => Poll(), null, 300, 1500);

        // ------------------------------------------------------------ опрос

        private void Poll()
        {
            try
            {
                lock (_lock) PollCore();
            }
            catch (Exception ex)
            {
                Log.Warn("Диски: " + ex.Message);
            }
        }

        private void PollCore()
        {
            _tick++;
            var current = new Dictionary<string, DriveInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                    if (!d.IsReady) continue;
                    current[d.Name] = d;
                }
                catch { }
            }

            if (_first)
            {
                _first = false;
                // встроенные диски, которые были при запуске, — не наши клиенты
                _internal = current.Values.Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var d in current.Values.Where(IsExternal))
                {
                    var state = Track(d);
                    PushActivity(d, state);
                }
                UpdateStatus();
                return;
            }

            // --- подключили
            foreach (var d in current.Values.Where(IsExternal))
            {
                if (_drives.ContainsKey(d.Name)) continue;
                var state = Track(d);
                Log.Info($"Подключён диск {state.Letter} «{state.Label}»");
                AnnounceArrival(d, state);
                PushActivity(d, state);
            }

            // --- отключили
            foreach (var root in _drives.Keys.Where(r => !current.ContainsKey(r)).ToList())
            {
                var state = _drives[root];
                _drives.Remove(root);
                _host.RemoveActivity(ActivityId(root));
                _host.RemoveActivity(CopyId(root));

                bool ejected = _ejecting.Remove(root);
                Log.Info($"Отключён диск {state.Letter} (извлечён через остров: {ejected})");

                _host.ShowAlert(new IslandAlert
                {
                    Key = "drive:" + root,
                    Glyph = ejected ? Glyphs.Check : UsbGlyph,
                    Accent = ejected ? Palette.Green : Palette.Gray,
                    Title = ejected ? "Можно вынимать" : (state.Removable ? "Флешка отключена" : "Диск отключён"),
                    Subtitle = $"{state.Label} ({state.Letter})",
                    Duration = TimeSpan.FromSeconds(3),
                });
            }

            // --- идёт ли запись на диск
            foreach (var d in current.Values.Where(d => _drives.ContainsKey(d.Name)))
                UpdateCopy(d, _drives[d.Name]);

            // --- раз в ~6 сек обновляем свободное место
            if (_tick % 4 == 0)
            {
                foreach (var d in current.Values.Where(d => _drives.ContainsKey(d.Name)))
                    PushActivity(d, _drives[d.Name]);
            }

            // --- извлечение зависло (диск занят)
            foreach (var (root, since) in _ejecting.ToList())
            {
                if (DateTime.UtcNow - since < TimeSpan.FromSeconds(8)) continue;
                _ejecting.Remove(root);
                if (!_drives.TryGetValue(root, out var state)) continue;

                _host.ShowAlert(new IslandAlert
                {
                    Key = "drive:" + root,
                    Glyph = UsbGlyph,
                    Accent = Palette.Orange,
                    Title = "Не получилось извлечь",
                    Subtitle = $"{state.Label} ({state.Letter}) сейчас занят",
                    Detail = "закрой файлы и окна Проводника с этим диском и попробуй ещё раз",
                    Duration = TimeSpan.FromSeconds(6),
                });
            }

            UpdateStatus();
        }

        // ------------------------------------------------------------ копирование

        private static string CopyId(string root) => "copy:" + root.ToUpperInvariant();

        private void UpdateCopy(DriveInfo d, DriveState state)
        {
            var (free, total, _) = Space(d);
            if (total <= 0) return;

            if (state.LastFree < 0)
            {
                state.LastFree = free;
                return;
            }

            long delta = state.LastFree - free; // > 0 — место убывает, значит пишут
            state.LastFree = free;
            double bps = Math.Max(0, delta) / PollSeconds;

            if (!state.Copying)
            {
                if (bps >= CopyStartBps)
                {
                    state.WriteTicks++;
                    state.Pending += delta;
                }
                else
                {
                    state.WriteTicks = 0;
                    state.Pending = 0;
                }

                if (state.WriteTicks < 2) return;

                state.Copying = true;
                state.Written = state.Pending;
                state.Speed = bps;
                state.IdleTicks = 0;
                state.CopyStart = DateTime.UtcNow;
                Log.Info($"Запись на {state.Letter} началась");
            }
            else
            {
                if (delta > 0) state.Written += delta;
                state.Speed = state.Speed * 0.6 + bps * 0.4;
                state.IdleTicks = bps < CopyIdleBps ? state.IdleTicks + 1 : 0;

                // ~4.5 сек тишины — копирование закончилось
                if (state.IdleTicks >= 3)
                {
                    FinishCopy(state);
                    return;
                }
            }

            _host.SetActivity(new LiveActivity(CopyId(state.Root))
            {
                Order = 5,
                Glyph = UsbGlyph,
                Accent = Palette.Cyan,
                Title = $"Копирую на {state.Label}",
                Subtitle = $"записано {Format.Size(state.Written)} · {Format.Speed(state.Speed)} · свободно {Format.Size(free)}",
                Badge = Format.Speed(state.Speed),
                Progress = 1 - (double)free / total, // заполненность диска растёт по ходу копирования
            });
        }

        private void FinishCopy(DriveState state)
        {
            state.Copying = false;
            state.WriteTicks = 0;
            state.Pending = 0;
            _host.RemoveActivity(CopyId(state.Root));

            var took = DateTime.UtcNow - state.CopyStart - TimeSpan.FromSeconds(PollSeconds * 3);
            if (took < TimeSpan.FromSeconds(1)) took = TimeSpan.FromSeconds(1);
            Log.Info($"Запись на {state.Letter} закончилась: {Format.Size(state.Written)}");

            string root = state.Root;
            _host.ShowAlert(new IslandAlert
            {
                Key = "drive:" + root,
                Glyph = Glyphs.Check,
                Accent = Palette.Green,
                Title = "Скопировано",
                Subtitle = $"{Format.Size(state.Written)} на {state.Label} ({state.Letter})",
                Detail = $"за {Format.HumanMinutes(TimeSpan.FromSeconds(Math.Round(took.TotalSeconds)))} · можно извлекать",
                Duration = TimeSpan.FromSeconds(6),
                Actions =
                {
                    new IslandAction { Glyph = UsbGlyph, Text = "Извлечь", OnClick = () => Eject(root) },
                },
            });
        }

        private bool IsExternal(DriveInfo d) =>
            d.DriveType == DriveType.Removable || !_internal.Contains(d.Name);

        private DriveState Track(DriveInfo d)
        {
            string label = "";
            try { label = d.VolumeLabel; } catch { }
            bool removable = d.DriveType == DriveType.Removable;

            var state = new DriveState
            {
                Root = d.Name,
                Letter = d.Name.TrimEnd('\\'),
                Label = string.IsNullOrWhiteSpace(label) ? (removable ? "Флешка" : "Внешний диск") : label.Trim(),
                Removable = removable,
            };
            _drives[d.Name] = state;
            return state;
        }

        // ------------------------------------------------------------ уведомления

        private static (long Free, long Total, string Format) Space(DriveInfo d)
        {
            try { return (d.AvailableFreeSpace, d.TotalSize, d.DriveFormat); }
            catch { return (0, 0, ""); }
        }

        private void AnnounceArrival(DriveInfo d, DriveState state)
        {
            var (free, total, format) = Space(d);
            string root = state.Root;

            _host.ShowAlert(new IslandAlert
            {
                Key = "drive:" + root,
                Glyph = UsbGlyph,
                Accent = Palette.Cyan,
                Title = state.Removable ? "Флешка подключена" : "Диск подключён",
                Subtitle = $"{state.Label} ({state.Letter})",
                Detail = total > 0
                    ? $"свободно {Format.Size(free)} из {Format.Size(total)}{(format.Length > 0 ? " · " + format : "")}"
                    : null,
                Progress = total > 0 ? 1 - (double)free / total : null,
                Duration = TimeSpan.FromSeconds(6),
                Actions =
                {
                    new IslandAction { Glyph = Glyphs.Folder, Text = "Открыть", OnClick = () => Open(root) },
                    new IslandAction { Glyph = UsbGlyph, Text = "Извлечь", OnClick = () => Eject(root) },
                },
            });
        }

        private static string ActivityId(string root) => "drive:" + root.ToUpperInvariant();

        private void PushActivity(DriveInfo d, DriveState state)
        {
            var (free, total, _) = Space(d);
            string root = state.Root;

            _host.SetActivity(new LiveActivity(ActivityId(root))
            {
                Order = 30,
                ExpandedOnly = true, // во второй кружок не лезем, это не «событие»
                Glyph = UsbGlyph,
                Accent = Palette.Cyan,
                Title = $"{state.Label} ({state.Letter})",
                Subtitle = total > 0 ? $"свободно {Format.Size(free)} из {Format.Size(total)}" : "",
                Progress = total > 0 ? 1 - (double)free / total : null,
                Actions =
                {
                    new IslandAction { Glyph = Glyphs.Folder, Tooltip = "Открыть", OnClick = () => Open(root) },
                    new IslandAction { Text = "Извлечь", OnClick = () => Eject(root) },
                },
            });
        }

        private void UpdateStatus()
        {
            if (_drives.Count == 0)
            {
                _host.SetStatus("drive", null, Palette.Cyan, "");
                return;
            }
            string list = string.Join(", ", _drives.Values.Select(d => $"{d.Label} ({d.Letter})"));
            _host.SetStatus("drive", UsbGlyph, Palette.Cyan, list);
        }

        // ------------------------------------------------------------ действия

        private static void Open(string root)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true }); } catch { }
        }

        private void Eject(string root)
        {
            lock (_lock)
            {
                if (!_drives.TryGetValue(root, out var state)) return;
                _ejecting[root] = DateTime.UtcNow;

                _host.ShowAlert(new IslandAlert
                {
                    Key = "drive:" + root,
                    Glyph = UsbGlyph,
                    Accent = Palette.Cyan,
                    Title = "Извлекаю…",
                    Subtitle = $"{state.Label} ({state.Letter})",
                    Duration = TimeSpan.FromSeconds(8),
                });
            }

            // то же, что «Извлечь» в меню Проводника (имя глагола не зависит от языка Windows)
            string path = root.Replace("'", "''");
            Task.Run(() => Elevation.RunPowerShell(
                "$item = (New-Object -ComObject Shell.Application).Namespace(17).ParseName('" + path + "');" +
                "if ($item) { $item.InvokeVerb('Eject') }; Start-Sleep -Seconds 3"));
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _host.SetStatus("drive", null, Palette.Cyan, "");
        }
    }
}
