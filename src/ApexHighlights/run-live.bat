@echo off
rem 从源码树启动一次「打完就退」的录制（根目录的 run-live.bat 是托盘常驻形态，日常用那个）。
rem 相对路径全部基于 %~dp0，换目录解压也能跑。
setlocal
chcp 65001 >nul
set "BIN=%~dp0bin\Release\net8.0-windows10.0.19041.0"
set "LOG=%~dp0live.log"
if not exist "%BIN%\ApexHighlights.exe" goto noexe
cd /d "%BIN%"
echo Apex live capture  (Ctrl+C or close window to stop)
echo log   : %LOG%
echo clips : 见启动输出里「[2d] 库 ...」那一行
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
