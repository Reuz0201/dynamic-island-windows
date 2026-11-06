using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Shared;

namespace DynamicIsland.SettingsApp
{
    public partial class MainWindow : Window
    {
        private IslandSettings _s;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _statusTimer;
        private TextBlock? _updateStatusText;
        private string _page = "look";

        private static readonly (string Key, string Glyph, string Title)[] Pages =
        {
            ("look", "", "Внешний вид"),
            ("place", "", "Расположение"),
            ("modules", "", "Модули"),
            ("updates", "", "Обновления"),
            ("system", "", "Система"),
        };

        private static readonly string[] BackgroundSwatches =
            { "#000000", "#1C1C1E", "#0B1A33", "#1E0F33", "#2A0A12", "#0A2418", "#2B2B2E", "#FFFFFF" };

        private static readonly string[] AccentSwatches =
            { "#7A5CFF", "#0A84FF", "#64D2FF", "#30D158", "#FFD60A", "#FF9F0A", "#FF453A", "#FF5CA8" };

        private static readonly (string Value, string Label)[] Fonts =
        {
            ("Segoe UI Variable Display", "Segoe Variable"),
            ("Segoe UI", "Segoe UI"),
            ("Bahnschrift", "Bahnschrift"),
            ("Cascadia Code", "Cascadia"),
            ("Consolas", "Consolas"),
        };

        public MainWindow()
        {
            InitializeComponent();
            Log.Init("settings");

            _s = IslandSettings.Load();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                Save();
            };

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) => RefreshUpdateStatus();
            _statusTimer.Start();

            VersionText.Text = AppInfo.IsDevBuild ? "локальная сборка" : $"версия {AppInfo.Version}";

            BuildNav();
            BuildEqualizer();
            ShowPage("look");
            UpdatePreview();

