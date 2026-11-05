using System;
using DynamicIsland.Core;
using Power = global::Windows.System.Power;

namespace DynamicIsland.Modules
{
    /// <summary>Зарядка подключена/отключена, низкий заряд, «заряжено полностью».</summary>
    public sealed class BatteryModule : IStartable, IDisposable
    {
        private readonly IIslandHost _host;
        private readonly object _lock = new();

        private bool _plugged;
        private int _percent;
        private bool _warned20, _warned10, _fullNotified;

        public BatteryModule(IIslandHost host) => _host = host;

        public void Start()
        {
            // На ПК без батареи модуль просто молчит
            if (Power.PowerManager.BatteryStatus == Power.BatteryStatus.NotPresent) return;

            _plugged = IsPlugged();
            _percent = Power.PowerManager.RemainingChargePercent;
            _warned20 = _percent <= 20;
            _warned10 = _percent <= 10;
            _fullNotified = _percent >= 100;

            Power.PowerManager.PowerSupplyStatusChanged += OnSupplyChanged;
            Power.PowerManager.RemainingChargePercentChanged += OnPercentChanged;
            Power.PowerManager.BatteryStatusChanged += OnSupplyChanged;
        }

        private static bool IsPlugged() =>
            Power.PowerManager.PowerSupplyStatus != Power.PowerSupplyStatus.NotPresent;

        private void OnSupplyChanged(object? sender, object e)
        {
            lock (_lock)
            {
                bool plugged = IsPlugged();
                if (plugged == _plugged) return;
                _plugged = plugged;
                _percent = Power.PowerManager.RemainingChargePercent;

                if (plugged)
                {
                    _warned20 = _warned10 = false;
                    _fullNotified = _percent >= 100;

                    int p = _percent;
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "power",
                        IconFactory = () => Views.Battery(p, charging: true),
                        Accent = Palette.Green,
                        Title = "Заряжается",
                        Subtitle = $"{p}%",
                        Duration = TimeSpan.FromSeconds(3),
                    });
                }
                else
                {
                    int p = _percent;
                    string subtitle = $"{p}%";
                    var left = Power.PowerManager.RemainingDischargeTime;
                    if (left > TimeSpan.FromMinutes(5) && left < TimeSpan.FromHours(24))
                        subtitle += $" · хватит примерно на {Format.HumanMinutes(TimeSpan.FromMinutes(Math.Round(left.TotalMinutes)))}";

                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "power",
                        IconFactory = () => Views.Battery(p, charging: false),
                        Accent = Palette.Gray,
                        Title = "От батареи",
                        Subtitle = subtitle,
                        Duration = TimeSpan.FromSeconds(3),
                    });
                }
            }
        }

        private void OnPercentChanged(object? sender, object e)
        {
            lock (_lock)
            {
                int p = Power.PowerManager.RemainingChargePercent;
                _percent = p;
                bool charging = IsPlugged();

                if (!charging && p <= 10 && !_warned10)
                {
                    _warned10 = _warned20 = true;
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "battery-low",
                        IconFactory = () => Views.Battery(p, charging: false),
                        Accent = Palette.Red,
                        Title = "Критический заряд",
                        Subtitle = $"{p}% — срочно на зарядку",
                        Duration = TimeSpan.FromSeconds(8),
                    });
                }
                else if (!charging && p <= 20 && !_warned20)
                {
                    _warned20 = true;
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "battery-low",
                        IconFactory = () => Views.Battery(p, charging: false),
                        Accent = Palette.Orange,
                        Title = "Низкий заряд",
                        Subtitle = $"{p}% — подключи зарядку",
                        Duration = TimeSpan.FromSeconds(6),
                    });
                }
                else if (charging && p >= 100 && !_fullNotified)
                {
                    _fullNotified = true;
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "power",
                        IconFactory = () => Views.Battery(100, charging: true),
                        Accent = Palette.Green,
                        Title = "Заряжено",
                        Subtitle = "100% — можно отключать",
                        Duration = TimeSpan.FromSeconds(4),
                    });
                }
            }
        }

        public void Dispose()
        {
            Power.PowerManager.PowerSupplyStatusChanged -= OnSupplyChanged;
            Power.PowerManager.RemainingChargePercentChanged -= OnPercentChanged;
            Power.PowerManager.BatteryStatusChanged -= OnSupplyChanged;
        }
    }
}
