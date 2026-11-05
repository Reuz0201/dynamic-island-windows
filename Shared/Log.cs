using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DynamicIsland.Shared
{
    /// <summary>
    /// Простой лог в %AppData%\DynamicIsland\logs\{имя}-ГГГГММДД.log.
    /// Хранятся последние 7 дней. Писать можно из любого потока.
    /// </summary>
    public static class Log
    {
        private static readonly object Lock = new();
        private static string _name = "island";
        private static bool _cleaned;

        public static string Folder => Path.Combine(IslandSettings.Folder, "logs");

        public static void Init(string name)
        {
            _name = name;
            Info($"=== запуск {name} {AppInfo.Version} · Windows {Environment.OSVersion.Version} · .NET {Environment.Version}");
        }

        public static void Info(string message) => Write("INFO ", message);

        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception? ex = null) =>
            Write("ERROR", ex == null ? message : $"{message}\n{ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, $"{_name}-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);

                    if (!_cleaned)
                    {
                        _cleaned = true;
                        foreach (var old in new DirectoryInfo(Folder).GetFiles("*.log")
                                     .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-7)))
                        {
                            try { old.Delete(); } catch { }
                        }
                    }
                }
            }
            catch
            {
                // лог не должен ронять приложение
            }
        }
    }
}
