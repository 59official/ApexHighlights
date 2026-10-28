@echo off
rem ApexHighlights 双击启动。
rem 优先用发布版单文件（dist\ApexHighlights.exe，自带运行时、可随便拷走），
rem 没有就用本地编译出来的 exe（需要装 .NET 8 桌面运行时）。
rem
rem 默认行为：托盘常驻 + 找到 Apex 窗口就开始录，命中击倒横幅自动存一段。
rem 游戏没开也没关系——托盘会每 3 秒找一次窗口，游戏起来自动开始录；
rem 游戏退出后又回到等待状态，下次开游戏自动接上。
rem 关掉这个黑窗口不影响录制（录制在托盘图标里），要退出请右键托盘图标 → 退出。
chcp 65001 >nul
cd /d "%~dp0"

set EXE=dist\ApexHighlights.exe
if not exist "%EXE%" set EXE=src\ApexHighlights\bin\Release\net8.0-windows10.0.19041.0\ApexHighlights.exe
if not exist "%EXE%" (
  echo [!] 还没编译。二选一：
  echo     发布版：.tools\dotnet\dotnet.exe publish src\ApexHighlights\ApexHighlights.csproj -c Release -p:PublishProfile=single-file
  echo     调试版：.tools\dotnet\dotnet.exe build -c Release src\ApexHighlights\ApexHighlights.csproj
  pause
  exit /b 1
)

rem --data 指回仓库根：发布版默认把数据放在 exe 旁边（dist\），
rem 但从这里启动时希望沿用仓库里的 captures\ 和 config.json，
rem 不然之前录的片段会“消失”。%~dp0 是相对本文件的位置，没有硬编码盘符。
echo [i] 启动 %EXE%
echo [i] 托盘常驻中。要退出请右键托盘图标 → 退出。
"%EXE%" --data="%~dp0." %*
if errorlevel 1 pause
