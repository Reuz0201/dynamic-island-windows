using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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
                var latest = ParseVersion(release.Value.Tag);

                if (latest == null || current == null || latest <= current)
                {
                    WriteStatus(release.Value.Tag.TrimStart('v'), "Установлена последняя версия");
                    if (manual) Alert("Обновлений нет", $"У тебя последняя версия {AppInfo.Version}", Palette.Green);
                    return;
                }

                string newVersion = latest.ToString();
                WriteStatus(newVersion, $"Доступна версия {newVersion}");

                if (settings.AutoInstall || manual)
                {
                    await InstallAsync(release.Value.ZipUrl, newVersion);
                }
                else
                {
                    _host.ShowAlert(new IslandAlert
                    {
                        Key = "update",
                        Glyph = Glyphs.Download,
                        Accent = Palette.Blue,
                        Title = $"Доступно обновление {newVersion}",
                        Subtitle = FirstLine(release.Value.Notes) ?? "Dynamic Island",
                        Duration = null,
                        Actions =
                        {
                            new IslandAction { Glyph = Glyphs.Download, Text = "Обновить", OnClick = () => _ = InstallAsync(release.Value.ZipUrl, newVersion) },
                            new IslandAction { Glyph = Glyphs.Close, Tooltip = "Позже" },
                        },
                    });
                }
            }
            catch (Exception ex)
            {
                WriteStatus(null, "Ошибка: " + ex.Message);
                if (manual) Alert("Ошибка обновления", ex.Message, Palette.Red);
            }
            finally
            {
                _busy.Release();
            }
        }

        // ------------------------------------------------------------ GitHub

        private static async Task<(string Tag, string ZipUrl, string Notes)?> FetchLatestAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                string? zip = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() == AppInfo.ReleaseAsset)
                    {
                        zip = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }

                return zip == null ? null : (tag, zip, notes);
            }
            catch
            {
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

        private async Task InstallAsync(string zipUrl, string version)
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
                using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
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

                // --- распаковываем
                progress.Subtitle = "распаковываю…";
                progress.Progress = double.NaN;
                _host.SetActivity(progress);

                ZipFile.ExtractToDirectory(zipPath, staging);

                if (!File.Exists(Path.Combine(staging, "DynamicIsland.exe")))
                    throw new InvalidOperationException("в архиве нет DynamicIsland.exe");

                // --- скрипт замены файлов
                string installDir = AppContext.BaseDirectory.TrimEnd('\\');
                string exe = Path.Combine(installDir, "DynamicIsland.exe");
                string script = Path.Combine(work, "apply-update.cmd");
                int pid = Environment.ProcessId;

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("chcp 65001 >nul");
                sb.AppendLine(":wait");
                sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
                sb.AppendLine("taskkill /im DynamicIsland.Settings.exe /f >nul 2>&1");
                sb.AppendLine($"robocopy \"{staging}\" \"{installDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
                sb.AppendLine($"start \"\" \"{exe}\" --updated {version}");
                sb.AppendLine($"rmdir /s /q \"{staging}\" >nul 2>&1");
                sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
                File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

                progress.Subtitle = "перезапускаюсь…";
                _host.SetActivity(progress);
                await Task.Delay(600);

                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });

                _shutdown();
            }
            catch (Exception ex)
            {
                _host.RemoveActivity("update");
                WriteStatus(version, "Не удалось установить: " + ex.Message);
                Alert("Не удалось обновиться", ex.Message, Palette.Red);
            }
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
