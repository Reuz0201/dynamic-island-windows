using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Modules;

namespace DynamicIsland
{
    public partial class MainWindow : Window, IIslandHost
    {
        // ------------------------------------------------------------ размеры

        private const double IdleWidth = 120, IdleHeight = 30;
        private const double CompactHeight = 36, CompactMinWidth = 170, CompactMaxWidth = 340;
        private const double ExpandedWidth = 430;
        private const double AlertMinWidth = 260, AlertMaxWidth = 440;

        private enum Mode { Idle, Compact, CompactActivity, Expanded, Alert }

        private static readonly Color DefaultAccent = Color.FromRgb(0x7A, 0x5C, 0xFF);

        // ------------------------------------------------------------ состояние

        private readonly MediaWatcher _media = new();
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _collapseTimer;
        private readonly DispatcherTimer _alertTimer;

        private MediaState? _state;
        private string? _lastTrackKey;
        private Color _musicColor = DefaultAccent;

        private readonly Dictionary<string, LiveActivity> _activities = new();
        private readonly Dictionary<string, ActivityRow> _rows = new();
        private readonly Dictionary<string, (string Glyph, Color Color, string Text)> _statuses = new();

        private readonly List<IslandAlert> _alertQueue = new();
        private IslandAlert? _alert;

        private Mode _mode = (Mode)(-1);
        private double _targetW = -1, _targetH = -1;
        private bool _hover;
        private bool _eqRunning;
        private bool _bubbleShown;

        private TimerModule? _timers;

        public MainWindow()
        {
            InitializeComponent();

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (_, _) => UpdateProgress();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (!_hover) Refresh();
            };

            _alertTimer = new DispatcherTimer();
            _alertTimer.Tick += (_, _) =>
            {
                // пока мышь над алертом — не убираем
                if (_hover) return;
                if (_alert != null) DismissAlert(_alert);
            };

            BuildEqualizer(EqCompact, 3, 12);
            BuildEqualizer(EqExpanded, 6, 16);

            IslandRow.MouseEnter += (_, _) =>
            {
                _hover = true;
                _collapseTimer.Stop();
                Refresh();
            };
            IslandRow.MouseLeave += (_, _) =>
            {
                _hover = false;
                _collapseTimer.Start();
            };

            // Скругление всегда «таблеткой», пока размер анимируется
            IslandHost.SizeChanged += (_, _) => UpdateCorners();

            Loaded += OnLoaded;
        }

