using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DynamicIsland.Core;
using Bt = global::Windows.Devices.Bluetooth;
using Enumeration = global::Windows.Devices.Enumeration;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// «Подключено: AirPods Pro» / «Отключено». Следит за сопряжёнными Bluetooth-устройствами
    /// и их свойством IsConnected.
    /// </summary>
    public sealed class BluetoothModule : IStartable, IDisposable
    {
        private const string IsConnectedKey = "System.Devices.Aep.IsConnected";

        private static readonly Regex AudioName = new(
            @"airpods|buds|beats|headphone|headset|наушник|гарнитур|earbuds|wh-|wf-|jbl|marshall|soundcore|sennheiser|bose|freebuds|redmi buds|nothing ear|haylou|qcy|speaker|колонк",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IIslandHost _host;
        private readonly object _lock = new();
        private readonly Dictionary<string, (string Name, bool Connected)> _devices = new();
        private readonly Dictionary<string, DateTime> _lastAlert = new();
        private Enumeration.DeviceWatcher? _watcher;
        private bool _ready;

        public BluetoothModule(IIslandHost host) => _host = host;

        public void Start()
        {
            string selector = Bt.BluetoothDevice.GetDeviceSelectorFromPairingState(true);

            _watcher = Enumeration.DeviceInformation.CreateWatcher(
                selector,
                new[] { IsConnectedKey },
                Enumeration.DeviceInformationKind.AssociationEndpoint);

            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            // до EnumerationCompleted приходят уже подключённые — про них не кричим
            _watcher.EnumerationCompleted += (_, _) => { lock (_lock) _ready = true; };
            _watcher.Start();
        }

        private static bool ReadConnected(IReadOnlyDictionary<string, object> props) =>
            props.TryGetValue(IsConnectedKey, out var value) && value is bool b && b;

        private void OnAdded(Enumeration.DeviceWatcher sender, Enumeration.DeviceInformation info)
        {
            lock (_lock)
            {
                bool connected = ReadConnected(info.Properties);
                string name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth-устройство" : info.Name;
                _devices[info.Id] = (name, connected);

                if (_ready && connected) Notify(info.Id, name, true);
            }
        }

        private void OnUpdated(Enumeration.DeviceWatcher sender, Enumeration.DeviceInformationUpdate update)
        {
            lock (_lock)
            {
                if (!update.Properties.TryGetValue(IsConnectedKey, out var value) || value is not bool connected) return;
                if (!_devices.TryGetValue(update.Id, out var device)) return;
                if (device.Connected == connected) return;

                _devices[update.Id] = (device.Name, connected);
                if (_ready) Notify(update.Id, device.Name, connected);
            }
        }

        private void OnRemoved(Enumeration.DeviceWatcher sender, Enumeration.DeviceInformationUpdate update)
        {
            lock (_lock) _devices.Remove(update.Id);
        }

        private void Notify(string id, string name, bool connected)
        {
            // AirPods любят «моргать» подключением — не чаще раза в 4 секунды
            string key = id + connected;
            if (_lastAlert.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(4)) return;
            _lastAlert[key] = DateTime.UtcNow;

            bool audio = AudioName.IsMatch(name);

            _host.ShowAlert(new IslandAlert
            {
                Key = "bt:" + id,
                Glyph = audio ? Glyphs.Headphones : Glyphs.Bluetooth,
                Accent = connected ? Palette.Blue : Palette.Gray,
                Title = connected ? "Подключено" : "Отключено",
                Subtitle = name,
                Duration = TimeSpan.FromSeconds(3),
            });
        }

        public void Dispose()
        {
            if (_watcher == null) return;
            try
            {
                if (_watcher.Status is Enumeration.DeviceWatcherStatus.Started
                    or Enumeration.DeviceWatcherStatus.EnumerationCompleted)
                    _watcher.Stop();
            }
            catch { }
        }
    }
}
