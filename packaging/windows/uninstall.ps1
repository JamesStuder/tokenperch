# Remove Token Perch (keeps %APPDATA%\TokenPerch settings).
Get-Process tokenperch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\TokenPerch') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Token Perch.lnk') -Force -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TokenPerch' -ErrorAction SilentlyContinue
Write-Host "Removed. Settings are still in $env:APPDATA\TokenPerch."