        // ================================================================ окно

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Не показываться в Alt+Tab и не воровать фокус при клике
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
            SystemParameters.StaticPropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SystemParameters.WorkArea)) PositionWindow();
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow);
            InitFullscreenWatch();
            InitShelf();

            BuildTools();
            BuildContextMenu();
            InitSettings();   // настройки + слежение за settings.json (MainWindow.Settings.cs)
            Refresh();

            SyncModules();
            StartUpdater();

            _media.StateChanged += state => Dispatcher.InvokeAsync(() => OnMediaState(state));

            try
            {
                await _media.StartAsync();
            }
            catch (Exception ex)
            {
                ShowAlert(new IslandAlert
                {
                    Glyph = Glyphs.Music,
                    Accent = Palette.Red,
                    Title = "Медиа недоступно",
                    Subtitle = ex.Message,
                    Duration = TimeSpan.FromSeconds(6),
                });
            }

            _progressTimer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            DisposeSettingsAndModules();
            base.OnClosed(e);
        }

        // ================================================================ меню и быстрые действия

        private void BuildTools()
        {
            ToolsButtons.Children.Add(Views.PillButton(Glyphs.Timer, "Таймер", () => OpenTimerMenu(ToolsButtons.Children[0] as FrameworkElement)));
            ToolsButtons.Children.Add(Views.PillButton(Glyphs.Stopwatch, "Секундомер", () => _timers?.ToggleStopwatch()));
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            var timer = new MenuItem { Header = "Таймер" };
            foreach (var item in TimerMenuItems()) timer.Items.Add(item);
            menu.Items.Add(timer);

            var stopwatch = new MenuItem { Header = "Секундомер" };
            stopwatch.Click += (_, _) => _timers?.ToggleStopwatch();
            menu.Items.Add(stopwatch);

            menu.Items.Add(new Separator());

            var settings = new MenuItem { Header = "Настройки…" };
            settings.Click += (_, _) => OpenSettingsApp();
            menu.Items.Add(settings);

            var update = new MenuItem { Header = "Проверить обновления" };
            update.Click += (_, _) => _ = _updater?.CheckAsync(manual: true);
            menu.Items.Add(update);

            menu.Items.Add(new Separator());

            var exit = new MenuItem { Header = "Выход" };
            exit.Click += (_, _) => Close();
            menu.Items.Add(exit);

            IslandRow.ContextMenu = menu;
        }

        private IEnumerable<object> TimerMenuItems()
        {
            int[] presets = { 1, 3, 5, 10, 15, 25, 30, 45, 60 };
            foreach (int m in presets)
            {
                var item = new MenuItem { Header = m == 60 ? "1 час" : $"{m} мин" };
                int minutes = m;
                item.Click += (_, _) => _timers?.StartTimer(TimeSpan.FromMinutes(minutes));
                yield return item;
            }

            yield return new Separator();

            var custom = new MenuItem { Header = "Своё время…" };
            custom.Click += (_, _) => AskCustomTimer();
            yield return custom;
        }

        private void OpenTimerMenu(FrameworkElement? target)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var item in TimerMenuItems()) menu.Items.Add(item);
            menu.IsOpen = true;
        }

        private void AskCustomTimer()
        {
            var dialog = new TimerInputWindow();
            var area = SystemParameters.WorkArea;
            dialog.Left = area.Left + (area.Width - dialog.Width) / 2;
            dialog.Top = area.Top + 60;
            if (dialog.ShowDialog() == true && dialog.Result is TimeSpan duration)
                _timers?.StartTimer(duration);
        }

        // ================================================================ IIslandHost

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.InvokeAsync(action);
        }

        public void SetActivity(LiveActivity activity) => OnUi(() =>
        {
            _activities[activity.Id] = activity;
            Refresh();
        });

        public void RemoveActivity(string id) => OnUi(() =>
        {
            if (_activities.Remove(id)) Refresh();
        });

        public void SetStatus(string key, string? glyph, Color color, string text) => OnUi(() =>
        {
            if (glyph == null) _statuses.Remove(key);
            else _statuses[key] = (glyph, color, text);
            RebuildStatus();
            Refresh(force: true);
        });

        public void ShowAlert(IslandAlert alert) => OnUi(() =>
        {
            if (alert.Key != null)
            {
                _alertQueue.RemoveAll(a => a.Key == alert.Key);
                if (_alert?.Key == alert.Key && !_hiddenForFullscreen)
                {
                    Present(alert, update: true);
                    return;
                }
            }

            // игра/видео на весь экран — остров спрятан: копим (или выбрасываем мелочь вроде громкости)
            if (_hiddenForFullscreen)
            {
                if (!alert.DropIfHidden) _alertQueue.Add(alert);
                return;
            }

            if (_alert == null) Present(alert);
            else _alertQueue.Add(alert);
        });

        public void DismissAlert(IslandAlert alert) => OnUi(() =>
        {
            if (!ReferenceEquals(_alert, alert))
            {
                _alertQueue.Remove(alert);
                return;
            }

            _alertTimer.Stop();
            _alert = null;
            try { alert.OnDismiss?.Invoke(); } catch { }

            if (_alertQueue.Count > 0 && !_hiddenForFullscreen)
            {
                var next = _alertQueue[0];
                _alertQueue.RemoveAt(0);
                Present(next);
            }
            else
            {
                AnimateGlow(MusicAccent());
                Refresh();
            }
        });

        private void Present(IslandAlert alert, bool update = false)
        {
            _alert = alert;
            AlertHost.Content = BuildAlertView(alert);
            if (!(update && alert.Quiet)) AnimateGlow(alert.Accent);

            _alertTimer.Stop();
            if (alert.Duration is TimeSpan d)
            {
                _alertTimer.Interval = d * _settings.AlertDuration;
                _alertTimer.Start();
            }

            Refresh(force: true);
            if (!(update && alert.Quiet)) Pulse();
        }

        private FrameworkElement BuildAlertView(IslandAlert alert)
        {
            var grid = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = alert.IconFactory?.Invoke() ?? Views.IconCircle(alert.Glyph, alert.Accent, 40);
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);

            var texts = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = alert.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(alert.Subtitle))
            {
                texts.Children.Add(new TextBlock
                {
                    Text = alert.Subtitle,
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 0),
                    Foreground = Views.Brush(Colors.White, 0xB3),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            if (!string.IsNullOrEmpty(alert.Detail))
            {
                texts.Children.Add(new TextBlock
                {
                    Text = alert.Detail,
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 0),
                    Foreground = Views.Brush(Colors.White, 0x73),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            if (alert.Progress is double level)
            {
                var track = new Grid { Height = 5, Margin = new Thickness(0, 7, 0, 2), MinWidth = 180 };
                track.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Views.Brush(Colors.White, 0x26) });
                track.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(2.5),
                    Background = Views.Brush(alert.Accent),
                    RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = new ScaleTransform(Math.Clamp(level, 0, 1), 1),
                });
                texts.Children.Add(track);
            }

            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            if (alert.Actions.Count > 0)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (var a in alert.Actions)
                {
                    var wrapped = new IslandAction
                    {
                        Glyph = a.Glyph,
                        Text = a.Text,
                        Tooltip = a.Tooltip,
                        OnClick = () =>
                        {
                            try { a.OnClick(); } catch { }
                            DismissAlert(alert);
                        },
                    };
                    actions.Children.Add(Views.ActionButton(wrapped, 32));
                }
                Grid.SetColumn(actions, 2);
                grid.Children.Add(actions);
            }

            // клик по самому алерту (не по кнопке) — закрыть
            grid.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
                DismissAlert(alert);
            };

            return grid;
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is T t) return t;
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        private void RebuildStatus()
        {
            StatusPanel.Children.Clear();
            foreach (var (_, s) in _statuses)
            {
                var g = Views.Glyph(s.Glyph, 12, Views.Brush(s.Color));
                g.Margin = new Thickness(6, 0, 0, 0);
                g.ToolTip = s.Text;
                StatusPanel.Children.Add(g);
            }
            ToolsStatus.Text = string.Join("  ·  ", _statuses.Values.Select(s => s.Text));
        }

        // ================================================================ медиа

        private void OnMediaState(MediaState? state)
        {
            _rawState = state;
            if (!_settings.ModuleMusic) state = null;

            bool trackChanged = state?.TrackKey != _lastTrackKey;
            _state = state;
            _lastTrackKey = state?.TrackKey;

            if (state != null)
            {
                CompactTitle.Text = string.IsNullOrEmpty(state.Artist) || !_settings.ShowArtist
                    ? state.Title
                    : $"{state.Title} · {state.Artist}";
                ExpandedTitle.Text = state.Title;
                ExpandedArtist.Text = state.Artist;
                ExpandedApp.Text = string.IsNullOrEmpty(state.AppName) ? "" : $"из {state.AppName}";

                PlayButton.Content = state.IsPlaying ? Glyphs.Pause : Glyphs.Play;
                PrevButton.IsEnabled = state.CanPrev;
                NextButton.IsEnabled = state.CanNext;

                ProgressRow.Visibility = state.Duration > TimeSpan.FromSeconds(1)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ProgressTrack.Cursor = state.CanSeek ? Cursors.Hand : Cursors.Arrow;

                if (trackChanged)
                {
                    SetArtwork(state.Thumbnail);
                    if (_alert == null) Pulse();
                }
            }
            else if (trackChanged)
            {
                _musicColor = DefaultAccent;
                if (_alert == null) AnimateGlow(MusicAccent());
            }

            SetEqualizer(state?.IsPlaying == true);
            Refresh(force: trackChanged);
            UpdateProgress();
        }

        private void UpdateProgress()
        {
            var s = _state;
            if (s == null || s.Duration <= TimeSpan.Zero) return;

            var pos = s.Position;
            if (s.IsPlaying)
                pos += DateTimeOffset.Now - s.PositionUpdatedAt;
            if (pos > s.Duration) pos = s.Duration;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

            TimeNow.Text = Format.Clock(pos);
            TimeTotal.Text = Format.Clock(s.Duration);

            double width = ProgressTrack.ActualWidth;
            if (width > 0)
                ProgressFill.Width = width * (pos.TotalMilliseconds / s.Duration.TotalMilliseconds);
        }

        // ================================================================ обложка и цвет

        private void SetArtwork(byte[]? bytes)
        {
            BitmapImage? image = bytes == null ? null : LoadImage(bytes, 200);

            if (image == null)
            {
                CompactArt.Fill = null;
                ExpandedArt.Background = null;
                _musicColor = DefaultAccent;
            }
            else
            {
                var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                brush.Freeze();

                CompactArt.Fill = brush;
                ExpandedArt.Background = brush;

                ExpandedArt.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
                CompactArt.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));

                _musicColor = DominantColor(bytes!);
            }

            if (_alert == null) AnimateGlow(MusicAccent());

            UpdateProgressColor();
        }

        private void UpdateProgressColor()
        {
            var accent = MusicAccent();
            var light = Color.FromRgb(
                (byte)(accent.R + (255 - accent.R) * 0.45),
                (byte)(accent.G + (255 - accent.G) * 0.45),
                (byte)(accent.B + (255 - accent.B) * 0.45));
            ProgressBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light, TimeSpan.FromMilliseconds(600)));
        }

        private static BitmapImage? LoadImage(byte[] bytes, int decodeWidth)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = decodeWidth;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Средний «сочный» цвет обложки — для свечения и прогресс-бара.</summary>
        private static Color DominantColor(byte[] bytes)
        {
            var small = LoadImage(bytes, 24);
            if (small == null) return DefaultAccent;

            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb));
                double min = Math.Min(pr, Math.Min(pg, pb));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double lightness = max / 255.0;

                // яркие насыщенные пиксели весят больше серых и чёрных
                double weight = 0.05 + sat * sat * lightness;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }

            r /= total; g /= total; b /= total;

            double peak = Math.Max(r, Math.Max(g, b));
            if (peak > 0 && peak < 200)
            {
                double k = 200 / peak;
                r *= k; g *= k; b *= k;
            }

            return Color.FromRgb(
                (byte)Math.Clamp(r, 0, 255),
                (byte)Math.Clamp(g, 0, 255),
                (byte)Math.Clamp(b, 0, 255));
        }

        private void AnimateGlow(Color color)
        {
            GlowBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        // ================================================================ раскладка

        /// <summary>Пересчитывает, что показывать, и анимирует остров к нужному размеру.</summary>
        private void Refresh(bool force = false)
        {
            bool hasMusic = _state != null;
            var acts = _activities.Values.OrderBy(a => a.Order).ToList();

            // «фоновые» активности (подключённая флешка) живут только в раскрытом виде
            var visible = acts.Where(a => !a.ExpandedOnly).ToList();
            LiveActivity? main = hasMusic ? null : visible.FirstOrDefault();
            LiveActivity? bubble = hasMusic ? visible.FirstOrDefault() : visible.Skip(1).FirstOrDefault();

            // --- компакт-активность
            if (main != null)
            {
                CompactActGlyph.Text = main.Glyph;
                CompactActGlyph.Foreground = Views.Brush(main.Accent);
                CompactActTitle.Text = main.Title;
                CompactActBadge.Text = main.Badge;
                CompactActBadge.Foreground = Views.Brush(main.Accent);
            }

            // --- строки активностей в раскрытом виде
            foreach (var id in _rows.Keys.Where(id => !_activities.ContainsKey(id)).ToList())
            {
                RowsPanel.Children.Remove(_rows[id]);
                _rows.Remove(id);
            }
            for (int i = 0; i < acts.Count; i++)
            {
                var a = acts[i];
                if (!_rows.TryGetValue(a.Id, out var row))
                {
                    row = new ActivityRow(a);
                    _rows[a.Id] = row;
                }
                else
                {
                    row.Update(a);
                }

                int current = RowsPanel.Children.IndexOf(row);
                if (current != i)
                {
                    if (current >= 0) RowsPanel.Children.RemoveAt(current);
                    RowsPanel.Children.Insert(Math.Min(i, RowsPanel.Children.Count), row);
                }
                row.Margin = new Thickness(0, i == 0 && !hasMusic ? 0 : 12, 0, 0);
            }

            MusicCard.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
            NothingPlaying.Visibility = !hasMusic && acts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // --- бубль справа
            BubbleGlyph.Text = bubble?.Glyph ?? "";
            BubbleText.Text = bubble?.Badge ?? "";
            if (bubble != null)
            {
                BubbleGlyph.Foreground = Views.Brush(bubble.Accent);
                BubbleText.Foreground = Views.Brush(bubble.Accent);
            }

            // --- режим
            Mode mode =
                _alert != null ? Mode.Alert :
                _hover ? Mode.Expanded :
                hasMusic ? Mode.Compact :
                main != null ? Mode.CompactActivity :
                Mode.Idle;

            ApplyMode(mode, force);

            bool showBubble = bubble != null && (mode == Mode.Compact || mode == Mode.CompactActivity);
            SetBubble(showBubble);
        }

        private void ApplyMode(Mode mode, bool force)
        {
            bool modeChanged = mode != _mode;
            _mode = mode;

            double statusW = _statuses.Count * 18;
            var rightMargin = 12 + statusW;
            Compact.Margin = new Thickness(6, 0, rightMargin, 0);
            CompactActivity.Margin = new Thickness(12, 0, rightMargin + 2, 0);

            double w, h;
            switch (mode)
            {
                case Mode.Expanded:
                    w = ExpandedWidth;
                    Expanded.Measure(new Size(w - 2, double.PositiveInfinity));
                    h = Math.Ceiling(Expanded.DesiredSize.Height + 2);
                    break;

                case Mode.Alert:
                    AlertHost.Measure(new Size(AlertMaxWidth - 2, double.PositiveInfinity));
                    w = Math.Clamp(Math.Ceiling(AlertHost.DesiredSize.Width + 2), AlertMinWidth, AlertMaxWidth);
                    h = Math.Ceiling(AlertHost.DesiredSize.Height + 2);
                    break;

                case Mode.Compact:
                    w = RoundUp(Math.Clamp(6 + 24 + 10 + TextWidth(CompactTitle) + 10 + 16 + rightMargin, CompactMinWidth, CompactMaxWidth));
                    h = CompactHeight;
                    break;

                case Mode.CompactActivity:
                    w = RoundUp(Math.Clamp(12 + 16 + 10 + TextWidth(CompactActTitle) + 10 + TextWidth(CompactActBadge) + rightMargin + 4,
                                           CompactMinWidth, CompactMaxWidth));
                    h = CompactHeight;
                    break;

                default:
                    if (_settings.IdleStyle == "mini")
                    {
                        // тонкая полоска-«ручка»: наведи — раскроется
                        w = 64;
                        h = 10;
                    }
                    else
                    {
                        w = Math.Max(IdleWidth, 14 + 12 + 8 + TextWidth(IdleLabel) + 14) + statusW;
                        h = IdleHeight;
                    }
                    break;
            }

            if (modeChanged || force || Math.Abs(w - _targetW) > 0.5 || Math.Abs(h - _targetH) > 0.5)
            {
                bool grow = mode is Mode.Expanded or Mode.Alert;
                double speed = _settings.AnimationSpeed;
                var duration = TimeSpan.FromMilliseconds(_targetW < 0 ? 1 : (grow ? 520 : 380) / speed);

                // при раскрытии — «пружинка», при сворачивании — мягко
                IEasingFunction ease = grow && modeChanged && _settings.SpringAnimations
                    ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                    : new CubicEase { EasingMode = EasingMode.EaseOut };

                IslandHost.BeginAnimation(WidthProperty, new DoubleAnimation(w, duration) { EasingFunction = ease });
                IslandHost.BeginAnimation(HeightProperty, new DoubleAnimation(h, duration) { EasingFunction = ease });

                _targetW = w;
                _targetH = h;
            }

            if (!modeChanged) return;

            bool miniIdle = _settings.IdleStyle == "mini";
            FadeTo(Idle, mode == Mode.Idle && !miniIdle ? 1 : 0, 200);
            FadeTo(Compact, mode == Mode.Compact ? 1 : 0, 180);
            FadeTo(CompactActivity, mode == Mode.CompactActivity ? 1 : 0, 180);
            FadeTo(StatusPanel, (mode == Mode.Idle && !miniIdle) || mode is Mode.Compact or Mode.CompactActivity ? 1 : 0, 180);
            FadeTo(Expanded, mode == Mode.Expanded ? 1 : 0, 160);
            FadeTo(AlertHost, mode == Mode.Alert ? 1 : 0, 170);

            Expanded.IsHitTestVisible = mode == Mode.Expanded;
            AlertHost.IsHitTestVisible = mode == Mode.Alert;

            double glow = mode switch
            {
                Mode.Idle => 0,
                Mode.Alert => 0.6,
                Mode.Expanded => 0.55,
                _ => _state != null ? 0.35 : 0.25,
            };
            glow = _settings.GlowEnabled ? Math.Clamp(glow * _settings.GlowIntensity, 0, 1) : 0;
            Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, TimeSpan.FromMilliseconds(500)));
        }

        private static double RoundUp(double w) => Math.Ceiling(w / 8) * 8;

        private double TextWidth(TextBlock tb)
        {
            if (string.IsNullOrEmpty(tb.Text)) return 0;
            var text = new FormattedText(
                tb.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return text.WidthIncludingTrailingWhitespace;
        }

        private static void FadeTo(UIElement element, double to, int delayMs)
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 200 : 110))
            {
                BeginTime = TimeSpan.FromMilliseconds(to > 0 ? delayMs : 0)
            };
            element.BeginAnimation(OpacityProperty, anim);
        }

        private void SetBubble(bool show)
        {
            if (show == _bubbleShown) return;
            _bubbleShown = show;

            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };

            if (show)
            {
                BubbleHost.Visibility = Visibility.Visible;
                BubbleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
                BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            }
            else
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
                fade.Completed += (_, _) =>
                {
                    if (!_bubbleShown) BubbleHost.Visibility = Visibility.Collapsed;
                };
                BubbleHost.BeginAnimation(OpacityProperty, fade);
                BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(150)));
                BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(150)));
            }
        }

        /// <summary>Короткий «пульс» при смене трека или алерте.</summary>
        private void Pulse()
        {
            var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }));

            IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        // ================================================================ эквалайзер

        private static readonly double[] EqDurations = { 0.42, 0.31, 0.53, 0.37 };

        private static void BuildEqualizer(Panel panel, int bars, double height)
        {
            for (int i = 0; i < bars; i++)
            {
                panel.Children.Add(new Rectangle
                {
                    Width = 3,
                    Height = height,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = new ScaleTransform(1, 0.25),
                    Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                });
            }
        }

        // SetEqualizer — в MainWindow.Equalizer.cs

        // ================================================================ кнопки плеера

        private async void Play_Click(object sender, RoutedEventArgs e) => await Safe(_media.TogglePlayPauseAsync);
        private async void Next_Click(object sender, RoutedEventArgs e) => await Safe(_media.NextAsync);
        private async void Prev_Click(object sender, RoutedEventArgs e) => await Safe(_media.PreviousAsync);

        private async void Progress_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var s = _state;
            if (s == null || !s.CanSeek || s.Duration <= TimeSpan.Zero) return;

            double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
            var target = TimeSpan.FromMilliseconds(s.Duration.TotalMilliseconds * ratio);

            _state = s with { Position = target, PositionUpdatedAt = DateTimeOffset.Now };
            UpdateProgress();

            await Safe(() => _media.SeekAsync(target));
        }

        private static async Task Safe(Func<Task> action)
        {
            try { await action(); }
            catch { /* плеер мог закрыться — не страшно */ }
        }

        // ================================================================ WinAPI

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
