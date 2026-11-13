# PingSight

A PingPlotter-style network monitor for Windows: continuous traceroute with
latency and packet loss at every hop, in a browser dashboard.

Nothing to install and no admin rights needed. It runs on the PowerShell and
.NET Framework that ship with Windows 10 and 11.

## Run it

From any PowerShell prompt:

```powershell
irm https://raw.githubusercontent.com/KommunistGangsta/PingSight/main/run.ps1 | iex
```

This downloads the latest version to `%LOCALAPPDATA%\PingSight`, starts it and
opens `http://localhost:8787`. Keep the console window open while you monitor
and press Ctrl+C to stop. If GitHub cannot be reached, the copy already on the
PC is started instead.

With options:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/KommunistGangsta/PingSight/main/run.ps1))) -Port 9000 -DataDir D:\logs
```

| Option | Default | Meaning |
|---|---|---|
| `-Port` | `8787` | Local port for the dashboard. The next free port is used if it is taken. |
| `-DataDir` | `C:\temp\PingSight` | Where results are saved. `none` saves nothing. |
| `-LogSeconds` | `60` | How many seconds each saved row summarises. |
| `-NoBrowser` | off | Do not open the browser automatically. |

Or download the repository and double-click `Start-PingSight.cmd`.

## What it does

- **Several hosts at once.** Type or paste a list of host names or IP addresses.
- **Route, hop by hop.** Loss, last / average / min / max latency and jitter for
  every router on the way.
- **Timelines.** Latency and packet loss for the destination and any hop you
  choose, over the last minute up to 24 hours.
- **A verdict in plain words.** Where loss or delay starts, and which hops only
  look lossy because the router gives ping replies low priority.
- **Results on disk.** One CSV per host per day, one row per hop per minute,
  written in the PC's regional format so the files open directly in Excel.

## Good to know

- Probes are ICMP echo (ping). A host that blocks ping shows as not answering
  even when it is up.
- The dashboard listens on `localhost` only and cannot be reached from other
  machines.
- History in the dashboard is kept in memory and starts empty after a restart;
  the CSV files are the lasting record.

## Files

| File | Purpose |
|---|---|
| `run.ps1` | Download-and-start script for the one-line command. |
| `PingSight.ps1` | Starts the engine and the local web server. |
| `engine.cs` | Probe engine and HTTP API, compiled at start-up. |
| `dashboard.html` | The dashboard. |
| `Start-PingSight.cmd` | Double-click launcher. |
