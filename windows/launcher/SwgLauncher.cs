// Star Wars Galaxies launcher for Windows in the spirit of the 2003-2005 LaunchPad, built from the game's own art
// (logo, planet loading screens, opening crawl - read out of the client's .tre files; nothing is shipped).
// Sits next to SWGEmu.exe; settings in launcher.conf (SERVER=, STATUS= host answering TCP 44455).
// usage: "SWG Launcher.exe" [--extract]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Retro
{
    public class SwgLauncher
    {
        static string Dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        static string ArtDir = Path.Combine(Dir, "launcher-art");
        static Dictionary<string, string> Conf = Art.ReadConf(Path.Combine(Dir, "launcher.conf"));
        static readonly string[] Planets = { "tatooine", "naboo", "corellia", "dantooine", "dathomir", "endor", "lok", "rori", "talus" };
        const int W = 640, H = 420;

        public static void Extract()
        {
            var idx = new Dictionary<string, Art.TreEntry>();
            var tres = Directory.GetFiles(Dir, "*.tre"); Array.Sort(tres, StringComparer.OrdinalIgnoreCase);
            foreach (var t in tres) Art.TreIndex(t, idx);                       // later archives (patches) win
            Directory.CreateDirectory(ArtDir);
            Func<string, string, int, int, bool> get = (name, outName, w, h) =>
            {
                Art.TreEntry e; if (!idx.TryGetValue(name, out e)) return false;
                using (var src = Art.Dds(Art.TreRead(e))) using (var r = Art.Resize(src, w, h)) Art.SavePng(r, Path.Combine(ArtDir, outName));
                return true;
            };
            string logo = idx.ContainsKey("texture/ui_starwars_logo_jtl.dds") ? "texture/ui_starwars_logo_jtl.dds" : "texture/ui_starwars_logo.dds";
            get(logo, "logo.png", 232, 232);
            foreach (var p in Planets) get("texture/loading/" + p + "/ui_load_" + p + ".dds", "planet_" + p + ".png", 216, 216);
            get("texture/intro_crawl.dds", "crawl.png", 176, 352);
            using (var src = Art.Dds(Art.TreRead(idx[logo]))) { Art.SavePng(Art.Resize(src, 128, 128), Path.Combine(ArtDir, "icon.png")); Art.SaveIco(src, Path.Combine(ArtDir, "icon.ico")); }
        }

        Form f; Image logo, crawl; List<KeyValuePair<string, Image>> planets = new List<KeyValuePair<string, Image>>();
        int planet, crawlY = 240; string status = "contacting the galaxy..."; Color statusColor = Color.FromArgb(204, 221, 238);
        List<Btn> buttons = new List<Btn>(); Btn over; Point drag; bool dragging;
        Point[] stars;
        static readonly Color Blue = Color.FromArgb(90, 160, 216), Gold = Color.FromArgb(240, 184, 72), PanelC = Color.FromArgb(10, 22, 34);

        class Btn { public Rectangle R; public string Label; public bool Big; public Action Click; }

        Image Load(string n) { string p = Path.Combine(ArtDir, n); return File.Exists(p) ? Image.FromFile(p) : null; }

        public void Run()
        {
            f = new Form { Text = "Star Wars Galaxies", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterScreen,
                           ClientSize = new Size(W, H), BackColor = Color.Black, KeyPreview = true };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(f, true, null);
            var ico = Load("icon.png"); if (ico != null) using (var b = new Bitmap(ico, 64, 64)) f.Icon = Icon.FromHandle(b.GetHicon());
            logo = Load("logo.png"); crawl = Load("crawl.png");
            foreach (var p in Planets) { var i = Load("planet_" + p + ".png"); if (i != null) planets.Add(new KeyValuePair<string, Image>(p, i)); }
            var rnd = new Random(1977); stars = new Point[260];
            for (int i = 0; i < stars.Length; i++) stars[i] = new Point(rnd.Next(W), rnd.Next(H));
            buttons.Add(new Btn { R = new Rectangle(22, 340, 140, 50), Label = "PLAY", Big = true, Click = Play });
            buttons.Add(new Btn { R = new Rectangle(172, 350, 100, 32), Label = "SETTINGS", Click = () => Art.Start(Path.Combine(Dir, "SWGEmu_Setup.exe"), "", Dir) });
            buttons.Add(new Btn { R = new Rectangle(280, 350, 100, 32), Label = "DISPLAY", Click = DisplaySettings });
            buttons.Add(new Btn { R = new Rectangle(388, 350, 124, 32), Label = "CONTROLLER", Click = Controller });
            buttons.Add(new Btn { R = new Rectangle(520, 350, 94, 32), Label = "EXIT", Click = () => f.Close() });
            f.Paint += Paint;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.MouseMove += (s, e) =>
            {
                if (dragging) { f.Location = new Point(f.Location.X + e.X - drag.X, f.Location.Y + e.Y - drag.Y); return; }
                Btn b = null; foreach (var x in buttons) if (x.R.Contains(e.Location)) b = x;
                if (b != over) { over = b; f.Cursor = b != null ? Cursors.Hand : Cursors.Default; f.Invalidate(); }
            };
            f.MouseDown += (s, e) => { if (over == null) { dragging = true; drag = e.Location; } };
            f.MouseUp += (s, e) => { dragging = false; if (over != null && over.R.Contains(e.Location)) over.Click(); };
            var anim = new System.Windows.Forms.Timer { Interval = 45 };
            anim.Tick += (s, e) => { crawlY -= 1; if (crawlY < -352) crawlY = 240; f.Invalidate(new Rectangle(226, 0, 188, 252)); };
            anim.Start();
            var cyc = new System.Windows.Forms.Timer { Interval = 5000 };
            cyc.Tick += (s, e) => { planet++; f.Invalidate(); }; cyc.Start();
            var poll = new System.Windows.Forms.Timer { Interval = 30000 };
            poll.Tick += (s, e) => Refresh(); poll.Start(); Refresh();
            Application.Run(f);
        }

        void Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(170, 170, 170))) foreach (var p in stars) g.FillRectangle(b, p.X, p.Y, 1, 1);
            if (crawl != null)
            {
                g.SetClip(new Rectangle(226, 14, 188, 238)); g.DrawImage(crawl, 232, crawlY, 176, 352); g.ResetClip();
            }
            if (logo != null) g.DrawImage(logo, 4, 6, 232, 232);
            if (planets.Count > 0)
            {
                var pl = planets[planet % planets.Count];
                g.DrawImage(pl.Value, 418, 10, 216, 216);
                using (var fnt = new Font("Segoe UI", 9f)) using (var br = new SolidBrush(Color.FromArgb(153, 170, 187)))
                    g.DrawString(pl.Key.ToUpper(), fnt, br, 526, 230, new StringFormat { Alignment = StringAlignment.Center });
            }
            var panel = new Rectangle(10, 252, W - 20, H - 262);
            using (var b = new SolidBrush(PanelC)) g.FillRectangle(b, panel);
            using (var p = new Pen(Blue)) g.DrawRectangle(p, panel);
            using (var p = new Pen(Color.FromArgb(31, 60, 85))) g.DrawLine(p, 10, 278, W - 10, 278);
            using (var bold = new Font("Segoe UI", 9f, FontStyle.Bold)) using (var b = new SolidBrush(Blue)) g.DrawString("GALAXY", bold, b, 20, 258);
            using (var fnt = new Font("Segoe UI", 10f)) using (var b = new SolidBrush(statusColor)) g.DrawString(status, fnt, b, 84, 257);
            using (var fnt = new Font("Segoe UI", 8.5f)) using (var b = new SolidBrush(Color.FromArgb(154, 170, 187)))
                g.DrawString("New player? Type any new name and password at the login screen - your account is made on the spot.\n" +
                             "Then build your character: species, looks and profession are all yours to choose.", fnt, b, 20, 288);
            foreach (var bt in buttons)
            {
                bool hot = bt == over;
                using (var b = new SolidBrush(hot ? Color.FromArgb(36, 73, 106) : Color.FromArgb(19, 40, 58))) g.FillRectangle(b, bt.R);
                using (var p = new Pen(hot ? Gold : Blue)) g.DrawRectangle(p, bt.R);
                using (var fnt = new Font("Segoe UI", bt.Big ? 16f : 10f, FontStyle.Bold)) using (var b = new SolidBrush(Gold))
                    g.DrawString(bt.Label, fnt, b, bt.R, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            int dw, dh; string dm; ReadDisplay(out dw, out dh, out dm);
            if (dw > 0)
                using (var fnt = new Font("Segoe UI", 8f)) using (var b = new SolidBrush(Color.FromArgb(154, 170, 187)))
                    g.DrawString(dw + " x " + dh + ", " + dm, fnt, b, 330, 388, new StringFormat { Alignment = StringAlignment.Center });
        }

        void Refresh()
        {
            new Thread(() =>
            {
                string host = Art.Get(Conf, "STATUS", Art.Get(Conf, "SERVER", "")); string text = null; Color col = statusColor;
                try
                {
                    using (var t = new TcpClient())
                    {
                        var ar = t.BeginConnect(host, int.Parse(Art.Get(Conf, "STATUS_PORT", "44455")), null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(2000) || !t.Connected) throw new Exception();
                        t.ReceiveTimeout = 2000; var s = t.GetStream(); var sb = new StringBuilder(); var buf = new byte[4096]; int n;
                        while (sb.ToString().IndexOf("</zoneServer>") < 0 && (n = s.Read(buf, 0, buf.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                        string x = sb.ToString();
                        Func<string, string> tag = k => { var m = Regex.Match(x, "<" + k + ">([^<]*)</" + k + ">"); return m.Success ? m.Groups[1].Value : ""; };
                        bool up = tag("status") == "up"; int c; int.TryParse(tag("connected"), out c);
                        text = string.Format("{0}    ● {1}    {2} player{3} online", tag("name"), up ? "ONLINE" : "DOWN", c, c == 1 ? "" : "s");
                        col = up ? Color.FromArgb(143, 224, 143) : Color.FromArgb(224, 143, 143);
                    }
                }
                catch { }
                f.BeginInvoke((Action)(() =>
                {
                    status = text ?? "status unknown (the login server may still work)";
                    statusColor = text != null ? col : Color.FromArgb(204, 221, 238); f.Invalidate();
                }));
            }) { IsBackground = true }.Start();
        }

        // ---- resolution: options.cfg [ClientGraphics] screenWidth / screenHeight / windowed / borderlessWindow
        static string OptionsCfg { get { return Path.Combine(Dir, "options.cfg"); } }

        static void ReadDisplay(out int w, out int h, out string mode)
        {
            var v = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(OptionsCfg))
                foreach (var line in File.ReadAllLines(OptionsCfg))
                {
                    var m = Regex.Match(line, @"^\s*(\w+)\s*=\s*(\d+)\s*$"); int n;
                    if (m.Success && int.TryParse(m.Groups[2].Value, out n)) v[m.Groups[1].Value] = n;
                }
            Func<string, int> get = k => { int n; return v.TryGetValue(k, out n) ? n : 0; };
            w = get("screenWidth"); h = get("screenHeight");
            mode = get("windowed") == 0 ? "fullscreen" : get("borderlessWindow") != 0 ? "borderless" : "windowed";
        }

        static void WriteDisplay(int w, int h, string mode)
        {
            var want = new Dictionary<string, int> { { "screenWidth", w }, { "screenHeight", h }, { "windowed", mode == "fullscreen" ? 0 : 1 },
                                                     { "borderlessWindow", mode == "borderless" ? 1 : 0 } };
            var lines = File.Exists(OptionsCfg) ? new List<string>(File.ReadAllLines(OptionsCfg)) : new List<string>();
            var o = new List<string>(); var done = new List<string>(); string section = null;
            Action flush = () => { foreach (var kv in want) if (!done.Contains(kv.Key)) { o.Add("\t" + kv.Key + "=" + kv.Value); done.Add(kv.Key); } };
            foreach (var line in lines)
            {
                var sec = Regex.Match(line, @"^\s*\[(\w+)\]");
                if (sec.Success)
                {
                    if (section == "ClientGraphics") { while (o.Count > 0 && o[o.Count - 1].Trim() == "") o.RemoveAt(o.Count - 1); flush(); o.Add(""); }
                    section = sec.Groups[1].Value;
                }
                else
                {
                    var k = Regex.Match(line, @"^\s*(\w+)\s*=");
                    if (section == "ClientGraphics" && k.Success && want.ContainsKey(k.Groups[1].Value))
                    { o.Add("\t" + k.Groups[1].Value + "=" + want[k.Groups[1].Value]); done.Add(k.Groups[1].Value); continue; }
                }
                o.Add(line);
            }
            if (section == "ClientGraphics") flush();
            if (done.Count < want.Count) { o.Add(""); o.Add("[ClientGraphics]"); flush(); }
            File.WriteAllLines(OptionsCfg, o.ToArray());
        }

        void DisplaySettings()
        {
            int w, h; string m; ReadDisplay(out w, out h, out m);
            Display.Show(f, PanelC, Color.FromArgb(204, 221, 238), Gold, Color.FromArgb(19, 40, 58), "Segoe UI", w, h, m,
                         new[] { "borderless", "fullscreen", "windowed" }, (nw, nh, nm) => { WriteDisplay(nw, nh, nm); f.Invalidate(); });
        }

        void Play()
        {
            Art.Start(Path.Combine(Dir, "SWGEmu.exe"), "", Dir);
            var t = new System.Windows.Forms.Timer { Interval = 600 }; t.Tick += (s, e) => f.Close(); t.Start();
        }

        void Controller()
        {
            string p = Path.Combine(Dir, "controller");
            Art.Start(Directory.Exists(p) ? p : Dir, "", Dir);
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            try
            {
                if (Array.IndexOf(args, "--extract") >= 0 || !File.Exists(Path.Combine(ArtDir, "icon.png"))) Extract();
                if (Array.IndexOf(args, "--extract") < 0) new SwgLauncher().Run();
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Star Wars Galaxies launcher"); }
        }
    }
}
