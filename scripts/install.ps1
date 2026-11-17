<#
.SYNOPSIS
  Installs (or updates) Folio as a background app with a tray icon: no terminal, desktop shortcut, starts when you sign in.

.DESCRIPTION
  - Publishes Release builds of Folio and its tray icon (FolioTray) to %LOCALAPPDATA%\Programs\Folio, without console windows.
  - Keeps data in %LOCALAPPDATA%\Folio (folio.db, backups, logs), separate from the program, so updates never touch it.
  - On first install, copies this folder's folio.db there (the original stays as a fallback).
  - Desktop shortcut "Folio" starts the tray (which starts Folio) and opens https://localhost:7245.
  - A Startup shortcut starts it quietly when you sign in to Windows.
  - The tray icon (by the clock) has Open, Restart, Open logs/data folder and Quit, and restarts Folio if it crashes.
  Re-run it after pulling new code to update; your data is kept.

.PARAMETER CopyBackupsTo
  A second folder that gets a copy of every backup (default: "%OneDrive%\Folio backups" when OneDrive is set up).
  Pass "" to turn copies off.

.PARAMETER NoStartup
  Don't start Folio automatically when you sign in.
#>
[CmdletBinding()]
param(
    [string]$CopyBackupsTo = $(if ($env:OneDrive) { '%OneDrive%\Folio backups' } else { '' }),
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $env:LOCALAPPDATA 'Programs\Folio'
$data = Join-Path $env:LOCALAPPDATA 'Folio'
$exe = Join-Path $app 'Folio.exe'
$tray = Join-Path $app 'FolioTray.exe'

function Step($text) { Write-Host "-> $text" -ForegroundColor Cyan }

# 1. Stop the installed copy: ask the tray to shut Folio down cleanly, then make sure (matched by path only).
$installed = { Get-Process -Name Folio, FolioTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -in $exe, $tray } }
if (& $installed) {
    Step 'Stopping the running Folio'
    if (Test-Path $tray) { Start-Process -FilePath $tray -ArgumentList '--quit' -Wait }
    & $installed | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
    & $installed | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 2. HTTPS needs the trusted localhost certificate.
dotnet dev-certs https --check --trust *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'The localhost HTTPS certificate is not trusted yet. Run: dotnet dev-certs https --trust'
}

# 3. Publish Folio (built without a console window) and the tray next to it.
Step "Publishing to $app"
dotnet publish (Join-Path $repo 'Folio.csproj') --configuration Release --output $app -p:OutputType=WinExe --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed (Folio).' }
dotnet publish (Join-Path $repo 'FolioTray\FolioTray.csproj') --configuration Release --output $app --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed (FolioTray).' }

# 4. Settings for the installed copy (Production). The API key still comes from your user secrets.
#    The log file (in the data folder) gets Folio's own messages plus warnings from everything else.
$settings = [ordered]@{
    Logging = @{ LogLevel = [ordered]@{
        Default                      = 'Information'
        'Microsoft.AspNetCore'       = 'Warning'
        'Microsoft.EntityFrameworkCore' = 'Warning'
        'System.Net.Http.HttpClient' = 'Warning'
    } }
    Folio   = [ordered]@{ DataDirectory = '%LOCALAPPDATA%\Folio'; CopyBackupsTo = $CopyBackupsTo }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $app 'appsettings.Production.json') -Encoding utf8

# 5. First install: copy this folder's data along (the original stays put).
New-Item -ItemType Directory -Force -Path $data | Out-Null
$target = Join-Path $data 'folio.db'
$source = Join-Path $repo 'folio.db'
if (-not (Test-Path $target) -and (Test-Path $source)) {
    $dev = Get-Process -Name Folio -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $repo 'bin\*') }
    if ($dev) { throw "Folio is running from $repo (a development run). Stop it, then run this script again so its data can be copied safely." }
    Step "Copying your data to $data"
    foreach ($suffix in '', '-wal', '-shm') {
        if (Test-Path "$source$suffix") { Copy-Item "$source$suffix" "$target$suffix" }
    }
}

# 6. Shortcuts start the tray, which starts Folio.
$shell = New-Object -ComObject WScript.Shell
function Shortcut($path, $arguments, $description) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $tray
    $s.Arguments = $arguments
    $s.WorkingDirectory = $app
    $s.Description = $description
    $s.Save()
}
$desktop = [Environment]::GetFolderPath('Desktop')
Step 'Creating the desktop shortcut'
Shortcut (Join-Path $desktop 'Folio.lnk') '--open' 'Open Folio (starts it if needed)'

$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Folio.lnk'
if ($NoStartup) {
    if (Test-Path $startup) { Remove-Item $startup }
} else {
    Step 'Starting Folio when you sign in'
    Shortcut $startup '' 'Start Folio in the background (tray icon)'
}

# 7. Start it and open the browser.
Step 'Starting Folio'
Start-Process -FilePath $tray -ArgumentList '--open' -WorkingDirectory $app

Write-Host ''
Write-Host 'Folio is installed: https://localhost:7245' -ForegroundColor Green
Write-Host "  Program: $app"
Write-Host "  Data:    $data  (logs in $data\logs)"
if ($CopyBackupsTo) { Write-Host "  Backup copies: $([Environment]::ExpandEnvironmentVariables($CopyBackupsTo))" }
Write-Host '  Control it from the tray icon by the clock: Open, Restart, Open logs, Quit.'
