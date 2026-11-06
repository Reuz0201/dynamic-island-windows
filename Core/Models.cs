using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Core
{
    /// <summary>Кнопка внутри активности или алерта.</summary>
    public sealed class IslandAction
    {
        public string Glyph { get; init; } = "";
        public string? Text { get; init; }
        public string Tooltip { get; init; } = "";
        public Action OnClick { get; init; } = () => { };
    }

    /// <summary>
    /// «Живая активность» — висит, пока идёт: таймер, секундомер, загрузка, отправка.
    /// Видна в компактном острове / во втором кружке и строкой в раскрытом виде.
    /// </summary>
    public sealed class LiveActivity
    {
        public LiveActivity(string id) => Id = id;

        public string Id { get; }

        /// <summary>Чем меньше, тем важнее (первым попадает в кружок).</summary>
        public int Order { get; set; }

        public string Glyph { get; set; } = "";
        public Color Accent { get; set; } = Colors.White;
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";

        /// <summary>Короткий текст для кружка/компакта: «4:32», «5 МБ/с».</summary>
        public string Badge { get; set; } = "";

        /// <summary>null — без полоски, double.NaN — «неизвестно сколько» (пульсирует), 0..1 — прогресс.</summary>
        public double? Progress { get; set; }

        public List<IslandAction> Actions { get; set; } = new();

        /// <summary>Показывать только строкой в раскрытом острове (не в компакте и не во втором кружке).</summary>
        public bool ExpandedOnly { get; set; }

        public string ActionsKey => string.Join("|", Actions.Select(a => a.Glyph + a.Text));
    }

    /// <summary>Всплывашка: «Заряжается», «Наушники подключены», «Скачано», «VPN подключён».</summary>
    public sealed class IslandAlert
    {
        public string Glyph { get; init; } = "";

        /// <summary>Своя иконка вместо глифа (батарейка, флаг страны).</summary>
        public Func<FrameworkElement>? IconFactory { get; init; }

        public Color Accent { get; init; } = Colors.White;
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string? Detail { get; init; }

        /// <summary>null — висит, пока не закроешь.</summary>
        public TimeSpan? Duration { get; init; } = TimeSpan.FromSeconds(4);

        public List<IslandAction> Actions { get; init; } = new();

        /// <summary>Одинаковый ключ — новый алерт заменяет старый, а не встаёт в очередь.</summary>
        public string? Key { get; init; }

        public Action? OnDismiss { get; init; }

        /// <summary>Полоска уровня 0..1 (громкость, яркость). null — без полоски.</summary>
        public double? Progress { get; init; }

        /// <summary>Не копить в очереди, если остров спрятан (игра на весь экран): громкость и т.п.</summary>
        public bool DropIfHidden { get; init; }

        /// <summary>Повторное обновление с тем же ключом — без «пульса» и вспышки.</summary>
        public bool Quiet { get; init; }
    }

    /// <summary>То, через что модули общаются с островом. Можно звать из любого потока.</summary>
    public interface IIslandHost
    {
        void ShowAlert(IslandAlert alert);
        void DismissAlert(IslandAlert alert);
        void SetActivity(LiveActivity activity);
        void RemoveActivity(string id);

        /// <summary>Маленький значок-статус в компактном острове (например, щит VPN). glyph = null — убрать.</summary>
        void SetStatus(string key, string? glyph, Color color, string text);
    }

    public static class Palette
    {
        public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
        public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
        public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
        public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
        public static readonly Color Cyan = Color.FromRgb(0x64, 0xD2, 0xFF);
        public static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
        public static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
        public static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    }

    /// <summary>Глифы шрифта Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
    public static class Glyphs
    {
        public const string Play = "";
        public const string Pause = "";
        public const string Prev = "";
        public const string Next = "";
        public const string Stop = "";
        public const string Close = "";
        public const string Add = "";
        public const string Check = "";
        public const string Reset = "";
        public const string Music = "";
        public const string Timer = "";
        public const string Stopwatch = "";
        public const string Download = "";
        public const string Upload = "";
        public const string Folder = "";
        public const string OpenFile = "";
        public const string Headphones = "";
        public const string Bluetooth = "";
        public const string Shield = "";
        public const string Globe = "";
        public const string Lightning = "";
        public const string Attach = "\uE723";
        public const string Microphone = "\uE720";
        public const string Camera = "\uE722";
        public const string Volume0 = "\uE992";
        public const string Volume1 = "\uE993";
        public const string Volume2 = "\uE994";
        public const string Volume3 = "\uE995";
        public const string Mute = "\uE74F";
        public const string Brightness = "\uE706";
        public const string Speaker = "\uE7F5";
    }

    public static class Format
    {
        public static string Size(double bytes)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int i = 0;
            while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
            return i == 0 ? $"{bytes:0} {units[i]}" : bytes >= 100 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
        }

        public static string Speed(double bytesPerSec) => Size(bytesPerSec) + "/с";

        public static string Clock(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        public static string HumanMinutes(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} сек";
            if (t.TotalMinutes < 60 && t.Seconds == 0) return $"{(int)t.TotalMinutes} мин";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} мин {t.Seconds} сек";
            return t.Minutes == 0 ? $"{(int)t.TotalHours} ч" : $"{(int)t.TotalHours} ч {t.Minutes} мин";
        }
    }
}
