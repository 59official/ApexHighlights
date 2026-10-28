# 确保绝对安静：把可能还在循环播放测试音的 powershell 进程找出来并停掉
$procs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='wscript.exe'" |
    Where-Object { $_.CommandLine -like '*playbeep*' -or $_.CommandLine -like '*SoundPlayer*' }
foreach ($p in $procs) {
    Write-Host "停掉 $($p.ProcessId): $($p.CommandLine)"
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}
if (-not $procs) { Write-Host "没有在放测试音的进程" }
