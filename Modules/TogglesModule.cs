using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using Connectivity = global::Windows.Networking.Connectivity;
using Power = global::Windows.System.Power;
using Radios = global::Windows.Devices.Radios;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Переключатели, как в «Пункте управления» на iPhone: Wi-Fi и Bluetooth вкл/выкл,
    /// к какой Wi-Fi сети подключились, режим экономии заряда.
    /// </summary>
    public sealed class TogglesModule : IStartable, IDisposable
    {
        private const string WifiGlyph = "";
        private const string LeafGlyph = "";

        private readonly IIslandHost _host;
        private readonly Dictionary<Radios.Radio, Radios.RadioState> _radios = new();
        private Power.EnergySaverStatus _saver;
        private string _ssid = "";
        private readonly object _lock = new();

        public TogglesModule(IIslandHost host) => _host = host;

        public void Start()
        {
            _ = InitRadiosAsync();

            try
            {
                _saver = Power.PowerManager.EnergySaverStatus;
                Power.PowerManager.EnergySaverStatusChanged += OnSaverChanged;
            }
            catch (Exception ex)
            {
                Log.Info("Экономия заряда недоступна: " + ex.Message);
            }

            _ssid = CurrentSsid();
            Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        }

        // ------------------------------------------------------------ Wi-Fi / Bluetooth

        private async Task InitRadiosAsync()
        {
            try
            {
                foreach (var radio in await Radios.Radio.GetRadiosAsync())
                {
                    if (radio.Kind is not (Radios.RadioKind.WiFi or Radios.RadioKind.Bluetooth)) continue;
                    lock (_lock) _radios[radio] = radio.State;
                    radio.StateChanged += OnRadioChanged;
                }
            }
            catch (Exception ex)
            {
                Log.Info("Радиомодули недоступны: " + ex.Message);
            }
        }

        private void OnRadioChanged(Radios.Radio radio, object args)
        {
            bool on;
            lock (_lock)
            {
                var state = radio.State;
                if (_radios.TryGetValue(radio, out var old) && old == state) return;
                _radios[radio] = state;
                on = state == Radios.RadioState.On;
            }

            bool wifi = radio.Kind == Radios.RadioKind.WiFi;
            _host.ShowAlert(new IslandAlert
            {
                Key = wifi ? "toggle:wifi" : "toggle:bt",
                Glyph = wifi ? WifiGlyph : Glyphs.Bluetooth,
                Accent = on ? Palette.Blue : Palette.Gray,
                Title = $"{(wifi ? "Wi-Fi" : "Bluetooth")} {(on ? "включён" : "выключен")}",
                Duration = TimeSpan.FromSeconds(2.5),
                DropIfHidden = true,
            });
        }

        // ------------------------------------------------------------ сеть Wi-Fi

        private static string CurrentSsid()
        {
            try
            {
                foreach (var profile in Connectivity.NetworkInformation.GetConnectionProfiles())
                {
                    if (!profile.IsWlanConnectionProfile) continue;
                    if (profile.GetNetworkConnectivityLevel() == Connectivity.NetworkConnectivityLevel.None) continue;
                    string ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? "";
                    if (ssid.Length > 0) return ssid;
                }
            }
            catch { }
            return "";
        }

        private void OnNetworkChanged(object sender)
        {
            string ssid = CurrentSsid();
            lock (_lock)
            {
                if (ssid == _ssid) return;
                _ssid = ssid;
            }
            // отключение от сети не объявляем — его и так видно (а при выключении Wi-Fi уже сказали)
            if (ssid.Length == 0) return;

            _host.ShowAlert(new IslandAlert
            {
                Key = "toggle:wifi",
                Glyph = WifiGlyph,
                Accent = Palette.Blue,
                Title = "Wi-Fi",
                Subtitle = $"Подключено к «{ssid}»",
                Duration = TimeSpan.FromSeconds(3),
                DropIfHidden = true,
            });
        }

        // ------------------------------------------------------------ экономия заряда

        private void OnSaverChanged(object? sender, object e)
        {
            Power.EnergySaverStatus status;
            try { status = Power.PowerManager.EnergySaverStatus; } catch { return; }

            lock (_lock)
            {
                bool wasOn = _saver == Power.EnergySaverStatus.On;
                _saver = status;
                if (wasOn == (status == Power.EnergySaverStatus.On)) return;
            }

            bool on = status == Power.EnergySaverStatus.On;
            _host.ShowAlert(new IslandAlert
            {
                Key = "toggle:saver",
                Glyph = LeafGlyph,
                Accent = on ? Palette.Yellow : Palette.Gray,
                Title = on ? "Экономия заряда включена" : "Экономия заряда выключена",
                Subtitle = on ? "яркость и фоновые задачи урезаны" : "",
                Duration = TimeSpan.FromSeconds(3),
                DropIfHidden = true,
            });
        }

        public void Dispose()
        {
            try { Power.PowerManager.EnergySaverStatusChanged -= OnSaverChanged; } catch { }
            try { Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkChanged; } catch { }
            lock (_lock)
            {
                foreach (var radio in _radios.Keys)
                {
                    try { radio.StateChanged -= OnRadioChanged; } catch { }
                }
                _radios.Clear();
            }
        }
    }
}
