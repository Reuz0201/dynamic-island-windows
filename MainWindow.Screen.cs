using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Shared;

namespace DynamicIsland
{
    /// <summary>Положение на нужном мониторе и прятание в полноэкранных играх/видео.</summary>
    public partial class MainWindow
    {
        private bool _hiddenForFullscreen;
        private DispatcherTimer? _fullscreenTimer;

        // ================================================================ положение

        private void PositionWindow()
        {
            double offX = _settings.OffsetX, offY = _settings.OffsetY;
            var monitor = Monitors.Find(_settings.Monitor);

            if (monitor == null)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - Width) / 2 + offX;
                Top = area.Top + offY - 10;
                return;
            }

            // WinAPI отдаёт пиксели, WPF живёт в DIP — переводим по системному DPI
            double scale = SystemDpiScale();
            var work = monitor.WorkArea;
            double left = work.Left / scale, top = work.Top / scale, width = (work.Right - work.Left) / scale;

            Left = left + (width - Width) / 2 + offX;
            // сам остров стоит на 10 px ниже края окна (место под свечение)
            Top = top + offY - 10;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private static double SystemDpiScale()
        {
            try { return Math.Max(1, GetDpiForSystem()) / 96.0; }
            catch { return 1; }
        }

        // ================================================================ полноэкранный режим

        private void InitFullscreenWatch()
        {
            _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
            _fullscreenTimer.Start();
        }

        private void CheckFullscreen()
        {
            bool fullscreen = false;
            try
            {
                fullscreen = _settings.HideInFullscreen && IsSomethingFullscreen();
            }
            catch (Exception ex)
            {
                Log.Warn("Проверка полноэкранного режима: " + ex.Message);
            }

            if (fullscreen == _hiddenForFullscreen) return;
            _hiddenForFullscreen = fullscreen;
            Log.Info(fullscreen ? "Полноэкранное приложение — прячусь" : "Полноэкранный режим закончился");

            IslandRow.BeginAnimation(OpacityProperty, new DoubleAnimation(fullscreen ? 0 : 1, TimeSpan.FromMilliseconds(250)));
            IslandRow.IsHitTestVisible = !fullscreen;

            // вышли из игры — показываем то, что накопилось
            if (!fullscreen && _alert == null && _alertQueue.Count > 0)
            {
                var next = _alertQueue[0];
                _alertQueue.RemoveAt(0);
                Present(next);
            }
        }

        private bool IsSomethingFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            IntPtr own = new WindowInteropHelper(this).Handle;
            if (fg == IntPtr.Zero || fg == own) return false;

            // рабочий стол и панель задач — не «полноэкранные»
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string name = cls.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

            if (!IsWindowVisible(fg) || IsIconic(fg)) return false;

            var fgMonitor = Monitors.ForWindow(fg);
            var ownMonitor = Monitors.ForWindow(own);
            if (fgMonitor == null || ownMonitor == null) return false;
            if (!string.Equals(fgMonitor.DeviceName, ownMonitor.DeviceName, StringComparison.OrdinalIgnoreCase)) return false;

            SHQueryUserNotificationState(out int state);

            // эксклюзивный полноэкранный режим DirectX / режим презентации
            if (state == 3 || state == 4) return true;

            if (!GetWindowRect(fg, out var r)) return false;
            var b = fgMonitor.Bounds;
            bool coversMonitor = r.Left <= b.Left + 2 && r.Top <= b.Top + 2 && r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2;
            if (!coversMonitor) return false;

            // Окно на весь монитор. Отличаем «полноэкранный режим» от просто развёрнутого окна:
            // у видео на весь экран (YouTube, плееры на сайтах — F11/кнопка ⛶), игр и плееров
            // нет заголовка окна. Браузер при этом может оставаться «развёрнутым» (IsZoomed),
            // поэтому на IsZoomed не смотрим.
            int style = GetWindowLong(fg, GWL_STYLE);
            bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
            bool fullscreen = !hasCaption || state == 2 /* QUNS_BUSY: Windows сама считает, что идёт полноэкранное приложение */ || !IsZoomed(fg);

            LogFullscreenDecision(fg, name, style, state, fullscreen);
            return fullscreen;
        }

        private IntPtr _lastLoggedWindow;
        private bool _lastLoggedDecision;

        /// <summary>Пишем в лог, почему окно посчитали (или нет) полноэкранным — раз на окно.</summary>
        private void LogFullscreenDecision(IntPtr hwnd, string cls, int style, int state, bool fullscreen)
        {
            if (hwnd == _lastLoggedWindow && fullscreen == _lastLoggedDecision) return;
            _lastLoggedWindow = hwnd;
            _lastLoggedDecision = fullscreen;
            Log.Info($"Окно на весь монитор: класс={cls} стиль=0x{style:X8} QUNS={state} развёрнуто={IsZoomed(hwnd)} → {(fullscreen ? "прячусь" : "не прячусь")}");
        }

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;

        // GetWindowLong объявлен в MainWindow.xaml.cs

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Monitors.RECT rect);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
    }
}
