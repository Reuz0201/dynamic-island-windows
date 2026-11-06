using System;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using Microsoft.Win32;
using Power = global::Windows.System.Power;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// «С возвращением»: открыл ноут после сна или разблокировал комп после перерыва —
    /// остров говорит, сколько тебя не было и сколько за это время ушло заряда.
    /// </summary>
    public sealed class WelcomeModule : IStartable, IDisposable
    {
        private static readonly TimeSpan MinAway = TimeSpan.FromMinutes(5);
        private const string MoonGlyph = "";

        private readonly IIslandHost _host;
        private DateTime? _awaySince;
        private int? _awayBattery;
        private string _reason = "";
        private DateTime _lastWelcome = DateTime.MinValue;

        public WelcomeModule(IIslandHost host) => _host = host;

        public void Start()
        {
            SystemEvents.PowerModeChanged += OnPowerMode;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) GoAway("sleep");
            else if (e.Mode == PowerModes.Resume) ComeBack();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) GoAway("lock");
            else if (e.Reason == SessionSwitchReason.SessionUnlock) ComeBack();
        }

        private void GoAway(string reason)
        {
            // сон после блокировки — считаем от блокировки
            if (_awaySince != null && reason == "sleep")
            {
                _reason = "sleep";
                return;
            }
            _awaySince = DateTime.Now;
            _awayBattery = Battery();
            _reason = reason;
        }

        private void ComeBack()
        {
            if (_awaySince is not DateTime since) return;

            // после сна приходят и Resume, и Unlock — здороваемся один раз
            if (DateTime.Now - _lastWelcome < TimeSpan.FromMinutes(1)) return;

            var away = DateTime.Now - since;
            int? before = _awayBattery;
            string reason = _reason;
            _awaySince = null;
            _awayBattery = null;

            if (away < MinAway) return;
            _lastWelcome = DateTime.Now;

            int? now = Battery();
            string? detail = before is int b && now is int n
                ? (b == n ? $"заряд {n}% — не тратился" : $"заряд {b}% → {n}%")
                : null;

            Log.Info($"С возвращением: {reason}, {away}");

            _host.ShowAlert(new IslandAlert
            {
                Key = "welcome",
                Glyph = MoonGlyph,
                Accent = Palette.Purple,
                Title = "С возвращением",
                Subtitle = reason == "sleep"
                    ? $"Комп спал {Format.HumanMinutes(Round(away))}"
                    : $"Тебя не было {Format.HumanMinutes(Round(away))}",
                Detail = detail,
                Duration = TimeSpan.FromSeconds(5),
            });
        }

        private static TimeSpan Round(TimeSpan t) => TimeSpan.FromMinutes(Math.Max(1, Math.Round(t.TotalMinutes)));

        private static int? Battery()
        {
            try
            {
                if (Power.PowerManager.BatteryStatus == Power.BatteryStatus.NotPresent) return null;
                return Power.PowerManager.RemainingChargePercent;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
    }
}
