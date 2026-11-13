// PingSight checks: alert settings, outage events, DNS check, local link info and speed test.
// Compiled together with engine.cs (C# 5, .NET Framework 4.x only).
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PingSight
{
    // ---------------------------------------------------------------- settings

    public static class Settings
    {
        public static int OutageSec = 5;          // no reply for this long = outage
        public static double LossPct = 5;         // loss over the last minute that counts as a problem
        public static double LatencyMs = 150;     // average ping / TCP latency over the last minute
        public static double HttpMs = 2000;       // average web response time over the last minute
        public static bool Notify = false;        // Windows notifications (off unless switched on under Alerts)
        public static int SpeedEveryMin = 0;      // scheduled speed test; 0 = off
        static string path;

        static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

        public static void Load(string file)
        {
            path = file;
            try
            {
                if (!File.Exists(file)) return;
                foreach (string line in File.ReadAllLines(file))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) Set(line.Substring(0, i).Trim(), line.Substring(i + 1).Trim());
                }
            }
            catch (Exception) { }
        }

        public static void Set(string key, string value)
        {
            double v;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return;
            if (key == "outageSec") OutageSec = (int)Clamp(v, 2, 600);
            else if (key == "lossPct") LossPct = Clamp(v, 1, 100);
            else if (key == "latencyMs") LatencyMs = Clamp(v, 5, 10000);
            else if (key == "httpMs") HttpMs = Clamp(v, 50, 60000);
            else if (key == "notify") Notify = v != 0;
            else if (key == "speedEveryMin") SpeedEveryMin = v <= 0 ? 0 : (int)Clamp(v, 15, 1440);
        }

        public static void Save()
        {
            if (path == null) return;
            CultureInfo inv = CultureInfo.InvariantCulture;
            try
            {
                File.WriteAllText(path,
                    "outageSec=" + OutageSec + "\r\nlossPct=" + LossPct.ToString(inv) + "\r\nlatencyMs=" + LatencyMs.ToString(inv) +
                    "\r\nhttpMs=" + HttpMs.ToString(inv) + "\r\nnotify=" + (Notify ? "1" : "0") + "\r\nspeedEveryMin=" + SpeedEveryMin + "\r\n");
            }
            catch (Exception) { }
        }

        internal static void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"outageSec\":").Append(OutageSec);
            sb.Append(",\"lossPct\":"); Server.Num(sb, LossPct);
            sb.Append(",\"latencyMs\":"); Server.Num(sb, LatencyMs);
            sb.Append(",\"httpMs\":"); Server.Num(sb, HttpMs);
            sb.Append(",\"notify\":").Append(Notify ? "true" : "false");
            sb.Append(",\"speedEveryMin\":").Append(SpeedEveryMin).Append('}');
        }
    }

    // ---------------------------------------------------------------- events and alerts

    public sealed class NetEvent
    {
        public int Id;
        public string Target = "", Type = "", Detail = "";   // Type: outage | loss | latency
        public long Start, End;                               // End 0 = still going on
        public double Peak;
        public bool Notified;
    }

    public static class Events
    {
        static readonly List<NetEvent> all = new List<NetEvent>();
        static readonly object gate = new object();
        static readonly Queue<string[]> notes = new Queue<string[]>();
        static readonly Dictionary<string, long> lastNote = new Dictionary<string, long>();
        static readonly long started = Engine.NowMs();
        static int nextId = 1;
        public static long QuietUntil;    // a speed test fills the line: do not raise loss / latency alerts for it
        const string Columns = "start;end;target;type;duration_s;peak;detail";

        public static void Start()
        {
            Restore();
            Thread th = new Thread(Loop);
            th.IsBackground = true;
            th.Name = "pingsight-events";
            th.Start();
        }

        static void Loop()
        {
            while (true)
            {
                Thread.Sleep(1000);
                foreach (Target t in Engine.Snapshot())
                {
                    try { Evaluate(t); } catch (Exception) { }
                }
            }
        }

        internal static string Duration(long ms)
        {
            long s = Math.Max(0, ms) / 1000;
            if (s < 60) return s + " s";
            if (s < 3600) return (s / 60) + " min " + (s % 60) + " s";
            return (s / 3600) + " h " + ((s % 3600) / 60) + " min";
        }

        static NetEvent Open(string target, string type, long start)
        {
            NetEvent e = new NetEvent();
            e.Target = target; e.Type = type; e.Start = start;
            lock (gate)
            {
                e.Id = nextId++;
                all.Add(e);
                if (all.Count > 2000) all.RemoveRange(0, 500);
            }
            return e;
        }

        static void Note(NetEvent ev, string title, string text, bool opening)
        {
            if (!Settings.Notify) return;
            string key = ev.Target + "|" + ev.Type;
            long now = Engine.NowMs();
            lock (gate)
            {
                if (opening)
                {
                    // A flapping host should not flood the screen: one alert per target and kind every 5 minutes.
                    long last;
                    if (lastNote.TryGetValue(key, out last) && now - last < 300000) return;
                    lastNote[key] = now;
                    ev.Notified = true;
                }
                else if (!ev.Notified) return;
                notes.Enqueue(new string[] { title, text, opening ? "1" : "0" });
                while (notes.Count > 20) notes.Dequeue();
            }
        }

        // Polled by the launcher, which shows the Windows notification.
        public static string[] TakeNotification()
        {
            lock (gate) { return notes.Count > 0 ? notes.Dequeue() : null; }
        }

        // Where the path stopped answering during [from, to]. Caller holds t.Lock.
        static string BreakPoint(Target t, long from, long to)
        {
            int vis = t.VisibleHops(), last = 0;
            for (int i = 0; i < vis; i++)
            {
                Stats st = Server.Compute(t.Hops[i].Samples, from, to + 1);
                if (st.Sent > st.Lost) last = i + 1;
            }
            if (t.Service != null && t.DestTtl > 0 && last >= t.DestTtl)
                return "Ping to the host kept working, so the service itself was failing (" + t.ServiceInfo + ")";
            if (vis == 0) return "";
            if (last == 0) return "No hop answered: the fault is on this PC or its local network";
            Hop h = t.Hops[last - 1];
            string who = Engine.NameOf(h.Ip);
            if (who.Length == 0) who = h.Ip;
            return "The path answered as far as hop " + last + (who.Length > 0 ? " (" + who + ")" : "");
        }

        static void Evaluate(Target t)
        {
            if (t.Paused || t.Removed) return;
            long now = Engine.NowMs();
            List<NetEvent> closed = new List<NetEvent>();
            lock (t.Lock)
            {
                Hop h = t.HealthHop();
                if (h == null) return;
                List<Sample> s = h.Samples;
                // Only judge probes whose answer can no longer arrive.
                long settled = now - (t.Kind == "http" ? Engine.HttpTimeoutMs : Engine.TimeoutMs) - 400;
                int hi = Server.LowerBound(s, settled + 1) - 1;
                if (hi < 0 || s[hi].T < started) return;

                int k = hi;
                while (k >= 0 && s[k].Rtt < 0 && s[k].T >= started) k--;
                int run = hi - k;   // unanswered probes in a row, up to now

                if (t.OpenOutage == null && run >= 2 && s[hi].T - s[k + 1].T >= (Settings.OutageSec - 1) * 1000L)
                {
                    NetEvent ev = Open(t.Name, "outage", s[k + 1].T);
                    t.OpenOutage = ev;
                    Note(ev, t.Name + " is not answering", "No reply since " + Engine.FromMs(ev.Start).ToString("HH:mm:ss") + ".", true);
                }
                if (t.OpenOutage != null)
                {
                    NetEvent ev = t.OpenOutage;
                    if (run == 0)
                    {
                        int i = Server.LowerBound(s, ev.Start);
                        while (i <= hi && s[i].Rtt < 0) i++;
                        ev.End = i <= hi ? s[i].T : s[hi].T;
                        ev.Detail = BreakPoint(t, ev.Start, ev.End);
                        t.LastOutageEnd = ev.End;
                        t.OpenOutage = null;
                        closed.Add(ev);
                        Note(ev, t.Name + " is answering again", "It was down for " + Duration(ev.End - ev.Start) + ".", false);
                    }
                    else ev.Detail = BreakPoint(t, ev.Start, now);
                }

                if (t.OpenOutage == null)
                {
                    int gap = t.Kind == "http" ? Math.Max((int)t.IntervalMs, 5000) : (int)t.IntervalMs;
                    long win = Math.Max(60000L, 12L * gap);
                    long from = Math.Max(Math.Max(settled - win, t.LastOutageEnd + 1), started);
                    Stats st = Server.Compute(s, from, settled + 1);
                    int got = st.Sent - st.Lost;
                    double loss = st.Sent > 0 ? st.Lost * 100.0 / st.Sent : 0;
                    double avg = got > 0 ? st.Sum / got : 0;
                    bool quiet = SpeedTest.Running || now < QuietUntil;
                    double limit = t.Kind == "http" ? Settings.HttpMs : Settings.LatencyMs;

                    if (t.OpenLoss == null)
                    {
                        if (!quiet && st.Sent >= 10 && loss >= Settings.LossPct)
                        {
                            int i = Server.LowerBound(s, from);
                            while (i < hi && s[i].Rtt >= 0) i++;
                            NetEvent ev = Open(t.Name, "loss", s[i].T);
                            ev.Peak = loss;
                            t.OpenLoss = ev;
                            Note(ev, t.Name + ": packet loss", loss.ToString("0.#") + "% of probes were lost in the last minute.", true);
                        }
                    }
                    else
                    {
                        NetEvent ev = t.OpenLoss;
                        if (loss > ev.Peak) ev.Peak = loss;
                        if (st.Sent >= 10 && loss <= Settings.LossPct / 2)
                        {
                            ev.End = settled; t.OpenLoss = null; closed.Add(ev);
                            Note(ev, t.Name + ": packet loss ended", "It lasted " + Duration(ev.End - ev.Start) + " and peaked at " + ev.Peak.ToString("0.#") + "%.", false);
                        }
                    }

                    if (t.OpenLatency == null)
                    {
                        if (!quiet && got >= 10 && avg >= limit)
                        {
                            NetEvent ev = Open(t.Name, "latency", from);
                            ev.Peak = avg;
                            t.OpenLatency = ev;
                            Note(ev, t.Name + ": high latency", avg.ToString("0") + " ms on average in the last minute.", true);
                        }
                    }
                    else
                    {
                        NetEvent ev = t.OpenLatency;
                        if (avg > ev.Peak) ev.Peak = avg;
                        if (got >= 10 && avg <= limit * 0.8)
                        {
                            ev.End = settled; t.OpenLatency = null; closed.Add(ev);
                            Note(ev, t.Name + ": latency back to normal", "It lasted " + Duration(ev.End - ev.Start) + " and peaked at " + ev.Peak.ToString("0") + " ms.", false);
                        }
                    }
                }
            }
            foreach (NetEvent ev in closed) Persist(ev);
        }

        static void Persist(NetEvent ev)
        {
            CultureInfo ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            DateTime start = Engine.FromMs(ev.Start);
            string row = start.ToString("yyyy-MM-dd HH:mm:ss") + sep + Engine.FromMs(ev.End).ToString("yyyy-MM-dd HH:mm:ss") + sep +
                Engine.CsvText(ev.Target, sep) + sep + ev.Type + sep + ((ev.End - ev.Start) / 1000) + sep +
                (ev.Type == "outage" ? "" : ev.Peak.ToString("0.0", ci)) + sep + Engine.CsvText(ev.Detail, sep) + "\r\n";
            Engine.AppendCsv("events_" + start.ToString("yyyy-MM") + ".csv", Columns, row);
        }

        // On shutdown: events still open are saved as ending now.
        public static void CloseAll()
        {
            long now = Engine.NowMs();
            foreach (Target t in Engine.Snapshot())
            {
                List<NetEvent> open = new List<NetEvent>();
                lock (t.Lock)
                {
                    if (t.OpenOutage != null) open.Add(t.OpenOutage);
                    if (t.OpenLoss != null) open.Add(t.OpenLoss);
                    if (t.OpenLatency != null) open.Add(t.OpenLatency);
                    t.OpenOutage = null; t.OpenLoss = null; t.OpenLatency = null;
                }
                foreach (NetEvent ev in open)
                {
                    ev.End = now;
                    ev.Detail = (ev.Detail.Length > 0 ? ev.Detail + ". " : "") + "Still going on when PingSight was stopped";
                    Persist(ev);
                }
            }
        }

        static void Restore()
        {
            if (Engine.LogDir == null) return;
            CultureInfo ci = CultureInfo.CurrentCulture;
            string[] sep = new string[] { ci.TextInfo.ListSeparator };
            long cutoff = Engine.NowMs() - 30L * 86400000L;
            for (int m = 1; m >= 0; m--)
            {
                string path = Path.Combine(Engine.LogDir, "events_" + DateTime.Now.AddMonths(-m).ToString("yyyy-MM") + ".csv");
                if (!File.Exists(path)) continue;
                string[] lines;
                try { lines = Engine.ReadShared(path); } catch (Exception) { continue; }
                foreach (string line in lines)
                {
                    string[] p = line.Split(sep, StringSplitOptions.None);
                    if (p.Length < 7) continue;
                    DateTime a, b;
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out a)) continue;
                    if (!DateTime.TryParseExact(p[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out b)) continue;
                    if (Engine.ToMs(a) < cutoff) continue;
                    NetEvent e = Open(p[2], p[3], Engine.ToMs(a));
                    e.End = Engine.ToMs(b); e.Detail = p[6];
                    double peak;
                    if (double.TryParse(p[5], NumberStyles.Float, ci, out peak)) e.Peak = peak;
                }
            }
        }

        internal static int CountSince(long from)
        {
            int n = 0;
            lock (gate) { foreach (NetEvent e in all) if (e.Start >= from || e.End == 0) n++; }
            return n;
        }

        // Newest first; target null = every target.
        internal static void AppendJson(StringBuilder sb, string target, int max)
        {
            sb.Append('[');
            lock (gate)
            {
                int n = 0;
                for (int i = all.Count - 1; i >= 0 && n < max; i--)
                {
                    NetEvent e = all[i];
                    if (target != null && e.Target != target) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(e.Id).Append(",\"target\":"); Server.Str(sb, e.Target);
                    sb.Append(",\"type\":"); Server.Str(sb, e.Type);
                    sb.Append(",\"start\":").Append(e.Start).Append(",\"end\":").Append(e.End);
                    sb.Append(",\"peak\":"); Server.Num(sb, e.Peak);
                    sb.Append(",\"detail\":"); Server.Str(sb, e.Detail); sb.Append('}');
                }
            }
            sb.Append(']');
        }
    }

    // ---------------------------------------------------------------- DNS check

    public sealed class DnsServerInfo
    {
        public string Ip = "", Label = "";
        public bool System;
        public readonly Hop Data = new Hop();
    }

    // Asks each DNS server for a well-known name every 15 seconds and times the answer.
    public static class DnsCheck
    {
        static readonly List<DnsServerInfo> servers = new List<DnsServerInfo>();
        static readonly object gate = new object();
        static readonly string[] names = { "www.google.com", "www.microsoft.com", "www.cloudflare.com", "www.wikipedia.org" };
        static readonly string[,] reference = { { "1.1.1.1", "Cloudflare" }, { "8.8.8.8", "Google" } };
        static readonly Random rnd = new Random();
        static int turn;

        public static void Start()
        {
            Thread th = new Thread(Loop);
            th.IsBackground = true;
            th.Name = "pingsight-dns";
            th.Start();
        }

        static void Loop()
        {
            while (true)
            {
                try
                {
                    Refresh();
                    long t = Engine.NowMs();
                    string name = names[turn++ % names.Length];
                    DnsServerInfo[] list;
                    lock (gate) { list = servers.ToArray(); }
                    foreach (DnsServerInfo s in list)
                    {
                        DnsServerInfo srv = s;
                        ThreadPool.QueueUserWorkItem(delegate(object o) { Probe(srv, name, t); });
                    }
                }
                catch (Exception) { }
                Thread.Sleep(15000);
            }
        }

        internal static List<string> SystemServers()
        {
            List<string> found = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties ip = ni.GetIPProperties();
                    if (ip.GatewayAddresses.Count == 0) continue;
                    foreach (IPAddress a in ip.DnsAddresses)
                    {
                        if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = a.ToString();
                        if (!found.Contains(s)) found.Add(s);
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        static void Refresh()
        {
            List<string> sys = SystemServers();
            lock (gate)
            {
                foreach (string ip in sys)
                {
                    DnsServerInfo s = servers.Find(delegate(DnsServerInfo x) { return x.Ip == ip; });
                    if (s == null) { s = new DnsServerInfo(); s.Ip = ip; servers.Add(s); }
                }
                for (int i = 0; i < reference.GetLength(0); i++)
                {
                    string ip = reference[i, 0];
                    DnsServerInfo s = servers.Find(delegate(DnsServerInfo x) { return x.Ip == ip; });
                    if (s == null) { s = new DnsServerInfo(); s.Ip = ip; servers.Add(s); }
                    s.Label = reference[i, 1];
                }
                foreach (DnsServerInfo s in servers) s.System = sys.Contains(s.Ip);
                servers.RemoveAll(delegate(DnsServerInfo x) { return !x.System && x.Label.Length == 0; });
                servers.Sort(delegate(DnsServerInfo a, DnsServerInfo b) { return b.System.CompareTo(a.System); });
            }
        }

        static byte[] Query(string name, ushort id)
        {
            List<byte> b = new List<byte>();
            b.Add((byte)(id >> 8)); b.Add((byte)id);
            b.AddRange(new byte[] { 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 });   // recursion desired, one question
            foreach (string label in name.Split('.'))
            {
                b.Add((byte)label.Length);
                b.AddRange(Encoding.ASCII.GetBytes(label));
            }
            b.AddRange(new byte[] { 0, 0, 1, 0, 1 });                         // end of name, type A, class IN
            return b.ToArray();
        }

        static void Probe(DnsServerInfo s, string name, long t)
        {
            float rtt = -1f;
            try
            {
                ushort id;
                lock (rnd) { id = (ushort)rnd.Next(1, 65535); }
                byte[] q = Query(name, id);
                using (UdpClient u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.Client.ReceiveTimeout = Engine.TimeoutMs;
                    u.Connect(new IPEndPoint(IPAddress.Parse(s.Ip), 53));
                    long start = Stopwatch.GetTimestamp();
                    u.Send(q, q.Length);
                    IPEndPoint from = null;
                    byte[] r = u.Receive(ref from);
                    // Same id, and an answer rather than "server failure" or "refused".
                    if (r.Length >= 12 && r[0] == q[0] && r[1] == q[1] && (r[3] & 0x0f) == 0)
                        rtt = (float)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                }
            }
            catch (Exception) { }
            lock (gate) { s.Data.Add(t, rtt); }
        }

        internal static void AppendJson(StringBuilder sb, long from)
        {
            sb.Append("{\"servers\":[");
            lock (gate)
            {
                for (int i = 0; i < servers.Count; i++)
                {
                    DnsServerInfo s = servers[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"ip\":"); Server.Str(sb, s.Ip);
                    sb.Append(",\"label\":"); Server.Str(sb, s.Label);
                    sb.Append(",\"system\":").Append(s.System ? "true" : "false").Append(',');
                    Server.AppendStats(sb, Server.Compute(s.Data.Samples, from, long.MaxValue));
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }
    }

    // ---------------------------------------------------------------- local link

    public static class LinkInfo
    {
        static readonly object gate = new object();
        static string publicIp = "", loc = "", colo = "", ssid = "";
        static int signal = -1;
        static double rx, tx;
        static readonly Hop wifi = new Hop();   // signal quality in percent over time
        static long publicAt;

        public static void Start()
        {
            Thread th = new Thread(Loop);
            th.IsBackground = true;
            th.Name = "pingsight-link";
            th.Start();
        }

        static void Loop()
        {
            while (true)
            {
                try
                {
                    long now = Engine.NowMs();
                    if (now - publicAt > 600000) { publicAt = now; ReadPublic(); }
                    ReadWifi(now);
                }
                catch (Exception) { }
                Thread.Sleep(15000);
            }
        }

        // The address the internet sees this network as, from Cloudflare's plain-text trace page.
        static void ReadPublic()
        {
            try
            {
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create("https://www.cloudflare.com/cdn-cgi/trace");
                rq.Timeout = 8000; rq.ReadWriteTimeout = 8000; rq.UserAgent = "PingSight";
                string body;
                using (WebResponse rs = rq.GetResponse())
                using (StreamReader r = new StreamReader(rs.GetResponseStream()))
                    body = r.ReadToEnd();
                string ip = "", l = "", c = "";
                foreach (string line in body.Split('\n'))
                {
                    if (line.StartsWith("ip=")) ip = line.Substring(3).Trim();
                    else if (line.StartsWith("loc=")) l = line.Substring(4).Trim();
                    else if (line.StartsWith("colo=")) c = line.Substring(5).Trim();
                }
                lock (gate) { publicIp = ip; loc = l; colo = c; }
            }
            catch (Exception) { publicAt = Engine.NowMs() - 540000; }   // try again in a minute
        }

        // Windows has no simple API for Wi-Fi signal, so read "netsh wlan show interfaces".
        // Its labels are translated, but the layout is not: the signal is the only percentage,
        // and the two "(Mbps)" lines are receive then transmit.
        static void ReadWifi(long now)
        {
            bool connected = false;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && ni.OperationalStatus == OperationalStatus.Up) connected = true;
            if (!connected) { lock (gate) { signal = -1; } return; }

            ProcessStartInfo psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces");
            psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.CreateNoWindow = true;
            string text;
            using (Process p = Process.Start(psi))
            {
                text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
            }
            int sig = -1, rates = 0; string name = ""; double r = 0, t = 0;
            foreach (string raw in text.Split('\n'))
            {
                int i = raw.IndexOf(" : ", StringComparison.Ordinal);
                if (i < 0) continue;
                string key = raw.Substring(0, i).Trim(), val = raw.Substring(i + 3).Trim();
                double d; int pct;
                if (key == "SSID") name = val;
                else if (val.EndsWith("%")) { if (int.TryParse(val.TrimEnd('%').Trim(), out pct)) sig = pct; }
                else if (key.Contains("(Mbps)") && double.TryParse(val.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    if (rates++ == 0) r = d; else t = d;
                }
            }
            lock (gate)
            {
                signal = sig; ssid = name; rx = r; tx = t;
                if (sig >= 0) wifi.Add(now, sig);
            }
        }

        internal static void AppendJson(StringBuilder sb, long from, long fromA, long step, int n)
        {
            sb.Append("{\"adapters\":[");
            bool first = true;
            try
            {
                List<NetworkInterface> list = new List<NetworkInterface>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (ni.GetIPProperties().GatewayAddresses.Count > 0) list.Add(ni);
                }
                foreach (NetworkInterface ni in list)
                {
                    IPInterfaceProperties ip = ni.GetIPProperties();
                    string addr = "", gw = "";
                    foreach (UnicastIPAddressInformation u in ip.UnicastAddresses)
                        if (addr.Length == 0 && u.Address.AddressFamily == AddressFamily.InterNetwork) addr = u.Address.ToString();
                    foreach (GatewayIPAddressInformation g in ip.GatewayAddresses)
                        if (gw.Length == 0 && g.Address.AddressFamily == AddressFamily.InterNetwork) gw = g.Address.ToString();
                    string type = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi"
                        : ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ? "Ethernet (cable)"
                        : ni.NetworkInterfaceType.ToString();
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"name\":"); Server.Str(sb, ni.Name);
                    sb.Append(",\"desc\":"); Server.Str(sb, ni.Description);
                    sb.Append(",\"type\":"); Server.Str(sb, type);
                    sb.Append(",\"speed\":"); Server.Num(sb, ni.Speed > 0 ? ni.Speed / 1e6 : double.NaN);
                    sb.Append(",\"ip\":"); Server.Str(sb, addr);
                    sb.Append(",\"gateway\":"); Server.Str(sb, gw);
                    sb.Append(",\"dns\":[");
                    bool f2 = true;
                    foreach (IPAddress a in ip.DnsAddresses)
                    {
                        if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (!f2) sb.Append(','); f2 = false;
                        Server.Str(sb, a.ToString());
                    }
                    sb.Append("]}");
                }
            }
            catch (Exception) { }
            sb.Append(']');
            lock (gate)
            {
                sb.Append(",\"publicIp\":"); Server.Str(sb, publicIp);
                sb.Append(",\"loc\":"); Server.Str(sb, loc);
                sb.Append(",\"colo\":"); Server.Str(sb, colo);
                sb.Append(",\"wifi\":");
                if (signal < 0 && Server.LowerBound(wifi.Samples, from) >= wifi.Samples.Count) sb.Append("null");
                else
                {
                    sb.Append("{\"connected\":").Append(signal >= 0 ? "true" : "false");
                    sb.Append(",\"ssid\":"); Server.Str(sb, ssid);
                    sb.Append(",\"signal\":").Append(signal);
                    sb.Append(",\"rx\":"); Server.Num(sb, rx);
                    sb.Append(",\"tx\":"); Server.Num(sb, tx); sb.Append(',');
                    Server.AppendStats(sb, Server.Compute(wifi.Samples, from, long.MaxValue));
                    sb.Append(",\"series\":"); Server.AppendSeries(sb, wifi.Samples, fromA, step, n);
                    sb.Append('}');
                }
            }
            sb.Append('}');
        }
    }

    // ---------------------------------------------------------------- speed test

    public sealed class SpeedResult
    {
        public long T;
        public double Down, Up, Latency, Jitter, DownMB, UpMB;
        public string Server = "", Error = "";
    }

    // Download, upload and latency against Cloudflare's public speed-test endpoints
    // (speed.cloudflare.com): several parallel streams for a fixed time, like other speed tests.
    public static class SpeedTest
    {
        public static volatile bool Running;
        public static string Phase = "";
        public static double Pct, Live;

        const string Base = "https://speed.cloudflare.com";
        const int Streams = 4, Seconds = 8;
        const string Columns = "time;download_mbps;upload_mbps;latency_ms;jitter_ms;download_mb;upload_mb;server;error";
        static readonly List<SpeedResult> results = new List<SpeedResult>();
        static readonly object gate = new object();
        static readonly List<HttpWebRequest> active = new List<HttpWebRequest>();
        static readonly long started = Engine.NowMs();
        static long lastRun, counter;
        static volatile bool stop;
        static byte[] junk;

        public static void StartScheduler()
        {
            Restore();
            Thread th = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(20000);
                    int every = Settings.SpeedEveryMin;
                    long now = Engine.NowMs();
                    if (every > 0 && !Running && now - started > 60000 && now - lastRun >= every * 60000L) Start();
                }
            });
            th.IsBackground = true;
            th.Name = "pingsight-speed-schedule";
            th.Start();
        }

        public static bool Start()
        {
            lock (gate)
            {
                if (Running) return false;
                Running = true;
            }
            Phase = "latency"; Pct = 0; Live = 0;
            Thread th = new Thread(Run);
            th.IsBackground = true;
            th.Name = "pingsight-speed";
            th.Start();
            return true;
        }

        static HttpWebRequest Request(string url, string method)
        {
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = method; rq.Timeout = 15000; rq.ReadWriteTimeout = 15000;
            rq.UserAgent = "PingSight"; rq.KeepAlive = true;
            return rq;
        }

        static void Run()
        {
            SpeedResult r = new SpeedResult();
            r.T = Engine.NowMs();
            lastRun = r.T;
            try
            {
                // Latency: time small requests on one kept-alive connection. Cloudflare reports the
                // connection's own TCP round-trip time in a response header; use it when present.
                List<double> lat = new List<double>();
                for (int i = 0; i < 9; i++)
                {
                    long s = Stopwatch.GetTimestamp();
                    HttpWebRequest rq = Request(Base + "/__down?bytes=0", "GET");
                    using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
                    {
                        double ms = (Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency;
                        string timing = rs.Headers["Server-Timing"] ?? "";
                        int at = timing.IndexOf("&rtt=", StringComparison.Ordinal);
                        if (at < 0) at = timing.IndexOf("?rtt=", StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            int e = at + 5;
                            while (e < timing.Length && char.IsDigit(timing[e])) e++;
                            double micro;
                            if (double.TryParse(timing.Substring(at + 5, e - at - 5), NumberStyles.Float, CultureInfo.InvariantCulture, out micro) && micro > 0) ms = micro / 1000.0;
                        }
                        if (i == 0)
                        {
                            string ray = rs.Headers["CF-RAY"] ?? "";
                            int dash = ray.LastIndexOf('-');
                            r.Server = "Cloudflare" + (dash >= 0 ? " " + ray.Substring(dash + 1) : "");
                        }
                        else lat.Add(ms);   // the first request also pays for the TLS handshake
                        using (Stream body = rs.GetResponseStream()) { byte[] b = new byte[256]; while (body.Read(b, 0, b.Length) > 0) { } }
                    }
                    Pct = (i + 1) / 9.0 * 0.08;
                }
                double jit = 0;
                for (int i = 1; i < lat.Count; i++) jit += Math.Abs(lat[i] - lat[i - 1]);
                r.Jitter = lat.Count > 1 ? jit / (lat.Count - 1) : 0;
                lat.Sort();
                r.Latency = lat[lat.Count / 2];

                Phase = "download";
                r.Down = Transfer(false, 0.08, out r.DownMB);
                Phase = "upload";
                r.Up = Transfer(true, 0.54, out r.UpMB);
            }
            catch (WebException ex)
            {
                HttpWebResponse rs = ex.Response as HttpWebResponse;
                r.Error = rs != null && (int)rs.StatusCode == 429 ? "The test server is limiting requests; try again later" : "Could not reach the test server (" + ex.Status + ")";
            }
            catch (Exception ex) { r.Error = ex.Message; }
            finally
            {
                lock (gate)
                {
                    results.Add(r);
                    if (results.Count > 500) results.RemoveRange(0, 100);
                }
                Persist(r);
                Events.QuietUntil = Engine.NowMs() + 75000;
                Phase = ""; Pct = 1; Live = 0;
                Running = false;
            }
        }

        static void DownOnce()
        {
            HttpWebRequest rq = Request(Base + "/__down?bytes=50000000", "GET");
            lock (active) { active.Add(rq); }
            try
            {
                using (WebResponse rs = rq.GetResponse())
                using (Stream body = rs.GetResponseStream())
                {
                    byte[] b = new byte[65536];
                    int n;
                    while (!stop && (n = body.Read(b, 0, b.Length)) > 0) Interlocked.Add(ref counter, n);
                    if (stop) rq.Abort();
                }
            }
            finally { lock (active) { active.Remove(rq); } }
        }

        static void UpOnce()
        {
            const long size = 25000000;
            HttpWebRequest rq = Request(Base + "/__up", "POST");
            rq.ContentType = "application/octet-stream";
            rq.AllowWriteStreamBuffering = false;   // send as we write, so written bytes are sent bytes
            rq.ContentLength = size;
            lock (active) { active.Add(rq); }
            try
            {
                using (Stream body = rq.GetRequestStream())
                {
                    long sent = 0;
                    while (sent < size)
                    {
                        if (stop) { rq.Abort(); return; }
                        int n = (int)Math.Min(junk.Length, size - sent);
                        body.Write(junk, 0, n);
                        sent += n;
                        Interlocked.Add(ref counter, n);
                    }
                }
                using (WebResponse rs = rq.GetResponse()) { }
            }
            finally { lock (active) { active.Remove(rq); } }
        }

        // Runs the streams for a fixed time and returns megabits per second, measured after the
        // first 1.5 s so the connections' slow start does not drag the figure down.
        static double Transfer(bool upload, double pctBase, out double megabytes)
        {
            if (junk == null) { junk = new byte[65536]; new Random(7).NextBytes(junk); }
            counter = 0; stop = false;
            int failures = 0;
            Exception firstError = null;
            Thread[] th = new Thread[Streams];
            for (int i = 0; i < Streams; i++)
            {
                th[i] = new Thread(delegate()
                {
                    while (!stop)
                    {
                        try { if (upload) UpOnce(); else DownOnce(); }
                        catch (Exception ex)
                        {
                            if (stop) break;
                            if (firstError == null) firstError = ex;
                            if (Interlocked.Increment(ref failures) > 12) break;
                            Thread.Sleep(250);
                        }
                    }
                });
                th[i].IsBackground = true;
                th[i].Start();
            }
            Stopwatch sw = Stopwatch.StartNew();
            long markBytes = 0; double markMs = 0; bool marked = false;
            while (sw.ElapsedMilliseconds < Seconds * 1000)
            {
                Thread.Sleep(200);
                long b = Interlocked.Read(ref counter);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (!marked && ms >= 1500) { marked = true; markBytes = b; markMs = ms; }
                Live = marked && ms > markMs + 100 ? (b - markBytes) * 8 / 1e6 / ((ms - markMs) / 1000) : b * 8 / 1e6 / (ms / 1000);
                Pct = pctBase + Math.Min(1, ms / (Seconds * 1000)) * 0.46;
            }
            long endBytes = Interlocked.Read(ref counter);
            double endMs = sw.Elapsed.TotalMilliseconds;
            stop = true;
            lock (active) { foreach (HttpWebRequest rq in active.ToArray()) { try { rq.Abort(); } catch (Exception) { } } }
            foreach (Thread t in th) t.Join(3000);
            megabytes = endBytes / 1e6;
            if (endBytes == 0 && firstError != null) throw firstError;
            return (endBytes - markBytes) * 8 / 1e6 / ((endMs - markMs) / 1000);
        }

        static void Persist(SpeedResult r)
        {
            CultureInfo ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            DateTime when = Engine.FromMs(r.T);
            string row = when.ToString("yyyy-MM-dd HH:mm:ss") + sep + r.Down.ToString("0.00", ci) + sep + r.Up.ToString("0.00", ci) + sep +
                r.Latency.ToString("0.00", ci) + sep + r.Jitter.ToString("0.00", ci) + sep + r.DownMB.ToString("0.0", ci) + sep +
                r.UpMB.ToString("0.0", ci) + sep + Engine.CsvText(r.Server, sep) + sep + Engine.CsvText(r.Error, sep) + "\r\n";
            Engine.AppendCsv("speedtest_" + when.ToString("yyyy-MM") + ".csv", Columns, row);
        }

        static void Restore()
        {
            if (Engine.LogDir == null) return;
            CultureInfo ci = CultureInfo.CurrentCulture;
            string[] sep = new string[] { ci.TextInfo.ListSeparator };
            for (int m = 2; m >= 0; m--)
            {
                string path = Path.Combine(Engine.LogDir, "speedtest_" + DateTime.Now.AddMonths(-m).ToString("yyyy-MM") + ".csv");
                if (!File.Exists(path)) continue;
                string[] lines;
                try { lines = Engine.ReadShared(path); } catch (Exception) { continue; }
                foreach (string line in lines)
                {
                    string[] p = line.Split(sep, StringSplitOptions.None);
                    if (p.Length < 9) continue;
                    DateTime dt;
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) continue;
                    SpeedResult r = new SpeedResult();
                    r.T = Engine.ToMs(dt);
                    double.TryParse(p[1], NumberStyles.Float, ci, out r.Down);
                    double.TryParse(p[2], NumberStyles.Float, ci, out r.Up);
                    double.TryParse(p[3], NumberStyles.Float, ci, out r.Latency);
                    double.TryParse(p[4], NumberStyles.Float, ci, out r.Jitter);
                    double.TryParse(p[5], NumberStyles.Float, ci, out r.DownMB);
                    double.TryParse(p[6], NumberStyles.Float, ci, out r.UpMB);
                    r.Server = p[7]; r.Error = p[8];
                    results.Add(r);
                    if (r.T > lastRun) lastRun = r.T;
                }
            }
            if (results.Count > 500) results.RemoveRange(0, results.Count - 500);
        }

        internal static void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"running\":").Append(Running ? "true" : "false");
            sb.Append(",\"phase\":"); Server.Str(sb, Phase);
            sb.Append(",\"pct\":"); Server.Num(sb, Pct);
            sb.Append(",\"live\":"); Server.Num(sb, Live);
            sb.Append(",\"results\":[");
            lock (gate)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    SpeedResult r = results[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"t\":").Append(r.T);
                    sb.Append(",\"down\":"); Server.Num(sb, r.Down);
                    sb.Append(",\"up\":"); Server.Num(sb, r.Up);
                    sb.Append(",\"latency\":"); Server.Num(sb, r.Latency);
                    sb.Append(",\"jitter\":"); Server.Num(sb, r.Jitter);
                    sb.Append(",\"downMB\":"); Server.Num(sb, r.DownMB);
                    sb.Append(",\"upMB\":"); Server.Num(sb, r.UpMB);
                    sb.Append(",\"server\":"); Server.Str(sb, r.Server);
                    sb.Append(",\"error\":"); Server.Str(sb, r.Error); sb.Append('}');
                }
            }
            sb.Append("]}");
        }
    }

    // ---------------------------------------------------------------- API and start-up

    public static class Extras
    {
        internal static string Build(NameValueCollection q)
        {
            int window = Math.Max(60, Math.Min(7 * 86400, Server.Int(q["window"], 300)));
            int points = Math.Max(20, Math.Min(1000, Server.Int(q["points"], 300)));
            long now = Engine.NowMs();
            long from = now - window * 1000L;
            long step = Math.Max(15000L, (long)Math.Ceiling(window * 1000.0 / points));
            step = (step + 14999) / 15000 * 15000;
            long fromA = from / step * step;
            int n = (int)((now - fromA) / step) + 1;
            StringBuilder sb = new StringBuilder(16384);
            sb.Append("{\"now\":").Append(now).Append(",\"from\":").Append(fromA).Append(",\"step\":").Append(step).Append(",\"n\":").Append(n);
            sb.Append(",\"dns\":"); DnsCheck.AppendJson(sb, from);
            sb.Append(",\"link\":"); LinkInfo.AppendJson(sb, from, fromA, step, n);
            sb.Append(",\"speed\":"); SpeedTest.AppendJson(sb);
            sb.Append(",\"events\":"); Events.AppendJson(sb, null, 200);
            sb.Append('}');
            return sb.ToString();
        }
    }

    public static class Checks
    {
        public static void Start(string settingsFile)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch (Exception) { }   // TLS 1.3 where Windows has it
            ServicePointManager.DefaultConnectionLimit = 16;
            ServicePointManager.Expect100Continue = false;
            Settings.Load(settingsFile);
            Events.Start();
            DnsCheck.Start();
            LinkInfo.Start();
            SpeedTest.StartScheduler();
        }

        public static void Stop()
        {
            try { Events.CloseAll(); } catch (Exception) { }
        }
    }
}
