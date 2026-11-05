using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Modules
{
    public interface IStartable
    {
        void Start();
    }

    /// <summary>Таймер обратного отсчёта и секундомер. Живёт на UI-потоке.</summary>
    public sealed class TimerModule : IStartable, IDisposable
    {
        private const string TimerId = "timer";
        private const string StopwatchId = "stopwatch";

        private readonly IIslandHost _host;
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };

        // таймер
        private TimeSpan _timerTotal;
        private DateTime _timerEndsAt;
        private TimeSpan? _timerPausedLeft;
        private bool _timerActive;

        // секундомер
        private DateTime _swStartedAt;
        private TimeSpan _swAccumulated;
        private bool _swRunning;
        private bool _swActive;

        // звонок
        private MediaPlayer? _alarm;
        private IslandAlert? _ringAlert;
        private readonly DispatcherTimer _alarmStop = new() { Interval = TimeSpan.FromSeconds(60) };

        public TimerModule(IIslandHost host)
        {
            _host = host;
            _tick.Tick += (_, _) => Tick();
            _alarmStop.Tick += (_, _) => StopAlarm();
        }

        public void Start() => _tick.Start();

        // ------------------------------------------------------------ таймер

        public void StartTimer(TimeSpan duration)
        {
            StopAlarm();
            _timerTotal = duration;
            _timerEndsAt = DateTime.UtcNow + duration;
            _timerPausedLeft = null;
            _timerActive = true;
            PushTimer();
        }

        private TimeSpan TimerLeft()
        {
            if (_timerPausedLeft is TimeSpan p) return p;
            var left = _timerEndsAt - DateTime.UtcNow;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        private void ToggleTimerPause()
        {
            if (!_timerActive) return;
            if (_timerPausedLeft is TimeSpan left)
            {
                _timerEndsAt = DateTime.UtcNow + left;
                _timerPausedLeft = null;
            }
            else
            {
                _timerPausedLeft = TimerLeft();
            }
            PushTimer();
        }

        private void AddMinute()
        {
            if (!_timerActive) return;
            _timerTotal += TimeSpan.FromMinutes(1);
            if (_timerPausedLeft is TimeSpan p) _timerPausedLeft = p + TimeSpan.FromMinutes(1);
            else _timerEndsAt += TimeSpan.FromMinutes(1);
            PushTimer();
        }

        private void CancelTimer()
        {
            _timerActive = false;
            _host.RemoveActivity(TimerId);
        }

        private void PushTimer()
        {
            var left = TimerLeft();
            bool paused = _timerPausedLeft != null;
            // округляем вверх: 4:59.3 показываем как 5:00, как в iOS
            var shown = TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));

            _host.SetActivity(new LiveActivity(TimerId)
            {
                Order = 10,
                Glyph = Glyphs.Timer,
                Accent = Palette.Orange,
                Title = $"Таймер · {Format.HumanMinutes(_timerTotal)}",
                Subtitle = paused ? $"на паузе · {Format.Clock(shown)}" : $"осталось {Format.Clock(shown)}",
                Badge = Format.Clock(shown),
                Progress = _timerTotal.TotalMilliseconds > 0 ? left.TotalMilliseconds / _timerTotal.TotalMilliseconds : 0,
                Actions = new List<IslandAction>
                {
                    new() { Glyph = paused ? Glyphs.Play : Glyphs.Pause, Tooltip = paused ? "Продолжить" : "Пауза", OnClick = ToggleTimerPause },
                    new() { Glyph = Glyphs.Add, Tooltip = "+1 минута", OnClick = AddMinute },
                    new() { Glyph = Glyphs.Close, Tooltip = "Отменить", OnClick = CancelTimer },
                },
            });
        }

        private void TimerFinished()
        {
            _timerActive = false;
            _host.RemoveActivity(TimerId);

            var total = _timerTotal;
            StartAlarm();

            _ringAlert = new IslandAlert
            {
                Key = "timer-done",
                Glyph = Glyphs.Timer,
                Accent = Palette.Orange,
                Title = "Время вышло!",
                Subtitle = $"Таймер на {Format.HumanMinutes(total)}",
                Duration = null, // висит, пока не нажмёшь
                Actions = new List<IslandAction>
                {
                    new() { Glyph = Glyphs.Add, Text = "1 мин", OnClick = () => StartTimer(TimeSpan.FromMinutes(1)) },
                    new() { Glyph = Glyphs.Check, Text = "Ок", OnClick = () => { } },
                },
                OnDismiss = StopAlarm,
            };
            _host.ShowAlert(_ringAlert);
        }

        // ------------------------------------------------------------ секундомер

        /// <summary>Кнопка «Секундомер»: запустить, если не идёт; иначе пауза/продолжить.</summary>
        public void ToggleStopwatch()
        {
            if (!_swActive)
            {
                _swActive = true;
                _swAccumulated = TimeSpan.Zero;
                _swStartedAt = DateTime.UtcNow;
                _swRunning = true;
            }
            else
            {
                ToggleStopwatchPause();
                return;
            }
            PushStopwatch();
        }

        private void ToggleStopwatchPause()
        {
            if (_swRunning)
            {
                _swAccumulated += DateTime.UtcNow - _swStartedAt;
                _swRunning = false;
            }
            else
            {
                _swStartedAt = DateTime.UtcNow;
                _swRunning = true;
            }
            PushStopwatch();
        }

        private void ResetStopwatch()
        {
            _swAccumulated = TimeSpan.Zero;
            _swStartedAt = DateTime.UtcNow;
            PushStopwatch();
        }

        private void StopStopwatch()
        {
            _swActive = false;
            _swRunning = false;
            _host.RemoveActivity(StopwatchId);
        }

        private TimeSpan StopwatchElapsed() =>
            _swAccumulated + (_swRunning ? DateTime.UtcNow - _swStartedAt : TimeSpan.Zero);

        private void PushStopwatch()
        {
            var t = StopwatchElapsed();
            string precise = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

            _host.SetActivity(new LiveActivity(StopwatchId)
            {
                Order = 11,
                Glyph = Glyphs.Stopwatch,
                Accent = Palette.Cyan,
                Title = "Секундомер",
                Subtitle = _swRunning ? precise : $"на паузе · {precise}",
                Badge = Format.Clock(t),
                Actions = new List<IslandAction>
                {
                    new() { Glyph = _swRunning ? Glyphs.Pause : Glyphs.Play, Tooltip = _swRunning ? "Пауза" : "Продолжить", OnClick = ToggleStopwatchPause },
                    new() { Glyph = Glyphs.Reset, Tooltip = "Сбросить", OnClick = ResetStopwatch },
                    new() { Glyph = Glyphs.Close, Tooltip = "Убрать", OnClick = StopStopwatch },
                },
            });
        }

        // ------------------------------------------------------------ общий тик

        private void Tick()
        {
            if (_timerActive)
            {
                if (_timerPausedLeft == null && DateTime.UtcNow >= _timerEndsAt) TimerFinished();
                else PushTimer();
            }

            if (_swActive && _swRunning) PushStopwatch();
        }

        // ------------------------------------------------------------ звук

        private void StartAlarm()
        {
            StopAlarm();

            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] candidates =
            {
                Path.Combine(windir, "Media", "Alarm01.wav"),
                Path.Combine(windir, "Media", "Windows Notify System Generic.wav"),
                Path.Combine(windir, "Media", "notify.wav"),
            };

            string? file = Array.Find(candidates, File.Exists);

            if (file == null)
            {
                try { Console.Beep(880, 400); } catch { }
                return;
            }

            _alarm = new MediaPlayer();
            _alarm.MediaEnded += (_, _) =>
            {
                if (_alarm == null) return;
                _alarm.Position = TimeSpan.Zero;
                _alarm.Play();
            };
            _alarm.Open(new Uri(file));
            _alarm.Volume = 0.8;
            _alarm.Play();
            _alarmStop.Start();
        }

        private void StopAlarm()
        {
            _alarmStop.Stop();
            if (_alarm != null)
            {
                _alarm.Stop();
                _alarm.Close();
                _alarm = null;
            }
        }

        public void Dispose()
        {
            _tick.Stop();
            StopAlarm();
        }
    }
}
