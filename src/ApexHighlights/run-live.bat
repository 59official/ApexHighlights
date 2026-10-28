@echo off
setlocal
set "BIN=D:\played\src\ApexHighlights\bin\Release\net8.0-windows10.0.19041.0"
set "LOG=D:\played\src\ApexHighlights\live.log"
if not exist "%BIN%\ApexHighlights.exe" goto noexe
cd /d "%BIN%"
echo Apex live capture  (Ctrl+C or close window to stop)
echo log   : %LOG%
echo clips : D:\played\captures\clips
echo -------------------------------------------------------
ApexHighlights.exe --log="%LOG%" --fps=60 --bitrate=20 --title="Apex Legends"
echo.
echo ==== stopped ====
pause
exit /b 0

:noexe
echo [X] exe not found under %BIN%
echo     run: dotnet build -c Release
pause
exit /b 1
