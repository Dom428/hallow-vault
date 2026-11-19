# Hallow Vault uninstaller (run from Windows Settings > Apps, or directly). Your backups are never deleted.
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Vault'
Add-Type -AssemblyName System.Windows.Forms
$keep = [System.Windows.Forms.MessageBox]::Show("Uninstall Hallow Vault?`n`nYour backups stay where they are (they're ordinary zip files).`n`nKeep Hallow Vault's settings in case you reinstall?", 'Uninstall Hallow Vault', 'YesNoCancel', 'Question')
if ($keep -eq 'Cancel') { return }
Get-Process HallowVault -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HallowVault' -ErrorAction SilentlyContinue
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) { Remove-Item (Join-Path $dir 'Hallow Vault.lnk') -ErrorAction SilentlyContinue }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowVault' -Recurse -ErrorAction SilentlyContinue
if ($keep -eq 'No') { Remove-Item (Join-Path $env:APPDATA 'HallowVault') -Recurse -Force -ErrorAction SilentlyContinue }
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show('Hallow Vault is uninstalled. Your backups are still there.', 'Hallow Vault') | Out-Null