using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamicIsland.Core;
using DynamicIsland.Shared;

namespace DynamicIsland.Update
{
    /// <summary>
    /// Автообновление из GitHub Releases:
    /// проверяет последний релиз → качает zip → распаковывает → скрипт ждёт,
    /// пока остров закроется, копирует файлы поверх и запускает новую версию.
    /// </summary>
    public sealed class Updater : IDisposable
    {
        private static readonly HttpClient Http = CreateClient();

        private readonly IIslandHost _host;
        private readonly Func<IslandSettings> _settings;
        private readonly Action _shutdown;
        private readonly SemaphoreSlim _busy = new(1, 1);
        private Timer? _timer;

        public Updater(IIslandHost host, Func<IslandSettings> settings, Action shutdown)
        {
            _host = host;
            _settings = settings;
            _shutdown = shutdown;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            // GitHub API требует User-Agent
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicIsland-Updater/{AppInfo.Version}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public void Start()
        {
            // первая проверка через 20 сек после запуска, потом по расписанию
            _timer = new Timer(_ => _ = CheckAsync(manual: false), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(1));
        }

        private DateTime _lastAutoCheck = DateTime.MinValue;

        public async Task CheckAsync(bool manual)
        {
            var settings = _settings();

            if (!manual)
            {
                if (!settings.AutoUpdate) return;
                if (DateTime.UtcNow - _lastAutoCheck < TimeSpan.FromHours(settings.UpdateCheckHours)) return;
            }

            if (!await _busy.WaitAsync(0)) return;
            try
            {
                _lastAutoCheck = DateTime.UtcNow;

                if (AppInfo.IsDevBuild)
                {
                    WriteStatus(null, "Локальная сборка — обновления только для версий из GitHub Releases");
                    if (manual)
                        Alert("Это локальная сборка", "Обновления приходят только в версию из GitHub Releases", Palette.Gray);
                    return;
                }

                var release = await FetchLatestAsync();
                if (release == null)
                {
                    WriteStatus(null, "Не удалось проверить (нет сети или GitHub недоступен)");
                    if (manual) Alert("Не удалось проверить обновления", "Нет сети или GitHub недоступен", Palette.Orange);
                    return;
                }

                var current = ParseVersion(AppInfo.Version);
                var latest = ParseVersion(release.Tag);

                if (latest == null || current == null || latest <= current)
                {
                    WriteStatus(release.Tag.TrimStart('v'), "Установлена последняя версия");
                    if (manual) Alert("Обновлений нет", $"У тебя последняя версия {AppInfo.Version}", Palette.Green);
                    return;
                }

                string newVersion = latest.ToString();

                // версия, которую уже откатывали (не запустилась), ставим только вручную
                if (!manual && IsBadVersion(newVersion))
                {
                    WriteStatus(newVersion, $"Версия {newVersion} пропущена: в прошлый раз она не запустилась");
                    return;
                }

                WriteStatus(newVersion, $"Доступна версия {newVersion}");
                Log.Info($"Доступна версия {newVersion} (сейчас {AppInfo.Version})");

                if (settings.AutoInstall || manual)
                {
                    await InstallAsync(release, newVersion);
                }
                else
                {
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "update",
                        Glyph = Glyphs.Download,
                        Accent = Palette.Blue,
                        Title = $"Доступно обновление {newVersion}",
                        Subtitle = FirstLine(release.Notes) ?? "Dynamic Island",
                        Duration = null,
                        Actions =
                        {
                            new IslandAction { Glyph = Glyphs.Download, Text = "Обновить", OnClick = () => _ = InstallAsync(release, newVersion) },
                            new IslandAction { Glyph = Glyphs.Close, Tooltip = "Позже" },
                        },
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("Проверка обновлений", ex);
                WriteStatus(null, "Ошибка: " + ex.Message);
                if (manual) Alert("Ошибка обновления", ex.Message, Palette.Red);
            }
            finally
            {
                _busy.Release();
            }
        }

        // ------------------------------------------------------------ GitHub

        private sealed record Release(string Tag, string ZipUrl, string? ShaUrl, string Notes);

        private static async Task<Release?> FetchLatestAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                string? zip = null, sha = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    string? name = asset.GetProperty("name").GetString();
                    string? url = asset.GetProperty("browser_download_url").GetString();
                    if (name == AppInfo.ReleaseAsset) zip = url;
                    else if (name == AppInfo.ReleaseAsset + ".sha256") sha = url;
                }

                return zip == null ? null : new Release(tag, zip, sha, notes);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось получить релиз: " + ex.Message);
                return null;
            }
        }

