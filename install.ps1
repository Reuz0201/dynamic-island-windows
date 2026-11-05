# Установка / переустановка Dynamic Island для Windows.
#   irm https://raw.githubusercontent.com/Reuz0201/dynamic-island-windows/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = 'Reuz0201/dynamic-island-windows'
$dir  = Join-Path $env:LOCALAPPDATA 'Programs\DynamicIsland'

Write-Host ''
Write-Host '  Dynamic Island для Windows' -ForegroundColor Magenta
Write-Host ''

# --- .NET Desktop Runtime 8+
$hasRuntime = $false
try {
    $runtimes = & dotnet --list-runtimes 2>$null
    $hasRuntime = [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App (\d+)\.' -and [int]$Matches[1] -ge 8 })
} catch { }

if (-not $hasRuntime) {
    Write-Host '  Нужен .NET Desktop Runtime 8 — ставлю через winget...' -ForegroundColor Yellow
    try {
        winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --accept-source-agreements --accept-package-agreements --silent
    } catch {
        Write-Host '  Не получилось. Скачай вручную: https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Red
        return
    }
}

# --- последний релиз
Write-Host '  Ищу последнюю версию...'
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'DynamicIsland-Installer' }
$asset = $release.assets | Where-Object { $_.name -eq 'DynamicIsland-win-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw 'В последнем релизе нет DynamicIsland-win-x64.zip' }

$zip = Join-Path $env:TEMP 'DynamicIsland-install.zip'
Write-Host "  Качаю $($release.tag_name)..."
Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing

# --- закрываем запущенный остров
foreach ($proc in @(Get-Process DynamicIsland, DynamicIsland.Settings -ErrorAction SilentlyContinue)) {
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {
        Write-Host '  Остров запущен от администратора — закрой его (ПКМ → Выход) и запусти установку ещё раз.' -ForegroundColor Red
        return
    }
}
Start-Sleep -Milliseconds 500

# --- распаковка
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Expand-Archive -Path $zip -DestinationPath $dir -Force
Remove-Item $zip -ErrorAction SilentlyContinue

# --- ярлыки в меню «Пуск»
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
foreach ($item in @(
    @{ Name = 'Dynamic Island';            Exe = 'DynamicIsland.exe' },
    @{ Name = 'Dynamic Island — настройки'; Exe = 'DynamicIsland.Settings.exe' })) {
    $lnk = $shell.CreateShortcut((Join-Path $programs "$($item.Name).lnk"))
    $lnk.TargetPath = Join-Path $dir $item.Exe
    $lnk.WorkingDirectory = $dir
    $lnk.Save()
}

Write-Host "  Установлено в $dir" -ForegroundColor Green
Write-Host '  Запускаю...'
Start-Process (Join-Path $dir 'DynamicIsland.exe')
Write-Host ''
Write-Host '  Готово! Настройки: ПКМ по острову → «Настройки…»' -ForegroundColor Green
Write-Host '  Для «трафика приложений» остров один раз попросит разрешение администратора.' -ForegroundColor DarkGray
Write-Host ''
