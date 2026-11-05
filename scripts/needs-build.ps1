# Prints "yes" if a rebuild is needed (no exe yet or sources changed since last build), otherwise "no".
$root  = Resolve-Path (Join-Path $PSScriptRoot '..')
$out   = Join-Path $root 'bin\Release\net8.0-windows10.0.19041.0'
$stamp = Join-Path $root 'bin\build.stamp'

if (-not (Test-Path (Join-Path $out 'DynamicIsland.exe')) -or
    -not (Test-Path (Join-Path $out 'DynamicIsland.Settings.exe')) -or
    -not (Test-Path $stamp)) { 'yes'; exit }

$built = (Get-Item $stamp).LastWriteTime

$newer = Get-ChildItem $root -Recurse -File -Include *.cs, *.xaml, *.csproj, *.manifest |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTime -gt $built } |
    Select-Object -First 1

if ($newer) { 'yes' } else { 'no' }
