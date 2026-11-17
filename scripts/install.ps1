<#
.SYNOPSIS
  Installs (or updates) Folio as a background app: no terminal, desktop shortcut, starts when you sign in.

.DESCRIPTION
  - Publishes a Release build to %LOCALAPPDATA%\Programs\Folio, built without a console window.
  - Keeps data in %LOCALAPPDATA%\Folio (folio.db and backups), separate from the program, so updates never touch it.
  - On first install, copies this folder's folio.db there (the original stays as a fallback).
  - Desktop shortcut "Folio" starts Folio if needed and opens https://localhost:7245.
  - A Startup shortcut starts Folio quietly when you sign in to Windows.
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

function Step($text) { Write-Host "-> $text" -ForegroundColor Cyan }

# 1. Stop the installed copy (only that one: matched by its path).
$running = Get-Process -Name Folio -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Step 'Stopping the running Folio'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

# 2. HTTPS needs the trusted localhost certificate.
dotnet dev-certs https --check --trust *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'The localhost HTTPS certificate is not trusted yet. Run: dotnet dev-certs https --trust'
}

# 3. Publish.
Step "Publishing to $app"
dotnet publish (Join-Path $repo 'Folio.csproj') --configuration Release --output $app -p:OutputType=WinExe --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# 4. Settings for the installed copy (Production). The API key still comes from your user secrets.
$settings = [ordered]@{
    Logging = @{ LogLevel = @{ Default = 'Warning' } }
    Folio   = [ordered]@{ DataDirectory = '%LOCALAPPDATA%\Folio'; CopyBackupsTo = $CopyBackupsTo }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $app 'appsettings.Production.json') -Encoding utf8

# 5. First install: copy this folder's data along (the original stays put).
New-Item -ItemType Directory -Force -Path $data | Out-Null
$target = Join-Path $data 'folio.db'
$source = Join-Path $repo 'folio.db'
if (-not (Test-Path $target) -and (Test-Path $source)) {
    $dev = Get-Process -Name Folio -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $repo 'bin\*') }
    if ($dev) { throw "Folio is running from $repo (a development run). Stop it, then run this script again so its data can be moved safely." }
    Step "Copying your data to $data"
    foreach ($suffix in '', '-wal', '-shm') {
        if (Test-Path "$source$suffix") { Copy-Item "$source$suffix" "$target$suffix" }
    }
}

# 6. Shortcuts. --contentRoot makes Folio find its files whatever folder it's started from.
$shell = New-Object -ComObject WScript.Shell
function Shortcut($path, $arguments, $description) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe
    $s.Arguments = $arguments
    $s.WorkingDirectory = $app
    $s.Description = $description
    $s.Save()
}
$desktop = [Environment]::GetFolderPath('Desktop')
Step 'Creating the desktop shortcut'
Shortcut (Join-Path $desktop 'Folio.lnk') "--contentRoot `"$app`" --open" 'Open Folio (starts it if needed)'

$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Folio.lnk'
if ($NoStartup) {
    if (Test-Path $startup) { Remove-Item $startup }
} else {
    Step 'Starting Folio when you sign in'
    Shortcut $startup "--contentRoot `"$app`"" 'Start Folio in the background'
}

# 7. Start it and open the browser.
Step 'Starting Folio'
Start-Process -FilePath $exe -ArgumentList '--contentRoot', "`"$app`"", '--open' -WorkingDirectory $app

Write-Host ''
Write-Host 'Folio is installed: https://localhost:7245' -ForegroundColor Green
Write-Host "  Program: $app"
Write-Host "  Data:    $data"
if ($CopyBackupsTo) { Write-Host "  Backup copies: $([Environment]::ExpandEnvironmentVariables($CopyBackupsTo))" }
Write-Host '  Stop it: Task Manager > Folio > End task   (or: Stop-Process -Name Folio)'
