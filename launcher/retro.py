"""Shared bits for the period-style game launchers (Linux, Python 3 + Tk only - no other packages).

The artwork is never shipped: it is read at install time out of the player's own game files (Windows .exe resources,
SWG .tre archives, DDS textures, GIFs) and cached as PNG next to the launcher.
"""
import os, re, socket, struct, subprocess, sys, zlib

# ---------------------------------------------------------------- PNG out
def png_bytes(w, h, rgba):
    """rgba: bytes of length w*h*4."""
    raw = b"".join(b"\0" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))

def save_png(path, w, h, rgba):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path + ".part", "wb") as f:
        f.write(png_bytes(w, h, rgba))
    os.replace(path + ".part", path)

# ---------------------------------------------------------------- Windows DIB (BMP resources, icons)
def dib_decode(d, icon=False):
    """A DIB (BITMAPINFOHEADER + palette + pixels) -> (w, h, rgba). Handles 4/8/24/32-bit, bottom-up or top-down."""
    hsz, w, h, _, bpp, comp = struct.unpack_from("<IiiHHI", d, 0)
    ncol = struct.unpack_from("<I", d, 32)[0] if hsz >= 36 else 0
    if icon:
        h //= 2                        # icons store colour + AND mask
    top_down = h < 0; h = abs(h)
    if bpp <= 8:
        ncol = ncol or (1 << bpp)
        pal = [d[hsz + i * 4:hsz + i * 4 + 4] for i in range(ncol)]
        off = hsz + ncol * 4
    else:
        pal = None; off = hsz + (12 if comp == 3 else 0)
    stride = ((w * bpp + 31) // 32) * 4
    out = bytearray(w * h * 4)
    for y in range(h):
        row = d[off + (y if top_down else h - 1 - y) * stride:]
        o = y * w * 4
        for x in range(w):
            if bpp == 32:
                b, g, r, a = row[x * 4:x * 4 + 4]
                if not icon: a = 255
            elif bpp == 24:
                b, g, r = row[x * 3:x * 3 + 3]; a = 255
            elif bpp == 8:
                b, g, r = pal[row[x]][:3]; a = 255
            elif bpp == 4:
                v = row[x // 2]; b, g, r = pal[(v >> 4) if x % 2 == 0 else (v & 15)][:3]; a = 255
            else:
                raise ValueError("unsupported bpp %d" % bpp)
            out[o + x * 4:o + x * 4 + 4] = bytes((r, g, b, a))
    if icon and bpp < 32:              # apply the AND mask for transparency
        moff = off + stride * h; ms = ((w + 31) // 32) * 4
        for y in range(h):
            row = d[moff + (y if top_down else h - 1 - y) * ms:]
            for x in range(w):
                if row[x // 8] >> (7 - x % 8) & 1:
                    out[(y * w + x) * 4 + 3] = 0
    return w, h, bytes(out)

# ---------------------------------------------------------------- PE resources
def pe_resources(path):
    """{(type, name): bytes} for every resource in a Windows .exe/.dll (first language only)."""
    with open(path, "rb") as f:
        d = f.read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    assert d[pe:pe + 4] == b"PE\0\0", "not a PE file"
    nsec, = struct.unpack_from("<H", d, pe + 6); optsz, = struct.unpack_from("<H", d, pe + 20)
    opt = pe + 24; magic, = struct.unpack_from("<H", d, opt)
    ddir = opt + (96 if magic == 0x10B else 112)
    rva, size = struct.unpack_from("<II", d, ddir + 2 * 8)
    secs = []
    for i in range(nsec):
        s = pe + 24 + optsz + i * 40
        vsz, va, rsz, rptr = struct.unpack_from("<IIII", d, s + 8)
        secs.append((va, max(vsz, rsz), rptr))
    def off(r):
        for va, sz, rp in secs:
            if va <= r < va + sz:
                return r - va + rp
        raise ValueError("rva outside sections")
    base = off(rva); res = {}
    def name_of(e):
        if e & 0x80000000:
            p = base + (e & 0x7fffffff); n, = struct.unpack_from("<H", d, p)
            return d[p + 2:p + 2 + n * 2].decode("utf-16-le")
        return e
    def walk(p, level, key):
        ne, ni = struct.unpack_from("<HH", d, p + 12)
        for i in range(ne + ni):
            nm, ent = struct.unpack_from("<II", d, p + 16 + i * 8)
            k = key + (name_of(nm),)
            if ent & 0x80000000:
                walk(base + (ent & 0x7fffffff), level + 1, k)
                if level == 1 and k[:2] in res:
                    continue
            elif k[:2] not in res:
                drva, dsz = struct.unpack_from("<II", d, base + ent)
                res[k[:2]] = d[off(drva):off(drva) + dsz]
    walk(base, 0, ())
    return res

def pe_bitmap(res, name):
    return dib_decode(res[(2, name)])

def pe_best_icon(res):
    """Largest image of the first icon group -> (w, h, rgba) or PNG bytes."""
    groups = sorted((k for k in res if k[0] == 14), key=lambda k: str(k[1]))
    if not groups:
        return None
    g = res[groups[0]]; n, = struct.unpack_from("<H", g, 4); best = None
    for i in range(n):
        w, h, _, _, _, bpp, _, iid = struct.unpack_from("<BBBBHHIH", g, 6 + i * 14)
        w = w or 256
        if best is None or (w, bpp) > best[:2]:
            best = (w, bpp, iid)
    data = res.get((3, best[2]))
    if data is None:
        return None
    if data[:4] == b"\x89PNG":
        return data
    return dib_decode(data, icon=True)

# ---------------------------------------------------------------- DDS (DXT1/3/5, 32-bit)
def _c565(c):
    return ((c >> 11 & 31) * 255 // 31, (c >> 5 & 63) * 255 // 63, (c & 31) * 255 // 31)

def dds_decode(d):
    h, w = struct.unpack_from("<II", d, 12)
    fourcc = d[84:88]; p = 128
    out = bytearray(w * h * 4)
    if fourcc not in (b"DXT1", b"DXT3", b"DXT5"):
        bpp, = struct.unpack_from("<I", d, 88)
        if bpp != 32:
            raise ValueError("unsupported DDS " + repr(fourcc))
        for i in range(w * h):
            b, g, r, a = d[p + i * 4:p + i * 4 + 4]
            out[i * 4:i * 4 + 4] = bytes((r, g, b, a))
        return w, h, bytes(out)
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            alpha = [255] * 16
            if fourcc == b"DXT3":
                a = int.from_bytes(d[p:p + 8], "little")
                alpha = [(a >> (4 * i) & 15) * 17 for i in range(16)]; p += 8
            elif fourcc == b"DXT5":
                a0, a1 = d[p], d[p + 1]; bits = int.from_bytes(d[p + 2:p + 8], "little")
                if a0 > a1:
                    tab = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
                else:
                    tab = [a0, a1] + [((5 - i) * a0 + i * a1) // 5 for i in range(1, 5)] + [0, 255]
                alpha = [tab[bits >> (3 * i) & 7] for i in range(16)]; p += 8
            c0, c1, bits = struct.unpack_from("<HHI", d, p); p += 8
            r0, r1 = _c565(c0), _c565(c1)
            if c0 > c1 or fourcc != b"DXT1":
                cols = [r0, r1, tuple((2 * a + b) // 3 for a, b in zip(r0, r1)), tuple((a + 2 * b) // 3 for a, b in zip(r0, r1))]
                trans = -1
            else:
                cols = [r0, r1, tuple((a + b) // 2 for a, b in zip(r0, r1)), (0, 0, 0)]
                trans = 3
            for i in range(16):
                x, y = bx + i % 4, by + i // 4
                if x < w and y < h:
                    ci = bits >> (2 * i) & 3; r, g, b = cols[ci]
                    o = (y * w + x) * 4
                    out[o:o + 4] = bytes((r, g, b, 0 if ci == trans else alpha[i]))
    return w, h, bytes(out)

# ---------------------------------------------------------------- SWG .tre archives
def tre_entries(path):
    with open(path, "rb") as f:
        magic, ver, n, toc_off, toc_comp, toc_size, nb_comp, nb_size, nb_usize = struct.unpack("<4s4s7I", f.read(36))
        if magic != b"EERT":
            return {}
        f.seek(toc_off)
        toc = f.read(toc_size); toc = zlib.decompress(toc) if toc_comp == 2 else toc
        names = f.read(nb_size); names = zlib.decompress(names) if nb_comp == 2 else names
    out = {}
    for i in range(n):
        crc, length, off, comp, clen, noff = struct.unpack_from("<6I", toc, i * 24)
        out[names[noff:names.index(b"\0", noff)].decode("latin-1")] = (path, length, off, comp, clen)
    return out

def tre_read(e):
    path, length, off, comp, clen = e
    with open(path, "rb") as f:
        f.seek(off); d = f.read(clen if comp else length)
    return zlib.decompress(d) if comp == 2 else d

# ---------------------------------------------------------------- image helpers (pure Python, small images only)
def crop(img, x, y, w, h):
    iw, ih, px = img
    return w, h, b"".join(px[((y + r) * iw + x) * 4:((y + r) * iw + x + w) * 4] for r in range(h))

def resize(img, w, h):
    """Box-filter downscale / nearest upscale - good enough for icons and thumbnails."""
    iw, ih, px = img; out = bytearray(w * h * 4)
    for y in range(h):
        y0, y1 = y * ih // h, max(y * ih // h + 1, (y + 1) * ih // h)
        for x in range(w):
            x0, x1 = x * iw // w, max(x * iw // w + 1, (x + 1) * iw // w)
            acc = [0, 0, 0, 0]; n = 0
            for yy in range(y0, y1):
                for xx in range(x0, x1):
                    o = (yy * iw + xx) * 4
                    for k in range(4):
                        acc[k] += px[o + k]
                    n += 1
            out[(y * w + x) * 4:(y * w + x) * 4 + 4] = bytes(v // n for v in acc)
    return w, h, bytes(out)

# ---------------------------------------------------------------- small runtime helpers
def read_conf(path):
    """KEY=value lines (shell style) -> dict."""
    conf = {}
    try:
        for line in open(path, encoding="utf-8"):
            line = line.strip()
            if "=" in line and not line.startswith("#"):
                k, v = line.split("=", 1); conf[k.strip()] = v.strip().strip('"').strip("'")
    except OSError:
        pass
    return conf

def tcp_up(host, port, timeout=2.0):
    try:
        socket.create_connection((host, int(port)), timeout).close()
        return True
    except OSError:
        return False

def detach(cmd, cwd=None):
    """Start the game and let the launcher close (like the originals did)."""
    subprocess.Popen(cmd, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL, start_new_session=True)

def open_path(p):
    detach(["xdg-open", p])

# ---------------------------------------------------------------- Tk: skinned borderless windows
def tk_scale(root):
    """Old launchers were made for 800x600 desktops; double them on big screens."""
    return 2 if root.winfo_screenheight() >= 1300 else 1

class Skin:
    """A borderless window drawn from one background image, with image buttons on top (normal/hover/pressed)."""
    def __init__(self, title, bg_png, icon_png=None, scale=None, extra_h=0, bg_color="black"):
        """extra_h: room (ORIGINAL pixels) below the background image for a status strip."""
        import tkinter as tk
        self.tk = tk
        self.root = tk.Tk(className=title)
        self.root.title(title)
        self.s = scale or tk_scale(self.root)
        self.bg = self.img(bg_png)
        self.w, self.h = self.bg.width(), self.bg.height() + extra_h * self.s
        self.root.overrideredirect(True)
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        self.root.geometry("%dx%d+%d+%d" % (self.w, self.h, (sw - self.w) // 2, (sh - self.h) // 2))
        self.c = tk.Canvas(self.root, width=self.w, height=self.h, highlightthickness=0, bd=0, bg=bg_color)
        self.c.pack()
        self.c.create_image(0, 0, image=self.bg, anchor="nw")
        if icon_png and os.path.exists(icon_png):
            self._icon = tk.PhotoImage(file=icon_png); self.root.iconphoto(True, self._icon)
        self._keep = []; self._drag = None; self._spots = []; self._over = None
        self.c.bind("<Motion>", self._spot_motion, add="+")
        self.c.bind("<Leave>", lambda e: self._spot_set(None), add="+")
        self.c.bind("<ButtonRelease-1>", self._spot_click, add="+")
        self.c.bind("<ButtonPress-1>", self._press, add="+")
        self.c.bind("<B1-Motion>", self._move, add="+")
        self.root.bind("<Escape>", lambda e: self.root.destroy())
        self.root.bind("<Map>", self._remap)

    def img(self, path):
        p = self.tk.PhotoImage(file=path)
        return p.zoom(self.s) if self.s > 1 else p

    def _press(self, e):
        on_button = (self.c.find_withtag("current") and "btn" in self.c.gettags("current")) or self._spot_at(e.x, e.y)
        self._drag = None if on_button else (e.x_root - self.root.winfo_x(), e.y_root - self.root.winfo_y())

    def _move(self, e):
        if self._drag:
            self.root.geometry("+%d+%d" % (e.x_root - self._drag[0], e.y_root - self._drag[1]))

    def minimize(self):
        self.root.overrideredirect(False); self.root.iconify()

    def _remap(self, e):
        if self.root.state() == "normal" and not self.root.overrideredirect():
            self.root.after(10, lambda: self.root.overrideredirect(True))

    def button(self, x, y, normal, hover=None, pressed=None, command=None, disabled=False):
        """Image button at (x, y) in ORIGINAL (unscaled) pixels. Images are file paths."""
        ims = [self.img(p) if p else None for p in (normal, hover, pressed)]
        n = ims[0]; hv = ims[1] or n; pr = ims[2] or hv
        self._keep += ims
        item = self.c.create_image(x * self.s, y * self.s, image=n, anchor="nw", tags=("btn",))
        if disabled:
            return item
        self.c.tag_bind(item, "<Enter>", lambda e: (self.c.itemconfig(item, image=hv), self.c.config(cursor="hand2")))
        self.c.tag_bind(item, "<Leave>", lambda e: (self.c.itemconfig(item, image=n), self.c.config(cursor="")))
        self.c.tag_bind(item, "<ButtonPress-1>", lambda e: self.c.itemconfig(item, image=pr))
        def release(e):
            self.c.itemconfig(item, image=hv)
            if command:
                self.root.after(80, command)
        self.c.tag_bind(item, "<ButtonRelease-1>", release)
        return item

    def hotspot(self, x0, y0, x1, y1, command, enter=None, leave=None):
        """Invisible clickable rectangle (ORIGINAL pixels), like an HTML image map. Hit-tested on the whole canvas
        (Tk only delivers events to filled shapes)."""
        self._spots.append(((x0 * self.s, y0 * self.s, x1 * self.s, y1 * self.s), command, enter, leave))

    def clear_spots(self):
        self._spot_set(None); self._spots = []

    def _spot_at(self, x, y):
        return next((sp for sp in self._spots if sp[0][0] <= x <= sp[0][2] and sp[0][1] <= y <= sp[0][3]), None)

    def _spot_set(self, sp):
        if sp is self._over:
            return
        if self._over and self._over[3]:
            self._over[3]()
        self._over = sp
        self.c.config(cursor="hand2" if sp else "")
        if sp and sp[2]:
            sp[2]()

    def _spot_motion(self, e):
        self._spot_set(self._spot_at(e.x, e.y))

    def _spot_click(self, e):
        sp = self._spot_at(e.x, e.y)
        if sp:
            self.root.after(60, sp[1])

    def text(self, x, y, s, size=9, color="white", anchor="nw", font="DejaVu Sans", weight="normal", width=None, tags=()):
        return self.c.create_text(x * self.s, y * self.s, text=s, fill=color, anchor=anchor, tags=tags,
                                  font=(font, -size * self.s, weight), width=width and width * self.s)

    def later(self, ms, fn):
        self.root.after(ms, fn)

    def run(self):
        self.root.mainloop()


def background(fn, done, root):
    """Run fn() in a thread and call done(result) on the Tk thread."""
    import threading
    box = {}
    def work():
        try:
            box["r"] = fn()
        except Exception as e:      # status probes must never crash the launcher
            box["r"] = e
    t = threading.Thread(target=work, daemon=True); t.start()
    def poll():
        if t.is_alive():
            root.after(150, poll)
        else:
            done(box.get("r"))
    root.after(150, poll)


class Wizard:
    """Game-themed setup wizard in front of the install-*-linux.sh scripts (they do the real work; we pass --yes).

    theme: title, fg, accent, bg, font, banner(canvas, w, h) -> draws the banner, script, args(values) -> list,
           fields: [(key, label, default, kind)] kind = "text" | "folder" | "check" | "discs" (folder / .iso files / CD drive,
           several joined with ";"), size (GB, for the welcome text),
           blurb (welcome text), launch (command list to start the game afterwards, given the install folder).
    The script's stdout drives the progress: "# text" lines are status, bare numbers are percent, and "?? text" asks the
    player (e.g. to change discs): the answer goes back on the script's stdin as "ok" or "skip".
    """
    def __init__(self, theme):
        import tkinter as tk
        self.tk, self.t = tk, theme
        self.root = tk.Tk(className=theme["title"]); self.root.title(theme["title"] + " Setup")
        self.root.configure(bg=theme["bg"]); self.root.resizable(False, False)
        self.s = s = tk_scale(self.root)
        self.W = 520 * s
        self.banner = tk.Canvas(self.root, width=self.W, height=theme.get("banner_h", 200) * s, bg="black", highlightthickness=0)
        self.banner.pack()
        theme["banner"](self.banner, self.W, theme.get("banner_h", 200) * s, s)
        self.body = tk.Frame(self.root, bg=theme["bg"], padx=16 * s, pady=10 * s); self.body.pack(fill="both", expand=True)
        self.foot = tk.Frame(self.root, bg=theme["bg"], padx=16 * s, pady=10 * s); self.foot.pack(fill="x")
        self.vars = {}; self.proc = None; self.dest = None
        self.page_welcome()
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        self.root.update_idletasks()
        self.root.geometry("+%d+%d" % ((sw - self.root.winfo_width()) // 2, (sh - self.root.winfo_height()) // 3))
        self.root.protocol("WM_DELETE_WINDOW", self.cancel)
        self.root.mainloop()

    # ---- widgets in the theme
    def font(self, size, weight="normal"):
        return (self.t.get("font", "DejaVu Sans"), -size * self.s, weight)

    def label(self, parent, text, size=10, color=None, **kw):
        l = self.tk.Label(parent, text=text, bg=self.t["bg"], fg=color or self.t["fg"], font=self.font(size), justify="left",
                          anchor="w", wraplength=480 * self.s, **kw)
        l.pack(fill="x", anchor="w"); return l

    def btn(self, text, cmd, side="right"):
        b = self.tk.Button(self.foot, text=text, command=cmd, bg=self.t["btn_bg"], fg=self.t["accent"], activebackground=self.t["accent"],
                           activeforeground=self.t["btn_bg"], relief="ridge", bd=2 * self.s, font=self.font(10, "bold"),
                           padx=12 * self.s, pady=2 * self.s, highlightthickness=0, cursor="hand2")
        b.pack(side=side, padx=4 * self.s); return b

    def clear(self):
        for f in (self.body, self.foot):
            for w in f.winfo_children():
                w.destroy()

    # ---- pages
    def page_welcome(self):
        tk = self.tk; self.clear()
        self.label(self.body, self.t["blurb"], 10)
        tk.Frame(self.body, height=8 * self.s, bg=self.t["bg"]).pack()
        for key, text, default, kind in self.t["fields"]:
            row = tk.Frame(self.body, bg=self.t["bg"]); row.pack(fill="x", pady=3 * self.s)
            if kind == "check":
                v = self.vars.setdefault(key, tk.BooleanVar(value=bool(default)))
                tk.Checkbutton(row, text=text, variable=v, bg=self.t["bg"], fg=self.t["fg"], selectcolor=self.t["btn_bg"],
                               activebackground=self.t["bg"], activeforeground=self.t["accent"], font=self.font(10),
                               highlightthickness=0).pack(anchor="w")
                continue
            tk.Label(row, text=text, bg=self.t["bg"], fg=self.t["fg"], font=self.font(10), width=14, anchor="w").pack(side="left")
            v = self.vars.setdefault(key, tk.StringVar(value=default))
            tk.Entry(row, textvariable=v, bg=self.t["btn_bg"], fg=self.t["fg"], insertbackground=self.t["accent"], relief="flat",
                     font=self.font(10), highlightthickness=1, highlightbackground=self.t["accent"]).pack(side="left", fill="x", expand=True)
            if kind == "discs":
                def add(v, more):
                    if more:
                        v.set(";".join([x for x in v.get().split(";") if x.strip() and x.strip() != "cdrom"] + list(more)))
                def disc_folder(v=v):
                    from tkinter import filedialog
                    d = filedialog.askdirectory(title="The folder with a copy of your discs")
                    add(v, [d] if d else [])
                def disc_isos(v=v):
                    from tkinter import filedialog
                    add(v, filedialog.askopenfilenames(title="Your disc images", filetypes=[("Disc images", "*.iso *.ISO"), ("All files", "*")]))
                for t, cmd in (("Folder", disc_folder), ("ISO files", disc_isos), ("CD drive", lambda v=v: v.set("cdrom"))):
                    tk.Button(row, text=t, command=cmd, bg=self.t["btn_bg"], fg=self.t["fg"], relief="ridge",
                              font=self.font(9), highlightthickness=0).pack(side="left", padx=(4 * self.s, 0))
            if kind in ("folder", "folder_src"):
                def pick(v=v, kind=kind, default=default):
                    from tkinter import filedialog
                    d = filedialog.askdirectory(initialdir=os.path.dirname(v.get()) or os.path.expanduser("~"))
                    if d:
                        v.set(d if kind == "folder_src" else os.path.join(d, os.path.basename(default)))
                tk.Button(row, text="Browse...", command=pick, bg=self.t["btn_bg"], fg=self.t["fg"], relief="ridge",
                          font=self.font(9), highlightthickness=0).pack(side="left", padx=(6 * self.s, 0))
        self.btn("Cancel", self.cancel)
        self.btn("Install", self.page_install)

    def page_install(self):
        tk = self.tk
        values = {k: v.get() for k, v in self.vars.items()}
        self.dest = os.path.expanduser(values.get("dest", ""))
        self.clear()
        self.status = self.label(self.body, "Starting...", 10)
        self.bar = tk.Canvas(self.body, height=16 * self.s, bg=self.t["btn_bg"], highlightthickness=1,
                             highlightbackground=self.t["accent"])
        self.bar.pack(fill="x", pady=8 * self.s)
        self.fill = self.bar.create_rectangle(0, 0, 0, 16 * self.s, fill=self.t["accent"], outline="")
        self.detail = self.label(self.body, "", 8, color=self.t.get("dim", self.t["fg"]))
        self.cancel_btn = self.btn("Cancel", self.cancel)
        self.pct = None; self.pulse = 0
        cmd = [self.t["script"]] + self.t["args"](values)
        env = dict(os.environ, RETRO_WIZARD="1")
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                     bufsize=1, env=env)
        self.out = []
        import threading
        threading.Thread(target=self._read, daemon=True).start()
        self._tick()

    def _read(self):
        for line in self.proc.stdout:
            self.out.append(line.rstrip("\n"))
        self.proc.wait()

    def _tick(self):
        while self.out:
            line = self.out.pop(0).strip()
            if re.fullmatch(r"\d{1,3}", line):
                self.pct = min(100, int(line))
            elif line.startswith("# "):
                self.detail.config(text=line[2:])
            elif line.startswith("?? "):         # the script asks something (e.g. "insert disc 2"): OK / skip
                from tkinter import messagebox
                ok = messagebox.askokcancel(self.t["title"] + " Setup", line[3:], parent=self.root)
                try:
                    self.proc.stdin.write("ok\n" if ok else "skip\n"); self.proc.stdin.flush()
                except OSError:
                    pass
            elif line:
                self.last = line
                if not line.startswith(("ERROR", "WARNING")):
                    self.status.config(text=line[:120])
        w = self.bar.winfo_width()
        if self.pct is None:            # no percentage yet: a moving block, like the old installers
            self.pulse = (self.pulse + 6 * self.s) % (w + 80 * self.s)
            self.bar.coords(self.fill, self.pulse - 80 * self.s, 0, self.pulse, 16 * self.s)
        else:
            self.bar.coords(self.fill, 0, 0, w * self.pct / 100, 16 * self.s)
        if self.proc.poll() is None or self.out:
            self.root.after(100, self._tick)
        else:
            self.page_done(self.proc.returncode)

    def page_done(self, rc):
        self.clear(); self.proc = None
        if rc == 0:
            self.label(self.body, self.t["title"] + " is installed.", 12, color=self.t["accent"])
            self.label(self.body, "\nIt's in " + self.dest + "\nThere's a shortcut in your menu" +
                       (" and on your desktop." if self.vars.get("desktop", self.tk.BooleanVar(value=False)).get() else "."), 10)
            self.btn("Close", self.root.destroy)
            self.btn("Play now", self.launch)
        else:
            self.label(self.body, "Setup didn't finish.", 12, color="#ff7070")
            self.label(self.body, "\n" + getattr(self, "last", "unknown error") + "\n\nYou can run setup again - it keeps "
                       "what's already done.", 10)
            self.btn("Close", self.root.destroy)
            self.btn("Try again", self.page_welcome)

    def launch(self):
        detach(self.t["launch"](self.dest)); self.root.after(300, self.root.destroy)

    def cancel(self):
        if self.proc and self.proc.poll() is None:
            from tkinter import messagebox
            if not messagebox.askyesno("Cancel setup", "Stop the installation? You can run setup again later."):
                return
            self.proc.terminate()
        self.root.destroy()


def matrix_rain(canvas, w, h, s, title="", sub=""):
    """Animated falling green code (for The Matrix Online) - drawn, no art needed."""
    import random
    glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄ0123456789Z:.=*+-<>"
    cols = w // (12 * s); drops = [random.randrange(-30, 0) for _ in range(cols)]
    canvas.configure(bg="black")
    def step():
        canvas.delete("rain")
        for i in range(cols):
            y = drops[i]
            for k in range(14):
                yy = (y - k) * 14 * s
                if 0 <= yy < h:
                    col = "#d8ffd8" if k == 0 else "#%02x%02x%02x" % (0, max(40, 230 - k * 16), 0)
                    canvas.create_text(i * 12 * s + 6 * s, yy, text=random.choice(glyphs), fill=col, tags="rain",
                                       font=("DejaVu Sans Mono", -12 * s))
            drops[i] = y + 1 if y * 14 * s < h + 200 * s else random.randrange(-20, 0)
        canvas.tag_raise("title")
        canvas.after(70, step)
    if title:
        canvas.create_rectangle(0, h // 2 - 34 * s, w, h // 2 + 36 * s, fill="black", outline="#1f6f1f", width=s, tags="title")
        canvas.create_text(w // 2, h // 2 - 10 * s, text=title, fill="#e8ffe8", font=("DejaVu Serif", -26 * s, "bold"), tags="title")
        canvas.create_text(w // 2, h // 2 + 22 * s, text=sub, fill="#8fe08f", font=("DejaVu Sans Mono", -11 * s), tags="title")
    step()


if __name__ == "__main__":
    sys.exit("This is a library used by the game launchers.")
