using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using Microsoft.Win32;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Изредка (не чаще раза в 6 часов) напоминает, что Windows поставила обновления
    /// и ждёт перезагрузки, — чтобы она не перезагрузилась сама в неудобный момент.
    /// </summary>
    public sealed class WindowsUpdateModule : IStartable, IDisposable
    {
        private static readonly TimeSpan RemindEvery = TimeSpan.FromHours(6);
        private const string UpdateGlyph = "";

        private readonly IIslandHost _host;
        private Timer? _timer;

        private static string StampFile => Path.Combine(IslandSettings.Folder, "windows-update-reminded.txt");

        public WindowsUpdateModule(IIslandHost host) => _host = host;

        // первая проверка через 3 минуты после запуска, дальше раз в 30 минут
        public void Start() => _timer = new Timer(_ => Check(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(30));

        private void Check()
        {
            try
            {
                if (!RebootPending()) return;
                if (DateTime.UtcNow - LastReminded() < RemindEvery) return;

                SaveReminded();
                Log.Info("Windows ждёт перезагрузки — напоминаем");

                _host.ShowAlert(new IslandAlert
                {
                    Key = "windows-update",
                    Glyph = UpdateGlyph,
                    Accent = Palette.Blue,
                    Title = "Windows ждёт перезагрузки",
                    Subtitle = "чтобы доустановить обновления",
                    Detail = "перезагрузись, когда будет удобно",
                    Duration = TimeSpan.FromSeconds(10),
                    DropIfHidden = true, // в игре не мешаем — напомним в следующий раз
                    Actions =
                    {
                        new IslandAction { Glyph = UpdateGlyph, Text = "Открыть", OnClick = OpenWindowsUpdate },
                        new IslandAction { Glyph = Glyphs.Close, Tooltip = "Позже" },
                    },
                });
            }
            catch (Exception ex)
            {
                Log.Warn("Проверка Windows Update: " + ex.Message);
            }
        }

        private static bool RebootPending()
        {
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu != null) return true;
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return cbs != null;
        }

        private static DateTime LastReminded()
        {
            try
            {
                if (File.Exists(StampFile) && DateTime.TryParse(File.ReadAllText(StampFile), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    return t;
            }
            catch { }
            return DateTime.MinValue;
        }

        private static void SaveReminded()
        {
            try
            {
                Directory.CreateDirectory(IslandSettings.Folder);
                File.WriteAllText(StampFile, DateTime.UtcNow.ToString("o"));
            }
            catch { }
        }

        private static void OpenWindowsUpdate()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true }); } catch { }
        }

        public void Dispose() => _timer?.Dispose();
    }
}
