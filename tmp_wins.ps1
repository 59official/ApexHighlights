Get-Process chrome, powershell -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -ne '' } |
    Select-Object Id, ProcessName, MainWindowTitle |
    Format-Table -AutoSize
