<#
  PingSight bootstrap: downloads the latest PingSight and starts it.

  From any PowerShell prompt:
      irm https://raw.githubusercontent.com/KommunistGangsta/PingSight/main/run.ps1 | iex

  With options:
      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/KommunistGangsta/PingSight/main/run.ps1))) -Port 9000 -DataDir D:\logs

  The files are kept in %LOCALAPPDATA%\PingSight, so the host list survives
  between runs and PingSight still starts when GitHub cannot be reached.
#>
param(
    [int]$Port = 8787,
    [switch]$NoBrowser,
    [string]$DataDir = 'C:\temp\PingSight',
    [int]$LogSeconds = 60
)

& {
    param($Port, $NoBrowser, $DataDir, $LogSeconds)

    $base  = 'https://raw.githubusercontent.com/KommunistGangsta/PingSight/main'
    $dir   = Join-Path $env:LOCALAPPDATA 'PingSight'
    $files = 'PingSight.ps1', 'engine.cs', 'checks.cs', 'tray.cs', 'dashboard.html'

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Force -Path $dir -ErrorAction Stop | Out-Null

    try {
        # Download everything first, then swap, so a failed download never leaves a half-updated copy.
        foreach ($f in $files) {
            Invoke-WebRequest -UseBasicParsing -Uri "$base/$f" -OutFile (Join-Path $dir "$f.new") -ErrorAction Stop
        }
        foreach ($f in $files) {
            Move-Item -Force -Path (Join-Path $dir "$f.new") -Destination (Join-Path $dir $f) -ErrorAction Stop
        }
    } catch {
        Get-ChildItem -Path $dir -Filter '*.new' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path (Join-Path $dir 'PingSight.ps1'))) {
            Write-Error "PingSight could not be downloaded: $($_.Exception.Message)"
            return
        }
        Write-Warning 'GitHub could not be reached; starting the copy already on this PC.'
    }

    $run = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $dir 'PingSight.ps1'),
             '-Port', $Port, '-DataDir', $DataDir, '-LogSeconds', $LogSeconds)
    if ($NoBrowser) { $run += '-NoBrowser' }
    & powershell.exe @run
} $Port $NoBrowser $DataDir $LogSeconds
