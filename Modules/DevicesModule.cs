using System;
using System.Collections.Generic;
using System.Linq;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using Enumeration = global::Windows.Devices.Enumeration;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// «Подключено: Logitech G102» — мыши, клавиатуры, принтеры, геймпады, вебки, мониторы,
    /// телефоны и прочее. Следим за «контейнерами устройств» Windows: один контейнер = одно
    /// физическое устройство, поэтому мышь с тремя внутренними интерфейсами даёт одно уведомление.
    /// Bluetooth-устройства и флешки здесь пропускаем — у них свои модули.
    /// </summary>
    public sealed class DevicesModule : IStartable, IDisposable
    {
        private const string Connected = "System.Devices.Connected";
        private const string Category = "System.Devices.Category";
        private const string Paired = "System.Devices.Paired";
        private const string Manufacturer = "System.Devices.Manufacturer";

        private sealed record Kind(string Glyph, string Label);

        private readonly IIslandHost _host;
        private readonly object _lock = new();
        private readonly Dictionary<string, (string Name, bool Connected, Kind Kind, bool Skip)> _devices = new();
        private readonly Dictionary<string, DateTime> _lastAlert = new();
        private Enumeration.DeviceWatcher? _watcher;
        private bool _ready;

        public DevicesModule(IIslandHost host) => _host = host;

        public void Start()
        {
            // все физические устройства, кроме самого компьютера
            _watcher = Enumeration.DeviceInformation.CreateWatcher(
                "System.Devices.LocalMachine:=System.StructuredQueryType.Boolean#False",
                new[] { Connected, Category, Paired, Manufacturer },
                Enumeration.DeviceInformationKind.DeviceContainer);

            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += (_, _) =>
            {
                lock (_lock) _ready = true;
                Log.Info($"Устройства: известно {_devices.Count}");
            };
            _watcher.Start();
        }

        // ------------------------------------------------------------ события

        private void OnAdded(Enumeration.DeviceWatcher sender, Enumeration.DeviceInformation info)
        {
            lock (_lock)
            {
                var categories = ReadCategories(info.Properties);
                bool connected = ReadBool(info.Properties, Connected) ?? true;
                bool skip = ShouldSkip(info.Properties, categories, info.Name);
                var kind = Classify(categories, info.Name);
                string name = string.IsNullOrWhiteSpace(info.Name) ? kind.Label : info.Name.Trim();

                _devices[info.Id] = (name, connected, kind, skip);

                if (_ready && connected && !skip) Notify(info.Id, name, kind, true);
            }
        }

        private void OnUpdated(Enumeration.DeviceWatcher sender, Enumeration.DeviceInformationUpdate update)
        {
            lock (_lock)
            {
                if (!_devices.TryGetValue(update.Id, out var d)) return;
                if (ReadBool(update.Properties, Connected) is not bool connected || connected == d.Connected) return;

                _devices[update.Id] = d with { Connected = connected };
                if (_ready && !d.Skip) Notify(update.Id, d.Name, d.Kind, connected);
            }
        }

        private void OnRemoved(Enumeration.DeviceWatcher sender, Enumeration.DeviceInformationUpdate update)
        {
            lock (_lock)
            {
                if (!_devices.Remove(update.Id, out var d)) return;
                if (_ready && d.Connected && !d.Skip) Notify(update.Id, d.Name, d.Kind, false);
            }
        }

        private void Notify(string id, string name, Kind kind, bool connected)
        {
            // устройство при подключении «моргает» несколько раз — одно уведомление на 4 секунды
            string key = id + connected;
            if (_lastAlert.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(4)) return;
            _lastAlert[key] = DateTime.UtcNow;

            Log.Info($"Устройство {(connected ? "подключено" : "отключено")}: {name} ({kind.Label})");

            _host.ShowAlert(new IslandAlert
            {
                Key = "dev:" + id,
                Glyph = kind.Glyph,
                Accent = connected ? Palette.Blue : Palette.Gray,
                Title = connected ? "Подключено" : "Отключено",
                Subtitle = name,
                Detail = string.Equals(kind.Label, name, StringComparison.OrdinalIgnoreCase) ? null : kind.Label,
                Duration = TimeSpan.FromSeconds(3),
                DropIfHidden = true,
            });
        }

        // ------------------------------------------------------------ что за устройство

        private static bool ShouldSkip(IReadOnlyDictionary<string, object> props, string[] categories, string name)
        {
            // Bluetooth (AirPods и т.п.) показывает свой модуль — с зарядом
            if (ReadBool(props, Paired) == true) return true;

            // флешки и диски показывает модуль дисков
            if (categories.Any(c => c.StartsWith("Storage", StringComparison.OrdinalIgnoreCase))) return true;

            // служебные «устройства» без имени
            return string.IsNullOrWhiteSpace(name);
        }

        private static Kind Classify(string[] categories, string name)
        {
            string text = (string.Join(" ", categories) + " " + name).ToLowerInvariant();

            if (text.Contains("mouse") || text.Contains("мышь")) return new("", "Мышь");
            if (text.Contains("keyboard") || text.Contains("клавиатур")) return new("", "Клавиатура");
            if (text.Contains("gam") || text.Contains("controller") || text.Contains("joystick")) return new("", "Геймпад");
            if (text.Contains("print") || text.Contains("принтер")) return new("", "Принтер");
            if (text.Contains("scan")) return new("", "Сканер");
            if (text.Contains("webcam") || text.Contains("camera") || text.Contains("камера")) return new(Glyphs.Camera, "Камера");
            if (text.Contains("headphone") || text.Contains("headset") || text.Contains("наушник")) return new(Glyphs.Headphones, "Наушники");
            if (text.Contains("speaker") || text.Contains("audio") || text.Contains("колонк")) return new(Glyphs.Speaker, "Звук");
            if (text.Contains("microphone") || text.Contains("микрофон")) return new(Glyphs.Microphone, "Микрофон");
            if (text.Contains("monitor") || text.Contains("display")) return new("", "Монитор");
            if (text.Contains("phone") || text.Contains("телефон") || text.Contains("android") || text.Contains("iphone")) return new("", "Телефон");
            if (text.Contains("pen") || text.Contains("tablet") || text.Contains("wacom")) return new("", "Планшет");
            return new("", "USB-устройство");
        }

        private static bool? ReadBool(IReadOnlyDictionary<string, object> props, string key) =>
            props.TryGetValue(key, out var v) && v is bool b ? b : null;

        private static string[] ReadCategories(IReadOnlyDictionary<string, object> props) =>
            props.TryGetValue(Category, out var v) && v is string[] arr ? arr : Array.Empty<string>();

        public void Dispose()
        {
            if (_watcher == null) return;
            try
            {
                if (_watcher.Status is Enumeration.DeviceWatcherStatus.Started or Enumeration.DeviceWatcherStatus.EnumerationCompleted)
                    _watcher.Stop();
            }
            catch { }
        }
    }
}
