// PingSight engine: continuous traceroute + local HTTP API.
// Compiled at start-up by PingSight.ps1 (Add-Type), so it is written in C# 5
// and uses only what ships with .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace PingSight
{
    public struct Sample
    {
        public long T;      // round start, unix ms
        public float Rtt;   // ms; negative = no reply
    }

    public sealed class Hop
    {
        public int Ttl;
        public string Ip = "";
        public readonly Dictionary<string, int> Ips = new Dictionary<string, int>();
        public readonly List<Sample> Samples = new List<Sample>();

        public void Add(long t, float rtt)
        {
            Sample x; x.T = t; x.Rtt = rtt;
            int i = Samples.Count;
            while (i > 0 && Samples[i - 1].T > t) i--;   // late timeouts land behind newer replies
            Samples.Insert(i, x);
            if (Samples.Count > Engine.MaxSamples + 4096) Samples.RemoveRange(0, 4096);
        }

        public void Clear()
        {
            Samples.Clear(); Ips.Clear(); Ip = "";
        }
    }

    public sealed class Spec
    {
        public string Name, Host, Kind = "", Url = "", Label = "";
        public int Port;
    }

    internal sealed class ProbeJob
    {
        public Target Target; public IPAddress Address; public int Ttl; public long T; public int Epoch;
    }

    public sealed class Target
    {
        public int Id;
        public string Name;              // unique key and display name: host, host:port or URL
        public string Host;
        public string Kind = "";         // "" = ping only, "tcp" = port check, "http" = web check
        public int Port;
        public string Url = "";
        public Hop Service;              // results of the TCP / HTTP check (null for ping-only targets)
        public string ServiceLabel = "";
        public string ServiceInfo = "";
        public int CertDays = int.MinValue;
        public NetEvent OpenOutage, OpenLoss, OpenLatency;
        public long LastOutageEnd;
        long lastService;
        public IPAddress Address;
        public string Error = "";
        public volatile int IntervalMs;
        public volatile bool Paused;
        public volatile bool Removed;
        public volatile bool Resched;
        public int DestTtl;     // hop count to the destination; 0 = not known (yet)
        public int Highest;     // highest hop that has ever answered
        public int Round;
        public int Epoch;
        public readonly List<Hop> Hops = new List<Hop>();
        public readonly object Lock = new object();

        public void Start()
        {
            Thread th = new Thread(Run);
            th.IsBackground = true;
            th.Name = "pingsight-" + Host;
            th.Start();
        }

        // The series that says whether the target is healthy: the service check when
        // there is one, otherwise the destination hop. Caller holds Lock.
        public Hop HealthHop()
        {
            if (Service != null) return Service;
            if (DestTtl > 0 && DestTtl <= Hops.Count) return Hops[DestTtl - 1];
            return null;
        }

        // Caller holds Lock.
        public int VisibleHops()
        {
            if (DestTtl > 0) return Math.Min(DestTtl, Hops.Count);
            return Math.Min(Hops.Count, Math.Max(Highest, 1) + 2);
        }

        public void Reset()
        {
            lock (Lock)
            {
                Epoch++; Round = 0; DestTtl = 0; Highest = 0;
                foreach (Hop h in Hops) h.Clear();
                if (Service != null) Service.Clear();
                ServiceInfo = ""; LastOutageEnd = 0;
            }
        }

        void Run()
        {
            Stopwatch clock = Stopwatch.StartNew();
            long next = 0;
            while (!Removed)
            {
                if (Address == null)
                {
                    string err;
                    IPAddress a = Engine.Resolve(Host, out err);
                    if (a == null)
                    {
                        Error = err;
                        for (int i = 0; i < 50 && !Removed; i++) Thread.Sleep(100);
                        continue;
                    }
                    lock (Lock) { Address = a; Error = ""; }
                    next = clock.ElapsedMilliseconds;
                }
                if (Paused) { Thread.Sleep(150); next = clock.ElapsedMilliseconds; continue; }
                long now = clock.ElapsedMilliseconds;
                if (Resched) { Resched = false; if (next > now) next = now; }
                if (now < next) { Thread.Sleep((int)Math.Min(100, next - now)); continue; }
                next += IntervalMs;
                if (next <= now) next = now + IntervalMs;   // never burst to catch up after a stall
                FireRound();
            }
        }

        void FireRound()
        {
            long t = Engine.NowMs();
            int max, epoch;
            IPAddress addr;
            lock (Lock)
            {
                Round++;
                if (DestTtl > 0) max = DestTtl;
                else if (Round <= 3 || Round % 30 == 0) max = Engine.MaxHops;          // full sweep
                else max = Math.Min(Engine.MaxHops, Math.Max(Highest, 1) + 2);       // route as far as it answers
                while (Hops.Count < max) { Hop h = new Hop(); h.Ttl = Hops.Count + 1; Hops.Add(h); }
                addr = Address; epoch = Epoch;
            }
            for (int ttl = 1; ttl <= max; ttl++)
            {
                ProbeJob j = new ProbeJob();
                j.Target = this; j.Address = addr; j.Ttl = ttl; j.T = t; j.Epoch = epoch;
                ThreadPool.QueueUserWorkItem(Probe, j);
            }
            if (Service != null)
            {
                // A web request per second would be rude to the server: at most one every 5 s.
                int gap = Kind == "http" ? Math.Max(IntervalMs, 5000) : IntervalMs;
                if (t - lastService >= gap - 50)
                {
                    lastService = t;
                    ProbeJob s = new ProbeJob();
                    s.Target = this; s.Address = addr; s.T = t; s.Epoch = epoch;
                    ThreadPool.QueueUserWorkItem(ProbeService, s);
                }
            }
        }

        static float Elapsed(long since)
        {
            return (float)((Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency);
        }

        static void ProbeService(object state)
        {
            ProbeJob j = (ProbeJob)state;
            Target t = j.Target;
            float rtt = -1f; string info = ""; int certDays = int.MinValue;
            long s = Stopwatch.GetTimestamp();
            if (t.Kind == "tcp")
            {
                try
                {
                    using (TcpClient c = new TcpClient(j.Address.AddressFamily))
                    {
                        IAsyncResult ar = c.BeginConnect(j.Address, t.Port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(Engine.TimeoutMs))
                        {
                            c.EndConnect(ar);
                            rtt = Elapsed(s); info = "port open";
                        }
                        else info = "no answer";
                    }
                }
                catch (SocketException ex)
                {
                    info = ex.SocketErrorCode == SocketError.ConnectionRefused ? "connection refused" : ex.SocketErrorCode.ToString();
                }
                catch (Exception) { info = "failed"; }
            }
            else
            {
                int code = 0; string err = ""; bool certBad = false; DateTime notAfter = DateTime.MinValue;
                try
                {
                    HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(t.Url);
                    rq.Method = "GET"; rq.Timeout = Engine.HttpTimeoutMs; rq.ReadWriteTimeout = Engine.HttpTimeoutMs;
                    rq.KeepAlive = false; rq.AllowAutoRedirect = false; rq.UserAgent = "PingSight";
                    rq.ServerCertificateValidationCallback = delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
                    {
                        try { if (cert != null) notAfter = new X509Certificate2(cert).NotAfter; } catch (Exception) { }
                        if (errors != SslPolicyErrors.None) certBad = true;
                        return errors == SslPolicyErrors.None;   // never accept a certificate Windows does not trust
                    };
                    try
                    {
                        using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse()) { code = (int)rs.StatusCode; }
                    }
                    catch (WebException ex)
                    {
                        HttpWebResponse rs = ex.Response as HttpWebResponse;
                        if (rs != null) { code = (int)rs.StatusCode; rs.Close(); }
                        else err = ex.Status.ToString();
                    }
                }
                catch (Exception ex) { err = ex.GetType().Name; }
                if (code > 0 && code < 500) { rtt = Elapsed(s); info = "HTTP " + code; }
                else if (code > 0) info = "HTTP " + code + " (server error)";
                else if (certBad) info = "certificate not trusted";
                else if (err == "Timeout") info = "no answer in " + (Engine.HttpTimeoutMs / 1000) + " s";
                else if (err == "NameResolutionFailure") info = "name not resolved";
                else if (err == "ConnectFailure") info = "connection failed";
                else info = err.Length > 0 ? err : "failed";
                if (notAfter != DateTime.MinValue) certDays = (int)Math.Floor((notAfter - DateTime.Now).TotalDays);
            }
            lock (t.Lock)
            {
                if (t.Removed || j.Epoch != t.Epoch || t.Service == null) return;
                t.Service.Add(j.T, rtt);
                t.ServiceInfo = info;
                if (certDays != int.MinValue) t.CertDays = certDays;
            }
        }

        static void Probe(object state)
        {
            ProbeJob j = (ProbeJob)state;
            float rtt = -1f; string from = null; IPStatus st = IPStatus.Unknown;
            try
            {
                using (Ping p = new Ping())
                {
                    PingOptions o = new PingOptions(j.Ttl, true);
                    long s = Stopwatch.GetTimestamp();
                    PingReply r = p.Send(j.Address, Engine.TimeoutMs, Engine.Payload, o);
                    long e = Stopwatch.GetTimestamp();
                    st = r.Status;
                    if (st == IPStatus.Success || st == IPStatus.TtlExpired || st == IPStatus.TimeExceeded)
                    {
                        // .NET reports 0 ms for "TTL expired" replies, so time it ourselves.
                        rtt = (float)((e - s) * 1000.0 / Stopwatch.Frequency);
                        if (r.Address != null) from = r.Address.ToString();
                    }
                }
            }
            catch (Exception) { }   // network down, adapter gone, ... = a lost probe
            j.Target.Record(j, st, rtt, from);
        }

        void Record(ProbeJob j, IPStatus st, float rtt, string from)
        {
            lock (Lock)
            {
                if (Removed || j.Epoch != Epoch) return;
                if (st == IPStatus.Success)
                {
                    if (DestTtl == 0 || j.Ttl < DestTtl)
                    {
                        DestTtl = j.Ttl; Highest = j.Ttl;
                        for (int i = DestTtl; i < Hops.Count; i++) Hops[i].Clear();
                    }
                }
                else if (st == IPStatus.TtlExpired || st == IPStatus.TimeExceeded)
                {
                    if (DestTtl > 0 && j.Ttl >= DestTtl) DestTtl = 0;   // route got longer: rediscover
                    if (j.Ttl > Highest) Highest = j.Ttl;
                }
                if (DestTtl > 0 && j.Ttl > DestTtl) return;             // probe went past the destination
                if (j.Ttl > Hops.Count) return;
                Hop h = Hops[j.Ttl - 1];
                h.Add(j.T, rtt);
                if (from != null)
                {
                    h.Ip = from;
                    int c; h.Ips.TryGetValue(from, out c); h.Ips[from] = c + 1;
                }
            }
            if (from != null) Engine.EnsureName(from);
        }
    }

    public static class Engine
    {
        public const int MaxHops = 30;
        public const int MaxSamples = 120000;      // per hop (~33 h at one probe per second)
        public const int MaxTargets = 24;
        public static int TimeoutMs = 2000;
        public static int HttpTimeoutMs = 10000;
        public static readonly byte[] Payload = new byte[32];

        static readonly DateTime Epoch1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly List<Target> targets = new List<Target>();
        static readonly object gate = new object();
        static readonly Dictionary<string, string> names = new Dictionary<string, string>();
        static int nextId = 1;
        static string configPath;
        static List<string> gateways = new List<string>();
        static long gatewaysAt;

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch1970).TotalMilliseconds; }
        public static DateTime FromMs(long ms) { return Epoch1970.AddMilliseconds(ms).ToLocalTime(); }

        public static IPAddress Resolve(string host, out string err)
        {
            err = "";
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return ip;
            try
            {
                IPAddress[] all = Dns.GetHostAddresses(host);
                foreach (IPAddress a in all) if (a.AddressFamily == AddressFamily.InterNetwork) return a;
                if (all.Length > 0) return all[0];
                err = "Name has no address";
            }
            catch (Exception) { err = "Cannot resolve this name"; }
            return null;
        }

        // Understands "host", "host:port" (adds a TCP port check) and "http(s)://..." (adds a web check).
        public static Spec ParseSpec(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0 || s.Length > 400) return null;
            Spec sp = new Spec();
            string low = s.ToLowerInvariant();
            if (low.StartsWith("http://") || low.StartsWith("https://"))
            {
                Uri u;
                if (!Uri.TryCreate(s, UriKind.Absolute, out u) || u.Host.Length == 0) return null;
                sp.Kind = "http"; sp.Host = u.DnsSafeHost.ToLowerInvariant(); sp.Port = u.Port;
                sp.Url = u.GetLeftPart(UriPartial.Query);
                sp.Label = u.Scheme == "https" ? "HTTPS" : "HTTP";
                sp.Name = u.AbsolutePath == "/" && u.Query.Length == 0 ? u.Scheme + "://" + u.Authority.ToLowerInvariant() : sp.Url;
            }
            else
            {
                int i = s.IndexOf("://", StringComparison.Ordinal);
                if (i >= 0) s = s.Substring(i + 3);
                i = s.IndexOfAny(new char[] { '/', '?', '#' });
                if (i >= 0) s = s.Substring(0, i);
                string host = s; int port = 0;
                if (s.StartsWith("["))
                {
                    int e = s.IndexOf(']');
                    if (e < 0) return null;
                    host = s.Substring(1, e - 1);
                    string rest = s.Substring(e + 1);
                    if (rest.StartsWith(":") && (!int.TryParse(rest.Substring(1), out port) || port < 1 || port > 65535)) return null;
                }
                else
                {
                    i = s.IndexOf(':');
                    if (i >= 0 && i == s.LastIndexOf(':'))
                    {
                        host = s.Substring(0, i);
                        if (!int.TryParse(s.Substring(i + 1), out port) || port < 1 || port > 65535) return null;
                    }
                }
                sp.Host = host.Trim().ToLowerInvariant();
                if (port > 0)
                {
                    sp.Kind = "tcp"; sp.Port = port; sp.Label = "TCP " + port;
                    sp.Name = (sp.Host.IndexOf(':') >= 0 ? "[" + sp.Host + "]" : sp.Host) + ":" + port;
                }
                else sp.Name = sp.Host;
            }
            if (sp.Host.Length == 0 || sp.Host.Length > 253 || Uri.CheckHostName(sp.Host) == UriHostNameType.Unknown) return null;
            return sp;
        }

        public static Target Add(string input, int intervalMs, bool paused, out string err)
        {
            err = "";
            Spec sp = ParseSpec(input);
            if (sp == null)
            {
                err = "Not a valid host name, IP address, host:port or web address";
                return null;
            }
            Target t;
            lock (gate)
            {
                foreach (Target x in targets) if (x.Name == sp.Name) return x;
                if (targets.Count >= MaxTargets) { err = "Target limit reached (" + MaxTargets + ")"; return null; }
                t = new Target();
                t.Id = nextId++; t.Name = sp.Name; t.Host = sp.Host; t.Paused = paused;
                t.Kind = sp.Kind; t.Port = sp.Port; t.Url = sp.Url; t.ServiceLabel = sp.Label;
                if (sp.Kind.Length > 0) t.Service = new Hop();
                t.IntervalMs = ClampInterval(intervalMs);
                targets.Add(t);
            }
            if (LogDir != null) RestoreTarget(t);
            t.Start();
            Save();
            return t;
        }

        public static int ClampInterval(int ms) { return Math.Max(250, Math.Min(600000, ms)); }

        public static Target Find(int id)
        {
            lock (gate) { foreach (Target t in targets) if (t.Id == id) return t; }
            return null;
        }

        public static List<Target> Snapshot()
        {
            lock (gate) { return new List<Target>(targets); }
        }

        public static bool Remove(int id)
        {
            Target t = Find(id);
            if (t == null) return false;
            t.Removed = true;
            lock (gate) { targets.Remove(t); }
            Save();
            return true;
        }

        public static void Load(string path)
        {
            configPath = path;
            int wt, io;
            ThreadPool.GetMinThreads(out wt, out io);
            ThreadPool.SetMinThreads(Math.Max(wt, 256), io);
            if (!File.Exists(path)) return;
            string keep = configPath; configPath = null;   // do not rewrite the file while reading it
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 1 || p[0].Trim().Length == 0) continue;
                    int iv = 1000; bool paused = false; string err;
                    if (p.Length > 1) int.TryParse(p[1], out iv);
                    if (p.Length > 2) paused = p[2] == "1";
                    Add(p[0], iv, paused, out err);
                }
            }
            catch (Exception) { }
            configPath = keep;
        }

        public static void Save()
        {
            string path = configPath;
            if (path == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (Target t in Snapshot())
                    sb.Append(t.Name).Append('\t').Append(t.IntervalMs).Append('\t').Append(t.Paused ? "1" : "0").Append("\r\n");
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception) { }
        }

        // ---------- results on disk ----------
        // One CSV per target per day; one row per hop per period (default: a minute).
        // Written in the PC's regional format so the files open cleanly in the local Excel.

        public static string LogDir;
        public static int LogStepMs = 60000;
        public static string LogError = "";
        static long logFrom;
        static readonly object logGate = new object();
        static readonly Dictionary<string, StringBuilder> logPending = new Dictionary<string, StringBuilder>();
        static readonly Dictionary<string, string> logHeaders = new Dictionary<string, string>();
        const string LogColumns = "time;target;hop;ip;hostname;destination;sent;lost;loss_pct;min_ms;avg_ms;max_ms;jitter_ms";

        public static void StartLog(string dir, int seconds)
        {
            lock (logGate)
            {
                LogError = "";
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { LogError = "Cannot create " + dir + ": " + ex.Message; }
                LogStepMs = Math.Max(10, Math.Min(3600, seconds)) * 1000;
                logFrom = NowMs() / LogStepMs * LogStepMs;
                bool first = LogDir == null;
                LogDir = dir;
                if (!first) return;
            }
            Thread th = new Thread(LogLoop);
            th.IsBackground = true;
            th.Name = "pingsight-log";
            th.Start();
        }

        static void LogLoop()
        {
            while (true)
            {
                Thread.Sleep(1000);
                try
                {
                    lock (logGate)
                    {
                        long now = NowMs();
                        if (now - logFrom > 6 * 3600000L) logFrom = now / LogStepMs * LogStepMs;   // PC was asleep
                        // Wait out the probe timeout so late "no reply" results are counted.
                        if (logPending.Count > 0) FlushPending();
                        while (logFrom + LogStepMs + HttpTimeoutMs + 500 <= now)
                        {
                            WritePeriod(logFrom, logFrom + LogStepMs);
                            logFrom += LogStepMs;
                        }
                    }
                }
                catch (Exception ex) { LogError = ex.Message; }
            }
        }

        // Writes the part of the current period that has not been saved yet (called on shutdown).
        public static void FlushLog()
        {
            if (LogDir == null) return;
            try
            {
                lock (logGate)
                {
                    long now = NowMs();
                    if (now > logFrom) { WritePeriod(logFrom, now); logFrom = now; }
                }
            }
            catch (Exception) { }
        }

        public static string CsvText(string s, string sep)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace(sep, " ").Replace("\"", "").Replace("\r", " ").Replace("\n", " ");
            if ("=+-@".IndexOf(s[0]) >= 0) s = "'" + s;   // never let a host name run as a spreadsheet formula
            return s;
        }

        internal static string SafeName(string host)
        {
            StringBuilder sb = new StringBuilder(host);
            foreach (char bad in Path.GetInvalidFileNameChars()) sb.Replace(bad, '-');
            return sb.ToString();
        }

        static void AppendRow(StringBuilder sb, string stamp, string sep, CultureInfo ci, string target, int hop, string ip, string name, bool dest, Stats st)
        {
            int got = st.Sent - st.Lost;
            sb.Append(stamp).Append(sep).Append(target).Append(sep).Append(hop).Append(sep);
            sb.Append(ip).Append(sep).Append(CsvText(name, sep)).Append(sep).Append(dest ? "1" : "0").Append(sep);
            sb.Append(st.Sent).Append(sep).Append(st.Lost).Append(sep);
            sb.Append((st.Lost * 100.0 / st.Sent).ToString("0.0", ci)).Append(sep);
            if (got > 0)
            {
                sb.Append(st.Min.ToString("0.00", ci)).Append(sep);
                sb.Append((st.Sum / got).ToString("0.00", ci)).Append(sep);
                sb.Append(st.Max.ToString("0.00", ci)).Append(sep);
            }
            else sb.Append(sep).Append(sep).Append(sep);
            if (st.JitN > 0) sb.Append((st.JitSum / st.JitN).ToString("0.00", ci));
            sb.Append("\r\n");
        }

        static string TargetFile(Target t, DateTime day)
        {
            return Path.Combine(LogDir, SafeName(t.Name) + "_" + day.ToString("yyyy-MM-dd") + ".csv");
        }

        static void WritePeriod(long from, long to)
        {
            CultureInfo ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            DateTime start = FromMs(from);
            string stamp = start.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (Target t in Snapshot())
            {
                StringBuilder sb = new StringBuilder();
                lock (t.Lock)
                {
                    string name = CsvText(t.Name, sep);
                    int vis = t.VisibleHops();
                    for (int i = 0; i < vis; i++)
                    {
                        Hop h = t.Hops[i];
                        Stats st = Server.Compute(h.Samples, from, to);
                        if (st.Sent > 0) AppendRow(sb, stamp, sep, ci, name, h.Ttl, h.Ip, NameOf(h.Ip), t.DestTtl == h.Ttl, st);
                    }
                    if (t.Service != null)
                    {
                        // The service check is saved as "hop 0".
                        Stats st = Server.Compute(t.Service.Samples, from, to);
                        if (st.Sent > 0) AppendRow(sb, stamp, sep, ci, name, 0, "", t.ServiceLabel, false, st);
                    }
                }
                if (sb.Length > 0) QueueCsv(TargetFile(t, start), LogColumns.Replace(";", sep), sb.ToString());
            }
            FlushPending();
        }

        // Caller holds logGate.
        static void QueueCsv(string path, string header, string rows)
        {
            StringBuilder p;
            if (!logPending.TryGetValue(path, out p)) { p = new StringBuilder(); logPending[path] = p; }
            logHeaders[path] = header;
            if (p.Length < 8000000) p.Append(rows);
        }

        // A file that is open in Excel cannot be appended to: its rows are kept and retried.
        // Caller holds logGate.
        static void FlushPending()
        {
            string failed = "";
            foreach (string path in new List<string>(logPending.Keys))
            {
                try
                {
                    bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
                    using (StreamWriter w = new StreamWriter(path, true, new UTF8Encoding(true)))
                    {
                        if (fresh) w.WriteLine(logHeaders[path]);
                        w.Write(logPending[path].ToString());
                    }
                    logPending.Remove(path);
                }
                catch (Exception)
                {
                    failed = "Cannot write " + Path.GetFileName(path) + " right now (is it open in Excel?). Its rows are kept and will be added once the file is free.";
                }
            }
            LogError = failed;
        }

        // For the other result files (events, speed tests). "header" uses ';' and is localised here.
        internal static void AppendCsv(string file, string header, string rows)
        {
            if (LogDir == null) return;
            string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            lock (logGate)
            {
                QueueCsv(Path.Combine(LogDir, file), header.Replace(";", sep), rows);
                FlushPending();
            }
        }

        internal static long ToMs(DateTime local) { return (long)(local.ToUniversalTime() - Epoch1970).TotalMilliseconds; }

        internal static string[] ReadShared(string path)
        {
            List<string> lines = new List<string>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null) lines.Add(line);
            }
            return lines.ToArray();
        }

        // Rebuilds the last 24 hours of a target's graphs from its saved CSV files. The files hold
        // one summary row per hop per period, so the restored detail is one period (a minute by default):
        // probes sent and lost, and the fastest, average and slowest reply, are exact for each period.
        static void RestoreTarget(Target t)
        {
            CultureInfo ci = CultureInfo.CurrentCulture;
            string[] sep = new string[] { ci.TextInfo.ListSeparator };
            long cutoff = NowMs() - 24 * 3600000L;
            for (int d = 1; d >= 0; d--)
            {
                string path = TargetFile(t, DateTime.Now.AddDays(-d));
                if (!File.Exists(path)) continue;
                string[] lines;
                try { lines = ReadShared(path); } catch (Exception) { continue; }
                lock (t.Lock)
                {
                    foreach (string line in lines)
                    {
                        string[] p = line.Split(sep, StringSplitOptions.None);
                        if (p.Length < 12) continue;
                        DateTime dt; int ttl, sent, lost;
                        if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) continue;
                        if (!int.TryParse(p[2], out ttl) || !int.TryParse(p[6], out sent) || !int.TryParse(p[7], out lost)) continue;
                        if (ttl < 0 || ttl > MaxHops || sent <= 0 || sent > 100000 || lost < 0 || lost > sent) continue;
                        long T = ToMs(dt);
                        if (T < cutoff) continue;
                        double mn = 0, av = 0, mx = 0;
                        if (lost < sent && (!double.TryParse(p[9], NumberStyles.Float, ci, out mn) || !double.TryParse(p[10], NumberStyles.Float, ci, out av) || !double.TryParse(p[11], NumberStyles.Float, ci, out mx))) continue;
                        Hop h;
                        if (ttl == 0) { h = t.Service; if (h == null) continue; }
                        else
                        {
                            while (t.Hops.Count < ttl) { Hop n = new Hop(); n.Ttl = t.Hops.Count + 1; t.Hops.Add(n); }
                            h = t.Hops[ttl - 1];
                            if (p[3].Length > 0) { h.Ip = p[3]; if (!h.Ips.ContainsKey(p[3])) h.Ips[p[3]] = 1; }
                            if (ttl > t.Highest && lost < sent) t.Highest = ttl;
                            if (p[5] == "1") t.DestTtl = ttl;
                        }
                        Synthesize(h, T, sent, lost, mn, av, mx);
                    }
                }
            }
            lock (t.Lock)
            {
                // Hops past the last known destination belong to an older, longer route.
                for (int i = t.DestTtl > 0 ? t.DestTtl : t.Hops.Count; i < t.Hops.Count; i++) t.Hops[i].Clear();
            }
            foreach (Hop h in t.Hops.ToArray()) if (h.Ip.Length > 0) EnsureName(h.Ip);
        }

        // Spreads one saved period back into samples that reproduce its sent / lost / min / avg / max.
        static void Synthesize(Hop h, long T, int sent, int lost, double mn, double av, double mx)
        {
            int got = sent - lost;
            double rest = got > 2 ? Math.Max(mn, Math.Min(mx, (av * got - mn - mx) / (got - 2))) : av;
            int lo = got / 3, hi = (2 * got) / 3;
            if (got == 2) { lo = 0; hi = 1; }
            double spacing = LogStepMs / (double)sent;
            int seen = 0;
            for (int k = 0; k < sent; k++)
            {
                bool isLost = (long)(k + 1) * lost / sent > (long)k * lost / sent;
                float v = -1f;
                if (!isLost)
                {
                    v = (float)(got == 1 ? av : seen == lo ? mn : seen == hi ? mx : rest);
                    seen++;
                }
                h.Add(T + (long)(k * spacing), v);
            }
        }

        public static void EnsureName(string ip)
        {
            lock (names)
            {
                if (names.ContainsKey(ip)) return;
                names[ip] = "";
            }
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                string n = "";
                try
                {
                    n = Dns.GetHostEntry(IPAddress.Parse(ip)).HostName;
                    if (n == null || n == ip) n = "";
                }
                catch (Exception) { n = ""; }
                lock (names) { names[ip] = n; }
            });
        }

        public static string NameOf(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            lock (names) { string n; return names.TryGetValue(ip, out n) ? n : ""; }
        }

        public static List<string> Gateways()
        {
            long now = NowMs();
            if (now - gatewaysAt < 15000) return gateways;
            List<string> g = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = gw.Address.ToString();
                        if (s != "0.0.0.0" && !g.Contains(s)) g.Add(s);
                    }
                }
            }
            catch (Exception) { }
            gateways = g; gatewaysAt = now;
            return g;
        }
    }

    internal struct Stats
    {
        public int Sent, Lost, JitN;
        public double Min, Max, Sum, JitSum;
        public float Last;
        public bool Any;
    }

    public static class Server
    {
        static HttpListener listener;
        static string root;
        static int port;

        public static void Start(int p, string dir)
        {
            HttpListener l = new HttpListener();
            l.Prefixes.Add("http://localhost:" + p + "/");
            l.Start();   // throws when the port is taken
            listener = l; port = p; root = dir;
            Thread th = new Thread(Loop);
            th.IsBackground = true;
            th.Name = "pingsight-http";
            th.Start();
        }

        public static void Stop()
        {
            try { if (listener != null) listener.Close(); } catch (Exception) { }
            listener = null;
        }

        static void Loop()
        {
            HttpListener l = listener;
            while (l != null && l.IsListening)
            {
                HttpListenerContext c;
                try { c = l.GetContext(); } catch (Exception) { break; }
                ThreadPool.QueueUserWorkItem(Handle, c);
            }
        }

        static bool LocalName(string s, string prefix)
        {
            return s == prefix + "localhost:" + port || s == prefix + "127.0.0.1:" + port;
        }

        static void Handle(object o)
        {
            HttpListenerContext c = (HttpListenerContext)o;
            try
            {
                HttpListenerRequest rq = c.Request;
                string path = rq.Url.AbsolutePath;
                NameValueCollection q = rq.QueryString;
                string origin = rq.Headers["Origin"];
                // Only this machine's own dashboard may talk to the engine.
                if (!LocalName(rq.Headers["Host"] ?? "", "") || (origin != null && !LocalName(origin, "http://")))
                {
                    Send(c, 403, "text/plain", "Forbidden");
                    return;
                }
                bool post = rq.HttpMethod == "POST";
                if (path == "/" || path == "/index.html")
                {
                    Send(c, 200, "text/html; charset=utf-8", File.ReadAllText(Path.Combine(root, "dashboard.html"), Encoding.UTF8));
                }
                else if (path == "/api/state")
                {
                    Send(c, 200, "application/json; charset=utf-8", BuildState(q));
                }
                else if (path == "/api/extras")
                {
                    Send(c, 200, "application/json; charset=utf-8", Extras.Build(q));
                }
                else if (path == "/api/export")
                {
                    Export(c, q);
                }
                else if (!post)
                {
                    Send(c, 404, "text/plain", "Not found");
                }
                else if (path == "/api/add")
                {
                    string err;
                    Target t = Engine.Add(q["host"], Int(q["interval"], 1000), false, out err);
                    if (t == null) Send(c, 200, "application/json", "{\"ok\":false,\"error\":" + Q(err) + "}");
                    else Send(c, 200, "application/json", "{\"ok\":true,\"id\":" + t.Id + ",\"host\":" + Q(t.Name) + "}");
                }
                else if (path == "/api/speed/run")
                {
                    Ok(c, SpeedTest.Start());
                }
                else if (path == "/api/settings")
                {
                    foreach (string key in q.AllKeys) if (key != null) Settings.Set(key, q[key]);
                    Settings.Save();
                    Ok(c, true);
                }
                else if (path == "/api/remove")
                {
                    Ok(c, Engine.Remove(Int(q["id"], 0)));
                }
                else if (path == "/api/pause")
                {
                    Target t = Engine.Find(Int(q["id"], 0));
                    if (t != null) { t.Paused = q["paused"] == "1"; Engine.Save(); }
                    Ok(c, t != null);
                }
                else if (path == "/api/interval")
                {
                    Target t = Engine.Find(Int(q["id"], 0));
                    if (t != null) { t.IntervalMs = Engine.ClampInterval(Int(q["ms"], 1000)); t.Resched = true; Engine.Save(); }
                    Ok(c, t != null);
                }
                else if (path == "/api/reset")
                {
                    Target t = Engine.Find(Int(q["id"], 0));
                    if (t != null) t.Reset();
                    Ok(c, t != null);
                }
                else
                {
                    Send(c, 404, "text/plain", "Not found");
                }
            }
            catch (Exception ex)
            {
                try { Send(c, 500, "text/plain; charset=utf-8", ex.Message); } catch (Exception) { }
            }
            finally
            {
                try { c.Response.Close(); } catch (Exception) { }
            }
        }

        static void Ok(HttpListenerContext c, bool ok)
        {
            Send(c, 200, "application/json", ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"Target not found\"}");
        }

        static void Send(HttpListenerContext c, int code, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = code;
            c.Response.ContentType = type;
            c.Response.Headers["Cache-Control"] = "no-store";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b, 0, b.Length);
        }

        internal static int Int(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        // ---------- JSON ----------

        internal static string Q(string s)
        {
            StringBuilder sb = new StringBuilder();
            Str(sb, s);
            return sb.ToString();
        }

        internal static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char ch in s)
                {
                    if (ch == '"') sb.Append("\\\"");
                    else if (ch == '\\') sb.Append("\\\\");
                    else if (ch < 0x20 || ch == '<' || ch == '>' || ch == '&' || ch > 0x7e)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                }
            }
            sb.Append('"');
        }

        internal static void Num(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append("null");
            else sb.Append(v.ToString("0.##", CultureInfo.InvariantCulture));
        }

        internal static int LowerBound(List<Sample> s, long t)
        {
            int lo = 0, hi = s.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (s[mid].T < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        internal static Stats Compute(List<Sample> s, long from) { return Compute(s, from, long.MaxValue); }

        internal static Stats Compute(List<Sample> s, long from, long to)
        {
            Stats st = new Stats();
            st.Min = double.MaxValue; st.Last = -1f;
            float prev = -1f;
            for (int i = LowerBound(s, from); i < s.Count && s[i].T < to; i++)
            {
                float r = s[i].Rtt;
                st.Sent++; st.Any = true; st.Last = r;
                if (r < 0) { st.Lost++; prev = -1f; continue; }
                if (r < st.Min) st.Min = r;
                if (r > st.Max) st.Max = r;
                st.Sum += r;
                if (prev >= 0) { st.JitSum += Math.Abs(r - prev); st.JitN++; }
                prev = r;
            }
            return st;
        }

        internal static void AppendStats(StringBuilder sb, Stats st)
        {
            int got = st.Sent - st.Lost;
            sb.Append("\"sent\":").Append(st.Sent).Append(",\"lost\":").Append(st.Lost);
            sb.Append(",\"min\":"); Num(sb, got > 0 ? st.Min : double.NaN);
            sb.Append(",\"avg\":"); Num(sb, got > 0 ? st.Sum / got : double.NaN);
            sb.Append(",\"max\":"); Num(sb, got > 0 ? st.Max : double.NaN);
            sb.Append(",\"last\":"); Num(sb, st.Any && st.Last >= 0 ? st.Last : double.NaN);
            sb.Append(",\"jitter\":"); Num(sb, st.JitN > 0 ? st.JitSum / st.JitN : double.NaN);
        }

        internal static void AppendSeries(StringBuilder sb, List<Sample> s, long from, long step, int n)
        {
            int[] sent = new int[n]; int[] lost = new int[n];
            double[] sum = new double[n]; float[] mn = new float[n]; float[] mx = new float[n];
            for (int i = LowerBound(s, from); i < s.Count; i++)
            {
                int b = (int)((s[i].T - from) / step);
                if (b < 0 || b >= n) continue;
                float r = s[i].Rtt;
                if (r < 0) { sent[b]++; lost[b]++; continue; }
                if (sent[b] == lost[b]) { mn[b] = r; mx[b] = r; }
                else { if (r < mn[b]) mn[b] = r; if (r > mx[b]) mx[b] = r; }
                sent[b]++; sum[b] += r;
            }
            sb.Append("{\"sent\":[");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(sent[i]); }
            sb.Append("],\"lost\":[");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(lost[i]); }
            sb.Append("],\"avg\":[");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); int g = sent[i] - lost[i]; Num(sb, g > 0 ? sum[i] / g : double.NaN); }
            sb.Append("],\"min\":[");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); Num(sb, sent[i] > lost[i] ? mn[i] : double.NaN); }
            sb.Append("],\"max\":[");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); Num(sb, sent[i] > lost[i] ? mx[i] : double.NaN); }
            sb.Append("]}");
        }

        static long WindowStart(Target t, long now, int windowSec)
        {
            if (windowSec > 0) return now - windowSec * 1000L;
            long first = now - 60000;   // "all": back to the oldest sample
            foreach (Hop h in t.Hops) if (h.Samples.Count > 0 && h.Samples[0].T < first) first = h.Samples[0].T;
            return first;
        }

        static string BuildState(NameValueCollection q)
        {
            int selId = Int(q["id"], 0);
            int window = Math.Max(0, Math.Min(7 * 86400, Int(q["window"], 600)));
            int points = Math.Max(20, Math.Min(1000, Int(q["points"], 300)));
            List<int> plot = new List<int>();
            foreach (string part in (q["plot"] ?? "").Split(','))
            {
                int v;
                if (int.TryParse(part, out v) && plot.Count < 12) plot.Add(v);
            }

            long now = Engine.NowMs();
            StringBuilder sb = new StringBuilder(32768);
            sb.Append("{\"v\":2,\"now\":").Append(now).Append(",\"timeout\":").Append(Engine.TimeoutMs);
            sb.Append(",\"speedRunning\":").Append(SpeedTest.Running ? "true" : "false");
            sb.Append(",\"events24\":").Append(Events.CountSince(now - 24 * 3600000L));
            sb.Append(",\"settings\":"); Settings.AppendJson(sb);
            sb.Append(",\"gateways\":[");
            bool first = true;
            foreach (string g in Engine.Gateways()) { if (!first) sb.Append(','); first = false; Str(sb, g); }
            sb.Append("],\"log\":");
            if (Engine.LogDir == null) sb.Append("null");
            else
            {
                sb.Append("{\"dir\":"); Str(sb, Engine.LogDir);
                sb.Append(",\"every\":").Append(Engine.LogStepMs / 1000);
                sb.Append(",\"error\":"); Str(sb, Engine.LogError); sb.Append('}');
            }
            sb.Append(",\"targets\":[");

            List<Target> all = Engine.Snapshot();
            first = true;
            foreach (Target t in all)
            {
                if (!first) sb.Append(','); first = false;
                lock (t.Lock)
                {
                    long from = WindowStart(t, now, window);
                    sb.Append("{\"id\":").Append(t.Id).Append(",\"host\":"); Str(sb, t.Name);
                    sb.Append(",\"svc\":"); Str(sb, t.ServiceLabel);
                    sb.Append(",\"svcInfo\":"); Str(sb, t.ServiceInfo);
                    sb.Append(",\"kind\":"); Str(sb, t.Kind);
                    sb.Append(",\"certDays\":"); if (t.CertDays == int.MinValue) sb.Append("null"); else sb.Append(t.CertDays);
                    sb.Append(",\"event\":"); Str(sb, t.OpenOutage != null ? "outage" : t.OpenLoss != null ? "loss" : t.OpenLatency != null ? "latency" : "");
                    sb.Append(",\"ip\":"); Str(sb, t.Address == null ? "" : t.Address.ToString());
                    sb.Append(",\"error\":"); Str(sb, t.Error);
                    sb.Append(",\"interval\":").Append(t.IntervalMs);
                    sb.Append(",\"paused\":").Append(t.Paused ? "true" : "false");
                    sb.Append(",\"destTtl\":").Append(t.DestTtl);
                    sb.Append(",\"rounds\":").Append(t.Round).Append(',');
                    Hop health = t.HealthHop();
                    Stats st = health != null ? Compute(health.Samples, from) : new Stats();
                    AppendStats(sb, st);
                    sb.Append('}');
                }
            }
            sb.Append("],\"detail\":");

            Target sel = null;
            foreach (Target t in all) if (t.Id == selId) sel = t;
            if (sel == null) sb.Append("null");
            else
            {
                lock (sel.Lock)
                {
                    long from = WindowStart(sel, now, window);
                    long iv = Math.Max(250, sel.IntervalMs);
                    long step = Math.Max(iv, (long)Math.Ceiling((now - from) / (double)points));
                    step = (step + iv - 1) / iv * iv;                 // whole probe intervals per bucket
                    long fromA = from / step * step;                    // stable bucket edges between polls
                    int n = (int)((now - fromA) / step) + 1;
                    int vis = sel.VisibleHops();
                    sb.Append("{\"id\":").Append(sel.Id).Append(",\"from\":").Append(fromA);
                    sb.Append(",\"step\":").Append(step).Append(",\"n\":").Append(n).Append(",\"hops\":[");
                    for (int i = 0; i < vis; i++)
                    {
                        Hop h = sel.Hops[i];
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"ttl\":").Append(h.Ttl).Append(",\"ip\":"); Str(sb, h.Ip);
                        sb.Append(",\"name\":"); Str(sb, Engine.NameOf(h.Ip));
                        sb.Append(",\"alt\":").Append(Math.Max(0, h.Ips.Count - 1)).Append(',');
                        AppendStats(sb, Compute(h.Samples, from));
                        if (i == vis - 1 || plot.Contains(h.Ttl))
                        {
                            sb.Append(",\"series\":");
                            AppendSeries(sb, h.Samples, fromA, step, n);
                        }
                        sb.Append('}');
                    }
                    sb.Append("],\"service\":");
                    if (sel.Service == null) sb.Append("null");
                    else
                    {
                        sb.Append("{\"label\":"); Str(sb, sel.ServiceLabel);
                        sb.Append(",\"info\":"); Str(sb, sel.ServiceInfo); sb.Append(',');
                        AppendStats(sb, Compute(sel.Service.Samples, from));
                        sb.Append(",\"series\":");
                        AppendSeries(sb, sel.Service.Samples, fromA, step, n);
                        sb.Append('}');
                    }
                    sb.Append(",\"events\":"); Events.AppendJson(sb, sel.Name, 30);
                    sb.Append('}');
                }
            }
            sb.Append('}');
            return sb.ToString();
        }

        // CSV in the PC's own regional format so it opens cleanly in the local Excel.
        static void Export(HttpListenerContext c, NameValueCollection q)
        {
            Target t = Engine.Find(Int(q["id"], 0));
            if (t == null) { Send(c, 404, "text/plain", "Target not found"); return; }
            int window = Math.Max(0, Int(q["window"], 0));
            long now = Engine.NowMs();
            List<object[]> hops = new List<object[]>();
            lock (t.Lock)
            {
                long from = WindowStart(t, now, window);
                int vis = t.VisibleHops();
                for (int i = 0; i < vis; i++)
                {
                    Hop h = t.Hops[i];
                    int a = LowerBound(h.Samples, from);
                    hops.Add(new object[] { h.Ttl, h.Ip, Engine.NameOf(h.Ip), h.Samples.GetRange(a, h.Samples.Count - a).ToArray() });
                }
            }
            CultureInfo ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            foreach (object[] h in hops) h[2] = Engine.CsvText((string)h[2], sep);
            string safe = Engine.SafeName(t.Name);
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/csv; charset=utf-8";
            c.Response.Headers["Content-Disposition"] = "attachment; filename=\"pingsight-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv\"";
            c.Response.SendChunked = true;
            using (StreamWriter w = new StreamWriter(c.Response.OutputStream, new UTF8Encoding(true)))
            {
                w.WriteLine("time" + sep + "target" + sep + "hop" + sep + "ip" + sep + "hostname" + sep + "latency_ms" + sep + "lost");
                foreach (object[] h in hops)
                {
                    string head = sep + Engine.CsvText(t.Name, sep) + sep + h[0] + sep + h[1] + sep + h[2] + sep;
                    foreach (Sample s in (Sample[])h[3])
                    {
                        w.Write(Engine.FromMs(s.T).ToString("yyyy-MM-dd HH:mm:ss"));
                        w.Write(head);
                        if (s.Rtt >= 0) { w.Write(s.Rtt.ToString("0.00", ci)); w.Write(sep); w.WriteLine("0"); }
                        else { w.Write(sep); w.WriteLine("1"); }
                    }
                }
            }
        }
    }
}
