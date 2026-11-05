using System;
using System.Threading;
using System.Windows;

namespace DynamicIsland
{
    public partial class App : Application
    {
        private Mutex? _mutex;

        /// <summary>Версия, до которой только что обновились (аргумент --updated X).</summary>
        public static string? UpdatedTo { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (e.Args[i] == "--updated") UpdatedTo = e.Args[i + 1];
            }

            // Только один остров за раз. После обновления старый процесс может ещё
            // закрываться — немного подождём, прежде чем сдаться.
            _mutex = new Mutex(false, "DynamicIsland.SingleInstance");
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(UpdatedTo != null ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // прошлый остров упал, не отпустив мьютекс
            }

            if (!acquired)
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
