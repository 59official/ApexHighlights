@echo off
chcp 65001 >nul
title ApexHighlights 识别诊断
setlocal
set "DIR=%~dp0captures\diag"
set "SEC=%~1"
if "%SEC%"=="" set "SEC=60"

if not exist "%~dp0ApexHighlights.exe" (
  echo [X] 这里没有 ApexHighlights.exe —— 把这个 bat 放到 exe 旁边再双击
  pause
  exit /b 1
)
if not exist "%DIR%" mkdir "%DIR%" >nul 2>&1

echo ============================================================
echo   识别诊断：接下来 %SEC% 秒
echo   请在游戏里正常打，尽量击倒几次（击倒后等一两秒再退）
echo   这个黑框别关，跑完会自动出报告
echo ============================================================
echo   先切进游戏（或者让它自己找窗口），3 秒后开始。
timeout /t 3 >nul
echo.

"%~dp0ApexHighlights.exe" --log="%DIR%\diag.log" --duration=%SEC%

echo.
echo ---------------- 诊断报告 ----------------
"%~dp0ApexHighlights.exe" --diag-report="%DIR%"

echo.
echo 逐帧数据、最高分条带图都在：
echo   %DIR%
echo 把这份报告和 diag_top 开头的几张 bmp 发给开发者，就能定位为什么不识别。
echo.
pause
