<#
.SYNOPSIS
  Removes the installed Folio (program and shortcuts). Your data in %LOCALAPPDATA%\Folio is kept.

.PARAMETER RemoveData
  Also delete %LOCALAPPDATA%\Folio (folio.db and its local backups). Off-PC backup copies are never touched.
#>
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Folio'
$data = Join-Path $env:LOCALAPPDATA 'Folio'
$exe = Join-Path $app 'Folio.exe'

Get-Process -Name Folio -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object {
    $_ | Stop-Process -Force
    $_ | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

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
