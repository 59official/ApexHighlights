# 生成「身份包」（sparse MSIX）：给 ApexHighlights.exe 一个包身份，
# 从而能声明 graphicsCaptureWithoutBorder 能力，把 Windows 11 的录制黄框关掉。
#
# 产物（都在 packaging/identity/out/）：
#   ApexHighlights.Identity.msix   安装器随包分发并注册
#   ApexHighlights.cer             公钥证书，安装器导进「当前用户 → 受信任的人」
#
# 证书（private key）落在 packaging/.secrets/，不进 git —— 它只是自签的开发证书，
# 换一台机器重新生成就行；只要 Subject 还是 CN=ApexHighlights，
# 清单里的 Publisher 就不用改。
param(
    [string]$Version = '0.7.0',
    [switch]$SkipIfExists
)
$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$idDir   = Join-Path $here 'identity'
$outDir  = Join-Path $idDir 'out'
$stage   = Join-Path $idDir 'stage'
$secrets = Join-Path $here '.secrets'
$pfx     = Join-Path $secrets 'ApexHighlights.pfx'
$cer     = Join-Path $secrets 'ApexHighlights.cer'
$msix    = Join-Path $outDir 'ApexHighlights.Identity.msix'
$subject = 'CN=ApexHighlights'
$pwdPlain = 'ApexHighlights'

if ($SkipIfExists -and (Test-Path $msix)) {
    Write-Host "[identity] 已有 $msix，跳过（-SkipIfExists）"
    exit 0
}

# makeappx / signtool 来自 Microsoft.Windows.SDK.BuildTools（见 .tools/sdkbt）
$tools = Get-ChildItem -Path (Join-Path $here '..\.tools\sdkbt') -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -like '*\x64\*' } | Select-Object -First 1
if (-not $tools) { throw "找不到 makeappx.exe，先下载 Microsoft.Windows.SDK.BuildTools 到 .tools/sdkbt（见 docs/HANDOFF-2026-10-29.md）" }
$binDir = $tools.DirectoryName

# 版本必须是四段数字，MSIX 不接受 0.7.0
$v = if ($Version -match '^\d+\.\d+\.\d+\.\d+$') { $Version } else { "$Version.0" }

New-Item -ItemType Directory -Force -Path $outDir, $stage, $secrets | Out-Null

# ── 1. 证书 ────────────────────────────────────────────────────────────────
if (-not (Test-Path $pfx)) {
    Write-Host "[identity] 生成自签证书 $subject …"
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
        -KeyUsage DigitalSignature -FriendlyName 'ApexHighlights 安装包签名（自签）' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $sec = ConvertTo-SecureString -String $pwdPlain -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $sec | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "[identity] 证书已写入 $pfx"
} else {
    Write-Host "[identity] 复用已有证书 $pfx"
    if (-not (Test-Path $cer)) {
        $sec = ConvertTo-SecureString -String $pwdPlain -Force -AsPlainText
        $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $pwdPlain)
        Export-Certificate -Cert $c -FilePath $cer | Out-Null
    }
}

# 证书的 Subject 必须和清单里的 Publisher 一致，否则注册时看身份对不上
# 不能用 Get-PfxCertificate：它要交互式输密码，安装/CI 里全是非交互
$pfxCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $pwdPlain)
if ($pfxCert.Subject -ne $subject) { throw "证书 Subject 是 $($pfxCert.Subject)，清单里写的是 $subject，两者必须一致" }

# ── 2. 摆好包里的东西 ──────────────────────────────────────────────────────
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets') | Out-Null

$exe = Join-Path $here '..\src\ApexHighlights\bin\Release\net8.0-windows10.0.22621.0\ApexHighlights.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，先 dotnet build -c Release" }
& $exe "--make-assets=$stage\Assets" | Out-Null
if (-not (Test-Path (Join-Path $stage 'Assets\Square150x150Logo.png'))) { throw '商店资产没生成出来' }

# 清单必须是无 BOM 的 UTF-8：带 BOM 时 makeappx 会抱怨 XML 声明不在第一格
$mf = (Get-Content (Join-Path $idDir 'AppxManifest.xml') -Raw -Encoding UTF8).Replace('__VERSION__', $v)
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $mf, (New-Object System.Text.UTF8Encoding $false))

# ── 3. 打包 + 签名 ─────────────────────────────────────────────────────────
if (Test-Path $msix) { Remove-Item $msix -Force }
& (Join-Path $binDir 'makeappx.exe') pack /o /nv /d $stage /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx 失败（$LASTEXITCODE）" }

& (Join-Path $binDir 'signtool.exe') sign /fd SHA256 /f $pfx /p $pwdPlain $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signtool 失败（$LASTEXITCODE）" }

Copy-Item $cer (Join-Path $outDir 'ApexHighlights.cer') -Force
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

$mi = Get-Item $msix
Write-Host "[identity] 完成：$($mi.FullName)  $($mi.Length) 字节  v$v  证书 $($pfxCert.Thumbprint.Substring(0,8))…"
