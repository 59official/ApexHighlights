@echo off
rem Run one console-mode (non-tray) capture session from the source tree.
rem For everyday use, use the repo-root run-live.bat (tray mode) instead.
rem All paths are relative to %~dp0 so this works from any checkout location.
rem
rem NOTE: keep this file pure ASCII with CRLF endings -- cmd.exe reads
rem batch files as GBK, so UTF-8 Chinese comments get mangled and can
rem break quoting via their trailing bytes.
setlocal
chcp 65001 >nul
set "BIN=%~dp0bin\Release\net8.0-windows10.0.19041.0"
set "LOG=%~dp0live.log"
if not exist "%BIN%\ApexHighlights.exe" goto noexe
cd /d "%BIN%"
echo Apex live capture  (Ctrl+C or close window to stop)
echo log   : %LOG%
echo clips : see the "[2d]" line in the startup output
echo -------------------------------------------------------
ApexHighlights.exe --log="%LOG%" --fps=60 --bitrate=20 --title="Apex Legends" --no-tray
echo.
echo ==== stopped ====
pause
exit /b 0

:noexe
echo [X] exe not found under %BIN%
echo     run: .tools\dotnet\dotnet.exe build -c Release src\ApexHighlights\ApexHighlights.csproj
pause
exit /b 1
