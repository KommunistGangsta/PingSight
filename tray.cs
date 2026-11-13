// PingSight notification icon: shows Windows notifications for alerts and opens the
// dashboard when clicked. Compiled on its own so PingSight still runs where
// Windows Forms is not available.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PingSightTray
{
    public static class Tray
    {
        static readonly Queue<string[]> pending = new Queue<string[]>();
        static NotifyIcon icon;
        static string url = "";
        static volatile bool stopping;

        public static void Start(string dashboardUrl)
        {
            url = dashboardUrl;
            Thread th = new Thread(Run);
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Name = "pingsight-tray";
            th.Start();
        }

        public static void Notify(string title, string text, bool warning)
        {
            lock (pending) { pending.Enqueue(new string[] { title, text, warning ? "1" : "0" }); }
        }

        public static void Stop() { stopping = true; }

        static void Open(object sender, EventArgs e)
        {
            try { Process.Start(url); } catch (Exception) { }
        }

        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush red = new SolidBrush(Color.FromArgb(196, 48, 58))) g.FillEllipse(red, 1, 1, 30, 30);
                    using (Pen white = new Pen(Color.White, 2.6f))
                    {
                        white.LineJoin = LineJoin.Round; white.StartCap = LineCap.Round; white.EndCap = LineCap.Round;
                        g.DrawLines(white, new PointF[] { new PointF(5, 16), new PointF(10, 16), new PointF(13, 8), new PointF(19, 24), new PointF(22, 16), new PointF(27, 16) });
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static void Run()
        {
            icon = new NotifyIcon();
            icon.Icon = MakeIcon();
            icon.Text = "PingSight - click to open the dashboard";
            icon.Click += Open;
            icon.BalloonTipClicked += Open;
            icon.Visible = true;

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += delegate(object sender, EventArgs e)
            {
                if (stopping)
                {
                    timer.Stop();
                    icon.Visible = false;
                    icon.Dispose();
                    Application.ExitThread();
                    return;
                }
                string[] n = null;
                lock (pending) { if (pending.Count > 0) n = pending.Dequeue(); }
                if (n != null) icon.ShowBalloonTip(8000, n[0], n[1], n[2] == "1" ? ToolTipIcon.Warning : ToolTipIcon.Info);
            };
            timer.Start();
            Application.Run();
        }
    }
}
