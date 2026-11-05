using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DynamicIsland.Core;
using DynamicIsland.Shared;

namespace DynamicIsland
{
    public partial class App : Application
    {
        private Mutex? _mutex;

        /// <summary>Версия, до которой только что обновились (--updated X).</summary>
        public static string? UpdatedTo { get; private set; }

        /// <summary>Версия, которую откатили, потому что она не запустилась (--rollback X).</summary>
        public static string? RolledBackFrom { get; private set; }

        // Аргументы не переживают перезапуск через explorer.exe — передаём их через файл
        private static string HandoffFile => Path.Combine(IslandSettings.Folder, "handoff.txt");

        protected override void OnStartup(StartupEventArgs e)
        {
            Log.Init("island");
            HookCrashes();

            var args = e.Args;
            UpdatedTo = ArgValue(args, "--updated");
            RolledBackFrom = ArgValue(args, "--rollback");

            // --- запущены из окна UAC, чтобы один раз поставить помощника трафика
            if (args.Contains("--install-helper"))
            {
                if (Elevation.IsAdmin())
                {
                    Elevation.RegisterHelper();
                    Elevation.RemoveLegacyIslandTask();
                    Elevation.RunHelper();
                }
                Shutdown(0);
                return;
            }

            // --- запущены с правами админа (старая версия 1.0, ярлык «от имени администратора»...).
            // Сам остров должен работать без админа, иначе не работает перетаскивание файлов.
            if (Elevation.IsAdmin() && !args.Contains("--as-admin"))
            {
                Log.Info("Запущен от администратора — ставлю помощника и перезапускаюсь обычным пользователем");
                try
                {
                    Elevation.RemoveLegacyIslandTask();
                    Elevation.RegisterHelper();
                }
                catch (Exception ex)
                {
                    Log.Error("Миграция прав", ex);
                }

                SaveHandoff();
                if (Elevation.RelaunchAsUser() && WaitForSecondIsland())
                {
                    Shutdown(0);
                    return;
                }
                // не получилось перезапуститься — работаем как есть (без перетаскивания файлов)
                Log.Warn("Перезапуск без админа не удался — остаюсь с правами администратора");
            }

            LoadHandoff();

            // --- только один остров. После обновления/перезапуска старый процесс может ещё закрываться.
            _mutex = new Mutex(false, "DynamicIsland.SingleInstance");
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(TimeSpan.FromSeconds(8));
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // прошлый остров упал, не отпустив мьютекс
            }

            if (!acquired)
            {
                Log.Info("Остров уже запущен — выходим");
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info("Выход");
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }

        /// <summary>Ждём, что explorer действительно запустил второй экземпляр острова.</summary>
        private static bool WaitForSecondIsland()
        {
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                if (System.Diagnostics.Process.GetProcessesByName("DynamicIsland").Length > 1) return true;
            }
            return false;
        }

        private static string? ArgValue(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static void SaveHandoff()
        {
            try
            {
                Directory.CreateDirectory(IslandSettings.Folder);
                File.WriteAllLines(HandoffFile, new[] { UpdatedTo ?? "", RolledBackFrom ?? "" });
            }
            catch { }
        }

        private static void LoadHandoff()
        {
            try
            {
                if (!File.Exists(HandoffFile)) return;
                if (File.GetLastWriteTimeUtc(HandoffFile) > DateTime.UtcNow.AddMinutes(-2))
                {
                    var lines = File.ReadAllLines(HandoffFile);
                    if (lines.Length > 0 && lines[0].Length > 0) UpdatedTo ??= lines[0];
                    if (lines.Length > 1 && lines[1].Length > 0) RolledBackFrom ??= lines[1];
                }
                File.Delete(HandoffFile);
            }
            catch { }
        }

        private void HookCrashes()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Error("Необработанное исключение (падение)", e.ExceptionObject as Exception);

            DispatcherUnhandledException += (_, e) =>
            {
                // ошибка в интерфейсе: пишем в лог и продолжаем жить
                Log.Error("Ошибка в интерфейсе", e.Exception);
                e.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error("Ошибка в фоновой задаче", e.Exception);
                e.SetObserved();
            };
        }
    }
}
