using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DynamicIsland.Shared
{
    /// <summary>Список мониторов через WinAPI (без WinForms). Координаты — в пикселях.</summary>
    public static class Monitors
    {
        public sealed record Info(string DeviceName, RECT Bounds, RECT WorkArea, bool IsPrimary)
        {
            public int Width => Bounds.Right - Bounds.Left;
            public int Height => Bounds.Bottom - Bounds.Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint MONITORINFOF_PRIMARY = 1;

        public static List<Info> All()
        {
            var list = new List<Info>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
                {
                    var info = FromHandle(h);
                    if (info != null) list.Add(info);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        public static Info? FromHandle(IntPtr hMonitor)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref mi)) return null;
            return new Info(mi.szDevice, mi.rcMonitor, mi.rcWork, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0);
        }

        public static Info? ForWindow(IntPtr hwnd) => FromHandle(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

        /// <summary>Монитор по имени из настроек, иначе основной.</summary>
        public static Info? Find(string? deviceName)
        {
            var all = All();
            if (!string.IsNullOrEmpty(deviceName))
            {
                var match = all.Find(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return all.Find(m => m.IsPrimary) ?? (all.Count > 0 ? all[0] : null);
        }
    }
}
