using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Modules;
using DynamicIsland.Shared;
using DynamicIsland.Update;

namespace DynamicIsland
{
    /// <summary>Настройки, включение/выключение модулей, автозапуск и обновления.</summary>
    public partial class MainWindow
    {
        private IslandSettings _settings = new();
        private MediaState? _rawState;

        private readonly Dictionary<string, IDisposable> _moduleMap = new();
        private volatile DownloadsModule? _downloads;
        private Updater? _updater;

        private FileSystemWatcher? _settingsWatcher;
        private DispatcherTimer? _settingsDebounce;
        private DateTime? _lastCheckRequest;
        private bool? _appliedAutostart;

        // ================================================================ загрузка и слежение

        private void InitSettings()
        {
            _settings = IslandSettings.Load();
            _lastCheckRequest = _settings.UpdateCheckRequested;
            ApplySettings();

            try
            {
                Directory.CreateDirectory(IslandSettings.Folder);

                _settingsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _settingsDebounce.Tick += (_, _) =>
                {
                    _settingsDebounce.Stop();
                    ReloadSettings();
                };

                _settingsWatcher = new FileSystemWatcher(IslandSettings.Folder, "settings.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (_, _) => Dispatcher.InvokeAsync(() =>
                {
                    _settingsDebounce.Stop();
                    _settingsDebounce.Start();
                });
                _settingsWatcher.Changed += changed;
                _settingsWatcher.Created += changed;
                _settingsWatcher.Renamed += (s, e) => changed(s, e);
                _settingsWatcher.EnableRaisingEvents = true;
            }
            catch
            {
                // без слежения — просто применим при следующем запуске
            }
        }

        private void ReloadSettings()
        {
            var fresh = IslandSettings.Load();

            bool checkRequested = fresh.UpdateCheckRequested != null && fresh.UpdateCheckRequested != _lastCheckRequest;
            _lastCheckRequest = fresh.UpdateCheckRequested;

            _settings = fresh;
            ApplySettings();
            SyncModules();

            if (checkRequested) _ = _updater?.CheckAsync(manual: true);
        }

        // ================================================================ внешний вид

        private void ApplySettings()
        {
            var s = _settings;

            var bg = ParseColor(s.BackgroundColor, Colors.Black);
            var bgBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(s.BackgroundOpacity * 255), bg.R, bg.G, bg.B));
            bgBrush.Freeze();
            Island.Background = bgBrush;
            BubbleBorder.Background = bgBrush;

            var border = new Thickness(s.BorderEnabled ? 1 : 0);
            Island.BorderThickness = border;
            BubbleBorder.BorderThickness = border;

            try { FontFamily = new FontFamily($"{s.FontFamily}, Segoe UI"); } catch { }

            IslandRow.LayoutTransform = Math.Abs(s.Scale - 1) < 0.001
                ? Transform.Identity
                : new ScaleTransform(s.Scale, s.Scale);

            double k = Math.Max(1, s.Scale);
            Width = 600 * k;
            Height = 480 * k;
            PositionWindow();

            IdleLabel.Text = string.IsNullOrWhiteSpace(s.IdleText) ? "Тишина" : s.IdleText;
            EqCompact.Visibility = s.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
            EqExpanded.Visibility = s.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
            ToolsButtons.Visibility = s.ModuleTimers ? Visibility.Visible : Visibility.Collapsed;
            UpdateShelf();

            if (_alert == null) AnimateGlow(MusicAccent());
            UpdateProgressColor();
            UpdateCorners();

            // перерисовать всё с нуля (размеры, видимость элементов)
            _mode = (Mode)(-1);
            OnMediaState(_rawState);

