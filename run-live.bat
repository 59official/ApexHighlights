@echo off
rem ============================================================
rem  ApexHighlights launcher  (double-click to run)
rem
rem  Data (clips, segments, config, games) always lives next to
rem  this file -- that is what --data=%~dp0 pins down, so clips
rem  made by the published build land in the same captures\clips
rem  as clips made by a debug build.
rem
rem  What it does: sits in the tray, waits for the Apex window,
rem  records, saves a clip + beeps when a knock banner is found.
rem  If the game is not running it retries every 3 seconds, so
rem  there is no need to restart this launcher.
rem
rem  Closing the black console window does NOT stop recording --
rem  recording lives in the tray icon. To quit use the tray menu.
rem
rem  NOTE: keep this file pure ASCII with CRLF endings. cmd.exe
rem  reads batch files as GBK; UTF-8 Chinese comments get mangled
rem  and their trailing bytes (which can be \ or ") break quoting,
rem  and LF-only endings break multi-line if() blocks.
rem ============================================================
chcp 65001 >nul
cd /d "%~dp0"

rem Prefer the published self-contained folder build, then the
rem plain build output (needs the .NET 8 desktop runtime).
set "EXE=dist\ApexHighlights.exe"
if not exist "%EXE%" set "EXE=src\ApexHighlights\bin\Release\net8.0-windows10.0.19041.0\ApexHighlights.exe"
if exist "%EXE%" goto :run

echo [!] Not built yet. Pick one:
echo     publish: .tools\dotnet\dotnet.exe publish src\ApexHighlights\ApexHighlights.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o dist
echo     build  : .tools\dotnet\dotnet.exe build -c Release src\ApexHighlights\ApexHighlights.csproj
pause
exit /b 1

:run
echo [i] Launching %EXE%
echo [i] Data dir: %~dp0
echo [i] Log file: %~dp0live.log
echo [i] Stays in the tray. To quit: right-click the tray icon -^> Quit.
"%EXE%" --data="%~dp0." --log="%~dp0live.log" %*
if errorlevel 1 pause
