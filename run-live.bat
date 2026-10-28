@echo off
rem ApexHighlights 双击启动。
rem 默认行为：托盘常驻 + 找到 Apex 窗口就开始录，命中击倒横幅自动存一段。
rem 关掉这个黑窗口不影响录制（录制在托盘图标里），要退出请右键托盘图标 → 退出。
chcp 65001 >nul
cd /d "%~dp0"

set EXE=src\ApexHighlights\bin\Release\net8.0-windows10.0.19041.0\ApexHighlights.exe
if not exist "%EXE%" (
  echo [!] 还没编译。先跑一次：
  echo     .tools\dotnet\dotnet.exe build -c Release src\ApexHighlights\ApexHighlights.csproj
  pause
  exit /b 1
)

"%EXE%" %*
if errorlevel 1 pause