        private static Version? ParseVersion(string text)
        {
            text = text.Trim().TrimStart('v', 'V');
            int dash = text.IndexOf('-');
            if (dash >= 0) text = text[..dash];
            return System.Version.TryParse(text, out var v) ? v : null;
        }

        private static string? FirstLine(string notes)
        {
            var line = notes.Split('\n').Select(l => l.Trim().TrimStart('#', '*', '-', ' ')).FirstOrDefault(l => l.Length > 0);
            return line == null ? null : line.Length > 60 ? line[..60] + "…" : line;
        }

        // ------------------------------------------------------------ установка

        private async Task InstallAsync(Release release, string version)
        {
            var progress = new LiveActivity("update")
            {
                Order = 0,
                Glyph = Glyphs.Download,
                Accent = Palette.Blue,
                Title = $"Обновление {version}",
                Subtitle = "скачиваю…",
                Badge = "↓",
                Progress = double.NaN,
            };
            _host.SetActivity(progress);

            try
            {
                string work = Path.Combine(Path.GetTempPath(), "DynamicIsland-update");
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);

                string zipPath = Path.Combine(work, "update.zip");
                string staging = Path.Combine(work, "files");

                // --- скачиваем с прогрессом
                using (var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? -1;

                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = File.Create(zipPath);

                    var buffer = new byte[81920];
                    long done = 0;
                    var lastReport = DateTime.UtcNow;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        done += read;
                        if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                        {
                            lastReport = DateTime.UtcNow;
                            progress.Progress = total > 0 ? (double)done / total : double.NaN;
                            progress.Subtitle = total > 0
                                ? $"скачиваю · {Format.Size(done)} из {Format.Size(total)}"
                                : $"скачиваю · {Format.Size(done)}";
                            progress.Badge = total > 0 ? $"{done * 100 / total}%" : "↓";
                            _host.SetActivity(progress);
                        }
                    }
                }

                // --- проверяем контрольную сумму (защита от битого/подменённого архива)
                progress.Subtitle = "проверяю архив…";
                progress.Progress = double.NaN;
                _host.SetActivity(progress);

                if (release.ShaUrl != null)
                {
                    string expected = (await Http.GetStringAsync(release.ShaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0];
                    string actual;
                    await using (var zip = File.OpenRead(zipPath))
                        actual = Convert.ToHexString(await SHA256.HashDataAsync(zip));

                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("контрольная сумма архива не совпала — файл повреждён");
                    Log.Info("SHA256 совпал: " + actual);
                }
                else
                {
                    Log.Warn("В релизе нет .sha256 — ставлю без проверки");
                }

                // --- распаковываем
                progress.Subtitle = "распаковываю…";
                _host.SetActivity(progress);

                ZipFile.ExtractToDirectory(zipPath, staging);

                if (!File.Exists(Path.Combine(staging, "DynamicIsland.exe")))
                    throw new InvalidOperationException("в архиве нет DynamicIsland.exe");

                // --- скрипт: замена файлов + проверка, что новая версия жива, иначе откат
                string installDir = AppContext.BaseDirectory.TrimEnd('\\');
                string backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "backup");
                string script = Path.Combine(work, "apply-update.cmd");
                File.Delete(UpdateOkFile);
                File.WriteAllText(script, BuildScript(installDir, staging, backupDir, version), new UTF8Encoding(false));

                progress.Subtitle = "перезапускаюсь…";
                _host.SetActivity(progress);
                Log.Info($"Ставлю {version}: {installDir}");
                await Task.Delay(600);

