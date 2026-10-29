# 把「身份包」注册到系统里，让 ApexHighlights.exe 获得包身份。
# 有了包身份才能声明 graphicsCaptureWithoutBorder 能力，从而把 Windows 11 的录制黄框关掉。
#
# 必须**管理员**运行：包部署服务（AppXSVC）只认机器级的证书信任，
# 把自签证书放进「当前用户 → 受信任的人」是不够的（会报 0x800B0109 / CERT_E_UNTRUSTEDROOT）。
# 安装器用 `Verb: runas` 拉起来时会弹一次 UAC，这是唯一需要提权的步骤。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File register-identity.ps1 -AppDir "C:\...\ApexHighlights"
#   powershell -ExecutionPolicy Bypass -File register-identity.ps1 -AppDir "..." -Unregister
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'
$name = 'ApexHighlights'

function Note($msg) { Write-Host "[identity] $msg" }

if ($Unregister) {
    try {
        Get-AppxPackage $name | ForEach-Object { Remove-AppxPackage $_.PackageFullName }
        Note '身份包已注销（黄框会重新出现，这是系统的提示，不影响录制内容）'
    } catch { Note "注销失败（不影响卸载）: $($_.Exception.Message)" }
    exit 0
}

$msix = Join-Path $AppDir 'ApexHighlights.Identity.msix'
$cer  = Join-Path $AppDir 'ApexHighlights.cer'
$exe  = Join-Path $AppDir 'ApexHighlights.exe'
foreach ($f in @($msix, $cer, $exe)) {
    if (-not (Test-Path $f)) { Note "缺文件 $f，跳过（黄框保留，其它功能不受影响）"; exit 0 }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Note '没有管理员权限，跳过身份包注册（黄框保留，录制本身不受影响）'; exit 0 }

# 1. 证书进机器级「受信任的人」——包部署只认这个
try {
    $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
    $store.Open('ReadWrite')
    if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $c.Thumbprint })) {
        $store.Add($c)
        Note "证书已装进「受信任的人」（$($c.Thumbprint.Substring(0,8))…）"
    } else { Note '证书已在受信任列表里' }
    $store.Close()
} catch { Note "装证书失败（继续试注册）: $($_.Exception.Message)" }

# 2. 注册（同版本重复注册会 0x80073CF9，先删再装）
try {
    Get-AppxPackage $name | ForEach-Object { Remove-AppxPackage $_.PackageFullName }
} catch { }

try {
    Add-AppxPackage -Path $msix -ExternalLocation $AppDir
    Note '身份包注册成功：第一次开始录制时会弹一次「允许无边框采集」，同意后黄框就没了'
    exit 0
} catch {
    Note "注册失败（黄框保留，录制不受影响）: $($_.Exception.Message)"
    exit 1
}
