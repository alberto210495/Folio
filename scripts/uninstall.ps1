<#
.SYNOPSIS
  Removes the installed Folio (program, tray icon and shortcuts). Your data in %LOCALAPPDATA%\Folio is kept.
  The same as "Uninstall Folio…" on the tray icon's menu.

.PARAMETER RemoveData
  Also delete %LOCALAPPDATA%\Folio (folio.db and its local backups). Off-PC backup copies are never touched.
#>
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Folio'
$data = Join-Path $env:LOCALAPPDATA 'Folio'
$tray = Join-Path $app 'FolioTray.exe'

if (Test-Path $tray) {
    # Stops Folio cleanly, removes the shortcuts, and deletes the program folder a moment after it exits.
    # Wait for the uninstaller itself, not -Wait: that would also wait for the command it leaves to delete the folder.
    $uninstall = Start-Process -FilePath $tray -ArgumentList '--uninstall', '--yes' -PassThru
    $uninstall.WaitForExit()
    if ($uninstall.ExitCode -ne 0) { throw "Uninstall stopped (exit code $($uninstall.ExitCode))." }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Test-Path $app) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} else {
    Write-Host 'Folio isn''t installed.'
}

if ($RemoveData) {
    $answer = Read-Host "Delete $data (all portfolios, trades and local backups)? Type DELETE to confirm"
    if ($answer -ceq 'DELETE') { Remove-Item -Recurse -Force $data; Write-Host 'Data deleted.' }
    else { Write-Host 'Data kept.' }
} else {
    Write-Host "Folio removed. Your data is still in $data."
}
