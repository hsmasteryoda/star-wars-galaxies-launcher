#!/usr/bin/env python3
"""Star Wars Galaxies launcher in the spirit of the 2003-2005 LaunchPad, built from the game's own art.

At install time the logo, the planet loading screens and the opening crawl are read out of YOUR client's .tre files
(nothing is shipped). Shows the galaxy's status and starts the game.
usage: swg_launcher.py [--extract | --install]   (--install = setup wizard, run from the repo / USB folder)
"""
import os, random, re, socket, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import retro

DEST = os.path.dirname(HERE)
GAME = os.path.join(DEST, "pfx", "drive_c", "SWGEmu")
ART = os.path.join(HERE, "art")
CONF = retro.read_conf(os.path.join(DEST, "swg-installer.conf"))
PLANETS = ["tatooine", "naboo", "corellia", "dantooine", "dathomir", "endor", "lok", "rori", "talus"]
W, H = 640, 420                       # window size in original pixels
VERSION = "1.3.0"                     # this launcher's release; signed updates come from UPDATES (GitHub releases)
UPDATES = CONF.get("UPDATES", "hsmasteryoda/star-wars-galaxies-launcher")


def extract():
    idx = {}
    for f in sorted(os.listdir(GAME)):
        if f.endswith(".tre"):
            idx.update(retro.tre_entries(os.path.join(GAME, f)))     # later archives (patches) win
    os.makedirs(ART, exist_ok=True)
    def get(name, out, w, h):
        if name not in idx:
            return False
        img = retro.dds_decode(retro.tre_read(idx[name]))
        retro.save_png(os.path.join(ART, out), *retro.resize(img, w, h))
        return True
    if not get("texture/ui_starwars_logo_jtl.dds", "logo.png", 232, 232):
        get("texture/ui_starwars_logo.dds", "logo.png", 232, 232)
    for p in PLANETS:
        get("texture/loading/%s/ui_load_%s.dds" % (p, p), "planet_%s.png" % p, 216, 216)
    get("texture/intro_crawl.dds", "crawl.png", 176, 352)
    logo = retro.dds_decode(retro.tre_read(idx.get("texture/ui_starwars_logo_jtl.dds") or idx["texture/ui_starwars_logo.dds"]))
    retro.save_png(os.path.join(ART, "icon.png"), *retro.resize(logo, 128, 128))
    print("SWG launcher art saved in", ART)


def read_display(path=None):
    """(width, height, mode) from options.cfg ([ClientGraphics] screenWidth/screenHeight/windowed/borderlessWindow)."""
    v = {}
    try:
        for line in open(path or os.path.join(GAME, "options.cfg"), encoding="latin-1"):
            m = re.match(r"\s*(\w+)\s*=\s*(\S+)", line)
            if m:
                v[m.group(1).lower()] = m.group(2)
    except OSError:
        pass
    num = lambda k: int(v[k]) if v.get(k, "").isdigit() else 0
    mode = "fullscreen" if not num("windowed") else ("borderless" if num("borderlesswindow") else "windowed")
    return num("screenwidth"), num("screenheight"), mode


def write_display(w, h, mode, path=None):
    """Set the resolution keys in options.cfg's [ClientGraphics] section, keeping everything else."""
    path = path or os.path.join(GAME, "options.cfg")
    want = {"screenWidth": w, "screenHeight": h, "windowed": int(mode != "fullscreen"), "borderlessWindow": int(mode == "borderless")}
    try:
        lines = open(path, encoding="latin-1").read().splitlines()
    except OSError:
        lines = []
    out, section, done = [], None, set()
    def flush():
        for k, val in want.items():
            if k not in done:
                out.append("\t%s=%s" % (k, val)); done.add(k)
    for line in lines:
        m = re.match(r"\s*\[(\w+)\]", line)
        if m:
            if section == "ClientGraphics":
                while out and not out[-1].strip():
                    out.pop()
                flush(); out.append("")
            section = m.group(1)
        else:
            k = re.match(r"\s*(\w+)\s*=", line)
            if section == "ClientGraphics" and k and k.group(1) in want:
                out.append("\t%s=%s" % (k.group(1), want[k.group(1)])); done.add(k.group(1)); continue
        out.append(line)
    if section == "ClientGraphics":
        flush()
    if len(done) < len(want):
        out += ["", "[ClientGraphics]"]; flush()
    open(path, "w", encoding="latin-1").write("\n".join(out) + "\n")


