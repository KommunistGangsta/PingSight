<#
  PingSight - continuous traceroute and latency / packet-loss monitor.

  Usage:   .\PingSight.ps1 [-Port 8787] [-NoBrowser]
  Stop:    Ctrl+C (or close the window)

  Nothing is installed and no admin rights are needed. The dashboard is served
  on http://localhost only and is not reachable from other machines.
#>
param(
    [int]$Port = 8787,
    [switch]$NoBrowser,
    # Where results are saved (one CSV per host per day). Use -DataDir none to save nothing.
    [string]$DataDir = 'C:\temp\PingSight',
    # How many seconds each saved row summarises.
    [int]$LogSeconds = 60
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not ('PingSight.Server' -as [type])) {
    $source = Get-Content -Raw -Encoding UTF8 (Join-Path $here 'engine.cs')
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -TypeDefinition $source -Language CSharp
    } else {
        Add-Type -TypeDefinition $source -Language CSharp -ReferencedAssemblies 'System.dll', 'System.Core.dll'
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

[PingSight.Engine]::Load((Join-Path $here 'targets.txt'))

if ($DataDir -eq 'none') { $DataDir = '' }
if ($DataDir) { [PingSight.Engine]::StartLog($DataDir, $LogSeconds) }

$url = "http://localhost:$Port/"
Write-Host ''
Write-Host "  PingSight is running:  $url" -ForegroundColor Green
if ($DataDir) { Write-Host "  Results are saved to:  $DataDir" }
Write-Host '  Keep this window open while you monitor. Press Ctrl+C to stop.'
Write-Host ''
if (-not $NoBrowser) { Start-Process $url }

try {
    while ($true) { Start-Sleep -Seconds 1 }
} finally {
    [PingSight.Engine]::FlushLog()
    [PingSight.Server]::Stop()
}
