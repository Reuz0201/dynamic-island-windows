using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland
{
    /// <summary>Маленькое окошко «на сколько поставить таймер».</summary>
    public sealed class TimerInputWindow : Window
    {
        private readonly TextBox _input;
        private readonly TextBlock _hint;

        public TimeSpan? Result { get; private set; }

        public TimerInputWindow()
        {
            Width = 320;
            Height = 118;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");

            _input = new TextBox
            {
                FontSize = 18,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 6, 0, 0),
            };

            _hint = new TextBlock
            {
                Text = "5 · 1:30 · 1ч20м · 45с — Enter, Esc отмена",
                FontSize = 11.5,
                Foreground = Views.Brush(Colors.White, 0x80),
                Margin = new Thickness(0, 6, 0, 0),
            };

            var panel = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Views.Glyph(Glyphs.Timer, 13, Views.Brush(Palette.Orange));
            title.Children.Add(icon);
            title.Children.Add(new TextBlock
            {
                Text = "Таймер на…",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(8, 0, 0, 0),
            });
            panel.Children.Add(title);
            panel.Children.Add(_input);
            panel.Children.Add(new Border { Height = 1, Background = Views.Brush(Colors.White, 0x33) });
            panel.Children.Add(_hint);

            Content = new Border
            {
                CornerRadius = new CornerRadius(22),
                Background = Views.Brush(Colors.Black, 0xF2),
                BorderBrush = Views.Brush(Colors.White, 0x1A),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 14, 20, 14),
                Child = panel,
            };

            Loaded += (_, _) =>
            {
                Activate();
                _input.Focus();
            };
            Deactivated += (_, _) =>
            {
                if (IsVisible && DialogResult == null) TryClose(false);
            };

            _input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    TryClose(false);
                }
                else if (e.Key == Key.Enter)
                {
                    var parsed = Parse(_input.Text);
                    if (parsed is TimeSpan t && t > TimeSpan.Zero && t < TimeSpan.FromHours(24))
                    {
                        Result = t;
                        TryClose(true);
                    }
                    else
                    {
                        _hint.Text = "Не понял 🤔  Примеры: 5 · 1:30 · 1ч20м · 45с";
                        _hint.Foreground = Views.Brush(Palette.Red);
                    }
                }
            };
        }

        private bool _closing;

        private void TryClose(bool result)
        {
            if (_closing) return;
            _closing = true;
            DialogResult = result;
        }

        /// <summary>«5» = 5 мин, «1:30» = 1 мин 30 с, «1:02:00», «1ч20м», «45с», «2.5» = 2.5 мин.</summary>
        public static TimeSpan? Parse(string raw)
        {
            string text = raw.Trim().ToLowerInvariant().Replace(',', '.');
            if (text.Length == 0) return null;

            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double minutes))
                return TimeSpan.FromMinutes(minutes);

            var clock = Regex.Match(text, @"^(?:(\d+):)?(\d{1,2}):(\d{2})$");
            if (clock.Success)
            {
                int h = clock.Groups[1].Success ? int.Parse(clock.Groups[1].Value) : 0;
                return new TimeSpan(h, int.Parse(clock.Groups[2].Value), int.Parse(clock.Groups[3].Value));
            }

            var parts = Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*(ч|час\w*|h|м|мин\w*|m|min|с|сек\w*|s|sec)");
            if (parts.Count == 0) return null;

            double seconds = 0;
            foreach (Match m in parts)
            {
                double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string u = m.Groups[2].Value;
                seconds += u[0] switch
                {
                    'ч' or 'h' => v * 3600,
                    'м' or 'm' => v * 60,
                    _ => v,
                };
            }
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
