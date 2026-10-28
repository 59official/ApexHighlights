@echo off
chcp 65001 >nul
cd /d "%~dp0bin\Release\net8.0-windows10.0.19041.0"
set "WIN=%~1"
if "%WIN%"=="" set "WIN=Apex Legends"
set "SEC=%~2"
if "%SEC%"=="" set "SEC=1200"
echo ==========================================================
echo  window   : %WIN%
echo  duration : %SEC% seconds
echo  output   : D:\played\probe\s_(timestamp)
echo ==========================================================
echo  progress prints every 5s. Close window or Ctrl+C to stop.
echo.
ApexProbe.exe "%WIN%" %SEC%
echo.
echo ==== done, press any key ====
pause >nul
