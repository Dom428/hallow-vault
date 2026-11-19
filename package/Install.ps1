# Hallow Vault installer: copies the app to your user folder (no admin needed) and adds Start menu + Desktop shortcuts.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Vault'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process HallowVault -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Copy-Item (Join-Path $PSScriptRoot 'HallowVault.exe') $dest -Force
Copy-Item (Join-Path $PSScriptRoot 'README.txt') $dest -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') $dest -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'FontAwesome-LICENSE.txt') $dest -Force -ErrorAction SilentlyContinue
$exe = Join-Path $dest 'HallowVault.exe'
# Windows Settings > Apps entry (per user, no admin)
$u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowVault'
New-Item $u -Force | Out-Null
$ver = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0\.0$', ''
$vals = @{ DisplayName = 'Hallow Vault'; DisplayVersion = $ver; Publisher = 'Hallow'; DisplayIcon = $exe; InstallLocation = $dest
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dest 'Uninstall.ps1')`""
    URLInfoAbout = 'https://github.com/Dom428/hallow-vault' }
foreach ($k in $vals.Keys) { Set-ItemProperty $u -Name $k -Value $vals[$k] }
Set-ItemProperty $u -Name NoModify -Value 1 -Type DWord; Set-ItemProperty $u -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $u -Name EstimatedSize -Value ([int]((Get-Item $exe).Length / 1KB)) -Type DWord
$w = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $s = $w.CreateShortcut((Join-Path $dir 'Hallow Vault.lnk'))
    $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'Backs up your game saves and settings'; $s.Save()
}
Start-Process $exe
Write-Host "Hallow Vault is installed and open. It's in your Start menu and on your Desktop."
