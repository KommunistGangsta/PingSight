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
using System.Net.Sockets;
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

    internal sealed class ProbeJob
    {
        public Target Target; public IPAddress Address; public int Ttl; public long T; public int Epoch;
    }

    public sealed class Target
    {
        public int Id;
        public string Host;
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

        public static string CleanHost(string h)
        {
            if (h == null) return "";
            h = h.Trim();
            int i = h.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) h = h.Substring(i + 3);
            i = h.IndexOfAny(new char[] { '/', '?', '#' });
            if (i >= 0) h = h.Substring(0, i);
            i = h.IndexOf(':');
            if (i >= 0 && i == h.LastIndexOf(':')) h = h.Substring(0, i);   // host:port
            return h.Trim('[', ']', ' ').ToLowerInvariant();
        }

        public static Target Add(string host, int intervalMs, bool paused, out string err)
        {
            err = "";
            host = CleanHost(host);
            if (host.Length == 0 || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                err = "That is not a valid host name or IP address";
                return null;
            }
            Target t;
            lock (gate)
            {
                foreach (Target x in targets) if (x.Host == host) return x;
                if (targets.Count >= MaxTargets) { err = "Target limit reached (" + MaxTargets + ")"; return null; }
                t = new Target();
                t.Id = nextId++; t.Host = host; t.Paused = paused;
                t.IntervalMs = ClampInterval(intervalMs);
                targets.Add(t);
            }
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
                    sb.Append(t.Host).Append('\t').Append(t.IntervalMs).Append('\t').Append(t.Paused ? "1" : "0").Append("\r\n");
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
                        while (logFrom + LogStepMs + TimeoutMs + 500 <= now)
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

        static string SafeName(string host)
        {
            StringBuilder sb = new StringBuilder(host);
            foreach (char bad in Path.GetInvalidFileNameChars()) sb.Replace(bad, '-');
            return sb.ToString();
        }

        static void WritePeriod(long from, long to)
        {
            CultureInfo ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            DateTime start = FromMs(from);
            string stamp = start.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (Target t in Snapshot())
            {
                StringBuilder sb = null;
                lock (t.Lock)
                {
                    int vis = t.VisibleHops();
                    for (int i = 0; i < vis; i++)
                    {
                        Hop h = t.Hops[i];
                        Stats st = Server.Compute(h.Samples, from, to);
                        if (st.Sent == 0) continue;
                        if (sb == null) sb = new StringBuilder();
                        int got = st.Sent - st.Lost;
                        sb.Append(stamp).Append(sep).Append(t.Host).Append(sep).Append(h.Ttl).Append(sep);
                        sb.Append(h.Ip).Append(sep).Append(CsvText(NameOf(h.Ip), sep)).Append(sep);
                        sb.Append(t.DestTtl == h.Ttl ? "1" : "0").Append(sep);
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
                }
                if (sb == null) continue;
                string path = Path.Combine(LogDir, SafeName(t.Host) + "_" + start.ToString("yyyy-MM-dd") + ".csv");
                StringBuilder p;
                if (!logPending.TryGetValue(path, out p)) { p = new StringBuilder(); logPending[path] = p; }
                if (p.Length < 8000000) p.Append(sb);
            }

            // A file that is open in Excel cannot be appended to: keep its rows and retry next period.
            string failed = "";
            foreach (string path in new List<string>(logPending.Keys))
            {
                try
                {
                    bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
                    using (StreamWriter w = new StreamWriter(path, true, new UTF8Encoding(true)))
                    {
                        if (fresh) w.WriteLine(LogColumns.Replace(";", sep));
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
                    else Send(c, 200, "application/json", "{\"ok\":true,\"id\":" + t.Id + ",\"host\":" + Q(t.Host) + "}");
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

        static int Int(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        // ---------- JSON ----------

        static string Q(string s)
        {
            StringBuilder sb = new StringBuilder();
            Str(sb, s);
            return sb.ToString();
        }

        static void Str(StringBuilder sb, string s)
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

        static void Num(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append("null");
            else sb.Append(v.ToString("0.##", CultureInfo.InvariantCulture));
        }

        static int LowerBound(List<Sample> s, long t)
        {
            int lo = 0, hi = s.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (s[mid].T < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        static Stats Compute(List<Sample> s, long from) { return Compute(s, from, long.MaxValue); }

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

        static void AppendStats(StringBuilder sb, Stats st)
        {
            int got = st.Sent - st.Lost;
            sb.Append("\"sent\":").Append(st.Sent).Append(",\"lost\":").Append(st.Lost);
            sb.Append(",\"min\":"); Num(sb, got > 0 ? st.Min : double.NaN);
            sb.Append(",\"avg\":"); Num(sb, got > 0 ? st.Sum / got : double.NaN);
            sb.Append(",\"max\":"); Num(sb, got > 0 ? st.Max : double.NaN);
            sb.Append(",\"last\":"); Num(sb, st.Any && st.Last >= 0 ? st.Last : double.NaN);
            sb.Append(",\"jitter\":"); Num(sb, st.JitN > 0 ? st.JitSum / st.JitN : double.NaN);
        }

        static void AppendSeries(StringBuilder sb, List<Sample> s, long from, long step, int n)
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
            sb.Append("{\"now\":").Append(now).Append(",\"timeout\":").Append(Engine.TimeoutMs).Append(",\"gateways\":[");
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
                    sb.Append("{\"id\":").Append(t.Id).Append(",\"host\":"); Str(sb, t.Host);
                    sb.Append(",\"ip\":"); Str(sb, t.Address == null ? "" : t.Address.ToString());
                    sb.Append(",\"error\":"); Str(sb, t.Error);
                    sb.Append(",\"interval\":").Append(t.IntervalMs);
                    sb.Append(",\"paused\":").Append(t.Paused ? "true" : "false");
                    sb.Append(",\"destTtl\":").Append(t.DestTtl);
                    sb.Append(",\"rounds\":").Append(t.Round).Append(',');
                    Stats st = t.DestTtl > 0 && t.DestTtl <= t.Hops.Count ? Compute(t.Hops[t.DestTtl - 1].Samples, from) : new Stats();
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
                    sb.Append("]}");
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
            string safe = t.Host.Replace(':', '-');
            c.Response.StatusCode = 200;
            c.Response.ContentType = "text/csv; charset=utf-8";
            c.Response.Headers["Content-Disposition"] = "attachment; filename=\"pingsight-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv\"";
            c.Response.SendChunked = true;
            using (StreamWriter w = new StreamWriter(c.Response.OutputStream, new UTF8Encoding(true)))
            {
                w.WriteLine("time" + sep + "target" + sep + "hop" + sep + "ip" + sep + "hostname" + sep + "latency_ms" + sep + "lost");
                foreach (object[] h in hops)
                {
                    string head = sep + t.Host + sep + h[0] + sep + h[1] + sep + h[2] + sep;
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