def galaxy():
    """(name, up, players) from the server's status port, or None."""
    host = CONF.get("STATUS") or CONF.get("SERVER")
    if not host:
        return None
    try:
        with socket.create_connection((host, int(CONF.get("STATUS_PORT", 44455))), timeout=2) as s:
            s.settimeout(2); d = b""
            while b"</zoneServer>" not in d:
                c = s.recv(4096)
                if not c:
                    break
                d += c
    except OSError:
        return None
    x = d.decode("utf-8", "replace")
    tag = lambda t: (re.search("<%s>([^<]*)</%s>" % (t, t), x) or [None, ""])[1]
    return tag("name"), tag("status") == "up", int(tag("connected") or 0)


class Launcher:
    BLUE, GOLD, PANEL = "#5aa0d8", "#f0b848", "#0a1622"

    def __init__(self):
        import tkinter as tk
        self.tk = tk
        root = tk.Tk(className="Star Wars Galaxies"); root.title("Star Wars Galaxies")
        self.root = root; self.s = retro.tk_scale(root); s = self.s
        root.overrideredirect(True)
        sw, sh = root.winfo_screenwidth(), root.winfo_screenheight()
        root.geometry("%dx%d+%d+%d" % (W * s, H * s, (sw - W * s) // 2, (sh - H * s) // 2))
        c = self.c = tk.Canvas(root, width=W * s, height=H * s, bg="black", highlightthickness=0, bd=0); c.pack()
        icon = os.path.join(ART, "icon.png")
        if os.path.exists(icon):
            self._icon = tk.PhotoImage(file=icon); root.iconphoto(True, self._icon)
        rnd = random.Random(1977)
        for _ in range(260):                                 # starfield
            x, y = rnd.randrange(W * s), rnd.randrange(H * s); b = rnd.choice(("#555", "#888", "#bbb", "#fff"))
            c.create_rectangle(x, y, x + s - 1, y + s - 1, fill=b, outline="")
        # opening crawl, scrolling in the middle column
        self.crawl = None
        if os.path.exists(os.path.join(ART, "crawl.png")):
            self.crawl_img = self.img("crawl.png")
            self.crawl = c.create_image(232 * s, 240 * s, image=self.crawl_img, anchor="nw")
            c.create_rectangle(226 * s, 0, 414 * s, 14 * s, fill="black", outline="")
            c.create_rectangle(226 * s, 236 * s, 414 * s, 252 * s, fill="black", outline="")
        if os.path.exists(os.path.join(ART, "logo.png")):
            self.logo = self.img("logo.png"); c.create_image(4 * s, 6 * s, image=self.logo, anchor="nw")
        self.planets = [(p, self.img("planet_%s.png" % p)) for p in PLANETS if os.path.exists(os.path.join(ART, "planet_%s.png" % p))]
        self.planet = c.create_image(418 * s, 10 * s, anchor="nw")
        self.pname = self.text(526, 232, "", 9, "#9ab", anchor="n")
        # status / buttons panel
        c.create_rectangle(0, 252 * s, W * s, H * s, fill="black", outline="")      # the crawl scrolls under here
        c.create_rectangle(10 * s, 252 * s, (W - 10) * s, (H - 10) * s, fill=self.PANEL, outline=self.BLUE, width=s)
        c.create_line(10 * s, 278 * s, (W - 10) * s, 278 * s, fill="#1f3c55", width=s)
        self.text(22, 265, "GALAXY", 9, self.BLUE, weight="bold", anchor="w")
        self.status = self.text(86, 265, "contacting the galaxy...", 10, "#cde", anchor="w")
        self.text(22, 292, "New player? Type any new name and password at the login screen - your account is made on the spot.\n"
                           "Then build your character: species, looks and profession are all yours to choose.", 8, "#9ab", anchor="nw")
        self.button(22, 340, 140, 50, "PLAY", self.play, big=True)
        self.button(172, 350, 100, 32, "SETTINGS", lambda: self.run("settings"))
        self.button(280, 350, 100, 32, "DISPLAY", self.display)
        self.button(388, 350, 124, 32, "CONTROLLER", self.controller)
        self.button(520, 350, 94, 32, "EXIT", root.destroy)
        self.disp = self.text(330, 396, "", 8, "#9ab", anchor="n"); self.show_display()
        c.bind("<ButtonPress-1>", self._press); c.bind("<B1-Motion>", self._move)
        root.bind("<Escape>", lambda e: root.destroy())
        self.upd = self.text(W - 22, 265, "v" + VERSION, 8, "#5d7a90", anchor="e")
        self.n = 0; self.cycle(); self.scroll(); self.refresh(); self.root.after(800, self.update)
        root.mainloop()

    def img(self, name):
        p = self.tk.PhotoImage(file=os.path.join(ART, name))
        return p.zoom(self.s) if self.s > 1 else p

    def text(self, x, y, t, size, color, anchor="nw", weight="normal"):
        return self.c.create_text(x * self.s, y * self.s, text=t, fill=color, anchor=anchor,
                                  font=("DejaVu Sans", -size * self.s, weight))

    def button(self, x, y, w, h, label, cmd, big=False):
        s, c = self.s, self.c
        r = c.create_rectangle(x * s, y * s, (x + w) * s, (y + h) * s, fill="#13283a", outline=self.BLUE, width=s, tags="btn")
        t = c.create_text((x + w / 2) * s, (y + h / 2) * s, text=label, fill=self.GOLD, tags="btn",
                          font=("DejaVu Sans", -(18 if big else 11) * s, "bold"))
        def hover(on):
            c.itemconfig(r, fill="#24496a" if on else "#13283a", outline=self.GOLD if on else self.BLUE)
            c.config(cursor="hand2" if on else "")
        for i in (r, t):
            c.tag_bind(i, "<Enter>", lambda e: hover(True)); c.tag_bind(i, "<Leave>", lambda e: hover(False))
            c.tag_bind(i, "<ButtonRelease-1>", lambda e: self.root.after(60, cmd))

    def _press(self, e):
        self._drag = None if "btn" in self.c.gettags("current") else (e.x_root - self.root.winfo_x(), e.y_root - self.root.winfo_y())

    def _move(self, e):
        if getattr(self, "_drag", None):
            self.root.geometry("+%d+%d" % (e.x_root - self._drag[0], e.y_root - self._drag[1]))

    def cycle(self):
        if self.planets:
            name, im = self.planets[self.n % len(self.planets)]
            self.c.itemconfig(self.planet, image=im); self.c.itemconfig(self.pname, text=name.upper())
            self.n += 1
        self.root.after(5000, self.cycle)

    def scroll(self):
        if self.crawl:
            x, y = self.c.coords(self.crawl)
            if y < -352 * self.s:
                y = 240 * self.s
            self.c.coords(self.crawl, x, y - self.s)
        self.root.after(45, self.scroll)

    def refresh(self):
        def done(r):
            if isinstance(r, tuple):
                name, up, n = r
                self.c.itemconfig(self.status, text="%s    %s    %d player%s online" % (
                    name, "● ONLINE" if up else "● DOWN", n, "" if n == 1 else "s"),
                    fill="#8fe08f" if up else "#e08f8f")
            else:
                self.c.itemconfig(self.status, text="status unknown (the login server may still work)", fill="#cde")
            self.root.after(30000, self.refresh)
        retro.background(galaxy, done, self.root)

    def update(self):
        """Signed launcher update from the GitHub releases: checked on every start, installed, then restart."""
        if not os.access(HERE, os.W_OK):
            return
        def found(u):
            if not isinstance(u, tuple):              # no update, or no internet
                return
            self.c.itemconfig(self.upd, text="updating launcher to v%s..." % u[0], fill=self.GOLD)
            retro.background(lambda: retro.apply_update(UPDATES, u, HERE), lambda ok: (
                self.c.itemconfig(self.upd, text="launcher updated - restarting" if ok else "update failed - try next time",
                                  fill=self.GOLD if ok is True else "#e08f8f"),
                self.root.after(900, retro.restart) if ok is True else None), self.root)
        retro.background(lambda: retro.check_update(UPDATES, VERSION), found, self.root)

    def run(self, *args):
        retro.detach([os.path.join(DEST, "swg.sh")] + list(args), cwd=GAME)

    def play(self):
        self.run(); self.root.after(600, self.root.destroy)

    def show_display(self):
        w, h, mode = read_display()
        self.c.itemconfig(self.disp, text=("%d x %d, %s" % (w, h, mode)) if w else "")

    def display(self):
        retro.display_dialog(self.root, {"bg": self.PANEL, "fg": "#cde", "accent": self.GOLD, "btn_bg": "#13283a"}, read_display(),
                             lambda w, h, m: (write_display(w, h, m), self.show_display()),
                             modes=("borderless", "fullscreen", "windowed"))

    def controller(self):
        p = os.path.join(DEST, "controller", "README.md")
        retro.open_path(p if os.path.exists(p) else DEST)


def install_wizard():
    """Setup wizard (run from the repo / USB folder): themed front end for install-swg-linux.sh."""
    repo = DEST
    def banner(c, w, h, s):
        rnd = random.Random(1977)
        for _ in range(220):
            x, y = rnd.randrange(w), rnd.randrange(h)
            c.create_rectangle(x, y, x + s - 1, y + s - 1, fill=rnd.choice(("#555", "#999", "#fff")), outline="")
        logo = None
        try:                                    # the logo from the offline client files, when they're here
            src = os.path.join(repo, "client-files"); idx = {}
            for f in sorted(os.listdir(src)):
                if f.endswith(".tre"):
                    idx.update(retro.tre_entries(os.path.join(src, f)))
            name = "texture/ui_starwars_logo_jtl.dds" if "texture/ui_starwars_logo_jtl.dds" in idx else "texture/ui_starwars_logo.dds"
            import tempfile
            tmp = os.path.join(tempfile.mkdtemp(), "logo.png")
            retro.save_png(tmp, *retro.resize(retro.dds_decode(retro.tre_read(idx[name])), 180, 180))
            import tkinter as tk
            logo = tk.PhotoImage(file=tmp); logo = logo.zoom(s) if s > 1 else logo
            c._logo = logo; c.create_image(w // 2, h // 2, image=logo)
        except Exception:
            c.create_text(w // 2, h // 2 - 14 * s, text="STAR WARS", fill="#f0b848", font=("DejaVu Sans", -40 * s, "bold"))
            c.create_text(w // 2, h // 2 + 26 * s, text="GALAXIES", fill="#f0b848", font=("DejaVu Sans", -28 * s, "bold"))
    retro.Wizard({
        "title": "Star Wars Galaxies", "bg": "#0a1622", "fg": "#cde", "accent": "#f0b848", "btn_bg": "#13283a", "dim": "#8aa",
        "banner_h": 190, "banner": banner,
        "blurb": "A long time ago in a galaxy far, far away... This installs Star Wars Galaxies (about 4 GB) and connects it "
                 "to our galaxy. New players make their own account and character the first time they log in.\n\n"
                 "Got the original discs? Pick them under Game discs (a copied folder, the .iso files, or the CD drive) and "
                 "about 2 GB of the game comes off them instead of the internet. Optional.",
        "fields": [("server", "Server address", CONF.get("SERVER", ""), "text"),
                   ("dest", "Install into", os.path.expanduser("~/Games/swg"), "folder"),
                   ("discs", "Game discs", "", "discs"),
                   ("desktop", "Put a shortcut on the desktop", True, "check")],
        "script": os.path.join(repo, "install-swg-linux.sh"),
        "args": lambda v: ["--yes", "--dest", os.path.expanduser(v["dest"]), "--server", v["server"]] + ([] if v["desktop"] else ["--no-desktop-icon"])
                          + (["--discs", v["discs"]] if v["discs"].strip() else []),
        "launch": lambda d: [sys.executable, os.path.join(d, "launcher", "swg_launcher.py")],
    })


if __name__ == "__main__":
    if "--install" in sys.argv:
        install_wizard(); sys.exit()
    if "--extract" in sys.argv or not os.path.exists(os.path.join(ART, "icon.png")):
        extract()
    if "--extract" not in sys.argv:
        Launcher()
