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

            // развёрнутое окно (браузер на весь экран с автоскрытием панели задач) — это не «полноэкранный режим»
            if (!IsWindowVisible(fg) || IsIconic(fg) || IsZoomed(fg)) return false;

            var fgMonitor = Monitors.ForWindow(fg);
            var ownMonitor = Monitors.ForWindow(own);
            if (fgMonitor == null || ownMonitor == null) return false;
            if (!string.Equals(fgMonitor.DeviceName, ownMonitor.DeviceName, StringComparison.OrdinalIgnoreCase)) return false;

            // окно закрывает весь монитор (видео на весь экран, borderless-игры)
            if (GetWindowRect(fg, out var r))
            {
                var b = fgMonitor.Bounds;
                if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) return true;
            }

            // эксклюзивный полноэкранный режим DirectX / режим презентации
            if (SHQueryUserNotificationState(out int state) == 0 && (state == 3 || state == 4)) return true;

            return false;
        }

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