            ApplyAutostart();
        }

        private void UpdateCorners()
        {
            double h = IslandHost.ActualHeight;
            if (h <= 0) h = IslandHost.Height;
            double pill = Math.Min(h / 2, 28);
            double r = Math.Max(4, pill * _settings.Roundness);

            Island.CornerRadius = new CornerRadius(r);
            Glow.CornerRadius = new CornerRadius(r);
            BubbleBorder.CornerRadius = new CornerRadius(Math.Max(4, 18 * _settings.Roundness));
        }

        private Color MusicAccent() =>
            _settings.AccentMode == "fixed" ? ParseColor(_settings.AccentColor, DefaultAccent) : _musicColor;

        private static Color ParseColor(string? text, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text.Trim()) is Color c)
                    return Color.FromRgb(c.R, c.G, c.B);
            }
            catch { }
            return fallback;
        }

        // ================================================================ модули

        private void SyncModules()
        {
            var s = _settings;

            Toggle("timers", s.ModuleTimers, () => _timers = new TimerModule(this), () =>
            {
                _timers = null;
                RemoveActivitiesWithPrefix("timer");
                RemoveActivitiesWithPrefix("stopwatch");
            });

            Toggle("battery", s.ModuleBattery, () => new BatteryModule(this), null);
            Toggle("bluetooth", s.ModuleBluetooth, () => new BluetoothModule(this), null);

            Toggle("downloads", s.ModuleDownloads, () => _downloads = new DownloadsModule(this), () =>
            {
                _downloads = null;
                RemoveActivitiesWithPrefix("dl:");
            });

            // трафик по приложениям (ETW), VPN-клиенты игнорируются
            Toggle("network", s.ModuleNetwork, () => new NetworkModule(this, () => _downloads?.HasActiveDownloads == true),
                () => RemoveActivitiesWithPrefix("net:"));

            Toggle("vpn", s.ModuleVpn, () => new VpnModule(this), () => SetStatus("vpn", null, Colors.White, ""));

            Toggle("volume", s.ModuleVolume, () => new VolumeModule(this, volume: true, brightness: false), null);
            Toggle("brightness", s.ModuleBrightness, () => new VolumeModule(this, volume: false, brightness: true), null);
            Toggle("privacy", s.ModulePrivacy, () => new PrivacyModule(this), null);
            Toggle("drives", s.ModuleDrives, () => new DrivesModule(this), () =>
            {
                RemoveActivitiesWithPrefix("drive:");
                RemoveActivitiesWithPrefix("copy:");
            });
            Toggle("welcome", s.ModuleWelcome, () => new WelcomeModule(this), null);
            Toggle("toggles", s.ModuleToggles, () => new TogglesModule(this), null);
            Toggle("windows-update", s.ModuleWindowsUpdate, () => new WindowsUpdateModule(this), null);
            Toggle("devices", s.ModuleDevices, () => new DevicesModule(this), null);
        }

        private void Toggle(string key, bool enabled, Func<IDisposable> create, Action? cleanup)
        {
            bool running = _moduleMap.ContainsKey(key);

            if (enabled && !running)
            {
                IDisposable? module = null;
                try
                {
                    module = create();
                    if (module is IStartable startable) startable.Start();
                    _moduleMap[key] = module;
                }
                catch (Exception ex)
                {
                    Log.Error($"Модуль {key} не запустился", ex);
                    try { module?.Dispose(); } catch { }
                }
            }
            else if (!enabled && running)
            {
                try { _moduleMap[key].Dispose(); } catch { }
                _moduleMap.Remove(key);
                cleanup?.Invoke();
            }
        }

        private void RemoveActivitiesWithPrefix(string prefix)
        {
            foreach (var id in _activities.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _activities.Remove(id);
            Refresh();
        }

        private void DisposeSettingsAndModules()
        {
            foreach (var module in _moduleMap.Values)
            {
                try { module.Dispose(); } catch { }
            }
            _moduleMap.Clear();
            _spectrum.Dispose();
            _updater?.Dispose();
            _settingsWatcher?.Dispose();
        }

        // ================================================================ обновления

        private void StartUpdater()
        {
            _updater = new Updater(this, () => _settings, () => Dispatcher.InvokeAsync(Close));
            _updater.Start();

            if (!string.IsNullOrEmpty(App.RolledBackFrom))
            {
                Updater.MarkBadVersion(App.RolledBackFrom);
                ShowAlert(new IslandAlert
                {
                    Key = "update",
                    Glyph = Glyphs.Reset,
                    Accent = Palette.Orange,
                    Title = "Обновление откатил",
                    Subtitle = $"Версия {App.RolledBackFrom} не запустилась — вернул прошлую",
                    Detail = "подробности в логах (Настройки → Система)",
                    Duration = TimeSpan.FromSeconds(8),
                });
            }

            if (!string.IsNullOrEmpty(App.UpdatedTo))
            {
                // через 10 секунд нормальной работы говорим скрипту обновления «я жив» — иначе он откатит
                var ok = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                ok.Tick += (_, _) =>
                {
                    ok.Stop();
                    Updater.ConfirmHealthy();
                };
                ok.Start();

                ShowAlert(new IslandAlert
                {
                    Key = "update",
                    Glyph = Glyphs.Check,
                    Accent = Palette.Green,
                    Title = "Обновлено",
                    Subtitle = $"Dynamic Island {App.UpdatedTo}",
                    Duration = TimeSpan.FromSeconds(5),
                });
            }
        }

        // ================================================================ приложение настроек

        private void OpenSettingsApp()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "DynamicIsland.Settings.exe");

            if (!File.Exists(exe))
            {
                ShowAlert(new IslandAlert
                {
                    Glyph = "",
                    Accent = Palette.Orange,
                    Title = "Настройки не найдены",
                    Subtitle = "Нет DynamicIsland.Settings.exe рядом с островом",
                    Duration = TimeSpan.FromSeconds(5),
                });
                return;
            }

            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { }
        }

        // ================================================================ автозапуск

        private void ApplyAutostart()
        {
            bool want = _settings.Autostart;
            if (_appliedAutostart == want) return;
            _appliedAutostart = want;

            // через реестр (HKCU\...\Run) — админ не нужен
            Task.Run(() => Elevation.SetAutostart(want));
        }
    }
}
