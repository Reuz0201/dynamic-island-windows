using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Shared
{
    /// <summary>
    /// Настройки острова. Хранятся в %AppData%\DynamicIsland\settings.json.
    /// Приложение настроек пишет файл, остров следит за ним и применяет изменения на лету.
    /// </summary>
    public sealed class IslandSettings
    {
        // ------------------------------------------------------------ внешний вид

        /// <summary>Цвет фона острова (#RRGGBB).</summary>
        public string BackgroundColor { get; set; } = "#000000";

        /// <summary>Непрозрачность фона 0.3–1.</summary>
        public double BackgroundOpacity { get; set; } = 0.95;

        /// <summary>"album" — цвет из обложки, "fixed" — свой цвет.</summary>
        public string AccentMode { get; set; } = "album";

        public string AccentColor { get; set; } = "#7A5CFF";

        public bool GlowEnabled { get; set; } = true;

        /// <summary>Сила свечения 0.2–1.6.</summary>
        public double GlowIntensity { get; set; } = 1.0;

        /// <summary>Размер острова 0.8–1.4.</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>Скруглённость 0.3 (почти квадрат) – 1 (таблетка).</summary>
        public double Roundness { get; set; } = 1.0;

        public bool BorderEnabled { get; set; } = true;

        /// <summary>Скорость анимаций 0.5–2 (больше — быстрее).</summary>
        public double AnimationSpeed { get; set; } = 1.0;

        /// <summary>«Пружинка» при раскрытии.</summary>
        public bool SpringAnimations { get; set; } = true;

        public string FontFamily { get; set; } = "Segoe UI Variable Display";

        /// <summary>"pill" — плашка с текстом, "mini" — тонкая полоска, когда ничего нет.</summary>
        public string IdleStyle { get; set; } = "pill";

        public string IdleText { get; set; } = "Тишина";

        public bool ShowEqualizer { get; set; } = true;

        /// <summary>Эквалайзер качает под реальный звук системы (иначе — просто анимация).</summary>
        public bool LiveEqualizer { get; set; } = true;

        public bool ShowArtist { get; set; } = true;

        /// <summary>Множитель длительности уведомлений 0.5–3.</summary>
        public double AlertDuration { get; set; } = 1.0;

        // ------------------------------------------------------------ расположение

        /// <summary>Отступ сверху, px.</summary>
        public double OffsetY { get; set; } = 10;

        /// <summary>Сдвиг по горизонтали от центра, px (минус — влево).</summary>
        public double OffsetX { get; set; } = 0;

        /// <summary>Имя монитора (\\.\DISPLAY2). Пусто — основной.</summary>
        public string Monitor { get; set; } = "";

        /// <summary>Прятать остров, когда открыта игра/видео на весь экран.</summary>
        public bool HideInFullscreen { get; set; } = true;

        // ------------------------------------------------------------ модули

        public bool ModuleMusic { get; set; } = true;
        public bool ModuleTimers { get; set; } = true;
        public bool ModuleBattery { get; set; } = true;
        public bool ModuleBluetooth { get; set; } = true;
        public bool ModuleDownloads { get; set; } = true;
        public bool ModuleNetwork { get; set; } = true;
        public bool ModuleVpn { get; set; } = true;
        public bool ModuleVolume { get; set; } = true;
        public bool ModuleBrightness { get; set; } = true;
        public bool ModulePrivacy { get; set; } = true;
        public bool ModuleShelf { get; set; } = true;
        public bool ModuleDrives { get; set; } = true;
        public bool ModuleWelcome { get; set; } = true;
        public bool ModuleToggles { get; set; } = true;
        public bool ModuleWindowsUpdate { get; set; } = true;
        public bool ModuleDevices { get; set; } = true;

        // ------------------------------------------------------------ система и обновления

        public bool Autostart { get; set; } = false;

        public bool AutoUpdate { get; set; } = true;

        /// <summary>true — ставить обновления сразу, false — спрашивать.</summary>
        public bool AutoInstall { get; set; } = true;

        public int UpdateCheckHours { get; set; } = 6;

        /// <summary>Приложение настроек ставит сюда время — остров видит и проверяет обновления.</summary>
        public DateTime? UpdateCheckRequested { get; set; }

        // ------------------------------------------------------------ файл

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

        public static string FilePath => Path.Combine(Folder, "settings.json");

        public static string UpdateStatusPath => Path.Combine(Folder, "update-status.json");

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static IslandSettings Load()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new IslandSettings();
                    string text = File.ReadAllText(FilePath);
                    return (JsonSerializer.Deserialize<IslandSettings>(text, Json) ?? new IslandSettings()).Normalized();
                }
                catch (IOException)
                {
                    // файл как раз пишется — чуть подождём
                    System.Threading.Thread.Sleep(60);
                }
                catch
                {
                    return new IslandSettings();
                }
            }
            return new IslandSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }

        public IslandSettings Clone() =>
            JsonSerializer.Deserialize<IslandSettings>(JsonSerializer.Serialize(this, Json), Json)!;

        /// <summary>Подрезает значения до разумных границ (если файл правили руками).</summary>
        public IslandSettings Normalized()
        {
            BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.3, 1);
            GlowIntensity = Math.Clamp(GlowIntensity, 0, 2);
            Scale = Math.Clamp(Scale, 0.7, 1.6);
            Roundness = Math.Clamp(Roundness, 0.2, 1);
            AnimationSpeed = Math.Clamp(AnimationSpeed, 0.3, 3);
            AlertDuration = Math.Clamp(AlertDuration, 0.3, 5);
            OffsetY = Math.Clamp(OffsetY, 0, 400);
            OffsetX = Math.Clamp(OffsetX, -2000, 2000);
            UpdateCheckHours = Math.Clamp(UpdateCheckHours, 1, 168);
            if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Segoe UI Variable Display";
            if (IdleStyle != "mini") IdleStyle = "pill";
            if (AccentMode != "fixed") AccentMode = "album";
            IdleText ??= "";
            Monitor ??= "";
            return this;
        }
    }

    /// <summary>Что остров пишет про обновления — приложение настроек это показывает.</summary>
    public sealed class UpdateStatus
    {
        public string CurrentVersion { get; set; } = "";
        public string? LatestVersion { get; set; }
        public DateTime? LastCheck { get; set; }
        public string State { get; set; } = "";

        public static UpdateStatus? Load()
        {
            try
            {
                if (!File.Exists(IslandSettings.UpdateStatusPath)) return null;
                return JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(IslandSettings.UpdateStatusPath));
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(IslandSettings.Folder);
                File.WriteAllText(IslandSettings.UpdateStatusPath, JsonSerializer.Serialize(this));
            }
            catch { }
        }
    }

    public static class AppInfo
    {
        public const string RepoOwner = "Reuz0201";
        public const string RepoName = "dynamic-island-windows";
        public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
        public const string ReleaseAsset = "DynamicIsland-win-x64.zip";

        /// <summary>«1.0.12» или «0.0.0-dev» для локальной сборки.</summary>
        public static string Version
        {
            get
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                var info = asm == null ? null
                    : (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                        asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                string v = info?.InformationalVersion ?? asm?.GetName().Version?.ToString() ?? "0.0.0-dev";
                int plus = v.IndexOf('+');
                return plus >= 0 ? v[..plus] : v;
            }
        }

        public static bool IsDevBuild => Version.Contains("-dev", StringComparison.OrdinalIgnoreCase) || Version.StartsWith("0.0.0");
    }
}