            SourceInitialized += (_, _) => UseDarkTitleBar();
            Closing += (_, _) =>
            {
                if (_saveTimer.IsEnabled)
                {
                    _saveTimer.Stop();
                    Save();
                }
            };
        }

        // ================================================================ сохранение

        private void Changed()
        {
            UpdatePreview();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Save()
        {
            try
            {
                _s.Normalized().Save();
                Flash("Сохранено ✓", Color.FromRgb(0x30, 0xD1, 0x58));
            }
            catch (Exception ex)
            {
                Flash("Не сохранилось: " + ex.Message, Color.FromRgb(0xFF, 0x45, 0x3A));
            }
        }

        private void Flash(string text, Color color)
        {
            SavedHint.Text = text;
            SavedHint.Foreground = new SolidColorBrush(color);
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
            SavedHint.BeginAnimation(OpacityProperty, anim);
        }

        // ================================================================ меню

        private void BuildNav()
        {
            foreach (var (key, glyph, title) in Pages)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = (FontFamily)FindResource("Icons"),
                    FontSize = 15,
                    Width = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                content.Children.Add(new TextBlock { Text = title, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

                var button = new RadioButton
                {
                    Content = content,
                    GroupName = "nav",
                    IsChecked = key == "look",
                    Style = (Style)FindResource("NavButton"),
                };
                string k = key;
                button.Checked += (_, _) => ShowPage(k);
                NavPanel.Children.Add(button);
            }
        }

        private void ShowPage(string key)
        {
            _page = key;
            _updateStatusText = null;

            PageHost.Content = key switch
            {
                "look" => LookPage(),
                "place" => PlacePage(),
                "modules" => ModulesPage(),
                "updates" => UpdatesPage(),
                _ => SystemPage(),
            };

            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        // ================================================================ страницы

        // ================================================================ темы

        private static readonly (string Name, Action<IslandSettings> Apply)[] Themes =
        {
            ("Классика", _ => { }),
            ("Стекло", t => { t.BackgroundColor = "#1C1C1E"; t.BackgroundOpacity = 0.65; t.GlowIntensity = 0.6; }),
            ("Неон", t => { t.AccentMode = "fixed"; t.AccentColor = "#FF5CA8"; t.GlowIntensity = 1.6; t.BorderEnabled = false; t.BackgroundOpacity = 1; }),
            ("Минимал", t => { t.GlowEnabled = false; t.BorderEnabled = false; t.IdleStyle = "mini"; t.ShowEqualizer = false; t.BackgroundOpacity = 0.9; }),
            ("Океан", t => { t.BackgroundColor = "#0B1A33"; t.BackgroundOpacity = 0.92; t.AccentMode = "fixed"; t.AccentColor = "#64D2FF"; t.GlowIntensity = 1.2; }),
            ("Кубик", t => { t.Roundness = 0.35; t.BackgroundColor = "#1C1C1E"; t.GlowIntensity = 0.8; }),
        };

        /// <summary>Перенести только «внешний вид» (не модули и не системное).</summary>
        private static void CopyLook(IslandSettings from, IslandSettings to)
        {
            to.BackgroundColor = from.BackgroundColor;
            to.BackgroundOpacity = from.BackgroundOpacity;
            to.AccentMode = from.AccentMode;
            to.AccentColor = from.AccentColor;
            to.GlowEnabled = from.GlowEnabled;
            to.GlowIntensity = from.GlowIntensity;
            to.Roundness = from.Roundness;
            to.BorderEnabled = from.BorderEnabled;
            to.IdleStyle = from.IdleStyle;
            to.IdleText = from.IdleText;
            to.ShowEqualizer = from.ShowEqualizer;
            to.ShowArtist = from.ShowArtist;
            to.SpringAnimations = from.SpringAnimations;
            to.AnimationSpeed = from.AnimationSpeed;
            to.FontFamily = from.FontFamily;
            to.Scale = from.Scale;
        }

        private void ApplyTheme(Action<IslandSettings> preset)
        {
            var theme = new IslandSettings { Scale = _s.Scale, FontFamily = _s.FontFamily, IdleText = _s.IdleText };
            preset(theme);
            CopyLook(theme, _s);
            Changed();
            ShowPage("look");
        }

        private void ExportTheme()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "моя тема.island.json",
                Filter = "Тема острова (*.island.json)|*.island.json",
            };
            if (dialog.ShowDialog(this) != true) return;

            var theme = new IslandSettings();
            CopyLook(_s, theme);
            try
            {
                File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(theme,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Flash("Тема сохранена ✓", Color.FromRgb(0x30, 0xD1, 0x58));
            }
            catch (Exception ex)
            {
                Flash("Не сохранилось: " + ex.Message, Color.FromRgb(0xFF, 0x45, 0x3A));
            }
        }

        private void ImportTheme()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Тема острова (*.island.json)|*.island.json|JSON|*.json" };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var theme = System.Text.Json.JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(dialog.FileName));
                if (theme == null) throw new InvalidDataException("пустой файл");
                CopyLook(theme.Normalized(), _s);
                Changed();
                ShowPage("look");
                Flash("Тема загружена ✓", Color.FromRgb(0x30, 0xD1, 0x58));
            }
            catch (Exception ex)
            {
                Flash("Не тема острова: " + ex.Message, Color.FromRgb(0xFF, 0x45, 0x3A));
            }
        }

        private FrameworkElement ThemeButtons()
        {
            var panel = new WrapPanel();
            foreach (var (name, apply) in Themes)
            {
                var b = new Button { Content = name, Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 8) };
                b.Click += (_, _) => ApplyTheme(apply);
                panel.Children.Add(b);
            }
            return panel;
        }

        private FrameworkElement LookPage() => Page("Внешний вид",
            Card("Темы",
                ThemeButtons(),
                Row("Своя тема", "сохранить в файл или загрузить от друга",
                    Buttons(("Экспорт", false, ExportTheme), ("Импорт", false, ImportTheme)))),

            Card("Цвета",
                Row("Фон острова", null, ColorPicker(BackgroundSwatches, _s.BackgroundColor, v => { _s.BackgroundColor = v; Changed(); })),
                Row("Прозрачность фона", null, SliderControl(30, 100, _s.BackgroundOpacity * 100, 1, v => $"{v:0}%", v => { _s.BackgroundOpacity = v / 100; Changed(); })),
                Row("Акцентный цвет", "свечение и полоска прогресса",
                    Choice(new[] { ("album", "Из обложки"), ("fixed", "Свой") }, _s.AccentMode, v => { _s.AccentMode = v; Changed(); })),
                Row("Свой акцент", "работает, если выбран «Свой»",
                    ColorPicker(AccentSwatches, _s.AccentColor, v => { _s.AccentColor = v; Changed(); }))),

            Card("Свечение",
                Row("Свечение вокруг острова", null, Toggle(_s.GlowEnabled, v => { _s.GlowEnabled = v; Changed(); })),
                Row("Сила свечения", null, SliderControl(20, 160, _s.GlowIntensity * 100, 5, v => $"{v:0}%", v => { _s.GlowIntensity = v / 100; Changed(); }))),

            Card("Форма",
                Row("Размер", null, SliderControl(80, 140, _s.Scale * 100, 5, v => $"{v:0}%", v => { _s.Scale = v / 100; Changed(); })),
                Row("Скруглённость", "100% — таблетка, меньше — квадратнее",
                    SliderControl(30, 100, _s.Roundness * 100, 5, v => $"{v:0}%", v => { _s.Roundness = v / 100; Changed(); })),
                Row("Тонкая обводка", null, Toggle(_s.BorderEnabled, v => { _s.BorderEnabled = v; Changed(); }))),

            Card("Текст",
                Row("Шрифт", null, Choice(Fonts, _s.FontFamily, v => { _s.FontFamily = v; Changed(); })),
                Row("Показывать артиста", "в компактном виде", Toggle(_s.ShowArtist, v => { _s.ShowArtist = v; Changed(); })),
                Row("Эквалайзер", null, Toggle(_s.ShowEqualizer, v => { _s.ShowEqualizer = v; Changed(); })),
                Row("Живой эквалайзер", "столбики качают под реальный звук, а не просто анимация",
                    Toggle(_s.LiveEqualizer, v => { _s.LiveEqualizer = v; Changed(); }))),

            Card("Когда ничего не играет",
                Row("Вид", null, Choice(new[] { ("pill", "Плашка с текстом"), ("mini", "Тонкая полоска") }, _s.IdleStyle,
                    v => { _s.IdleStyle = v; Changed(); })),
                Row("Текст на плашке", null, TextInput(_s.IdleText, v => { _s.IdleText = v; Changed(); }))),

            Card("Анимации",
                Row("Скорость", null, SliderControl(50, 200, _s.AnimationSpeed * 100, 10, v => $"{v:0}%", v => { _s.AnimationSpeed = v / 100; Changed(); })),
                Row("Пружинка при раскрытии", null, Toggle(_s.SpringAnimations, v => { _s.SpringAnimations = v; Changed(); })),
                Row("Длительность уведомлений", "зарядка, наушники, VPN…",
                    SliderControl(50, 300, _s.AlertDuration * 100, 10, v => $"{v:0}%", v => { _s.AlertDuration = v / 100; Changed(); }))));

        private FrameworkElement MonitorChoice()
        {
            var options = new List<(string Value, string Label)> { ("", "Основной") };
            int i = 1;
            foreach (var m in Monitors.All())
            {
                options.Add((m.DeviceName, $"{i}: {m.Width}×{m.Height}{(m.IsPrimary ? " · осн." : "")}"));
                i++;
            }
            return Choice(options.ToArray(), _s.Monitor, v => { _s.Monitor = v; Changed(); });
        }

        private FrameworkElement PlacePage() => Page("Расположение",
            Card("Монитор",
                Row("Где показывать остров", null, MonitorChoice()),
                Row("Прятать в играх и видео на весь экран", "уведомления подождут, пока выйдешь",
                    Toggle(_s.HideInFullscreen, v => { _s.HideInFullscreen = v; Changed(); }))),

            Card("Положение на экране",
                Row("Отступ сверху", null, SliderControl(0, 200, _s.OffsetY, 1, v => $"{v:0} px", v => { _s.OffsetY = v; Changed(); })),
                Row("Сдвиг по горизонтали", "минус — влево, плюс — вправо",
                    SliderControl(-800, 800, _s.OffsetX, 5, v => $"{v:+0;-0;0} px", v => { _s.OffsetX = v; Changed(); })),
                Row("", null, Buttons(("По центру", false, () => { _s.OffsetX = 0; _s.OffsetY = 10; Changed(); ShowPage("place"); })))));

        private FrameworkElement ModulesPage() => Page("Модули",
            Card("Что показывает остров",
                Row("Музыка", "что играет в любом плеере, обложка, кнопки", Toggle(_s.ModuleMusic, v => { _s.ModuleMusic = v; Changed(); })),
                Row("Таймер и секундомер", "кнопки в раскрытом острове", Toggle(_s.ModuleTimers, v => { _s.ModuleTimers = v; Changed(); })),
                Row("Зарядка", "подключение зарядки и низкий заряд", Toggle(_s.ModuleBattery, v => { _s.ModuleBattery = v; Changed(); })),
                Row("Bluetooth", "подключение наушников и других устройств", Toggle(_s.ModuleBluetooth, v => { _s.ModuleBluetooth = v; Changed(); })),
                Row("Загрузки", "файлы в папке «Загрузки»", Toggle(_s.ModuleDownloads, v => { _s.ModuleDownloads = v; Changed(); })),
                Row("Трафик приложений", "кто отправляет и качает (помощнику нужно разрешение админа один раз)", Toggle(_s.ModuleNetwork, v => { _s.ModuleNetwork = v; Changed(); })),
                Row("VPN", "подключение, страна и IP", Toggle(_s.ModuleVpn, v => { _s.ModuleVpn = v; Changed(); })),
                Row("Громкость", "полоска вместо системной плашки + смена устройства звука", Toggle(_s.ModuleVolume, v => { _s.ModuleVolume = v; Changed(); })),
                Row("Яркость", "на ноутбуках", Toggle(_s.ModuleBrightness, v => { _s.ModuleBrightness = v; Changed(); })),
                Row("Микрофон и камера", "кто сейчас слушает или смотрит", Toggle(_s.ModulePrivacy, v => { _s.ModulePrivacy = v; Changed(); })),
                Row("Полка для файлов", "перетащи файлы на остров, потом забери", Toggle(_s.ModuleShelf, v => { _s.ModuleShelf = v; Changed(); })),
                Row("Флешки и диски", "подключение, копирование, свободное место, извлечение", Toggle(_s.ModuleDrives, v => { _s.ModuleDrives = v; Changed(); })),
                Row("«С возвращением»", "после сна или перерыва: сколько тебя не было и сколько ушло заряда", Toggle(_s.ModuleWelcome, v => { _s.ModuleWelcome = v; Changed(); })),
                Row("Переключатели", "Wi-Fi и Bluetooth вкл/выкл, сеть Wi-Fi, экономия заряда", Toggle(_s.ModuleToggles, v => { _s.ModuleToggles = v; Changed(); })),
                Row("Windows Update", "изредка напомнить, что Windows ждёт перезагрузки", Toggle(_s.ModuleWindowsUpdate, v => { _s.ModuleWindowsUpdate = v; Changed(); }))));

        private FrameworkElement UpdatesPage()
        {
            _updateStatusText = new TextBlock
            {
                FontSize = 13,
                Foreground = (Brush)FindResource("TextDim"),
                TextWrapping = TextWrapping.Wrap,
            };
            RefreshUpdateStatus();

            return Page("Обновления",
                Card("Автообновление",
                    Row("Проверять обновления", "из GitHub Releases", Toggle(_s.AutoUpdate, v => { _s.AutoUpdate = v; Changed(); })),
                    Row("Ставить сразу", "иначе остров спросит", Toggle(_s.AutoInstall, v => { _s.AutoInstall = v; Changed(); })),
                    Row("Как часто проверять", null, Choice(
                        new[] { ("1", "1 ч"), ("3", "3 ч"), ("6", "6 ч"), ("12", "12 ч"), ("24", "сутки") },
                        _s.UpdateCheckHours.ToString(CultureInfo.InvariantCulture),
                        v => { _s.UpdateCheckHours = int.Parse(v, CultureInfo.InvariantCulture); Changed(); }))),

                Card("Статус",
                    _updateStatusText,
                    Spacer(12),
                    Buttons(
                        ("Проверить сейчас", true, () =>
                        {
                            _s.UpdateCheckRequested = DateTime.Now;
                            Save();
                            Flash("Остров проверяет обновления…", Color.FromRgb(0x0A, 0x84, 0xFF));
                        }),
                        ("Все версии на GitHub", false, () => OpenUrl(AppInfo.RepoUrl + "/releases")))));
        }

        private FrameworkElement SystemPage() => Page("Система",
            Card("Запуск",
                Row("Запускать вместе с Windows", null,
                    Toggle(_s.Autostart, v => { _s.Autostart = v; Changed(); })),
                Row("Помощник трафика", "единственная часть с правами админа; разрешение даётся один раз",
                    Buttons(("Разрешить / переустановить", false, InstallHelper)))),

            Card("Файлы",
                Row("Папка настроек", IslandSettings.Folder, Buttons(("Открыть", false, () => OpenUrl(IslandSettings.Folder)))),
                Row("Логи", "если что-то глючит — скинь файл отсюда", Buttons(("Открыть", false, () =>
                {
                    Directory.CreateDirectory(Log.Folder);
                    OpenUrl(Log.Folder);
                }))),
                Row("Сбросить всё", "вернуть настройки по умолчанию", Buttons(("Сбросить", false, ResetAll)))),

            Card("О программе",
                Text($"Dynamic Island для Windows · {(AppInfo.IsDevBuild ? "локальная сборка" : "версия " + AppInfo.Version)}"),
                Spacer(10),
                Buttons(("Страница на GitHub", false, () => OpenUrl(AppInfo.RepoUrl)))));

        private void InstallHelper()
        {
            string island = Path.Combine(AppContext.BaseDirectory, "DynamicIsland.exe");
            try
            {
                Process.Start(new ProcessStartInfo(island, "--install-helper") { UseShellExecute = true, Verb = "runas" });
                Flash("Помощник устанавливается…", Color.FromRgb(0x0A, 0x84, 0xFF));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Flash("Отменено", Color.FromRgb(0xFF, 0x9F, 0x0A));
            }
            catch (Exception ex)
            {
                Flash(ex.Message, Color.FromRgb(0xFF, 0x45, 0x3A));
            }
        }

        private void ResetAll()
        {
            var result = MessageBox.Show(this, "Вернуть все настройки по умолчанию?", "Dynamic Island",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _s = new IslandSettings();
            Save();
            UpdatePreview();
            ShowPage(_page);
        }

        private void RefreshUpdateStatus()
        {
            if (_updateStatusText == null) return;

            var status = UpdateStatus.Load();
            string current = AppInfo.IsDevBuild ? "локальная сборка" : AppInfo.Version;

            if (status == null)
            {
                _updateStatusText.Text = $"Установлено: {current}\nОстров ещё не проверял обновления.";
                return;
            }

            string last = status.LastCheck is DateTime t ? t.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) : "—";
            _updateStatusText.Text =
                $"Установлено: {current}\n" +
                $"Последняя на GitHub: {status.LatestVersion ?? "—"}\n" +
                $"Проверка: {last} · {status.State}";
        }

        // ================================================================ превью

        private void BuildEqualizer()
        {
            double[] durations = { 0.42, 0.31, 0.53 };
            for (int i = 0; i < 3; i++)
            {
                var scale = new ScaleTransform(1, 0.3);
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = 3,
                    Height = 12,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                    Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = scale,
                };
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 90),
                });
                PreviewEq.Children.Add(bar);
            }
        }

        private void UpdatePreview()
        {
            var bg = ParseColor(_s.BackgroundColor, Colors.Black);
            var bgBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(_s.BackgroundOpacity, 0.3, 1) * 255), bg.R, bg.G, bg.B));
            PreviewIsland.Background = bgBrush;
            PreviewIdle.Background = bgBrush;

            // на светлом фоне белый текст не виден — подстраиваем
            bool light = bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114 > 160 && _s.BackgroundOpacity > 0.6;
            PreviewTitle.Foreground = light ? Brushes.Black : Brushes.White;

            var border = new Thickness(_s.BorderEnabled ? 1 : 0);
            PreviewIsland.BorderThickness = border;
            PreviewIdle.BorderThickness = border;

            double r = Math.Max(4, 18 * Math.Clamp(_s.Roundness, 0.2, 1));
            PreviewIsland.CornerRadius = new CornerRadius(r);
            PreviewGlow.CornerRadius = new CornerRadius(r);

            bool mini = _s.IdleStyle == "mini";
            PreviewIdle.Height = mini ? 10 : 30;
            PreviewIdle.Width = mini ? 64 : double.NaN;
            PreviewIdle.Padding = mini ? new Thickness(0) : new Thickness(14, 0, 14, 0);
            PreviewIdle.CornerRadius = new CornerRadius(Math.Max(3, (mini ? 5 : 15) * Math.Clamp(_s.Roundness, 0.2, 1)));
            PreviewIdleContent.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            PreviewIdleText.Text = string.IsNullOrWhiteSpace(_s.IdleText) ? "Тишина" : _s.IdleText;

            double scale = Math.Clamp(_s.Scale, 0.7, 1.6);
            PreviewHost.LayoutTransform = new ScaleTransform(scale, scale);
            PreviewIdle.LayoutTransform = new ScaleTransform(scale, scale);

            try
            {
                var font = new FontFamily($"{_s.FontFamily}, Segoe UI");
                PreviewTitle.FontFamily = font;
                PreviewIdleText.FontFamily = font;
            }
            catch { }

            PreviewTitle.Text = _s.ShowArtist ? "Мой трек · Артист" : "Мой трек";
            PreviewEq.Visibility = _s.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;

            var accent = _s.AccentMode == "fixed" ? ParseColor(_s.AccentColor, Color.FromRgb(0x7A, 0x5C, 0xFF)) : Color.FromRgb(0xFF, 0x5C, 0xA8);
            PreviewGlowBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(accent, TimeSpan.FromMilliseconds(300)));
            PreviewGlow.Opacity = _s.GlowEnabled ? Math.Clamp(0.4 * _s.GlowIntensity, 0, 1) : 0;

            // акцент приложения настроек = акцент острова
            Resources["Accent"] = new SolidColorBrush(accent);
        }

        // ================================================================ конструктор интерфейса

        private FrameworkElement Page(string title, params UIElement[] cards)
        {
            var panel = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 0, 14) });
            foreach (var c in cards) panel.Children.Add(c);
            return panel;
        }

        private FrameworkElement Card(string title, params UIElement[] rows)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextDim"),
                Margin = new Thickness(0, 0, 0, 8),
            });
            foreach (var row in rows) panel.Children.Add(row);

            return new Border
            {
                Background = (Brush)FindResource("Card"),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 14, 18, 10),
                Margin = new Thickness(0, 0, 0, 14),
                Child = panel,
            };
        }

        private FrameworkElement Row(string label, string? hint, UIElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6), MinHeight = 34 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            if (!string.IsNullOrEmpty(label))
                texts.Children.Add(new TextBlock { Text = label, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(hint))
                texts.Children.Add(new TextBlock
                {
                    Text = hint,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextDim"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            grid.Children.Add(texts);

            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private CheckBox Toggle(bool value, Action<bool> set)
        {
            var box = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch") };
            box.Checked += (_, _) => set(true);
            box.Unchecked += (_, _) => set(false);
            return box;
        }

        private FrameworkElement SliderControl(double min, double max, double value, double step, Func<double, string> format, Action<double> set)
        {
            var label = new TextBlock
            {
                Width = 64,
                TextAlignment = TextAlignment.Right,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Text = format(value),
            };

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                Width = 220,
                SmallChange = step,
                LargeChange = step * 5,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                Style = (Style)FindResource("DarkSlider"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += (_, e) =>
            {
                label.Text = format(e.NewValue);
                set(e.NewValue);
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(slider);
            panel.Children.Add(label);
            return panel;
        }

        private FrameworkElement Choice((string Value, string Label)[] options, string current, Action<string> set)
        {
            var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 420 };
            string group = "choice" + Guid.NewGuid().ToString("N");

            foreach (var (value, label) in options)
            {
                var button = new RadioButton
                {
                    Content = label,
                    GroupName = group,
                    IsChecked = string.Equals(value, current, StringComparison.OrdinalIgnoreCase),
                    Style = (Style)FindResource("Choice"),
                    Margin = new Thickness(0, 2, 6, 2),
                };
                string v = value;
                button.Checked += (_, _) => set(v);
                panel.Children.Add(button);
            }
            return panel;
        }

        private FrameworkElement ColorPicker(string[] swatches, string current, Action<string> set)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            string group = "color" + Guid.NewGuid().ToString("N");

            var hex = new TextBox { Text = current, Width = 86, Style = (Style)FindResource("DarkText"), Margin = new Thickness(4, 0, 0, 0) };
            var buttons = new List<RadioButton>();
            bool syncing = false;

            foreach (var color in swatches)
            {
                var button = new RadioButton
                {
                    GroupName = group,
                    Background = new SolidColorBrush(ParseColor(color, Colors.Black)),
                    IsChecked = string.Equals(color, current, StringComparison.OrdinalIgnoreCase),
                    Style = (Style)FindResource("Swatch"),
                    ToolTip = color,
                };
                string c = color;
                button.Checked += (_, _) =>
                {
                    if (syncing) return;
                    syncing = true;
                    hex.Text = c;
                    syncing = false;
                    set(c);
                };
                buttons.Add(button);
                panel.Children.Add(button);
            }

            hex.TextChanged += (_, _) =>
            {
                if (syncing) return;
                string text = hex.Text.Trim();
                if (!text.StartsWith("#")) text = "#" + text;
                if (text.Length != 7 || !IsHex(text[1..])) return;

                syncing = true;
                foreach (var b in buttons) b.IsChecked = string.Equals((string)b.ToolTip, text, StringComparison.OrdinalIgnoreCase);
                syncing = false;
                set(text.ToUpperInvariant());
            };

            panel.Children.Add(hex);
            return panel;
        }

        private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

        private FrameworkElement TextInput(string value, Action<string> set)
        {
            var box = new TextBox { Text = value, Width = 220, Style = (Style)FindResource("DarkText"), MaxLength = 30 };
            box.TextChanged += (_, _) => set(box.Text);
            return box;
        }

        private FrameworkElement Buttons(params (string Text, bool Primary, Action OnClick)[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (text, primary, onClick) in buttons)
            {
                var button = new Button { Content = text, Style = (Style)FindResource(primary ? "Primary" : "Ghost") };
                button.Click += (_, _) => onClick();
                panel.Children.Add(button);
            }
            return panel;
        }

        private static TextBlock Text(string text) => new()
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
        };

        private static FrameworkElement Spacer(double height) => new Border { Height = height };

        // ================================================================ разное

        private static Color ParseColor(string? text, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text.Trim()) is Color c)
                    return Color.FromRgb(c.R, c.G, c.B);
            }
            catch { }
            return fallback;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void UseDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int on = 1;
                DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
        }
    }
}
