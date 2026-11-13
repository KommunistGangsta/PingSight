# PingSight

A network monitor for Windows in the spirit of PingPlotter: continuous
traceroute with latency and packet loss at every hop, service checks, DNS check,
speed test, and an outage log with alerts, all in a browser dashboard.

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

Starting PingSight while it is already running on the same port just opens the
dashboard of the running copy.

## What it does

### Hosts

Type or paste one host or a whole list. Three kinds are understood:

| You type | What is checked |
|---|---|
| `example.com` or `10.0.0.1` | Ping and the route, hop by hop. |
| `example.com:443` | The same, plus whether that TCP port accepts connections and how fast. |
| `https://example.com/page` | The same, plus a web request: response time, status code and certificate expiry. |

For each host you get:

- **Route, hop by hop.** Loss, last / average / min / max latency and jitter for
  every router on the way.
- **Timelines.** Latency and loss for the destination, the service check and any
  hop you choose, over the last minute up to 24 hours.
- **A verdict in plain words.** Where loss or delay starts, whether a failing
  service is a network or a server problem, and which hops only look lossy
  because the router gives ping replies low priority.
- **Events.** Every outage, spell of packet loss and spell of high latency, with
  start, duration and where the path stopped answering.

### Network checks

- **Speed test.** Download, upload and latency against Cloudflare's public test
  servers (`speed.cloudflare.com`), on demand or on a schedule. A test moves
  real data: about 8 seconds of download and 8 of upload at full line speed.
- **DNS.** Lookup time and failures for the DNS servers this PC uses, next to
  two public servers for comparison.
- **Local link.** Adapter, link speed, addresses, gateway, public address, and
  Wi-Fi signal over time when connected by Wi-Fi.
- **Alerts.** Adjustable limits for what counts as an outage, packet loss or
  high latency. Optionally a Windows notification when one starts and ends;
  this is off until you switch it on under Alerts.

### Results on disk

Everything is saved as CSV in the PC's regional format, so the files open
directly in Excel:

| File | Content |
|---|---|
| `<host>_<date>.csv` | One row per hop per minute: sent, lost, min / avg / max latency, jitter. Hop `0` is the service check. |
| `events_<month>.csv` | Outages, packet loss and high latency, with start, end and details. |
| `speedtest_<month>.csv` | Every speed test. |

On start-up the last 24 hours of each host's graphs are rebuilt from these
files, at one-minute detail.

## Good to know

- Ping probes are ICMP echo. A host that blocks ping shows as not answering;
  add it as `host:port` or as a web address to check the service instead.
- Web checks run at most once every 5 seconds and never accept a certificate
  that Windows does not trust.
- The public address is read from Cloudflare (`cloudflare.com/cdn-cgi/trace`).
- The dashboard listens on `localhost` only and cannot be reached from other
  machines.
- PingSight does not install a service or start with Windows. Notifications,
  when switched on, appear only while it is running.

## Files

| File | Purpose |
|---|---|
| `run.ps1` | Download-and-start script for the one-line command. |
| `PingSight.ps1` | Starts the engine, the local web server and the notification icon. |
| `engine.cs` | Traceroute engine, service checks, result files and HTTP API. |
| `checks.cs` | Events and alerts, DNS check, local link info and speed test. |
| `tray.cs` | Notification icon. |
| `dashboard.html` | The dashboard. |
| `Start-PingSight.cmd` | Double-click launcher. |
