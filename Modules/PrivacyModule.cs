using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DynamicIsland.Core;
using DynamicIsland.Shared;
using Microsoft.Win32;

namespace DynamicIsland.Modules
{
    /// <summary>
    /// Индикатор микрофона и камеры, как оранжевая/зелёная точка на iPhone.
    /// Windows сама записывает в реестр, какое приложение сейчас пользуется микрофоном/камерой
    /// (LastUsedTimeStop = 0 — значит, используется прямо сейчас).
    /// </summary>
    public sealed class PrivacyModule : IStartable, IDisposable
    {
        private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        private readonly IIslandHost _host;
        private Timer? _timer;
        private HashSet<string> _mic = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _cam = new(StringComparer.OrdinalIgnoreCase);
        private bool _first = true;

        public PrivacyModule(IIslandHost host) => _host = host;

        public void Start() => _timer = new Timer(_ => Poll(), null, 500, 1500);

        private void Poll()
        {
            try
            {
                var mic = InUse("microphone");
                var cam = InUse("webcam");

                Update(mic, ref _mic, "mic", Glyphs.Microphone, Palette.Orange, "Микрофон");
                Update(cam, ref _cam, "cam", Glyphs.Camera, Palette.Green, "Камера");
                _first = false;
            }
            catch (Exception ex)
            {
                Log.Warn("Микрофон/камера: " + ex.Message);
            }
        }

        private void Update(HashSet<string> now, ref HashSet<string> before, string key, string glyph, System.Windows.Media.Color color, string title)
        {
            var started = now.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
            bool changed = started.Count > 0 || before.Except(now, StringComparer.OrdinalIgnoreCase).Any();
            before = now;
            if (!changed) return;

            if (now.Count > 0)
                _host.SetStatus(key, glyph, color, $"{title}: {string.Join(", ", now)}");
            else
                _host.SetStatus(key, null, color, "");

            // про уже идущий при запуске острова звонок не кричим
            if (_first) return;

            foreach (var app in started)
            {
                _host.ShowAlert(new IslandAlert
                {
                    Key = key + ":" + app,
                    Glyph = glyph,
                    Accent = color,
                    Title = title,
                    Subtitle = $"{app} использует {(key == "mic" ? "микрофон" : "камеру")}",
                    Duration = TimeSpan.FromSeconds(3),
                    DropIfHidden = true,
                });
            }
        }

        private static HashSet<string> InUse(string capability)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var root = Registry.CurrentUser.OpenSubKey(ConsentStore + capability);
            if (root == null) return result;

            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var np = root.OpenSubKey(name);
                    if (np == null) continue;
                    foreach (var app in np.GetSubKeyNames())
                    {
                        using var k = np.OpenSubKey(app);
                        if (IsActive(k)) result.Add(PrettyNonPackaged(app));
                    }
                }
                else
                {
                    using var k = root.OpenSubKey(name);
                    if (IsActive(k)) result.Add(PrettyPackaged(name));
                }
            }

            return result;
        }

        private static bool IsActive(RegistryKey? key)
        {
            if (key == null) return false;
            long start = key.GetValue("LastUsedTimeStart") is long s ? s : 0;
            long stop = key.GetValue("LastUsedTimeStop") is long t ? t : -1;
            return start > 0 && stop == 0;
        }

        // «C:#Users#Никита#AppData#Local#Discord#app-1.0#Discord.exe» → «Discord»
        private static string PrettyNonPackaged(string key)
        {
            string file = key.Split('#').Last();
            return Path.GetFileNameWithoutExtension(file);
        }

        // «Microsoft.WindowsCamera_8wekyb3d8bbwe» → «WindowsCamera»
        private static string PrettyPackaged(string key)
        {
            string name = key.Split('_')[0];
            int dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _host.SetStatus("mic", null, Palette.Orange, "");
            _host.SetStatus("cam", null, Palette.Green, "");
        }
    }
}
