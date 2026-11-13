<#
  PingSight - network monitor: continuous traceroute, service checks, DNS check,
  speed test, outage log and alerts, in a browser dashboard.

  Usage:   .\PingSight.ps1 [-Port 8787] [-NoBrowser] [-DataDir C:\temp\PingSight]
  Stop:    Ctrl+C (or close the window)

  Nothing is installed and no admin rights are needed. The dashboard is served
  on http://localhost only and is not reachable from other machines.
#>
param(
    [int]$Port = 8787,
    [switch]$NoBrowser,
    # Where results are saved (CSV files). Use -DataDir none to save nothing.
    [string]$DataDir = 'C:\temp\PingSight',
    # How many seconds each saved row summarises.
    [int]$LogSeconds = 60,
    # Where the host list and settings are kept. Default: next to this script.
    [string]$ConfigDir = ''
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ConfigDir) { $ConfigDir = $here }
if ($DataDir -eq 'none') { $DataDir = '' }

# Only one PingSight per port: a second copy would double the probes and the saved rows.
try {
    $running = Invoke-RestMethod -Uri "http://localhost:$Port/api/state" -TimeoutSec 2
    if ($null -ne $running.targets) {
        Write-Host "PingSight is already running on http://localhost:$Port/ - opening it." -ForegroundColor Yellow
        if (-not $NoBrowser) { Start-Process "http://localhost:$Port/" }
        return
    }
} catch { }

if (-not ('PingSight.Server' -as [type])) {
    $sources = (Join-Path $here 'engine.cs'), (Join-Path $here 'checks.cs')
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -Path $sources
    } else {
        Add-Type -Path $sources -ReferencedAssemblies 'System.dll', 'System.Core.dll'
    }
}

$started = $false
foreach ($p in $Port..($Port + 9)) {
    try {
        [PingSight.Server]::Start($p, $here)
        $Port = $p
        $started = $true
        break
    } catch {
        Write-Host "Port $p is not available, trying the next one..." -ForegroundColor DarkYellow
    }
}
if (-not $started) { throw "Could not open a local port between $Port and $($Port + 9)." }
$url = "http://localhost:$Port/"

# Results folder first, so each host's graphs are rebuilt from its saved files as it is loaded.
if ($DataDir) { [PingSight.Engine]::StartLog($DataDir, $LogSeconds) }
[PingSight.Engine]::Load((Join-Path $ConfigDir 'targets.txt'))
[PingSight.Checks]::Start((Join-Path $ConfigDir 'settings.txt'))

# Notification icon for alerts. PingSight works without it.
$tray = $false
try {
    if (-not ('PingSightTray.Tray' -as [type])) {
        Add-Type -Path (Join-Path $here 'tray.cs') -ReferencedAssemblies 'System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll'
    }
    [PingSightTray.Tray]::Start($url)
    $tray = $true
} catch {
    Write-Host '  Windows notifications are not available here; alerts still show in the dashboard.' -ForegroundColor DarkYellow
}

Write-Host ''
Write-Host "  PingSight is running:  $url" -ForegroundColor Green
if ($DataDir) { Write-Host "  Results are saved to:  $DataDir" }
Write-Host '  Keep this window open while you monitor. Press Ctrl+C to stop.'
Write-Host ''
if (-not $NoBrowser) { Start-Process $url }

try {
    while ($true) {
        Start-Sleep -Milliseconds 500
        while ($null -ne ($note = [PingSight.Events]::TakeNotification())) {
            if ($tray) { [PingSightTray.Tray]::Notify($note[0], $note[1], $note[2] -eq '1') }
            Write-Host ("  {0}  {1} - {2}" -f (Get-Date -Format 'HH:mm:ss'), $note[0], $note[1])
        }
    }
} finally {
    [PingSight.Checks]::Stop()
    [PingSight.Engine]::FlushLog()
    [PingSight.Server]::Stop()
    if ($tray) { [PingSightTray.Tray]::Stop() }
}
