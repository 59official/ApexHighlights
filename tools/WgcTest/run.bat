@echo off
chcp 65001 >nul
cd /d "%~dp0bin\Release\net8.0-windows10.0.19041.0"
set "ARGS=%~1"
if "%ARGS%"=="" set "ARGS=Apex Legends"
echo Looking for window: %ARGS%
echo.
WgcTest.exe %ARGS%
echo.
echo ==== done, press any key ====
pause >nul
