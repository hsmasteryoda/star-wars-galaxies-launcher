// Copies the Star Wars Galaxies files SWGEmu still uses off the original game discs (28 of the 74 files, the big .tre
// archives, about 2.1 GB), so the installer only downloads the rest. Loaded by Install-SWG.ps1 with Add-Type.
// Sources: a folder with a copy of the discs (or one holding several), the discs' .iso files (read directly, nothing is
// mounted), or "cdrom": the discs themselves - it asks for each disc in turn and copies its archives to the hard disk first.
// Every file is checked against SWGEmu's MD5; anything that doesn't match is simply downloaded by the installer.
// The InstallShield 5/6 archive reading follows unshield (MIT, (C) 2003-2023 David Eriksson). C# 5, no extra libraries.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SwgDiscs
{
    // one disc file: a plain file, or a file inside an .iso
    public class Part
    {
        public string Path; public long Offset, Size;
        public Part(string path) { Path = path; Offset = 0; Size = new FileInfo(path).Length; }
        public Part(string path, long offset, long size) { Path = path; Offset = offset; Size = size; }
        public Slice Open() { return new Slice(this); }
    }

    public class Slice : IDisposable
    {
        FileStream f; long b, size;
        public Slice(Part p) { f = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20); b = p.Offset; size = p.Size; f.Position = b; }
        public void Seek(long pos) { f.Position = b + pos; }
        public int Read(byte[] buf, int off, int n)
        {
            long left = b + size - f.Position; if (n > left) n = (int)Math.Max(0, left);
            int got = 0;
            while (got < n) { int k = f.Read(buf, off + got, n - got); if (k <= 0) break; got += k; }
            return got;
        }
        public byte[] Read(int n) { var buf = new byte[n]; int k = Read(buf, 0, n); if (k != n) Array.Resize(ref buf, k); return buf; }
        public void Dispose() { f.Dispose(); }
    }

    public class Discs
    {
        const int Sector = 2048;
        public Func<string, bool> Ask;            // "insert disc 2" -> true = OK, false = skip the discs
        public Action<string> Status;
        public Action<int> Percent;
        public Func<List<string>> Drives = CdRoots;   // the discs in the CD drives (replaceable for testing)

        static uint U32(byte[] d, int p) { return BitConverter.ToUInt32(d, p); }
        static ushort U16(byte[] d, int p) { return BitConverter.ToUInt16(d, p); }
        static ulong U64(byte[] d, int p) { return BitConverter.ToUInt64(d, p); }

        // ---------------------------------------------------------------- where the disc files are
        public static Dictionary<string, Part> IsoFiles(string iso)
        {
            var o = new Dictionary<string, Part>();
            using (var f = File.OpenRead(iso))
            {
                var pvd = new byte[Sector]; f.Position = 16 * Sector; f.Read(pvd, 0, Sector);
                if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return o;
                long lba = U32(pvd, 158); int size = (int)U32(pvd, 166);
                var d = new byte[size]; f.Position = lba * Sector; f.Read(d, 0, size);
                int i = 0;
                while (i < d.Length)
                {
                    int n = d[i];
                    if (n == 0) { i = (i / Sector + 1) * Sector; continue; }
                    if ((d[i + 25] & 2) == 0)
                    {
                        string name = Encoding.GetEncoding(28591).GetString(d, i + 33, d[i + 32]).Split(';')[0].ToLowerInvariant();
                        if (!o.ContainsKey(name)) o[name] = new Part(iso, (long)U32(d, i + 2) * Sector, U32(d, i + 10));
                    }
                    i += n;
                }
            }
            return o;
        }

        public static Dictionary<string, Part> FolderFiles(string top, int depth)
        {
            var o = new Dictionary<string, Part>();
            Walk(top, depth, o);
            return o;
        }
        static void Walk(string dir, int depth, Dictionary<string, Part> o)
        {
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var f in files)
            {
                string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                if (n.StartsWith("data") && (n.EndsWith(".hdr") || n.EndsWith(".cab"))) { if (!o.ContainsKey(n)) o[n] = new Part(f); }
                else if (n.EndsWith(".iso")) foreach (var kv in IsoFiles(f)) if (!o.ContainsKey(kv.Key)) o[kv.Key] = kv.Value;
            }
            if (depth > 0) foreach (var d in dirs) Walk(d, depth - 1, o);
        }

        static List<string> CdRoots()
        {
            var o = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.DriveType == DriveType.CDRom && d.IsReady) o.Add(d.RootDirectory.FullName); } catch { }
            return o;
        }
        static bool HasCdDrive() { foreach (var d in DriveInfo.GetDrives()) if (d.DriveType == DriveType.CDRom) return true; return false; }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
        static void Eject() { try { mciSendString("set cdaudio door open", null, 0, IntPtr.Zero); } catch { } }

        // ---------------------------------------------------------------- InstallShield 5/6 archives
        public class Fd { public uint Name, Dir, LinkPrev; public ushort Flags, Volume; public byte LinkFlags; public ulong Size, CSize, DataOffset; public string Md5; }

        Dictionary<string, Part> parts; byte[] h; int major, cdOff; uint ftOff, ftOff2, dirCount, fileCount; uint[] table;

        void OpenCab(Dictionary<string, Part> p)
        {
            parts = p;
            Part hp = p.ContainsKey("data1.hdr") ? p["data1.hdr"] : p["data1.cab"];
            using (var s = hp.Open()) h = s.Read((int)hp.Size);
            if (U32(h, 0) != 0x28635349) throw new Exception("not an InstallShield archive");
            uint ver = U32(h, 4); cdOff = (int)U32(h, 12);
            if (ver >> 24 == 1) major = (int)((ver >> 12) & 0xf);
            else if (ver >> 24 == 2 || ver >> 24 == 4) major = (ver & 0xffff) != 0 ? (int)(ver & 0xffff) / 100 : 0;
            else major = 0;
            ftOff = U32(h, cdOff + 0xc); dirCount = U32(h, cdOff + 0x1c); fileCount = U32(h, cdOff + 0x28); ftOff2 = U32(h, cdOff + 0x2c);
            table = new uint[dirCount + fileCount];
            for (int i = 0; i < table.Length; i++) table[i] = U32(h, cdOff + (int)ftOff + 4 * i);
        }

        string Str(uint off)
        {
            int p = cdOff + (int)ftOff + (int)off, e = p; while (h[e] != 0) e++;
            return Encoding.GetEncoding(28591).GetString(h, p, e - p);
        }

        static string Hex(byte[] d, int p, int n) { var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append(d[p + i].ToString("x2")); return sb.ToString(); }

        Fd Descriptor(uint i)
        {
            var fd = new Fd(); int c = cdOff + (int)ftOff;
            if (major == 0 || major == 5)
            {
                int p = c + (int)table[dirCount + i];
                fd.Name = U32(h, p); fd.Dir = U32(h, p + 4); fd.Flags = U16(h, p + 8); fd.Size = U32(h, p + 10); fd.CSize = U32(h, p + 14);
                fd.DataOffset = U32(h, p + 0x26); fd.Md5 = major == 5 ? Hex(h, p + 0x2a, 16) : ""; fd.Volume = 1;
                return fd;
            }
            int q = c + (int)ftOff2 + (int)i * 0x57;
            fd.Flags = U16(h, q); fd.Size = U64(h, q + 2); fd.CSize = U64(h, q + 10); fd.DataOffset = U64(h, q + 18);
            fd.Md5 = Hex(h, q + 26, 16); fd.Name = U32(h, q + 58); fd.Dir = U16(h, q + 62);
            fd.LinkPrev = U32(h, q + 76); fd.LinkFlags = h[q + 84]; fd.Volume = U16(h, q + 85);
            return fd;
        }


        class Reader { public uint Index; public Fd Fd; public Slice F; public int Volume; public ulong Left; public uint FirstI, LastI; public int Seed; }

        void OpenVolume(Reader r, int v)
        {
            if (r.F != null) r.F.Dispose();
            Part part; if (!parts.TryGetValue("data" + v + ".cab", out part)) throw new IOException("data" + v + ".cab is missing");
            var f = part.Open(); f.Read(20);
            Fd fd = r.Fd; ulong firstOff, firstX, firstC, lastOff, lastX, lastC;
            if (major == 0 || major == 5)
            {
                var b = f.Read(40);
                r.FirstI = U32(b, 8); r.LastI = U32(b, 12);
                firstOff = U32(b, 16); firstX = U32(b, 20); firstC = U32(b, 24); lastOff = U32(b, 28); lastX = U32(b, 32); lastC = U32(b, 36);
                if (lastOff == 0) lastOff = 0x7fffffff;
                if (major == 5)
                {
                    if (r.Index < fileCount - 1 && r.Index == r.LastI && lastC != fd.CSize) fd.Flags |= 1;
                    else if (r.Index > 0 && r.Index == r.FirstI && firstC != fd.CSize) fd.Flags |= 1;
                }
            }
            else
            {
                var b = f.Read(64);
                r.FirstI = U32(b, 8); r.LastI = U32(b, 12);
                firstOff = U64(b, 16); firstX = U64(b, 24); firstC = U64(b, 32); lastOff = U64(b, 40); lastX = U64(b, 48); lastC = U64(b, 56);
            }
            ulong off, x, c;
            if ((fd.Flags & 1) != 0)
            {
                if (r.Index == r.LastI && lastOff != 0x7fffffff) { off = lastOff; x = lastX; c = lastC; }
                else if (r.Index == r.FirstI) { off = firstOff; x = firstX; c = firstC; }
                else { r.F = f; r.Volume = v; r.Left = 0; return; }
            }
            else { off = fd.DataOffset; x = fd.Size; c = fd.CSize; }
            r.F = f; r.Volume = v; r.Left = (fd.Flags & 4) != 0 ? c : x;
            f.Seek((long)off);
        }

        byte[] ReadBytes(Reader r, int n)
        {
            var o = new byte[n]; int got = 0;
            while (got < n)
            {
                int k = (int)Math.Min((ulong)(n - got), r.Left);
                if (k > 0)
                {
                    if (r.F.Read(o, got, k) != k) throw new IOException("disc read error");
                    got += k; r.Left -= (ulong)k;
                }
                if (got < n) OpenVolume(r, r.Volume + 1);
            }
            if ((r.Fd.Flags & 2) != 0)
                for (int i = 0; i < n; i++)
                {
                    int b = o[i] ^ 0xd5; b = ((b >> 2) | (b << 6)) & 0xff;
                    o[i] = (byte)((b - (r.Seed + i) % 0x47) & 0xff);
                }
            r.Seed += n;
            return o;
        }

        // writes file <index> to <dest>, returns its md5 (hex)
        string Extract(uint index, Fd fd, string dest, Action<long> progress)
        {
            if ((fd.LinkFlags & 1) != 0) return Extract(fd.LinkPrev, Descriptor(fd.LinkPrev), dest, progress);
            var r = new Reader { Index = index, Fd = new Fd { Flags = fd.Flags, Size = fd.Size, CSize = fd.CSize, DataOffset = fd.DataOffset } };
            int v = fd.Volume;
            while (true) { OpenVolume(r, v); if (major <= 5 && index > r.LastI) { v++; continue; } break; }
            ulong left = (fd.Flags & 4) != 0 ? fd.CSize : fd.Size; long done = 0;
            using (var md5 = MD5.Create())
            {
                using (var o = new FileStream(dest + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    while (left > 0)
                    {
                        byte[] data; int len;
                        if ((fd.Flags & 4) != 0)
                        {
                            int n = U16(ReadBytes(r, 2), 0);
                            if (n == 0) throw new IOException("bad compressed block");
                            var chunk = ReadBytes(r, n);
                            using (var z = new DeflateStream(new MemoryStream(chunk), CompressionMode.Decompress))
                            using (var ms = new MemoryStream())
                            {
                                int k;
                                try { while ((k = z.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, k); } catch (InvalidDataException) { }
                                data = ms.ToArray(); len = data.Length;
                            }
                            left -= (ulong)(2 + n);
                        }
                        else { data = ReadBytes(r, (int)Math.Min(left, 1UL << 20)); len = data.Length; left -= (ulong)len; }
                        md5.TransformBlock(data, 0, len, null, 0); o.Write(data, 0, len); done += len;
                        if (progress != null) progress(done);
                    }
                }
                r.F.Dispose();
                md5.TransformFinalBlock(new byte[0], 0, 0);
                if ((ulong)done != fd.Size) { File.Delete(dest + ".part"); throw new IOException("wrong size"); }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(dest + ".part", dest);
                return Hex(md5.Hash, 0, 16);
            }
        }

        // ---------------------------------------------------------------- the CD drive: one disc at a time
        static int DiscOf(string name) { int n; return int.TryParse(name.Substring(4, name.Length - 8), out n) && n > 2 ? n - 1 : 1; }

        Part CopyPart(Part p, string dest, string label)
        {
            long done = 0;
            using (var s = p.Open())
            using (var o = new FileStream(dest + ".part", FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[4 << 20]; int k;
                while ((k = s.Read(buf, 0, buf.Length)) > 0) { o.Write(buf, 0, k); done += k; if (Percent != null) Percent((int)(done * 100 / Math.Max(1, p.Size))); }
            }
            if (done != p.Size) throw new IOException("could only read " + done + " of " + p.Size + " bytes of " + label + " - is the disc scratched?");
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(dest + ".part", dest);
            return new Part(dest);
        }

        Dictionary<string, Part> FromCd(string stage, Func<Dictionary<string, Part>, List<string>> needMore)
        {
            Directory.CreateDirectory(stage);
            var have = new Dictionary<string, Part>();
            foreach (var f in Directory.GetFiles(stage)) if (!f.EndsWith(".part")) have[System.IO.Path.GetFileName(f).ToLowerInvariant()] = new Part(f);
            var want = new List<string> { "data1.hdr", "data1.cab" };
            int asked = 0;
            while (true)
            {
                var missing = want.FindAll(w => !have.ContainsKey(w));
                if (missing.Count == 0)
                {
                    var extra = needMore(have);
                    foreach (var e in extra) if (!want.Contains(e)) want.Add(e);
                    missing = want.FindAll(w => !have.ContainsKey(w));
                    if (missing.Count == 0) return have;
                }
                int disc = DiscOf(missing[0]);
                var found = new Dictionary<string, Part>();
                for (int tries = 0; tries < (asked == disc ? 8 : 1); tries++)   // just answered OK: give the drive time to spin up
                {
                    foreach (var root in Drives()) foreach (var kv in FolderFiles(root, 0)) found[kv.Key] = kv.Value;
                    if (missing.Exists(w => found.ContainsKey(w))) break;
                    if (asked == disc) System.Threading.Thread.Sleep(1500);
                }
                if (!missing.Exists(w => found.ContainsKey(w)))
                {
                    if (found.Count > 0 || asked != 0) { if (Drives == CdRoots) Eject(); }     // the wrong disc, or the one just copied: open the tray
                    if (Ask == null || !Ask("Insert Star Wars Galaxies disc " + disc + " (of the original 2003 discs) and click OK.")) return new Dictionary<string, Part>();
                    asked = disc; continue;
                }
                foreach (var w in missing)
                    if (found.ContainsKey(w))
                    {
                        if (Status != null) Status("Copying " + w + " from disc " + disc + "...");
                        have[w] = CopyPart(found[w], System.IO.Path.Combine(stage, w), w);
                    }
                asked = 0;
            }
        }

        // ---------------------------------------------------------------- main entry: returns how many files came off the discs
        public int Run(string sources, string listFile, string gameDir, string stage)
        {
            var need = new Dictionary<string, List<KeyValuePair<string, long>>>();
            foreach (var line in File.ReadAllLines(listFile))
            {
                var p = line.Trim().Split(';'); if (p.Length != 3) continue;
                string k = p[2].ToLowerInvariant();
                if (!need.ContainsKey(k)) need[k] = new List<KeyValuePair<string, long>>();
                need[k].Add(new KeyValuePair<string, long>(p[0], long.Parse(p[1])));
            }
            var found = new Dictionary<string, Part>(); bool cd = false;
            foreach (var raw in sources.Split(';'))
            {
                string s = raw.Trim().Trim('"'); if (s.Length == 0) continue;
                string ls = s.ToLowerInvariant();
                if (ls == "cdrom" || ls == "cd" || ls == "cd drive") cd = true;
                else if (ls.EndsWith(".iso") && File.Exists(s)) foreach (var kv in IsoFiles(s)) found[kv.Key] = kv.Value;
                else if (Directory.Exists(s)) foreach (var kv in FolderFiles(s, 3)) if (!found.ContainsKey(kv.Key)) found[kv.Key] = kv.Value;
                else if (Status != null) Status("Not found, skipped: " + s);
            }
            Func<List<object[]>> wanted = () =>
            {
                var o = new List<object[]>();
                for (uint i = 0; i < fileCount; i++)
                {
                    var fd = Descriptor(i);
                    if ((fd.Flags & 8) != 0 || fd.Name == 0 || fd.DataOffset == 0) continue;
                    List<KeyValuePair<string, long>> rels; if (!need.TryGetValue(fd.Md5, out rels)) continue;
                    foreach (var rel in rels)
                    {
                        if ((ulong)rel.Value != fd.Size) continue;
                        string dest = System.IO.Path.Combine(gameDir, rel.Key.Replace('/', '\\'));
                        if (File.Exists(dest) && new FileInfo(dest).Length == rel.Value) continue;
                        o.Add(new object[] { i, rel.Key, fd, dest });
                    }
                }
                return o;
            };
            if (cd && !found.ContainsKey("data1.hdr"))
            {
                if (Drives == CdRoots && !HasCdDrive()) { if (Status != null) Status("No CD drive found - everything will be downloaded."); return 0; }
                try
                {
                    var got = FromCd(stage, have =>
                    {
                        OpenCab(have); var vols = new SortedSet<int>();
                        foreach (var w in wanted())
                        {
                            var fd = (Fd)w[2];
                            vols.Add(fd.Volume); if ((fd.Flags & 1) != 0) vols.Add(fd.Volume + 1);   // a split file runs on into the next one
                        }
                        var names = new List<string>(); foreach (var v in vols) names.Add("data" + v + ".cab"); return names;
                    });
                    foreach (var kv in got) found[kv.Key] = kv.Value;
                }
                catch (Exception e) { if (Status != null) Status("Couldn't copy from the CD drive (" + e.Message + ") - everything will be downloaded."); Cleanup(stage); return 0; }
            }
            if (!found.ContainsKey("data1.hdr") && !found.ContainsKey("data1.cab")) { if (Status != null) Status("No Star Wars Galaxies disc files found - everything will be downloaded."); return 0; }
            List<object[]> todo;
            try { OpenCab(found); todo = wanted(); }
            catch (Exception e) { if (Status != null) Status("Those discs couldn't be read (" + e.Message + ") - everything will be downloaded."); return 0; }
            long total = 1; foreach (var t in todo) total += (long)((Fd)t[2]).Size;
            long before = 0; int ok = 0, num = 0;
            foreach (var t in todo)
            {
                num++; var fd = (Fd)t[2]; string dest = (string)t[3];
                if (Status != null) Status("From your discs (" + num + "/" + todo.Count + "): " + t[1]);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                long b0 = before;
                try
                {
                    string md5 = Extract((uint)t[0], fd, dest, d => { if (Percent != null) Percent((int)((b0 + d) * 100 / total)); });
                    if (md5 == fd.Md5) ok++; else File.Delete(dest);
                }
                catch (Exception e)
                {
                    if (Status != null) Status("Couldn't read " + t[1] + " from the discs (" + e.Message + ") - it will be downloaded.");
                    try { File.Delete(dest + ".part"); } catch { }
                }
                before += (long)fd.Size;
            }
            Cleanup(stage);
            if (Status != null) Status("Copied " + ok + " game files from your discs.");
            return ok;
        }

        static void Cleanup(string stage) { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } }
    }
}
