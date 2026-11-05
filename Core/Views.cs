using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Core
{
    /// <summary>Сборка мелких элементов интерфейса из кода.</summary>
    public static class Views
    {
        public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static SolidColorBrush Brush(Color c, byte alpha = 0xFF)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static TextBlock Glyph(string glyph, double size, Brush foreground) => new()
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>Кружок с иконкой на подкрашенном фоне.</summary>
        public static Border IconCircle(string glyph, Color accent, double size)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = Brush(accent, 0x33),
                Child = Glyph(glyph, size * 0.46, Brush(accent)),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>Круглая кнопка-иконка (стиль IslandButton из App.xaml).</summary>
        public static Button IconButton(string glyph, string tooltip, double size, Action onClick)
        {
            var button = new Button
            {
                Content = glyph,
                Width = size,
                Height = size,
                FontSize = size * 0.42,
                ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip,
                Focusable = false,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "IslandButton");
            button.Click += (_, _) => onClick();
            return button;
        }

        /// <summary>Кнопка-«пилюля» с текстом (и иконкой).</summary>
        public static Button PillButton(string? glyph, string text, Action onClick, Color? accent = null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(glyph))
            {
                var g = Glyph(glyph, 12, Brushes.White);
                g.Margin = new Thickness(0, 0, 6, 0);
                content.Children.Add(g);
            }
            content.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new Button
            {
                Content = content,
                Focusable = false,
                Tag = accent.HasValue ? Brush(accent.Value, 0x55) : Brush(Colors.White, 0x1F),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "IslandPill");
            button.Click += (_, _) => onClick();
            return button;
        }

        public static Button ActionButton(IslandAction action, double size = 30) =>
            string.IsNullOrEmpty(action.Text)
                ? IconButton(action.Glyph, action.Tooltip, size, action.OnClick)
                : PillButton(action.Glyph, action.Text!, action.OnClick);

        /// <summary>Батарейка в стиле iOS: заливка по проценту, молния при зарядке.</summary>
        public static FrameworkElement Battery(int percent, bool charging)
        {
            Color fill = charging ? Palette.Green : percent <= 10 ? Palette.Red : percent <= 20 ? Palette.Orange : Colors.White;

            var body = new Border
            {
                Width = 34,
                Height = 17,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1.5),
                BorderBrush = Brush(Colors.White, 0x80),
                Padding = new Thickness(2),
            };

            var inner = new Grid();
            inner.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2.5),
                Background = Brush(fill),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, 26 * Math.Clamp(percent, 0, 100) / 100.0),
            });
            if (charging)
            {
                var bolt = Glyph(Glyphs.Lightning, 10, Brushes.White);
                bolt.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 };
                inner.Children.Add(bolt);
            }
            body.Child = inner;

            var nub = new Border
            {
                Width = 2.5,
                Height = 6,
                CornerRadius = new CornerRadius(0, 1.5, 1.5, 0),
                Background = Brush(Colors.White, 0x80),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 0, 0),
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(body);
            panel.Children.Add(nub);
            return panel;
        }

        /// <summary>Картинка по ссылке в скруглённой рамке (флаг страны и т.п.).</summary>
        public static FrameworkElement RemoteImage(string url, double width, double height, double radius, string fallbackGlyph, Color accent)
        {
            var grid = new Grid { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
            var fallback = IconCircle(fallbackGlyph, accent, Math.Min(width, height));
            grid.Children.Add(fallback);

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(url);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();

                var border = new Border
                {
                    CornerRadius = new CornerRadius(radius),
                    Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
                    Opacity = 0,
                };
                grid.Children.Add(border);

                void Show()
                {
                    fallback.Visibility = Visibility.Collapsed;
                    border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                }

                if (image.IsDownloading) image.DownloadCompleted += (_, _) => Show();
                else Show();
            }
            catch
            {
                // нет сети — остаётся иконка
            }

            return grid;
        }
    }

    /// <summary>Строка активности в раскрытом острове. Обновляется на месте, без пересоздания кнопок.</summary>
    public sealed class ActivityRow : Grid
    {
        private readonly Border _icon;
        private readonly TextBlock _glyph;
        private readonly TextBlock _title;
        private readonly TextBlock _subtitle;
        private readonly Grid _progress;
        private readonly Border _progressFill;
        private readonly ScaleTransform _progressScale;
        private readonly StackPanel _actions;
        private string? _actionsKey;
        private LiveActivity _current;
        private bool _pulsing;

        public ActivityRow(LiveActivity activity)
        {
            _current = activity;
            Margin = new Thickness(0, 12, 0, 0);
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _glyph = Views.Glyph("", 15, Brushes.White);
            _icon = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Child = _glyph,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(_icon);

            var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _title = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _subtitle = new TextBlock
            {
                FontSize = 11.5,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = Views.Brush(Colors.White, 0x99),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(_subtitle, FontNumeralAlignment.Tabular);

            _progressScale = new ScaleTransform(0, 1);
            _progressFill = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                RenderTransform = _progressScale,
                RenderTransformOrigin = new Point(0, 0.5),
            };
            _progress = new Grid { Margin = new Thickness(0, 6, 0, 0), Height = 3 };
            _progress.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Views.Brush(Colors.White, 0x26) });
            _progress.Children.Add(_progressFill);

            texts.Children.Add(_title);
            texts.Children.Add(_subtitle);
            texts.Children.Add(_progress);
            SetColumn(texts, 1);
            Children.Add(texts);

            _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(_actions, 2);
            Children.Add(_actions);

            Update(activity);
        }

        public void Update(LiveActivity a)
        {
            _current = a;

            _glyph.Text = a.Glyph;
            _glyph.Foreground = Views.Brush(a.Accent);
            _icon.Background = Views.Brush(a.Accent, 0x33);
            _title.Text = a.Title;
            _subtitle.Text = a.Subtitle;
            _subtitle.Visibility = string.IsNullOrEmpty(a.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            _progressFill.Background = Views.Brush(a.Accent);

            if (a.Progress is null)
            {
                _progress.Visibility = Visibility.Collapsed;
            }
            else
            {
                _progress.Visibility = Visibility.Visible;

                if (double.IsNaN(a.Progress.Value))
                {
                    _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    _progressScale.ScaleX = 1;
                    if (!_pulsing)
                    {
                        _pulsing = true;
                        _progressFill.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(700))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                        });
                    }
                }
                else
                {
                    if (_pulsing)
                    {
                        _pulsing = false;
                        _progressFill.BeginAnimation(OpacityProperty, null);
                        _progressFill.Opacity = 1;
                    }
                    _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(Math.Clamp(a.Progress.Value, 0, 1), TimeSpan.FromMilliseconds(300)));
                }
            }

            // Кнопки пересоздаём только если их набор поменялся —
            // иначе клик может «потеряться» при ежесекундном обновлении
            if (a.ActionsKey != _actionsKey)
            {
                _actionsKey = a.ActionsKey;
                _actions.Children.Clear();
                for (int i = 0; i < a.Actions.Count; i++)
                {
                    int index = i;
                    var template = a.Actions[i];
                    var button = string.IsNullOrEmpty(template.Text)
                        ? Views.IconButton(template.Glyph, template.Tooltip, 30, () => Invoke(index))
                        : Views.PillButton(template.Glyph, template.Text!, () => Invoke(index));
                    button.Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0);
                    _actions.Children.Add(button);
                }
            }
        }

        private void Invoke(int index)
        {
            // берём актуальный обработчик на момент клика
            if (index < _current.Actions.Count) _current.Actions[index].OnClick();
        }
    }
}
