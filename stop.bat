@echo off
taskkill /im DynamicIsland.exe /f >nul 2>&1
taskkill /im DynamicIsland.Settings.exe /f >nul 2>&1
rem помощник трафика закроется сам через пару секунд