                bool needAdmin = !CanWrite(installDir);
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = needAdmin,           // runas работает только через ShellExecute
                    Verb = needAdmin ? "runas" : "",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });

                _shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("Установка обновления", ex);
                _host.RemoveActivity("update");
                WriteStatus(version, "Не удалось установить: " + ex.Message);
                Alert("Не удалось обновиться", ex.Message, Palette.Red);
            }
        }

        /// <summary>
        /// cmd-скрипт обновления. Пауза — через ping (timeout не работает без консоли).
        /// 1) ждём, пока остров закроется; 2) копия текущей версии в backup;
        /// 3) кладём новые файлы; 4) запускаем и ждём «я жив» (update-ok) до 45 сек;
        /// 5) не дождались — возвращаем backup и запускаем старую версию с --rollback.
        /// </summary>
        private static string BuildScript(string install, string staging, string backup, string version)
        {
            int pid = Environment.ProcessId;
            string exe = Path.Combine(install, "DynamicIsland.exe");
            const string quiet = "/NFL /NDL /NJH /NJS /NP >nul";

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"set \"OK={UpdateOkFile}\"");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)");
            sb.AppendLine("taskkill /im DynamicIsland.Settings.exe /f >nul 2>&1");
            // помощник трафика сам закрывается через пару секунд после острова
            sb.AppendLine("ping -n 4 127.0.0.1 >nul");
            sb.AppendLine($"robocopy \"{install}\" \"{backup}\" /MIR /R:2 /W:1 {quiet}");
            sb.AppendLine($"robocopy \"{staging}\" \"{install}\" /E /R:10 /W:1 {quiet}");
            sb.AppendLine($"start \"\" \"{exe}\" --updated {version}");
            sb.AppendLine("set /a n=0");
            sb.AppendLine(":health");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("if exist \"%OK%\" goto healthy");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if %n% lss 45 goto health");
            sb.AppendLine("rem новая версия не подтвердила, что запустилась — откат");
            sb.AppendLine("taskkill /im DynamicIsland.exe /f >nul 2>&1");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine($"robocopy \"{backup}\" \"{install}\" /MIR /R:5 /W:1 {quiet}");
            sb.AppendLine($"start \"\" \"{exe}\" --rollback {version}");
            sb.AppendLine("goto done");
            sb.AppendLine(":healthy");
            sb.AppendLine("del \"%OK%\" >nul 2>&1");
            sb.AppendLine(":done");
            sb.AppendLine($"rmdir /s /q \"{staging}\" >nul 2>&1");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            return sb.ToString();
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ «я жив» и откат

        public static string UpdateOkFile => Path.Combine(IslandSettings.Folder, "update-ok");
        private static string BadVersionFile => Path.Combine(IslandSettings.Folder, "bad-version.txt");

        /// <summary>Новая версия сообщает скрипту обновления, что запустилась нормально.</summary>
        public static void ConfirmHealthy()
        {
            try
            {
                Directory.CreateDirectory(IslandSettings.Folder);
                File.WriteAllText(UpdateOkFile, AppInfo.Version);
                Log.Info("Обновление подтверждено: остров работает");
            }
            catch { }
        }

        public static void MarkBadVersion(string version)
        {
            try { File.WriteAllText(BadVersionFile, version.Trim()); } catch { }
            Log.Warn($"Версия {version} не запустилась — откатились и пропускаем её");
        }

        private static bool IsBadVersion(string version)
        {
            try { return File.Exists(BadVersionFile) && File.ReadAllText(BadVersionFile).Trim() == version; }
            catch { return false; }
        }

        // ------------------------------------------------------------ helpers

        private void Alert(string title, string subtitle, System.Windows.Media.Color accent) =>
            _host.ShowAlert(new IslandAlert
            {
                Key = "update",
                Glyph = Glyphs.Download,
                Accent = accent,
                Title = title,
                Subtitle = subtitle,
                Duration = TimeSpan.FromSeconds(5),
            });

        private static void WriteStatus(string? latest, string state) =>
            new UpdateStatus
            {
                CurrentVersion = AppInfo.Version,
                LatestVersion = latest,
                LastCheck = DateTime.Now,
                State = state,
            }.Save();

        public void Dispose() => _timer?.Dispose();
    }
}
