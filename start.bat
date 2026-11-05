@echo off
chcp 65001 >nul

rem pushd, а не cd: работает и для сетевых дисков (UNC)
pushd "%~dp0"
set "EXE=bin\Release\net8.0-windows10.0.19041.0\DynamicIsland.exe"

rem Закрываем уже запущенный остров, чтобы стартовала свежая версия
taskkill /im DynamicIsland.exe /f >nul 2>&1
taskkill /im DynamicIsland.Settings.exe /f >nul 2>&1
rem помощник трафика закрывается сам после острова — даём ему пару секунд
ping -n 3 127.0.0.1 >nul

rem Пересобираем, только если код менялся (или запуск: start.bat rebuild)
set "NEED=yes"
if /i not "%~1"=="rebuild" (
    for /f %%i in ('powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\needs-build.ps1"') do set "NEED=%%i"
)

if "%NEED%"=="yes" (
    where dotnet >nul 2>&1 || (
        echo [!] Не найден .NET SDK. Поставь .NET 8 SDK: https://dotnet.microsoft.com/download
        pause
        exit /b 1
    )
    echo Собираю Dynamic Island...
    dotnet build DynamicIsland.csproj -c Release -nologo -v q -clp:ErrorsOnly
    if errorlevel 1 (
        echo.
        echo [!] Сборка острова упала, ошибки выше.
        pause
        exit /b 1
    )
    echo Собираю настройки...
    dotnet build Settings\DynamicIsland.Settings.csproj -c Release -nologo -v q -clp:ErrorsOnly
    if errorlevel 1 (
        echo.
        echo [!] Сборка настроек упала, ошибки выше.
        pause
        exit /b 1
    )
    echo Собираю помощника трафика...
    dotnet build NetHelper\DynamicIsland.NetHelper.csproj -c Release -nologo -v q -clp:ErrorsOnly
    if errorlevel 1 (
        echo.
        echo [!] Сборка помощника упала, ошибки выше.
        pause
        exit /b 1
    )
    rem отметка «собрано» — чтобы в следующий раз не пересобирать без изменений
    echo.> "bin\build.stamp"
)

start "" "%EXE%"
popd
