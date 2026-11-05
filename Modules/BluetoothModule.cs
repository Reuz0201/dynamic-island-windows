using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using Adv = global::Windows.Devices.Bluetooth.Advertisement;
using Bt = global::Windows.Devices.Bluetooth;
using Enumeration = global::Windows.Devices.Enumeration;
using Streams = global::Windows.Storage.Streams;

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

            // AirPods/Beats сами рассказывают о заряде по Bluetooth LE — подслушаем
            if (connected && AppleAudio.IsMatch(name))
                _ = ReadAppleBatteryAsync(id, name);
        }

        // ------------------------------------------------------------ заряд AirPods

        private static readonly Regex AppleAudio = new(@"airpods|beats|powerbeats", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private async Task ReadAppleBatteryAsync(string id, string name)
        {
            var found = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Adv.BluetoothLEAdvertisementWatcher? watcher = null;

            try
            {
                watcher = new Adv.BluetoothLEAdvertisementWatcher { ScanningMode = Adv.BluetoothLEScanningMode.Active };
                watcher.Received += (_, args) =>
                {
                    // только свои наушники рядом, а не чужие в метро
                    if (args.RawSignalStrengthInDBm < -65) return;
                    foreach (var md in args.Advertisement.ManufacturerData)
                    {
                        if (md.CompanyId != 0x004C || md.Data.Length < 10) continue;
                        var data = new byte[md.Data.Length];
                        using (var reader = Streams.DataReader.FromBuffer(md.Data)) reader.ReadBytes(data);
                        if (data[0] == 0x07) found.TrySetResult(data); // proximity pairing
                    }
                };
                watcher.Start();

                var done = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(12)));
                if (done != found.Task) return;

                var b = ParseApple(found.Task.Result);
                if (b == null) return;

                string Part(string label, int? level, bool charging) =>
                    level is int v ? $"{label} {v}%{(charging ? "⚡" : "")}" : $"{label} —";

                var (l, r, c, lc, rc, cc) = b.Value;

                _host.ShowAlert(new IslandAlert
                {
                    Key = "bt:" + id,
                    Glyph = Glyphs.Headphones,
                    Accent = Palette.Blue,
                    Title = name,
                    Subtitle = $"{Part("L", l, lc)} · {Part("R", r, rc)}",
                    Detail = c is int ? Part("кейс", c, cc) : null,
                    Duration = TimeSpan.FromSeconds(5),
                });
            }
            catch (Exception ex)
            {
                Log.Info("Заряд AirPods не прочитан: " + ex.Message);
            }
            finally
            {
                try { watcher?.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Разбор Apple «proximity pairing» (как в OpenPods/MagicPods):
        /// байт 5 — флаги (кто из наушников «главный»), байт 6 — заряд левого/правого (по 4 бита),
        /// байт 7 — заряд кейса (младшие 4 бита) и флаги зарядки (старшие). Значение 0–10 = 0–100%.
        /// </summary>
        private static (int? Left, int? Right, int? Case, bool LeftCharging, bool RightCharging, bool CaseCharging)? ParseApple(byte[] d)
        {
            if (d.Length < 8) return null;

            bool flipped = (d[5] & 0x20) == 0;
            int hi = d[6] >> 4, lo = d[6] & 0x0F;
            int left = flipped ? hi : lo;
            int right = flipped ? lo : hi;
            int box = d[7] & 0x0F;
            int charge = d[7] >> 4;

            static int? Level(int v) => v <= 10 ? v * 10 : null;

            bool leftCharging = (charge & (flipped ? 0b10 : 0b01)) != 0;
            bool rightCharging = (charge & (flipped ? 0b01 : 0b10)) != 0;
            bool caseCharging = (charge & 0b100) != 0;

            if (Level(left) == null && Level(right) == null) return null;
            return (Level(left), Level(right), Level(box), leftCharging, rightCharging, caseCharging);
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
