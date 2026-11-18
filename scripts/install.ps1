<#
.SYNOPSIS
  Builds Folio from this source folder and installs (or updates) it. For developers; everyone else downloads the
  zip from the GitHub Releases page and double-clicks FolioTray.exe.

.DESCRIPTION
  - Builds Release copies of Folio and its tray icon (FolioTray), without console windows, into a temporary folder.
  - On first install, copies this folder's folio.db to %LOCALAPPDATA%\Folio (the original stays as a fallback).
  - Hands the build to "FolioTray --install", the same installer the download uses: the program goes in
    %LOCALAPPDATA%\Programs\Folio, shortcuts on the desktop and Start menu, start at sign-in, served on
    http://localhost:7245. Your data in %LOCALAPPDATA%\Folio is never touched.
  Re-run it after pulling new code to update; your data is kept.

.PARAMETER CopyBackupsTo
  A second folder that gets a copy of every backup. Left out: keeps the current setting (on first install,
  "%OneDrive%\Folio backups" when OneDrive is set up). Pass "" to turn copies off.

.PARAMETER NoStartup
  Don't start Folio automatically when you sign in.
#>
[CmdletBinding()]
param(
    [string]$CopyBackupsTo,
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$data = Join-Path $env:LOCALAPPDATA 'Folio'
$staging = Join-Path ([IO.Path]::GetTempPath()) "folio-install-$([guid]::NewGuid().ToString('N'))"

function Step($text) { Write-Host "-> $text" -ForegroundColor Cyan }

try {
    # 1. Build Folio (no console window) and the tray next to it.
    Step 'Building Folio'
    dotnet publish (Join-Path $repo 'Folio.csproj') --configuration Release --output $staging -p:OutputType=WinExe --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed (Folio).' }
    dotnet publish (Join-Path $repo 'FolioTray\FolioTray.csproj') --configuration Release --output $staging --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed (FolioTray).' }

    # 2. First install: copy this folder's data along (the original stays put).
    $target = Join-Path $data 'folio.db'
    $source = Join-Path $repo 'folio.db'
    if (-not (Test-Path $target) -and (Test-Path $source)) {
        $dev = Get-Process -Name Folio -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $repo 'bin\*') }
        if ($dev) { throw "Folio is running from $repo (a development run). Stop it, then run this script again so its data can be copied safely." }
        Step "Copying your data to $data"
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        foreach ($suffix in '', '-wal', '-shm') {
            if (Test-Path "$source$suffix") { Copy-Item "$source$suffix" "$target$suffix" }
        }
    }

    # 3. Install with the tray's installer (it stops the running copy, replaces the program, makes the shortcuts).
    Step 'Installing'
    $arguments = @('--install', '--yes')
    if ($NoStartup) { $arguments += '--no-startup' }
    if ($PSBoundParameters.ContainsKey('CopyBackupsTo')) { $arguments += @('--copy-backups-to', "`"$CopyBackupsTo`"") }
    # Wait for the installer itself, not -Wait: that also waits for the tray it starts, which keeps running.
    $installer = Start-Process -FilePath (Join-Path $staging 'FolioTray.exe') -ArgumentList $arguments -PassThru
    $installer.WaitForExit()
    if ($installer.ExitCode -ne 0) { throw "The installer stopped (exit code $($installer.ExitCode)); it showed why." }
}
finally {
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host 'Folio is installed: http://localhost:7245' -ForegroundColor Green
Write-Host "  Program: $env:LOCALAPPDATA\Programs\Folio"
Write-Host "  Data:    $data  (logs in $data\logs)"
Write-Host '  Control it from the tray icon by the clock: Open, Restart, Open logs, Quit, Uninstall.'
