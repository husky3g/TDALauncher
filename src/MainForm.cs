using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TDALauncher
{
    static class Program
    {
        [DllImport("user32")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--workshop") return Workshop.Run(args);
            try { SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    // Palette taken from the server's "How to join" banner.
    static class Pal
    {
        public static readonly Color SkyTop = Color.FromArgb(20, 28, 32);
        public static readonly Color SkyMid = Color.FromArgb(30, 40, 43);
        public static readonly Color Glow = Color.FromArgb(200, 114, 58);
        public static readonly Color Sun = Color.FromArgb(240, 192, 128);
        public static readonly Color Land = Color.FromArgb(27, 33, 35);
        public static readonly Color Water = Color.FromArgb(31, 39, 41);
        public static readonly Color Cream = Color.FromArgb(236, 233, 222);
        public static readonly Color Tan = Color.FromArgb(214, 152, 92);
        public static readonly Color Muted = Color.FromArgb(138, 146, 146);
        public static readonly Color Panel = Color.FromArgb(205, 17, 23, 26);
        public static readonly Color Line = Color.FromArgb(52, 62, 64);
        public static readonly Color Ok = Color.FromArgb(132, 190, 120);
        public static readonly Color Bad = Color.FromArgb(214, 96, 80);
        public static readonly Color Discord = Color.FromArgb(88, 101, 242);
    }

    enum Phase { NoSteam, NoArma, Checking, NeedInstall, Installing, Manual, Ready, Launching, Updating }

    class MainForm : Form
    {
        const int W = 1180, H = 520, MainW = 860;   // left 860px: play area, right: announcements
        float scale = 1f;

        Phase phase = Phase.Checking;
        string arma, message = "", error;
        bool armaRunningWarned;
        ServerInfo server; bool serverQueried;
        Process helper;
        int tick;
        Bitmap scene;

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 400 };
        readonly ToolTip tip = new ToolTip();

        Rectangle rClose = new Rectangle(W - 46, 0, 46, 36), rMin = new Rectangle(W - 92, 0, 46, 36);
        Rectangle rPlay = new Rectangle(626, 424, 194, 58), rDiscord = new Rectangle(552, 425, 56, 56);
        Rectangle rPanel = new Rectangle(40, 344, 420, 140), rLocate;
        string hover;

        // announcements
        Rectangle rNews = new Rectangle(MainW + 20, 52, W - MainW - 40, 444), rNewsLink;
        List<NewsPost> news; string newsState = "Loading announcements...";
        float newsScroll, newsMax;
        readonly List<KeyValuePair<RectangleF, string>> newsHits = new List<KeyValuePair<RectangleF, string>>();

        // self-update
        UpdateInfo pendingUpdate; string updateError;

        Font fTitle, fSub, fBody, fBodyB, fSmall, fButton, fBar, fNews;

        public MainForm()
        {
            Text = "TDA Launcher - " + Config.ServerName;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Pal.SkyTop;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            using (var g = CreateGraphics()) scale = Math.Max(1f, g.DpiX / 96f);
            ClientSize = new Size((int)(W * scale), (int)(H * scale));

            string fam = FontExists("Segoe UI") ? "Segoe UI" : FontFamily.GenericSansSerif.Name;
            fTitle = new Font(fam, 50, FontStyle.Bold, GraphicsUnit.Pixel);
            fSub = new Font(fam, 15, FontStyle.Bold, GraphicsUnit.Pixel);
            fBody = new Font(fam, 14, FontStyle.Regular, GraphicsUnit.Pixel);
            fBodyB = new Font(fam, 14, FontStyle.Bold, GraphicsUnit.Pixel);
            fSmall = new Font(fam, 12, FontStyle.Regular, GraphicsUnit.Pixel);
            fButton = new Font(fam, 22, FontStyle.Bold, GraphicsUnit.Pixel);
            fBar = new Font(fam, 12, FontStyle.Bold, GraphicsUnit.Pixel);
            fNews = new Font(fam, 13, FontStyle.Regular, GraphicsUnit.Pixel);
            rLocate = new Rectangle(rPanel.Left, rPanel.Bottom + 4, 140, 18);
            rNewsLink = new Rectangle(rNews.Left + 16, rNews.Top + 38, 170, 16);

            scene = DrawScene();
            timer.Tick += (s, e) => OnTick();
            timer.Start();
            Updater.CleanUp();
            new Thread(QueryServerLoop) { IsBackground = true }.Start();
            new Thread(NewsLoop) { IsBackground = true }.Start();
            new Thread(CheckForUpdate) { IsBackground = true }.Start();
            Evaluate();
        }

        // ------------------------------------------------------------------ self-update
        void CheckForUpdate()
        {
            try
            {
                UpdateInfo u = Updater.Check();
                if (u != null) BeginInvoke((Action)(() => { pendingUpdate = u; TryApplyUpdate(); }));
            }
            catch (Exception) { }   // offline / GitHub unreachable: just keep the current version
        }

        // Updates right away unless mods are downloading or the game is launching (then it waits for that to finish).
        void TryApplyUpdate()
        {
            if (pendingUpdate == null || phase == Phase.Installing || phase == Phase.Launching || phase == Phase.Updating) return;
            UpdateInfo u = pendingUpdate; pendingUpdate = null;
            if (helper != null) try { helper.Kill(); } catch (Exception) { }
            SetPhase(Phase.Updating, "Updating the launcher to v" + u.Version + "...");
            new Thread(() =>
            {
                string err = null;
                try { Updater.Apply(u); } catch (Exception e) { err = e.Message; }
                BeginInvoke((Action)(() =>
                {
                    if (err == null)
                    {
                        try { Updater.Restart(); Close(); return; } catch (Exception e) { err = e.Message; }
                    }
                    updateError = "Launcher update failed (" + err + "). Get the latest from GitHub.";
                    phase = Phase.Checking; Evaluate();
                }));
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------------ announcements
        void NewsLoop()
        {
            while (!IsDisposed)
            {
                List<NewsPost> list = null; string state;
                try { list = News.Fetch(); state = list.Count == 0 ? "No announcements yet." : null; }
                catch (WebException e)
                {
                    var r = e.Response as HttpWebResponse;
                    state = r != null && r.StatusCode == HttpStatusCode.NotFound ? "Announcements will appear here soon." : "Couldn't load announcements.";
                }
                catch (Exception) { state = "Couldn't load announcements."; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (list != null || news == null) { news = list; newsState = state; }   // keep old posts if a refresh fails
                        Invalidate();
                    }));
                }
                catch (Exception) { return; }
                Thread.Sleep(5 * 60 * 1000);
            }
        }

        static bool FontExists(string name) { using (var f = new Font(name, 10)) return f.Name == name; }

        // ------------------------------------------------------------------ state machine
        void Evaluate()
        {
            error = null;
            if (!Steam.Running()) { SetPhase(Phase.NoSteam, "Steam isn't running. Start Steam and log in."); return; }
            arma = Arma.Find();
            if (arma == null) { SetPhase(Phase.NoArma, "Arma 3 isn't installed (or wasn't found)."); return; }
            RunHelper("check");
        }

        void SetPhase(Phase p, string msg) { phase = p; message = msg; Invalidate(); }

        void RunHelper(string mode)
        {
            if (helper != null) return;
            SetPhase(mode == "install" ? Phase.Installing : Phase.Checking, mode == "install" ? "Subscribing and downloading through Steam..." : "Checking your mods with Steam...");
            string err = null;
            foreach (Mod m in Config.Mods) if (mode == "check") { m.Known = false; m.Path = null; }
            try
            {
                helper = Workshop.Start(mode, arma, line => BeginInvoke((Action)(() =>
                {
                    if (line == null) { HelperFinished(mode, err); return; }
                    string[] p = line.Split(new[] { ' ' }, 3);
                    if (p[0] == "ERR") err = line.Substring(4);
                    else if (p[0] == "STATE" && p.Length == 3)
                    {
                        Mod m = Find(p[1]); string[] v = p[2].Split(' ');
                        if (m != null && v.Length == 3) { m.State = uint.Parse(v[0]); m.Done = ulong.Parse(v[1]); m.Total = ulong.Parse(v[2]); m.Known = true; }
                        if (mode == "install") message = "Downloading mods through Steam... keep this window open.";
                    }
                    else if (p[0] == "PATH" && p.Length == 3) { Mod m = Find(p[1]); if (m != null) m.Path = p[2]; }
                    Invalidate();
                })));
            }
            catch (Exception e) { HelperFinished(mode, e.Message); }
        }

        static Mod Find(string id) { return Config.Mods.FirstOrDefault(m => m.Id.ToString() == id); }

        void HelperFinished(string mode, string err)
        {
            if (helper != null) { try { helper.WaitForExit(2000); helper.Dispose(); } catch (Exception) { } helper = null; }
            if (phase == Phase.Updating) return;
            HelperResult(mode, err);
            TryApplyUpdate();
        }

        void HelperResult(string mode, string err)
        {
            if (err != null)
            {
                // Steam API unavailable: fall back to what's on disk and the Workshop pages.
                foreach (Mod m in Config.Mods) { m.Known = false; m.Path = Arma.WorkshopFolder(m.Id); }
                error = err;
                if (Config.Mods.All(m => m.Ready)) SetPhase(Phase.Ready, "Mods found on disk. Ready to play.");
                else if (mode == "install") { OpenWorkshopPages(); SetPhase(Phase.Manual, "Click Subscribe on each Workshop page Steam opened. This updates automatically."); }
                else SetPhase(Phase.NeedInstall, "Some required mods are missing.");
                return;
            }
            foreach (Mod m in Config.Mods) if (m.Path == null && m.Known && Mod.IsReady(m.State)) m.Path = Arma.WorkshopFolder(m.Id);
            if (Config.Mods.All(m => m.Ready)) SetPhase(Phase.Ready, "Everything is installed. Ready to play.");
            else if (mode == "install") RunHelper("check");
            else SetPhase(Phase.NeedInstall, Config.Mods.Any(m => m.Known && (m.State & Mod.Subscribed) != 0 && (m.State & Mod.NeedsUpdate) != 0)
                ? "Updates are available for the server's mods." : "Some required mods aren't installed yet.");
        }

        void OpenWorkshopPages()
        {
            foreach (Mod m in Config.Mods.Where(x => !x.Ready)) Steam.Open("steam://url/CommunityFilePage/" + m.Id);
        }

        void OnTick()
        {
            tick++;
            if (tick % 5 == 0)
            {
                if (phase == Phase.NoSteam || phase == Phase.NoArma) Evaluate();
                else if (phase == Phase.Manual)
                {
                    foreach (Mod m in Config.Mods) m.Path = Arma.WorkshopFolder(m.Id);
                    if (Config.Mods.All(m => m.Ready)) RunHelperOrReady();
                }
            }
            if (phase == Phase.Checking || phase == Phase.Installing || phase == Phase.Launching || phase == Phase.Updating || Config.PreLaunch || (phase == Phase.Ready && tick % 5 == 0)) Invalidate();
        }

        void RunHelperOrReady() { error = null; RunHelper("check"); }

        void PrimaryClick()
        {
            switch (phase)
            {
                case Phase.NoSteam: Steam.Open("steam://open/main"); message = "Starting Steam..."; break;
                case Phase.NoArma: Steam.Open("steam://install/" + Config.ArmaAppId); message = "Install Arma 3 from Steam. This updates automatically."; break;
                case Phase.NeedInstall: RunHelper("install"); break;
                case Phase.Manual: OpenWorkshopPages(); break;
                case Phase.Ready: if (Config.PreLaunch) Steam.Open(Config.Discord); else Play(); break;   // before launch: send players to the Discord
            }
            Invalidate();
        }

        void Play()
        {
            if (Arma.GameRunning() && !armaRunningWarned)
            {
                armaRunningWarned = true;
                SetPhase(Phase.Ready, "Arma 3 is already running. Close it first, or press Play again to launch anyway.");
                return;
            }
            try
            {
                Arma.Launch(arma, Config.Mods.Select(m => m.Path));
                SetPhase(Phase.Launching, "Launching Arma 3 and joining " + Config.ServerName + "...");
                var t = new System.Windows.Forms.Timer { Interval = 4000 };
                t.Tick += (s, e) => { t.Stop(); Close(); };
                t.Start();
            }
            catch (Exception e) { error = e.Message; SetPhase(Phase.Ready, "Couldn't start Arma 3."); }
        }

        void QueryServerLoop()
        {
            while (!IsDisposed)
            {
                var s = ServerInfo.Query();
                try { BeginInvoke((Action)(() => { server = s; serverQueried = true; Invalidate(); })); } catch (Exception) { return; }
                Thread.Sleep(30000);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (helper != null) try { helper.Kill(); } catch (Exception) { }
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ input
        [DllImport("user32")] static extern bool ReleaseCapture();
        [DllImport("user32")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        Point Logical(Point p) { return new Point((int)(p.X / scale), (int)(p.Y / scale)); }

        string HitTest(Point p)
        {
            if (rClose.Contains(p)) return "close";
            if (rMin.Contains(p)) return "min";
            if (rPlay.Contains(p)) return "play";
            if (Distance(p, rDiscord) <= rDiscord.Width / 2) return "discord";
            if (rLocate.Contains(p) && phase != Phase.Installing && phase != Phase.Updating) return "locate";
            if (rNewsLink.Contains(p)) return "newslink";
            for (int i = 0; i < newsHits.Count; i++)
                if (newsHits[i].Key.Contains(p) && p.Y >= rNews.Top + 66 && p.Y <= rNews.Bottom - 8) return "post:" + i;
            return null;
        }

        static double Distance(Point p, Rectangle r) { double dx = p.X - (r.Left + r.Width / 2.0), dy = p.Y - (r.Top + r.Height / 2.0); return Math.Sqrt(dx * dx + dy * dy); }

        bool PlayEnabled { get { return phase != Phase.Checking && phase != Phase.Installing && phase != Phase.Launching && phase != Phase.Updating; } }
        bool ComingSoon { get { return phase == Phase.Ready && Config.PreLaunch; } }

        static string Countdown()
        {
            TimeSpan t = Config.LaunchUtc - DateTime.UtcNow;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format("{0}:{1:00}:{2:00}:{3:00}", t.Days, t.Hours, t.Minutes, t.Seconds);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!rNews.Contains(Logical(e.Location))) return;
            newsScroll = Math.Max(0, Math.Min(newsMax, newsScroll - e.Delta * 0.5f));
            hover = null; Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = HitTest(Logical(e.Location));
            if (h != hover)
            {
                hover = h;
                Cursor = h == null || (h == "play" && !PlayEnabled) ? Cursors.Default : Cursors.Hand;
                if (h == "discord" || (h == "play" && ComingSoon)) tip.Show(h == "discord" ? "Join the official Discord" : "Opens " + Config.LaunchText + " - join the Discord for news", this, e.X + 14, e.Y + 18, 2500);
                else tip.Hide(this);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            string h = HitTest(Logical(e.Location));
            if (h == null) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); return; }
            switch (h)
            {
                case "close": Close(); break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "discord": Steam.Open(Config.Discord); break;
                case "play": if (PlayEnabled) PrimaryClick(); break;
                case "locate": Locate(); break;
                case "newslink": Steam.Open(Config.AnnouncementsUrl); break;
                default:
                    if (h.StartsWith("post:")) { int i = int.Parse(h.Substring(5)); if (i < newsHits.Count) Steam.Open(newsHits[i].Value); }
                    break;
            }
        }

        void Locate()
        {
            using (var d = new FolderBrowserDialog { Description = "Select your Arma 3 folder (the one containing arma3_x64.exe)" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!Arma.IsArma(d.SelectedPath)) { MessageBox.Show(this, "That folder doesn't contain arma3_x64.exe.", "TDA Launcher"); return; }
                Settings.Set("arma", d.SelectedPath);
                Evaluate();
            }
        }

        // ------------------------------------------------------------------ painting
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.ScaleTransform(scale, scale);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(scene, 0, 0, W, H);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // title bar
            using (var b = new SolidBrush(Pal.Muted))
            {
                g.DrawString("TDA LAUNCHER", fBar, b, 18, 11);
                g.DrawString("v" + AppVersion.Text, fSmall, b, 18 + g.MeasureString("TDA LAUNCHER", fBar).Width + 2, 12);
            }
            if (hover == "min") using (var b = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillRectangle(b, rMin);
            if (hover == "close") using (var b = new SolidBrush(Color.FromArgb(200, 180, 60, 50))) g.FillRectangle(b, rClose);
            using (var p = new Pen(Pal.Cream, 1.3f))
            {
                g.DrawLine(p, rMin.Left + 18, 18, rMin.Left + 28, 18);
                g.DrawLine(p, rClose.Left + 18, 13, rClose.Left + 28, 23); g.DrawLine(p, rClose.Left + 28, 13, rClose.Left + 18, 23);
            }

            // headline (spaced, with soft shadow like the banner)
            DrawSpaced(g, "THE DAY AFTER", fTitle, Pal.Cream, MainW / 2f, 66, 7f, true);
            DrawSpaced(g, "BREAKING POINT   ·   BATTLEYE ON   ·   TEMBELAN", fSub, Pal.Tan, MainW / 2f, 136, 4f, true);

            // server status
            string st; Color dot;
            if (!serverQueried) { st = "Checking server..."; dot = Pal.Muted; }
            else if (server.Online) { st = "Online  ·  " + server.Players + " / " + server.MaxPlayers + " players" + (string.IsNullOrEmpty(server.Map) ? "" : "  ·  " + server.Map); dot = Pal.Ok; }
            else { st = "Restarting / Maintenance"; dot = Pal.Tan; }
            SizeF ss = g.MeasureString(st, fBody);
            float sx = MainW / 2f - (ss.Width + 16) / 2f;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, sx, 172, 9, 9);
            using (var b = new SolidBrush(Pal.Cream)) g.DrawString(st, fBody, b, sx + 16, 166);

            // requirements panel
            using (var path = Round(rPanel, 10)) using (var b = new SolidBrush(Pal.Panel)) using (var p = new Pen(Pal.Line)) { g.FillPath(b, path); g.DrawPath(p, path); }
            int y = rPanel.Top + 12;
            DrawRow(g, y, "Arma 3", arma != null ? 2 : (phase == Phase.NoSteam ? 0 : 1), arma != null ? "Installed" : (phase == Phase.NoSteam ? "Waiting for Steam" : "Not installed"));
            foreach (Mod m in Config.Mods) { y += 31; string s; DrawRow(g, y, m.Name, ModLevel(m, out s), s); }
            using (var b = new SolidBrush(hover == "locate" ? Pal.Tan : Pal.Muted)) g.DrawString("Arma 3 folder...", fSmall, b, rLocate.Left + 2, rLocate.Top);

            // status message + progress
            string shown = ComingSoon ? "Mods are ready. The Day After opens " + Config.LaunchText + "." : message;
            using (var b = new SolidBrush(Pal.Cream)) g.DrawString(shown, fBody, b, new RectangleF(552, 368, 270, 40));
            string err = error ?? updateError;
            if (err != null) using (var b = new SolidBrush(Pal.Bad)) g.DrawString(err, fSmall, b, new RectangleF(rLocate.Right + 8, rLocate.Top, 380, 32));
            DrawNews(g);
            if (phase == Phase.Installing)
            {
                double pr = Progress();
                var bar = new RectangleF(552, 410, 268, 5);
                using (var b = new SolidBrush(Pal.Line)) g.FillRectangle(b, bar);
                using (var b = new SolidBrush(Pal.Tan)) g.FillRectangle(b, bar.X, bar.Y, (float)(bar.Width * pr), bar.Height);
            }

            // discord button
            bool dh = hover == "discord";
            using (var b = new SolidBrush(dh ? ControlPaint.Light(Pal.Discord, 0.2f) : Pal.Discord)) g.FillEllipse(b, rDiscord);
            DrawDiscordGlyph(g, rDiscord);

            // play button
            string label; bool enabled = PlayEnabled;
            switch (phase)
            {
                case Phase.NoSteam: label = "START STEAM"; break;
                case Phase.NoArma: label = "GET ARMA 3"; break;
                case Phase.Checking: label = "CHECKING" + Dots(); break;
                case Phase.NeedInstall: label = Config.Mods.Any(m => m.Known && (m.State & Mod.Subscribed) != 0) && Config.Mods.All(m => !m.Known || (m.State & Mod.Subscribed) != 0) ? "UPDATE" : "INSTALL"; break;
                case Phase.Installing: label = (int)(Progress() * 100) + "%"; break;
                case Phase.Manual: label = "OPEN PAGES"; break;
                case Phase.Launching: label = "LAUNCHING" + Dots(); break;
                case Phase.Updating: label = "UPDATING" + Dots(); break;
                default: label = Config.PreLaunch ? "COMING SOON!" : "PLAY"; break;
            }

            // pre-launch: outlined "coming soon" button with the countdown underneath
            if (Config.PreLaunch)
                DrawSpaced(g, Countdown(), fSub, Pal.Tan, rPlay.Left + rPlay.Width / 2f, rPlay.Bottom + 7, 3f, true);
            if (ComingSoon)
            {
                using (var path = Round(rPlay, 8))
                using (var b = new SolidBrush(hover == "play" ? Color.FromArgb(230, 52, 42, 34) : Color.FromArgb(215, 24, 30, 32)))
                using (var p = new Pen(hover == "play" ? Color.FromArgb(232, 168, 104) : Pal.Tan, 1.6f)) { g.FillPath(b, path); g.DrawPath(p, path); }
                using (var f = new Font(fButton.FontFamily, 19, FontStyle.Bold, GraphicsUnit.Pixel))
                    DrawSpaced(g, label, f, Pal.Tan, rPlay.Left + rPlay.Width / 2f, rPlay.Top + (rPlay.Height - f.Height) / 2f - 1, 1.5f, false);
                return;
            }

            Color top = enabled ? (hover == "play" ? Color.FromArgb(232, 168, 104) : Pal.Tan) : Color.FromArgb(70, 78, 80);
            Color bottom = enabled ? (hover == "play" ? Color.FromArgb(206, 120, 64) : Color.FromArgb(184, 102, 52)) : Color.FromArgb(56, 63, 65);
            using (var path = Round(rPlay, 8))
            using (var b = new LinearGradientBrush(rPlay, top, bottom, 90f)) g.FillPath(b, path);
            DrawSpaced(g, label, fButton, enabled ? Color.FromArgb(28, 22, 18) : Pal.Muted, rPlay.Left + rPlay.Width / 2f, rPlay.Top + (rPlay.Height - fButton.Height) / 2f - 1, 3f, false);
        }

        string Dots() { return new string('.', tick % 4); }

        void DrawNews(Graphics g)
        {
            using (var path = Round(rNews, 10)) using (var b = new SolidBrush(Pal.Panel)) using (var p = new Pen(Pal.Line)) { g.FillPath(b, path); g.DrawPath(p, path); }
            float x = rNews.Left + 16;
            DrawSpacedLeft(g, "ANNOUNCEMENTS", fSub, Pal.Tan, x, rNews.Top + 14, 3f);
            using (var b = new SolidBrush(hover == "newslink" ? Pal.Tan : Pal.Muted)) g.DrawString("#server-announcements  ↗", fSmall, b, rNewsLink.Left - 2, rNewsLink.Top);
            using (var p = new Pen(Pal.Line)) g.DrawLine(p, rNews.Left + 14, rNews.Top + 60, rNews.Right - 14, rNews.Top + 60);

            newsHits.Clear();
            var list = new RectangleF(rNews.Left + 6, rNews.Top + 66, rNews.Width - 12, rNews.Height - 74);
            if (news == null || news.Count == 0)
            {
                using (var b = new SolidBrush(Pal.Muted))
                using (var f = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(newsState ?? "", fBody, b, new RectangleF(list.Left + 10, list.Top + 24, list.Width - 20, 60), f);
                newsMax = 0;
                return;
            }

            GraphicsState saved = g.Save();
            g.SetClip(list);
            float tw = list.Width - 26, y = list.Top - newsScroll;
            using (var cream = new SolidBrush(Pal.Cream))
            using (var body = new SolidBrush(Color.FromArgb(214, 210, 200)))
            using (var muted = new SolidBrush(Pal.Muted))
            using (var hl = new SolidBrush(Color.FromArgb(18, 255, 255, 255)))
            using (var line = new Pen(Pal.Line))
                for (int i = 0; i < news.Count; i++)
                {
                    NewsPost n = news[i];
                    float th = GdiHeight(n.Text, fNews, tw);
                    var card = new RectangleF(list.Left, y, list.Width - 8, th + 42);
                    if (card.Bottom > list.Top && card.Top < list.Bottom)
                    {
                        if (hover == "post:" + i) using (var hp = Round(card, 6)) g.FillPath(hl, hp);
                        string ago = News.Ago(n.Time);
                        SizeF agoSize = g.MeasureString(ago, fSmall);
                        GdiText(g, n.Author, fBodyB, Pal.Cream, new RectangleF(card.Left + 10, y + 8, tw - agoSize.Width - 6, 18), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                        g.DrawString(ago, fSmall, muted, card.Left + 10 + tw - agoSize.Width, y + 9);
                        GdiText(g, n.Text, fNews, Color.FromArgb(214, 210, 200), new RectangleF(card.Left + 10, y + 30, tw, th + 2), TextFormatFlags.WordBreak);
                        if (i < news.Count - 1) g.DrawLine(line, card.Left + 10, card.Bottom, card.Left + 10 + tw, card.Bottom);
                    }
                    newsHits.Add(new KeyValuePair<RectangleF, string>(card, n.Url));
                    y = card.Bottom + 1;
                }
            g.Restore(saved);

            float total = y + newsScroll - list.Top;
            newsMax = Math.Max(0, total - list.Height);
            if (newsScroll > newsMax) newsScroll = newsMax;
            if (newsMax > 0)
            {
                float thumb = Math.Max(30, list.Height * list.Height / total);
                float ty = list.Top + (list.Height - thumb) * (newsScroll / newsMax);
                using (var b = new SolidBrush(Color.FromArgb(90, Pal.Tan))) using (var tp = Round(new RectangleF(list.Right - 4, ty, 3, thumb), 1.5f)) g.FillPath(b, tp);
            }
        }

        // Announcement text goes through GDI (TextRenderer): unlike GDI+ it falls back to Segoe UI Emoji etc.
        // TextRenderer ignores the DPI scale transform, so the font and rectangle are scaled by hand.
        readonly Dictionary<Font, Font> gdiFonts = new Dictionary<Font, Font>();
        Font Scaled(Font f)
        {
            Font s;
            if (!gdiFonts.TryGetValue(f, out s)) gdiFonts[f] = s = new Font(f.FontFamily, f.Size * scale, f.Style, GraphicsUnit.Pixel);
            return s;
        }

        float GdiHeight(string text, Font f, float width)
        {
            return TextRenderer.MeasureText(text, Scaled(f), new Size((int)(width * scale), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height / scale;
        }

        void GdiText(Graphics g, string text, Font f, Color c, RectangleF r, TextFormatFlags flags)
        {
            GraphicsState st = g.Save();
            g.ResetTransform();
            var rr = Rectangle.Round(new RectangleF(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale));
            TextRenderer.DrawText(g, text, Scaled(f), rr, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
            g.Restore(st);
        }

        static void DrawSpacedLeft(Graphics g, string text, Font f, Color color, float left, float y, float spacing)
        {
            var fmt = StringFormat.GenericTypographic;
            float w = text.Sum(ch => g.MeasureString(ch.ToString(), f, 1000, fmt).Width) + spacing * (text.Length - 1);
            DrawSpaced(g, text, f, color, left + w / 2f, y, spacing, false);
        }

        double Progress()
        {
            ulong done = 0, total = 0; int ready = 0;
            foreach (Mod m in Config.Mods)
            {
                if (m.Known && Mod.IsReady(m.State)) { ready++; continue; }
                done += m.Done; total += m.Total;
            }
            double partial = total > 0 ? (double)done / total : 0;
            int n = Config.Mods.Length;
            return Math.Max(0, Math.Min(1, (ready + partial * (n - ready)) / n));
        }

        int ModLevel(Mod m, out string text)
        {
            if (m.Known)
            {
                uint s = m.State;
                if (Mod.IsReady(s) && m.Path != null) { text = "Ready"; return 2; }
                if ((s & Mod.Downloading) != 0) { text = m.Total > 0 ? "Downloading " + (int)(100.0 * m.Done / m.Total) + "%" : "Downloading"; return 1; }
                if ((s & Mod.DownloadPending) != 0) { text = "Queued"; return 1; }
                if ((s & Mod.NeedsUpdate) != 0) { text = "Update needed"; return 1; }
                if ((s & Mod.Subscribed) == 0) { text = "Not subscribed"; return 0; }
                text = "Waiting for Steam"; return 1;
            }
            if (m.Path != null) { text = "Found"; return 2; }
            text = phase == Phase.Checking ? "Checking..." : (phase == Phase.NoSteam || phase == Phase.NoArma ? "-" : "Missing");
            return 0;
        }

        void DrawRow(Graphics g, int y, string name, int level, string status)
        {
            Color c = level == 2 ? Pal.Ok : level == 1 ? Pal.Tan : Pal.Muted;
            float x = rPanel.Left + 16;
            using (var p = new Pen(c, 1.8f)) g.DrawEllipse(p, x, y + 2, 15, 15);
            if (level == 2) using (var p = new Pen(c, 2f)) g.DrawLines(p, new[] { new PointF(x + 4, y + 10), new PointF(x + 6.8f, y + 13), new PointF(x + 11.5f, y + 6.5f) });
            else if (level == 1) using (var b = new SolidBrush(c)) g.FillEllipse(b, x + 4.5f, y + 6.5f, 6, 6);
            using (var b = new SolidBrush(Pal.Cream)) g.DrawString(name, fBodyB, b, x + 26, y);
            using (var b = new SolidBrush(c))
            {
                SizeF sz = g.MeasureString(status, fBody);
                g.DrawString(status, fBody, b, rPanel.Right - 16 - sz.Width, y);
            }
            if (y < rPanel.Bottom - 40) using (var p = new Pen(Pal.Line)) g.DrawLine(p, rPanel.Left + 14, y + 25, rPanel.Right - 14, y + 25);
        }

        static void DrawSpaced(Graphics g, string text, Font f, Color color, float centerX, float y, float spacing, bool shadow)
        {
            var fmt = StringFormat.GenericTypographic; fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            var widths = text.Select(ch => g.MeasureString(ch.ToString(), f, 1000, fmt).Width).ToArray();
            float total = widths.Sum() + spacing * (text.Length - 1);
            float x = centerX - total / 2f;
            using (var sb = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            using (var b = new SolidBrush(color))
                for (int i = 0; i < text.Length; i++)
                {
                    string ch = text[i].ToString();
                    if (shadow) g.DrawString(ch, f, sb, x + 2, y + 3, fmt);
                    g.DrawString(ch, f, b, x, y, fmt);
                    x += widths[i] + spacing;
                }
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // Simplified Discord controller-face mark in white.
        static void DrawDiscordGlyph(Graphics g, Rectangle r)
        {
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f + 1, w = r.Width * 0.56f, h = w * 0.74f;
            float l = cx - w / 2, t = cy - h / 2;
            using (var path = new GraphicsPath())
            {
                path.AddBezier(l + w * 0.18f, t + h * 0.05f, l + w * 0.36f, t - h * 0.02f, l + w * 0.64f, t - h * 0.02f, l + w * 0.82f, t + h * 0.05f);
                path.AddBezier(l + w * 0.82f, t + h * 0.05f, l + w * 1.02f, t + h * 0.38f, l + w * 1.03f, t + h * 0.72f, l + w * 0.98f, t + h * 0.86f);
                path.AddBezier(l + w * 0.98f, t + h * 0.86f, l + w * 0.88f, t + h * 0.96f, l + w * 0.78f, t + h * 1.0f, l + w * 0.70f, t + h * 1.02f);
                path.AddLine(l + w * 0.70f, t + h * 1.02f, l + w * 0.64f, t + h * 0.88f);
                path.AddBezier(l + w * 0.64f, t + h * 0.88f, l + w * 0.55f, t + h * 0.91f, l + w * 0.45f, t + h * 0.91f, l + w * 0.36f, t + h * 0.88f);
                path.AddLine(l + w * 0.36f, t + h * 0.88f, l + w * 0.30f, t + h * 1.02f);
                path.AddBezier(l + w * 0.30f, t + h * 1.02f, l + w * 0.22f, t + h * 1.0f, l + w * 0.12f, t + h * 0.96f, l + w * 0.02f, t + h * 0.86f);
                path.AddBezier(l + w * 0.02f, t + h * 0.86f, l - w * 0.03f, t + h * 0.72f, l - w * 0.02f, t + h * 0.38f, l + w * 0.18f, t + h * 0.05f);
                path.CloseFigure();
                g.FillPath(Brushes.White, path);
            }
            using (var b = new SolidBrush(Pal.Discord))
            {
                float ew = w * 0.17f, eh = h * 0.27f;
                g.FillEllipse(b, l + w * 0.34f - ew / 2, t + h * 0.55f - eh / 2, ew, eh);
                g.FillEllipse(b, l + w * 0.66f - ew / 2, t + h * 0.55f - eh / 2, ew, eh);
            }
        }

        // ------------------------------------------------------------------ background: dusk over a palm island
        Bitmap DrawScene()
        {
            int sw = (int)(W * scale), sh = (int)(H * scale);
            var bmp = new Bitmap(sw, sh);
            using (var g = Graphics.FromImage(bmp))
            {
                g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                const float horizon = 330f;
                var sun = new PointF(530, horizon);

                using (var b = new LinearGradientBrush(new RectangleF(0, 0, W, horizon), Pal.SkyTop, Pal.SkyMid, 90f)) g.FillRectangle(b, 0, 0, W, horizon);
                // warm band along the horizon
                using (var b = new LinearGradientBrush(new RectangleF(0, 170, W, horizon - 170), Color.FromArgb(0, Pal.Glow), Color.FromArgb(150, 120, 74, 44), 90f))
                    g.FillRectangle(b, 0, 171, W, horizon - 171);
                // glow around the sun
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(sun.X - 330, sun.Y - 230, 660, 460);
                    using (var b = new PathGradientBrush(gp) { CenterPoint = sun, CenterColor = Color.FromArgb(235, Pal.Glow), SurroundColors = new[] { Color.FromArgb(0, Pal.Glow) } })
                        g.FillRectangle(b, 0, 0, W, horizon);
                }
                using (var b = new SolidBrush(Pal.Sun)) g.FillEllipse(b, sun.X - 42, sun.Y - 40, 84, 84);

                // island ridge
                var rnd = new Random(11);
                var ridge = new List<PointF> { new PointF(-10, horizon + 2), new PointF(-10, 300) };
                float[] hills = { 300, 296, 288, 274, 270, 276, 282, 286, 294, 304, 312, 320, 326, 328, 326, 318, 306, 300, 292, 286, 280, 284, 290, 300 };
                for (int i = 0; i < hills.Length; i++) ridge.Add(new PointF(i * W / (float)(hills.Length - 1), hills[i] + (float)rnd.NextDouble() * 5));
                ridge.Add(new PointF(W + 10, 296)); ridge.Add(new PointF(W + 10, horizon + 2));
                using (var b = new SolidBrush(Pal.Land)) g.FillPolygon(b, ridge.ToArray());

                // palms on the ridge
                using (var p = new Pen(Pal.Land, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float[] xs = { 30, 70, 92, 150, 250, 268, 370, 386, 455, 640, 672, 690, 738, 760, 806, 900, 990, 1010, 1060, 1080, 1150, 1180 };
                    foreach (float x0 in xs)
                    {
                        float x = x0 * W / 1200f; if (Math.Abs(x - sun.X) < 50 && rnd.Next(2) == 0) continue;
                        float gy = RidgeY(ridge, x) + 2, hgt = 22 + (float)rnd.NextDouble() * 20, lean = (float)(rnd.NextDouble() - 0.5) * 12;
                        var top = new PointF(x + lean, gy - hgt);
                        g.DrawBezier(p, new PointF(x, gy), new PointF(x + lean * 0.2f, gy - hgt * 0.4f), new PointF(x + lean * 0.8f, gy - hgt * 0.7f), top);
                        for (int k = 0; k < 6; k++)
                        {
                            double a = Math.PI * (1.05 + k * 0.18) + (rnd.NextDouble() - 0.5) * 0.2;
                            float len = 12 + (float)rnd.NextDouble() * 8;
                            var end = new PointF(top.X + (float)Math.Cos(a) * len, top.Y + (float)Math.Sin(a) * len * 0.5f + 7);
                            g.DrawBezier(p, top, new PointF(top.X + (float)Math.Cos(a) * len * 0.5f, top.Y - 4), new PointF(end.X, end.Y - 5), end);
                        }
                    }
                }

                // sea + sun reflection
                using (var b = new SolidBrush(Pal.Water)) g.FillRectangle(b, 0, horizon, W, H - horizon);
                using (var p = new Pen(Color.FromArgb(110, 140, 112, 82), 1.4f))
                    for (int i = 0; i < 6; i++)
                    {
                        float half = 54 - i * 7, yy = horizon + 10 + i * 8;
                        g.DrawLine(p, sun.X - half, yy, sun.X + half, yy);
                    }
            }
            return bmp;
        }

        static float RidgeY(List<PointF> ridge, float x)
        {
            for (int i = 2; i < ridge.Count - 2; i++)
                if (ridge[i].X >= x)
                {
                    var a = ridge[i - 1]; var b = ridge[i];
                    float t = (b.X - a.X) == 0 ? 0 : (x - a.X) / (b.X - a.X);
                    return a.Y + (b.Y - a.Y) * t;
                }
            return 300;
        }
    }
}
