using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using DynamicIsland.Shared;

namespace DynamicIsland.Core
{
    /// <summary>
    /// Права администратора. Сам остров работает БЕЗ них (иначе Windows запрещает
    /// перетаскивать на него файлы). Админ нужен только помощнику трафика —
    /// он ставится один раз как задача планировщика «с наивысшими правами»
    /// и дальше запускается без окна UAC.
    /// </summary>
    public static class Elevation
    {
        public const string HelperTaskName = "DynamicIsland NetHelper";
        private const string LegacyIslandTask = "DynamicIsland";

        public static string HelperExe => Path.Combine(AppContext.BaseDirectory, "DynamicIsland.NetHelper.exe");

        public static string IslandExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DynamicIsland.exe");

        public static bool IsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ задача помощника

        /// <summary>Задача есть и указывает на помощника из ЭТОЙ папки.</summary>
        public static bool IsHelperInstalled()
        {
            // через PowerShell с UTF-8: в пути может быть кириллица (C:\Users\Никита\...)
            var (code, output) = RunPowerShell(
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8;" +
                "(Get-ScheduledTask -TaskName '" + HelperTaskName + "' -ErrorAction SilentlyContinue).Actions[0].Execute");
            return code == 0 && output.Trim().Trim('"').Equals(HelperExe, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Запустить помощника (работает и без админа — задача уже «с наивысшими правами»).</summary>
        public static void RunHelper()
        {
            var (code, output) = Run("schtasks.exe", $"/Run /TN \"{HelperTaskName}\"");
            if (code != 0) Log.Warn($"Не удалось запустить помощника: {output.Trim()}");
        }

        /// <summary>Только из-под админа: создать/обновить задачу помощника.</summary>
        public static void RegisterHelper()
        {
            if (!File.Exists(HelperExe))
            {
                Log.Warn("Помощник не найден: " + HelperExe);
                return;
            }

            string exe = HelperExe.Replace("'", "''");
            string script =
                "$a = New-ScheduledTaskAction -Execute '" + exe + "';" +
                "$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive;" +
                "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                "-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable;" +
                "Register-ScheduledTask -TaskName '" + HelperTaskName + "' -Action $a -Principal $p -Settings $s -Force | Out-Null";

            var (code, output) = RunPowerShell(script);
            Log.Info($"Регистрация помощника: код {code} {output.Trim()}");
        }

        /// <summary>Только из-под админа: убрать задачу автозапуска острова из версии 1.0 (теперь автозапуск через реестр).</summary>
        public static void RemoveLegacyIslandTask()
        {
            var (code, _) = Run("schtasks.exe", $"/Query /TN \"{LegacyIslandTask}\"");
            if (code != 0) return;
            Run("schtasks.exe", $"/Delete /TN \"{LegacyIslandTask}\" /F");
            Log.Info("Старая задача автозапуска 1.0 удалена");
        }

        /// <summary>Показать окно UAC и установить помощника. false — пользователь отказался.</summary>
        public static bool RequestHelperInstall()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(IslandExe, "--install-helper")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit(60000);
                return p?.ExitCode == 0 || IsHelperInstalled();
            }
            catch (Win32Exception)
            {
                return false; // нажали «Нет» в окне UAC
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось запросить права", ex);
                return false;
            }
        }

        /// <summary>
        /// Перезапустить остров обычным пользователем (без админа).
        /// Хитрость: explorer.exe всегда запускает программы с правами текущего пользователя.
        /// </summary>
        public static bool RelaunchAsUser()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{IslandExe}\"") { UseShellExecute = false });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось перезапуститься без админа", ex);
                return false;
            }
        }

        // ------------------------------------------------------------ автозапуск (без админа)

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void SetAutostart(bool enabled)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key.SetValue("DynamicIsland", $"\"{IslandExe}\"");
                else if (key.GetValue("DynamicIsland") != null) key.DeleteValue("DynamicIsland");
            }
            catch (Exception ex)
            {
                Log.Error("Автозапуск", ex);
            }
        }

        // ------------------------------------------------------------ процессы

        public static (int Code, string Output) RunPowerShell(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", 30000);
        }

        public static (int Code, string Output) Run(string file, string args, int timeoutMs = 15000)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                });
                if (p == null) return (-1, "");
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutMs);
                return (p.HasExited ? p.ExitCode : -1, output);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }
    }
}
