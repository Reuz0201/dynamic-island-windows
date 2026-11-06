using System;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland
{
    /// <summary>
    /// Эквалайзер: «живой» (качает под реальный звук системы) или просто анимация.
    /// </summary>
    public partial class MainWindow
    {
        private readonly LiveSpectrum _spectrum = new();
        private DispatcherTimer? _eqTimer;
        private string _eqMode = "";
        private DateTime _lastSpectrumRestart = DateTime.MinValue;

        private readonly float[] _rawBands = new float[6];
        private readonly float[] _compactLevels = new float[3];
        private readonly float[] _expandedLevels = new float[6];

        private void SetEqualizer(bool playing)
        {
            string mode = !playing ? "off" : _settings.LiveEqualizer ? "live" : "fake";
            if (mode == _eqMode) return;
            _eqMode = mode;
            _eqRunning = playing;

            if (mode == "live")
            {
                StopBarAnimations();
                _spectrum.Start();
                if (_eqTimer == null)
                {
                    // ~30 кадров в секунду
                    _eqTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                    _eqTimer.Tick += (_, _) => EqTick();
                }
                _eqTimer.Start();
                return;
            }

            _eqTimer?.Stop();
            _spectrum.Stop();
            Array.Clear(_compactLevels);
            Array.Clear(_expandedLevels);

            if (mode == "fake") StartFakeAnimation();
            else RestBars();
        }

        private void EqTick()
        {
            // долго нет звука при «играет» — возможно, сменилось устройство вывода: переподключаемся
            if (_spectrum.Stale && DateTime.UtcNow - _lastSpectrumRestart > TimeSpan.FromSeconds(4))
            {
                _lastSpectrumRestart = DateTime.UtcNow;
                _spectrum.Restart();
            }

            _spectrum.Compute(_rawBands);

            for (int i = 0; i < _expandedLevels.Length; i++)
                _expandedLevels[i] = Smooth(_expandedLevels[i], _rawBands[i]);

            for (int i = 0; i < _compactLevels.Length; i++)
            {
                float v = (_rawBands[i * 2] + _rawBands[i * 2 + 1]) / 2f;
                _compactLevels[i] = Smooth(_compactLevels[i], v);
            }

            ApplyLevels(EqCompact, _compactLevels);
            ApplyLevels(EqExpanded, _expandedLevels);
        }

        /// <summary>Вверх — быстро (удар бочки), вниз — плавно.</summary>
        private static float Smooth(float current, float target) =>
            target > current ? current + (target - current) * 0.6f : current + (target - current) * 0.18f;

        private static void ApplyLevels(System.Windows.Controls.Panel panel, float[] levels)
        {
            int i = 0;
            foreach (var child in panel.Children)
            {
                if (child is not Rectangle rect || rect.RenderTransform is not ScaleTransform scale) continue;
                if (i >= levels.Length) break;
                scale.ScaleY = 0.15 + 0.85 * levels[i];
                i++;
            }
        }

        private void StopBarAnimations()
        {
            foreach (var panel in new[] { EqCompact, EqExpanded })
            foreach (var child in panel.Children)
                if (child is Rectangle { RenderTransform: ScaleTransform scale })
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        private void StartFakeAnimation()
        {
            foreach (var panel in new[] { EqCompact, EqExpanded })
            {
                int i = 0;
                foreach (var child in panel.Children)
                {
                    if (child is not Rectangle { RenderTransform: ScaleTransform scale }) continue;
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(EqDurations[i % EqDurations.Length]))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                            BeginTime = TimeSpan.FromMilliseconds(i * 90),
                        });
                    i++;
                }
            }
        }

        private void RestBars()
        {
            foreach (var panel in new[] { EqCompact, EqExpanded })
            foreach (var child in panel.Children)
                if (child is Rectangle { RenderTransform: ScaleTransform scale })
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(250)));
        }
    }
}
