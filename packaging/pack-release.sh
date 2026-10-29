#!/usr/bin/env bash
# 一键出两个发布产物：
#   release/ApexHighlights-<版本>-setup.exe     安装包（Inno Setup，主推）
#   release/ApexHighlights-<版本>-portable.zip  解压即用（U 盘 / 不想装东西的人）
#
# 用法：
#   packaging/pack-release.sh                 # 版本号从 csproj 的 <Version> 读
#   packaging/pack-release.sh 0.7.1           # 临时指定版本
#   REFRESH_ICON=1 packaging/pack-release.sh  # 顺带从 AppIcon 重画 ico
#
# 前置：.tools/dotnet（仓库自带的 SDK）、.tools/innosetup/ISCC.exe。
# 这两个都在 .gitignore 里，换机器第一次跑要先装（见 docs/HANDOFF-2026-10-29.md）。
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"

VERSION=${1:-}
if [ -z "$VERSION" ]; then
  VERSION=$(grep -oE '<Version>[^<]+' src/ApexHighlights/ApexHighlights.csproj | head -1 | cut -d'>' -f2)
fi
if [ -z "$VERSION" ]; then
  echo "读不到版本号，直接给一个：packaging/pack-release.sh 0.7.0" >&2
  exit 1
fi

DOTNET="$ROOT/.tools/dotnet"
ISCC="$ROOT/.tools/innosetup/ISCC.exe"
[ -x "$DOTNET/dotnet.exe" ] || [ -x "$DOTNET/dotnet" ] || { echo "缺 .tools/dotnet（自带 SDK）" >&2; exit 1; }
[ -f "$ISCC" ] || { echo "缺 .tools/innosetup/ISCC.exe（Inno Setup 6）" >&2; exit 1; }

export DOTNET_ROOT="$DOTNET"
export PATH="$DOTNET:$PATH"
export DOTNET_CLI_UI_LANGUAGE=en
export DOTNET_NOLOGO=1

echo "==> 版本 $VERSION"
mkdir -p release
rm -rf dist release/ApexHighlights-*.exe release/ApexHighlights-*.zip

if [ "${REFRESH_ICON:-0}" = "1" ]; then
  echo "==> 重画图标（先临时 publish 一份，图标是 exe 自己渲染的）"
  # AppIcon.Render 在程序里，所以先有个能跑的 exe 才能生成 ico
  dotnet publish src/ApexHighlights/ApexHighlights.csproj -c Release -r win-x64 \
    --self-contained false -p:DebugType=none -o .tools/icon-tmp -v q
  ./.tools/icon-tmp/ApexHighlights.exe --make-icon=packaging/ApexHighlights.ico
  rm -rf .tools/icon-tmp
fi

echo "==> publish dist（self-contained，用户不需要装 .NET）"
dotnet publish src/ApexHighlights/ApexHighlights.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o dist -v q

echo "==> 生成身份包（sparse MSIX，用来关掉录制黄框）"
# 失败不算致命：没有它安装包照样能装，只是黄框关不掉（IsBorderRequired 那条路走不通）
if powershell -NoProfile -ExecutionPolicy Bypass -File packaging/make-identity.ps1 -Version "$VERSION"; then
  cp packaging/identity/out/ApexHighlights.Identity.msix dist/
  cp packaging/identity/out/ApexHighlights.cer        dist/
else
  echo "   （身份包没做出来，这一版没有无边框录制；其余功能不受影响）" >&2
fi
cp packaging/identity/register-identity.ps1 dist/
# 识别诊断脚本：用户「怎么又没录上」时的第一手工具，跟着便携版一起发

echo "==> 编译安装包"
# MSYS_NO_PATHCONV：Git Bash 会把 /DAppVersion=… 当成路径改写成 D:\…，ISCC 就不认了
MSYS_NO_PATHCONV=1 "$ISCC" /Q "/DAppVersion=$VERSION" packaging/ApexHighlights.iss

echo "==> 打便携 zip"
powershell -NoProfile -Command "\$ErrorActionPreference='Stop'; Compress-Archive -Path 'dist\*' -DestinationPath ('release\ApexHighlights-$VERSION-portable.zip') -CompressionLevel Optimal -Force"

echo
echo "==> 产物"
powershell -NoProfile -Command "Get-ChildItem 'release' | Sort-Object Name | ForEach-Object { '{0,-42} {1,7:N1} MB  {2}' -f \$_.Name, (\$_.Length/1MB), (Get-FileHash \$_.FullName -Algorithm SHA256).Hash.Substring(0,16) }"
