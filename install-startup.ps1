$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $root "bin\CosmoAIAssistant.exe"
$startupDir = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startupDir "Cosmo.lnk"

$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($shortcutPath)
$Shortcut.TargetPath = $exePath
$Shortcut.WorkingDirectory = Join-Path $root "bin"
$Shortcut.Description = "Cosmo voice assistant"
$Shortcut.Save()

Write-Host "Startup shortcut created at $shortcutPath"
