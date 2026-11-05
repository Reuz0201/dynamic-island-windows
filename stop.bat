@echo off
rem Остров запущен от администратора — и закрывать его нужно с правами
fltmc >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath cmd.exe -ArgumentList '/c taskkill /im DynamicIsland.exe /f' -Verb RunAs -WindowStyle Hidden"
    exit /b
)
taskkill /im DynamicIsland.exe /f >nul 2>&1
