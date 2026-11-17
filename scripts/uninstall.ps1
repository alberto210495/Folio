<#
.SYNOPSIS
  Removes the installed Folio (program, tray icon and shortcuts). Your data in %LOCALAPPDATA%\Folio is kept.

.PARAMETER RemoveData
  Also delete %LOCALAPPDATA%\Folio (folio.db and its local backups). Off-PC backup copies are never touched.
#>
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Folio'
$data = Join-Path $env:LOCALAPPDATA 'Folio'
$exe = Join-Path $app 'Folio.exe'
$tray = Join-Path $app 'FolioTray.exe'

# Ask the tray to shut Folio down cleanly, then make sure (matched by path only).
$installed = { Get-Process -Name Folio, FolioTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -in $exe, $tray } }
if (Test-Path $tray) { Start-Process -FilePath $tray -ArgumentList '--quit' -Wait }
& $installed | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
& $installed | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

foreach ($link in (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Folio.lnk'),
                  (Join-Path ([Environment]::GetFolderPath('Startup')) 'Folio.lnk')) {
    if (Test-Path $link) { Remove-Item $link }
}
if (Test-Path $app) { Remove-Item -Recurse -Force $app }

if ($RemoveData) {
    $answer = Read-Host "Delete $data (all portfolios, trades and local backups)? Type DELETE to confirm"
    if ($answer -ceq 'DELETE') { Remove-Item -Recurse -Force $data; Write-Host 'Data deleted.' }
    else { Write-Host 'Data kept.' }
} else {
    Write-Host "Folio removed. Your data is still in $data."
}
