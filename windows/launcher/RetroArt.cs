// Shared bits for the period-style game launchers on Windows (C# 5, .NET Framework 4 - compiled on the player's PC by
// the installer with the compiler that ships with Windows). The artwork is never shipped: it is read out of the
// player's own game files (.exe resources, SWG .tre archives, DDS textures) and cached as PNG next to the launcher.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Retro
{
    public static class Art
    {
        // ------------------------------------------------------------ PE resources
        // key: "<type>/<name>" with numeric types/names as numbers, e.g. "2/186" (bitmap 186), "WAVE/189", "14/128"
        public static Dictionary<string, byte[]> PeResources(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            int pe = BitConverter.ToInt32(d, 0x3C);
            if (d[pe] != 'P' || d[pe + 1] != 'E') throw new Exception("not a PE file: " + path);
            int nsec = BitConverter.ToUInt16(d, pe + 6), optsz = BitConverter.ToUInt16(d, pe + 20);
            int opt = pe + 24, magic = BitConverter.ToUInt16(d, opt);
            int ddir = opt + (magic == 0x10B ? 96 : 112);
            int rva = BitConverter.ToInt32(d, ddir + 16);
            var secs = new List<int[]>();
            for (int i = 0; i < nsec; i++)
            {
                int s = pe + 24 + optsz + i * 40;
                int vsz = BitConverter.ToInt32(d, s + 8), va = BitConverter.ToInt32(d, s + 12);
                int rsz = BitConverter.ToInt32(d, s + 16), rptr = BitConverter.ToInt32(d, s + 20);
                secs.Add(new int[] { va, Math.Max(vsz, rsz), rptr });
            }
            Func<int, int> off = r =>
            {
                foreach (var s in secs) if (r >= s[0] && r < s[0] + s[1]) return r - s[0] + s[2];
                throw new Exception("rva outside sections");
            };
            int bas = off(rva);
            var res = new Dictionary<string, byte[]>();
            Func<uint, string> nameOf = e =>
            {
                if ((e & 0x80000000) != 0)
                {
                    int p = bas + (int)(e & 0x7fffffff); int n = BitConverter.ToUInt16(d, p);
                    return Encoding.Unicode.GetString(d, p + 2, n * 2).ToUpperInvariant();
                }
                return e.ToString();
            };
            Action<int, int, string, string> walk = null;
            walk = (p, level, t, name) =>
            {
                int ne = BitConverter.ToUInt16(d, p + 12), ni = BitConverter.ToUInt16(d, p + 14);
                for (int i = 0; i < ne + ni; i++)
                {
                    uint nm = BitConverter.ToUInt32(d, p + 16 + i * 8), ent = BitConverter.ToUInt32(d, p + 20 + i * 8);
                    string k = nameOf(nm);
                    string t2 = level == 0 ? k : t, n2 = level == 1 ? k : name;
                    if ((ent & 0x80000000) != 0) walk(bas + (int)(ent & 0x7fffffff), level + 1, t2, n2);
                    else
                    {
                        string key = t2 + "/" + n2;
                        if (res.ContainsKey(key)) continue;
                        int drva = BitConverter.ToInt32(d, bas + (int)ent), dsz = BitConverter.ToInt32(d, bas + (int)ent + 4);
                        var b = new byte[dsz]; Array.Copy(d, off(drva), b, 0, dsz); res[key] = b;
                    }
                }
            };
            walk(bas, 0, "", "");
            return res;
        }

        // ------------------------------------------------------------ images (ARGB pixel arrays -> Bitmap)
        public static Bitmap FromArgb(int w, int h, int[] px)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, bd.Scan0 + y * bd.Stride, w);
            bmp.UnlockBits(bd);
            return bmp;
        }

        // A DIB (BITMAPINFOHEADER + palette + pixels): bitmap resources and icon images
        public static Bitmap Dib(byte[] d, bool icon)
        {
            int hsz = BitConverter.ToInt32(d, 0), w = BitConverter.ToInt32(d, 4), h = BitConverter.ToInt32(d, 8);
            int bpp = BitConverter.ToUInt16(d, 14), comp = BitConverter.ToInt32(d, 16);
            int ncol = hsz >= 36 ? BitConverter.ToInt32(d, 32) : 0;
            if (icon) h /= 2;
            bool topDown = h < 0; h = Math.Abs(h);
            int off;
            if (bpp <= 8) { if (ncol == 0) ncol = 1 << bpp; off = hsz + ncol * 4; } else off = hsz + (comp == 3 ? 12 : 0);
            int stride = ((w * bpp + 31) / 32) * 4;
            var px = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = off + (topDown ? y : h - 1 - y) * stride;
                for (int x = 0; x < w; x++)
                {
                    int b, g, r, a = 255, pi = -1;
                    if (bpp == 32) { b = d[row + x * 4]; g = d[row + x * 4 + 1]; r = d[row + x * 4 + 2]; if (icon) a = d[row + x * 4 + 3]; }
                    else if (bpp == 24) { b = d[row + x * 3]; g = d[row + x * 3 + 1]; r = d[row + x * 3 + 2]; }
                    else
                    {
                        if (bpp == 8) pi = d[row + x];
                        else if (bpp == 4) { int v = d[row + x / 2]; pi = x % 2 == 0 ? v >> 4 : v & 15; }
                        else throw new Exception("unsupported bpp " + bpp);
                        b = d[hsz + pi * 4]; g = d[hsz + pi * 4 + 1]; r = d[hsz + pi * 4 + 2];
                    }
                    px[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
            if (icon && bpp < 32)
            {
                int moff = off + stride * h, ms = ((w + 31) / 32) * 4;
                for (int y = 0; y < h; y++)
                {
                    int row = moff + (topDown ? y : h - 1 - y) * ms;
                    for (int x = 0; x < w; x++) if (((d[row + x / 8] >> (7 - x % 8)) & 1) != 0) px[y * w + x] &= 0x00ffffff;
                }
            }
            return FromArgb(w, h, px);
        }

        public static Bitmap PeBitmap(Dictionary<string, byte[]> res, int id) { return Dib(res["2/" + id], false); }

        // The largest image of the first icon group, as PNG bytes (for the shortcut .ico and the window icon)
        public static Bitmap PeIcon(Dictionary<string, byte[]> res)
        {
            string grp = null;
            foreach (var k in res.Keys) if (k.StartsWith("14/") && (grp == null || string.CompareOrdinal(k, grp) < 0)) grp = k;
            if (grp == null) return null;
            byte[] g = res[grp]; int n = BitConverter.ToUInt16(g, 4), best = -1, bw = 0, bbpp = 0;
            for (int i = 0; i < n; i++)
            {
                int w = g[6 + i * 14]; if (w == 0) w = 256;
                int bpp = BitConverter.ToUInt16(g, 6 + i * 14 + 6), id = BitConverter.ToUInt16(g, 6 + i * 14 + 12);
                if (best < 0 || w > bw || (w == bw && bpp > bbpp)) { best = id; bw = w; bbpp = bpp; }
            }
            byte[] data;
            if (!res.TryGetValue("3/" + best, out data)) return null;
            if (data.Length > 4 && data[0] == 0x89 && data[1] == (byte)'P') return new Bitmap(new MemoryStream(data));
            return Dib(data, true);
        }

        // ------------------------------------------------------------ DDS (DXT1/3/5 and 32-bit)
        static int C565(int c, out int g, out int b)
        {
            g = ((c >> 5) & 63) * 255 / 63; b = (c & 31) * 255 / 31; return ((c >> 11) & 31) * 255 / 31;
        }

        public static Bitmap Dds(byte[] d)
        {
            int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
            string four = Encoding.ASCII.GetString(d, 84, 4);
            int p = 128; var px = new int[w * h];
            if (four != "DXT1" && four != "DXT3" && four != "DXT5")
            {
                for (int i = 0; i < w * h; i++) px[i] = BitConverter.ToInt32(d, p + i * 4);   // already BGRA = ARGB little-endian
                return FromArgb(w, h, px);
            }
            var alpha = new int[16]; var cr = new int[4]; var cg = new int[4]; var cb = new int[4];
            for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4)
                {
                    for (int i = 0; i < 16; i++) alpha[i] = 255;
                    if (four == "DXT3")
                    {
                        ulong a = BitConverter.ToUInt64(d, p);
                        for (int i = 0; i < 16; i++) alpha[i] = (int)((a >> (4 * i)) & 15) * 17;
                        p += 8;
                    }
                    else if (four == "DXT5")
                    {
                        int a0 = d[p], a1 = d[p + 1]; ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)d[p + 2 + i] << (8 * i);
                        var tab = new int[8]; tab[0] = a0; tab[1] = a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) tab[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                        else { for (int i = 1; i < 5; i++) tab[i + 1] = ((5 - i) * a0 + i * a1) / 5; tab[6] = 0; tab[7] = 255; }
                        for (int i = 0; i < 16; i++) alpha[i] = tab[(int)((bits >> (3 * i)) & 7)];
                        p += 8;
                    }
                    int c0 = BitConverter.ToUInt16(d, p), c1 = BitConverter.ToUInt16(d, p + 2);
                    uint cbits = BitConverter.ToUInt32(d, p + 4); p += 8;
                    cr[0] = C565(c0, out cg[0], out cb[0]); cr[1] = C565(c1, out cg[1], out cb[1]);
                    int trans = -1;
                    if (c0 > c1 || four != "DXT1")
                    {
                        cr[2] = (2 * cr[0] + cr[1]) / 3; cg[2] = (2 * cg[0] + cg[1]) / 3; cb[2] = (2 * cb[0] + cb[1]) / 3;
                        cr[3] = (cr[0] + 2 * cr[1]) / 3; cg[3] = (cg[0] + 2 * cg[1]) / 3; cb[3] = (cb[0] + 2 * cb[1]) / 3;
                    }
                    else
                    {
                        cr[2] = (cr[0] + cr[1]) / 2; cg[2] = (cg[0] + cg[1]) / 2; cb[2] = (cb[0] + cb[1]) / 2;
                        cr[3] = cg[3] = cb[3] = 0; trans = 3;
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx + i % 4, y = by + i / 4;
                        if (x >= w || y >= h) continue;
                        int ci = (int)((cbits >> (2 * i)) & 3), a = ci == trans ? 0 : alpha[i];
                        px[y * w + x] = (a << 24) | (cr[ci] << 16) | (cg[ci] << 8) | cb[ci];
                    }
                }
            return FromArgb(w, h, px);
        }

        // ------------------------------------------------------------ SWG .tre archives
        public class TreEntry { public string File; public int Length, Offset, Comp, CLen; }

        static byte[] Zlib(byte[] data, int len)
        {
            using (var ms = new MemoryStream(data, 2, data.Length - 2))      // skip the 2-byte zlib header
            using (var z = new DeflateStream(ms, CompressionMode.Decompress))
            {
                var outb = new byte[len]; int got = 0, n;
                while (got < len && (n = z.Read(outb, got, len - got)) > 0) got += n;
                return outb;
            }
        }

        public static void TreIndex(string path, Dictionary<string, TreEntry> into)
        {
            using (var f = File.OpenRead(path))
            {
                var hd = new byte[36]; f.Read(hd, 0, 36);
                if (Encoding.ASCII.GetString(hd, 0, 4) != "EERT") return;
                int n = BitConverter.ToInt32(hd, 8), tocOff = BitConverter.ToInt32(hd, 12), tocComp = BitConverter.ToInt32(hd, 16);
                int tocSize = BitConverter.ToInt32(hd, 20), nbComp = BitConverter.ToInt32(hd, 24), nbSize = BitConverter.ToInt32(hd, 28);
                int nbU = BitConverter.ToInt32(hd, 32);
                f.Seek(tocOff, SeekOrigin.Begin);
                var toc = new byte[tocSize]; f.Read(toc, 0, tocSize); if (tocComp == 2) toc = Zlib(toc, n * 24);
                var nb = new byte[nbSize]; f.Read(nb, 0, nbSize); if (nbComp == 2) nb = Zlib(nb, nbU);
                for (int i = 0; i < n; i++)
                {
                    int o = i * 24, noff = BitConverter.ToInt32(toc, o + 20), end = noff;
                    while (nb[end] != 0) end++;
                    into[Encoding.ASCII.GetString(nb, noff, end - noff)] = new TreEntry
                    {
                        File = path, Length = BitConverter.ToInt32(toc, o + 4), Offset = BitConverter.ToInt32(toc, o + 8),
                        Comp = BitConverter.ToInt32(toc, o + 12), CLen = BitConverter.ToInt32(toc, o + 16)
                    };
                }
            }
        }

        public static byte[] TreRead(TreEntry e)
        {
            using (var f = File.OpenRead(e.File))
            {
                f.Seek(e.Offset, SeekOrigin.Begin);
                var b = new byte[e.Comp != 0 ? e.CLen : e.Length]; f.Read(b, 0, b.Length);
                return e.Comp == 2 ? Zlib(b, e.Length) : b;
            }
        }

        // ------------------------------------------------------------ small helpers
        public static Bitmap Resize(Image img, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, 0, 0, w, h);
            }
            return b;
        }

        public static void SavePng(Image img, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            img.Save(path, ImageFormat.Png);
        }

        // A classic .ico (32-bit bitmaps at 256/48/32/16 px) - for the shortcuts and the window icon; every Windows reads it
        public static void SaveIco(Image img, string path)
        {
            int[] sizes = { 256, 48, 32, 16 };
            var blobs = new List<byte[]>();
            foreach (int s in sizes)
                using (var b = Resize(img, s, s))
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(0);
                    w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                    for (int y = s - 1; y >= 0; y--) for (int x = 0; x < s; x++) w.Write(b.GetPixel(x, y).ToArgb());
                    int mask = ((s + 31) / 32) * 4; w.Write(new byte[mask * s]);     // AND mask: all 0, alpha does the work
                    blobs.Add(ms.ToArray());
                }
            using (var f = new BinaryWriter(File.Create(path)))
            {
                f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
                int off = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    f.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32); f.Write(blobs[i].Length); f.Write(off);
                    off += blobs[i].Length;
                }
                foreach (var b in blobs) f.Write(b);
            }
        }

        public static Dictionary<string, string> ReadConf(string path)
        {
            var c = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return c;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim(); int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith("#")) c[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"', '\'');
            }
            return c;
        }

        public static string Get(Dictionary<string, string> c, string k, string def)
        {
            string v; return c.TryGetValue(k, out v) && v.Length > 0 ? v : def;
        }

        public static bool TcpUp(string host, int port, int ms)
        {
            try
            {
                using (var t = new TcpClient())
                {
                    var ar = t.BeginConnect(host, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(ms) && t.Connected;
                    return ok;
                }
            }
            catch { return false; }
        }

        public static void Start(string exe, string args, string dir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args ?? "");
            psi.WorkingDirectory = dir; psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }

        public static void Play(string wav)
        {
            try { if (File.Exists(wav)) new System.Media.SoundPlayer(wav).Play(); } catch { }
        }
    }

    // ---------------------------------------------------------------- a borderless, skinned window
    public class Skin : Form
    {
        public class Sprite
        {
            public Image Img, Hover, Pressed; public int X, Y; public bool Visible = true; public Action Click;
            public Rectangle Rect { get { return new Rectangle(X, Y, Img.Width, Img.Height); } }
        }
        public class Spot { public Rectangle Rect; public Action Click, Enter, Leave; }

        public Image Background;
        public List<Sprite> Sprites = new List<Sprite>();
        public List<Spot> Spots = new List<Spot>();
        public Action<Graphics> Painter;
        Sprite over, down; Spot overSpot; Point dragFrom; bool dragging;

        public Skin(string title, Image bg, Image icon, int extraH)
        {
            Text = title; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black; DoubleBuffered = true; KeyPreview = true;
            Background = bg; ClientSize = new Size(bg.Width, bg.Height + extraH);
            if (icon != null) using (var b = new Bitmap(icon, 64, 64)) Icon = System.Drawing.Icon.FromHandle(b.GetHicon());
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        public Sprite Button(int x, int y, Image normal, Image hover, Image pressed, Action click)
        {
            var s = new Sprite { Img = normal, Hover = hover ?? normal, Pressed = pressed ?? hover ?? normal, X = x, Y = y, Click = click };
            Sprites.Add(s); Invalidate(); return s;
        }

        public Spot Hotspot(Rectangle r, Action click, Action enter, Action leave)
        {
            var s = new Spot { Rect = r, Click = click, Enter = enter, Leave = leave }; Spots.Add(s); return s;
        }

        Sprite SpriteAt(Point p)
        {
            for (int i = Sprites.Count - 1; i >= 0; i--)
                if (Sprites[i].Visible && Sprites[i].Click != null && Sprites[i].Rect.Contains(p)) return Sprites[i];
            return null;
        }
        Spot SpotAt(Point p) { foreach (var s in Spots) if (s.Rect.Contains(p)) return s; return null; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(Background, 0, 0);
            if (Painter != null) Painter(g);
            foreach (var s in Sprites)
                if (s.Visible) g.DrawImageUnscaled(s == down ? s.Pressed : s == over ? s.Hover : s.Img, s.X, s.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y); return; }
            var s = SpriteAt(e.Location);
            if (s != over) { over = s; Invalidate(); }
            var sp = SpotAt(e.Location);
            if (sp != overSpot)
            {
                if (overSpot != null && overSpot.Leave != null) overSpot.Leave();
                overSpot = sp;
                if (sp != null && sp.Enter != null) sp.Enter();
                Invalidate();
            }
            Cursor = s != null || sp != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            over = null; if (overSpot != null && overSpot.Leave != null) overSpot.Leave(); overSpot = null; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            down = SpriteAt(e.Location);
            if (down == null && SpotAt(e.Location) == null) { dragging = true; dragFrom = e.Location; }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            var s = down; down = null; Invalidate();
            if (s != null && s.Rect.Contains(e.Location)) s.Click();
            else { var sp = SpotAt(e.Location); if (sp != null && s == null) sp.Click(); }
            base.OnMouseUp(e);
        }
    }

    // ---------------------------------------------------------------- setup-window banners (drawn, no art needed)
    public class MatrixRain : Control
    {
        const string Glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄ0123456789Z:.=*+-<>";
        int[] drops; Random rnd = new Random(); Timer t; Font f = new Font("Consolas", 10f); public string Title = "", Sub = "";
        public MatrixRain() { DoubleBuffered = true; BackColor = Color.Black; t = new Timer { Interval = 70 }; t.Tick += (s, e) => { Step(); Invalidate(); }; t.Start(); }
        void Step()
        {
            int cols = Math.Max(1, Width / 12);
            if (drops == null || drops.Length != cols) { drops = new int[cols]; for (int i = 0; i < cols; i++) drops[i] = rnd.Next(-30, 0); }
            for (int i = 0; i < cols; i++) drops[i] = drops[i] * 14 < Height + 200 ? drops[i] + 1 : rnd.Next(-20, 0);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Color.Black);
            if (drops != null)
                for (int i = 0; i < drops.Length; i++)
                    for (int k = 0; k < 14; k++)
                    {
                        int y = (drops[i] - k) * 14; if (y < 0 || y >= Height) continue;
                        var c = k == 0 ? Color.FromArgb(216, 255, 216) : Color.FromArgb(0, Math.Max(40, 230 - k * 16), 0);
                        using (var b = new SolidBrush(c)) g.DrawString(Glyphs[rnd.Next(Glyphs.Length)].ToString(), f, b, i * 12, y);
                    }
            if (Title.Length > 0)
            {
                var band = new Rectangle(0, Height / 2 - 34, Width, 70);
                g.FillRectangle(Brushes.Black, band); using (var p = new Pen(Color.FromArgb(31, 111, 31))) g.DrawRectangle(p, band);
                var sf = new StringFormat { Alignment = StringAlignment.Center };
                using (var tf = new Font("Georgia", 22f, FontStyle.Bold)) using (var b = new SolidBrush(Color.FromArgb(232, 255, 232)))
                    g.DrawString(Title, tf, b, Width / 2, Height / 2 - 30, sf);
                using (var b = new SolidBrush(Color.FromArgb(143, 224, 143))) g.DrawString(Sub, f, b, Width / 2, Height / 2 + 12, sf);
            }
        }
    }

    public class Starfield : Control
    {
        public string Title = "", Sub = ""; public Image Logo; Point[] stars;
        public Starfield() { DoubleBuffered = true; BackColor = Color.Black; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Color.Black); g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (stars == null) { var r = new Random(1977); stars = new Point[220]; for (int i = 0; i < stars.Length; i++) stars[i] = new Point(r.Next(Math.Max(1, Width)), r.Next(Math.Max(1, Height))); }
            foreach (var p in stars) g.FillRectangle(Brushes.Gray, p.X, p.Y, 1, 1);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var gold = Color.FromArgb(240, 184, 72);
            if (Logo != null) { int s = Height - 10; g.DrawImage(Logo, (Width - s) / 2, 5, s, s); }
            else using (var tf = new Font("Arial Black", 26f, FontStyle.Bold)) using (var b = new SolidBrush(gold))
                g.DrawString(Title, tf, b, new RectangleF(0, 0, Width, Height - 24), sf);
            using (var tf = new Font("Segoe UI", 9f)) using (var b = new SolidBrush(Color.FromArgb(154, 170, 187)))
                g.DrawString(Sub, tf, b, new RectangleF(0, Height - 26, Width, 22), sf);
        }
    }

    // ---------------------------------------------------------------- screen resolution picker (the games have none built in)
    public static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        // the real sizes this screen supports (not DPI-scaled), and its current one
        public static List<Size> Modes(out Size native)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            var o = new List<Size>();
            try
            {
                native = EnumDisplaySettings(null, -1, ref dm) ? new Size(dm.dmPelsWidth, dm.dmPelsHeight) : Screen.PrimaryScreen.Bounds.Size;
                for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
                {
                    var s = new Size(dm.dmPelsWidth, dm.dmPelsHeight);
                    if (dm.dmBitsPerPel >= 32 && s.Width >= 800 && !o.Contains(s)) o.Add(s);
                }
            }
            catch (Exception)                     // no Windows display API (e.g. Mono): the usual sizes that fit the screen
            {
                native = Screen.PrimaryScreen.Bounds.Size;
                foreach (var s in new[] { new Size(1024, 768), new Size(1280, 720), new Size(1280, 800), new Size(1280, 1024), new Size(1366, 768),
                                          new Size(1440, 900), new Size(1600, 900), new Size(1680, 1050), new Size(1920, 1080), new Size(1920, 1200),
                                          new Size(2560, 1080), new Size(2560, 1440), new Size(2560, 1600), new Size(3440, 1440), new Size(3840, 2160) })
                    if (s.Width <= native.Width && s.Height <= native.Height) o.Add(s);
            }
            if (!o.Contains(native)) o.Add(native);
            o.Sort((a, b) => a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Height.CompareTo(b.Height));
            return o;
        }

        public static string Aspect(int w, int h)
        {
            double r = (double)w / h; string best = null; double d = 9;
            foreach (var n in new[] { new KeyValuePair<double, string>(4 / 3.0, "4:3"), new KeyValuePair<double, string>(5 / 4.0, "5:4"),
                                      new KeyValuePair<double, string>(16 / 9.0, "16:9"), new KeyValuePair<double, string>(16 / 10.0, "16:10"),
                                      new KeyValuePair<double, string>(3 / 2.0, "3:2"), new KeyValuePair<double, string>(21 / 9.0, "21:9"),
                                      new KeyValuePair<double, string>(32 / 9.0, "32:9") })
                if (Math.Abs(n.Key - r) < d) { d = Math.Abs(n.Key - r); best = n.Value; }
            return d < 0.03 ? best : w + ":" + h;
        }

        // Pick a resolution and fullscreen / windowed / borderless; save(w, h, mode) writes the game's config.
        // modes: which of "fullscreen", "borderless", "windowed" the game supports.
        public static void Show(IWin32Window owner, Color bg, Color fg, Color accent, Color box, string font,
                                int w, int h, string mode, string[] modes, Action<int, int, string> save)
        {
            Size native; var sizes = Modes(out native);
            var cur = new Size(w, h); if (w > 0 && h > 0 && !sizes.Contains(cur)) { sizes.Add(cur); sizes.Sort((a, b) => a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Height.CompareTo(b.Height)); }
            var d = new Form { Text = "Display", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                               ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, BackColor = bg, ForeColor = fg,
                               ClientSize = new Size(380, 410), Font = new Font(font, 9.5f) };
            Func<string, int, float, Color, bool, Label> label = (t, y, size, c, bold) =>
            {
                var l = new Label { Text = t, Left = 16, Top = y, AutoSize = true, ForeColor = c, Font = new Font(font, size, bold ? FontStyle.Bold : FontStyle.Regular) };
                d.Controls.Add(l); return l;
            };
            label("DISPLAY", 10, 14f, accent, true);
            label("Your screen: " + native.Width + " x " + native.Height + " (" + Aspect(native.Width, native.Height) + ")", 40, 9.5f, fg, false);
            label("Resolution", 68, 9.5f, accent, true);
            var lb = new ListBox { Left = 16, Top = 90, Width = 348, Height = 150, BackColor = box, ForeColor = fg, BorderStyle = BorderStyle.FixedSingle,
                                   Font = new Font("Consolas", 10f), IntegralHeight = false };
            foreach (var s in sizes)
                lb.Items.Add(string.Format("{0,5} x {1,-5}  {2,-5}{3}", s.Width, s.Height, Aspect(s.Width, s.Height), s == native ? "  (your screen)" : ""));
            int pick = sizes.IndexOf(cur); lb.SelectedIndex = pick >= 0 ? pick : sizes.IndexOf(native);
            d.Controls.Add(lb);
            label("Mode", 250, 9.5f, accent, true);
            var names = new Dictionary<string, string> { { "fullscreen", "Fullscreen" }, { "windowed", "Windowed" },
                                                         { "borderless", "Borderless window (fullscreen look, easy Alt+Tab)" } };
            var radios = new List<RadioButton>(); int ry = 272;
            foreach (var m in modes)
            {
                var r = new RadioButton { Text = names[m], Tag = m, Left = 20, Top = ry, AutoSize = true, ForeColor = fg, Checked = m == mode };
                d.Controls.Add(r); radios.Add(r); ry += 22;
            }
            if (!radios.Exists(r => r.Checked)) radios[0].Checked = true;
            label("Tip: your screen's own size looks sharpest.", ry + 4, 8.5f, fg, false);
            Func<string, int, Button> btn = (t, x) =>
            {
                var b = new Button { Text = t, Left = x, Top = 368, Width = 84, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = box, ForeColor = accent,
                                     Font = new Font(font, 9.5f, FontStyle.Bold) };
                b.FlatAppearance.BorderColor = accent; d.Controls.Add(b); return b;
            };
            var ok = btn("Save", 188); var cancel = btn("Cancel", 280);
            ok.Click += (s, e) =>
            {
                var sz = lb.SelectedIndex >= 0 ? sizes[lb.SelectedIndex] : native;
                save(sz.Width, sz.Height, (string)radios.Find(r => r.Checked).Tag); d.Close();
            };
            cancel.Click += (s, e) => d.Close();
            d.AcceptButton = ok; d.CancelButton = cancel;
            d.ShowDialog(owner);
        }
    }
}
