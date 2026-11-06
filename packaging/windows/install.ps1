# Install Token Perch for the current user (no admin needed).
#   .\install.ps1                 from an unpacked dist\win-x64 folder, or from a checkout (builds first)
#   .\install.ps1 -NoAutostart    don't start it at sign-in
param([switch]$NoAutostart)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (Test-Path (Join-Path $here 'tokenperch.exe')) {
    $src = $here
} else {
    $root = Resolve-Path (Join-Path $here '..\..')
    $rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    dotnet publish "$root\src\TokenPerch" -c Release -r $rid --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$root\dist\$rid" --nologo -v quiet
    if ($LASTEXITCODE) { throw 'build failed' }
    $src = "$root\dist\$rid"
}

$dest = Join-Path $env:LOCALAPPDATA 'Programs\TokenPerch'
Get-Process tokenperch -ErrorAction SilentlyContinue | Stop-Process -Force

# Token Perch was called Claude Tray before 3.0: remove that install (its settings are copied on first start).
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Get-Process claude-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty $run -Name 'ClaudeTray' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Tray.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\ClaudeTray') -Recurse -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $src 'tokenperch.exe') $dest -Force
$exe = Join-Path $dest 'tokenperch.exe'

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Token Perch.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.Description = 'Coding agent sessions, models and usage in the system tray'
$lnk.Save()

if ($NoAutostart) { Remove-ItemProperty $run -Name 'TokenPerch' -ErrorAction SilentlyContinue }
else { Set-ItemProperty $run -Name 'TokenPerch' -Value "`"$exe`"" }

Start-Process $exe
Write-Host "Installed to $dest and started. Settings: $env:APPDATA\TokenPerch\config.json"
