#!/usr/bin/env python3
"""Copy the Star Wars Galaxies files that SWGEmu still uses off the original game discs, so the installer only has to
download the rest. The 2003 discs hold 28 of the 74 files SWGEmu needs (the big .tre archives, about 2.1 GB).

Sources: a folder with a copy of the discs (or a folder holding several disc folders), .iso files of the discs, or
"cdrom" for the discs themselves in a CD/DVD drive (it asks for each disc in turn, and copies its archives first, so
the drive is only needed once per disc).
Every file is checked against SWGEmu's MD5 before it is used; anything that doesn't match is simply downloaded.

usage: swg_discs.py --list required2.txt --game DIR [--stage DIR] SOURCE [SOURCE ...]
Output: "# text" status lines and bare percentages (the setup window turns them into its progress bar).
Disc prompts: "?? text" when run by the setup window (it answers "ok" or "skip" on stdin), a terminal question or zenity.

The InstallShield 6 archive reading follows unshield (MIT, (C) 2003-2023 David Eriksson).
"""
import hashlib, os, shutil, struct, subprocess, sys, time, zlib

SECTOR = 2048


# ---------------------------------------------------------------- where the disc files are
class Part:
    """One disc file: a plain file, or a file inside an .iso (path + offset + size)."""
    def __init__(self, path, offset=0, size=None):
        self.path, self.offset = path, offset
        self.size = os.path.getsize(path) if size is None else size

    def open(self):
        return Slice(self.path, self.offset, self.size)


class Slice:
    def __init__(self, path, offset, size):
        self.f, self.base, self.size = open(path, "rb"), offset, size
        self.f.seek(offset)

    def seek(self, pos):
        self.f.seek(self.base + pos)

    def read(self, n):
        left = self.base + self.size - self.f.tell()
        return self.f.read(max(0, min(n, left)))

    def close(self):
        self.f.close()


def iso_files(iso):
    """The files in the root folder of an ISO 9660 image: {lower-case name: Part}."""
    out = {}
    with open(iso, "rb") as f:
        f.seek(16 * SECTOR)
        pvd = f.read(SECTOR)
        if pvd[1:6] != b"CD001":
            return out
        lba, size = struct.unpack_from("<I", pvd, 156 + 2)[0], struct.unpack_from("<I", pvd, 156 + 10)[0]
        f.seek(lba * SECTOR)
        d, i = f.read(size), 0
        while i < len(d):
            n = d[i]
            if n == 0:                                   # records don't cross sectors: skip to the next one
                i = (i // SECTOR + 1) * SECTOR; continue
            rec = d[i:i + n]; i += n
            if rec[25] & 2:
                continue                                 # a folder
            name = rec[33:33 + rec[32]].decode("latin-1").split(";")[0].lower()
            out[name] = Part(iso, struct.unpack_from("<I", rec, 2)[0] * SECTOR, struct.unpack_from("<I", rec, 10)[0])
    return out


def folder_files(top, depth=3):
    """data*.hdr / data*.cab files in a folder and the folders under it (a disc copy, or several)."""
    out = {}
    for root, dirs, files in os.walk(top):
        if root[len(top):].count(os.sep) >= depth:
            dirs[:] = []
        for f in files:
            lf = f.lower()
            if lf.startswith("data") and lf.endswith((".hdr", ".cab")):
                out.setdefault(lf, Part(os.path.join(root, f)))
            elif lf.endswith(".iso"):
                for k, v in iso_files(os.path.join(root, f)).items():
                    out.setdefault(k, v)
    return out


def cd_mounts():
    """Mount points of the discs in the CD/DVD drives (mounts them through udisks when the desktop hasn't)."""
    def mounted():
        pts = []
        try:
            for line in open("/proc/mounts"):
                dev, mnt, fs = line.split()[:3]
                if dev.startswith("/dev/sr") or fs in ("iso9660", "udf"):
                    pts.append(mnt.replace("\\040", " "))
        except OSError:
            pass
        return pts
    pts = mounted()
    if not pts and shutil.which("udisksctl"):
        for dev in sorted(d for d in os.listdir("/dev") if d.startswith("sr")):
            subprocess.call(["udisksctl", "mount", "-b", "/dev/" + dev], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        pts = mounted()
    return pts


def cd_eject():
    for line in open("/proc/mounts"):
        dev = line.split()[0]
        if dev.startswith("/dev/sr"):
            if shutil.which("udisksctl"):
                subprocess.call(["udisksctl", "unmount", "-b", dev], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if shutil.which("eject"):
                subprocess.call(["eject", dev], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


# ---------------------------------------------------------------- InstallShield 5/6 archives
class Cab:
    def __init__(self, parts):
        self.parts = parts                               # {"data1.hdr": Part, "data1.cab": Part, ...}
        h = parts.get("data1.hdr") or parts["data1.cab"]
        s = h.open(); self.h = s.read(h.size); s.close()
        sig, ver, _, self.cd_off, _ = struct.unpack_from("<5I", self.h, 0)
        if sig != 0x28635349:
            raise ValueError("not an InstallShield archive")
        if ver >> 24 == 1:
            self.major = (ver >> 12) & 0xf
        elif ver >> 24 in (2, 4):
            self.major = (ver & 0xffff) // 100 if ver & 0xffff else 0
        else:
            self.major = 0
        c = self.cd_off
        self.ft_off = struct.unpack_from("<I", self.h, c + 0xc)[0]
        self.dir_count = struct.unpack_from("<I", self.h, c + 0x1c)[0]
        self.file_count = struct.unpack_from("<I", self.h, c + 0x28)[0]
        self.ft_off2 = struct.unpack_from("<I", self.h, c + 0x2c)[0]
        self.table = struct.unpack_from("<%dI" % (self.dir_count + self.file_count), self.h, c + self.ft_off)

    def _str(self, off):
        p = self.cd_off + self.ft_off + off
        return self.h[p:self.h.index(b"\0", p)].decode("latin-1")

    def files(self):
        """[(index, path inside the archive, size, md5 hex, descriptor)] for every real file."""
        out = []
        for i in range(self.file_count):
            fd = self.descriptor(i)
            if fd["flags"] & 8 or not fd["name"] or not fd["data_offset"]:
                continue
            d = self._str(self.table[fd["dir"]]) if fd["dir"] < self.dir_count else ""
            out.append((i, (d.replace("\\", "/") + "/" if d else "") + self._str(fd["name"]), fd["size"], fd["md5"], fd))
        return out

    def descriptor(self, i):
        h, c = self.h, self.cd_off + self.ft_off
        if self.major in (0, 5):
            p = c + self.table[self.dir_count + i]
            name, dr, flags, size, csize = struct.unpack_from("<IIHII", h, p)
            data_offset = struct.unpack_from("<I", h, p + 0x26)[0]
            md5 = h[p + 0x2a:p + 0x3a].hex() if self.major == 5 else ""
            return dict(name=name, dir=dr, flags=flags, size=size, csize=csize, data_offset=data_offset, md5=md5,
                        volume=1, link_prev=0, link_flags=0)
        p = c + self.ft_off2 + i * 0x57
        flags, size, csize, data_offset = struct.unpack_from("<HQQQ", h, p)
        md5 = h[p + 26:p + 42].hex()
        name, dr = struct.unpack_from("<IH", h, p + 58)
        link_prev, link_next, link_flags, volume = struct.unpack_from("<IIBH", h, p + 76)
        return dict(name=name, dir=dr, flags=flags, size=size, csize=csize, data_offset=data_offset, md5=md5,
                    volume=volume, link_prev=link_prev, link_flags=link_flags)

    def volumes(self, fd):
        """The cab volumes a file's data is in (a big file can run on into the next one)."""
        return [fd["volume"], fd["volume"] + 1] if fd["flags"] & 1 else [fd["volume"]]

    # ---- reading one file
    def _open_volume(self, r, v):
        if r.get("f"):
            r["f"].close()
        part = self.parts.get("data%d.cab" % v)
        if part is None:
            raise IOError("data%d.cab is missing" % v)
        f = part.open()
        f.read(20)
        fd, idx = r["fd"], r["index"]
        if self.major in (0, 5):
            vh = struct.unpack_from("<10I", f.read(40))
            data_off, first_i, last_i, first_off, first_x, first_c, last_off, last_x, last_c = (vh[0],) + vh[2:]
            if last_off == 0:
                last_off = 0x7fffffff
            if self.major == 5:
                if idx < self.file_count - 1 and idx == last_i and last_c != fd["csize"]:
                    fd["flags"] |= 1
                elif idx > 0 and idx == first_i and first_c != fd["csize"]:
                    fd["flags"] |= 1
        else:
            vh = struct.unpack_from("<16I", f.read(64))
            first_i, last_i = vh[2], vh[3]
            first_off, first_x, first_c = vh[4] | vh[5] << 32, vh[6] | vh[7] << 32, vh[8] | vh[9] << 32
            last_off, last_x, last_c = vh[10] | vh[11] << 32, vh[12] | vh[13] << 32, vh[14] | vh[15] << 32
        r["first_i"], r["last_i"] = first_i, last_i
        if fd["flags"] & 1:
            if idx == last_i and last_off != 0x7fffffff:
                off, x, c = last_off, last_x, last_c
            elif idx == first_i:
                off, x, c = first_off, first_x, first_c
            else:
                r.update(f=f, volume=v, left=0); return
        else:
            off, x, c = fd["data_offset"], fd["size"], fd["csize"]
        r.update(f=f, volume=v, left=c if fd["flags"] & 4 else x)
        f.seek(off)

    def _read(self, r, n):
        out = bytearray()
        while n:
            k = min(n, r["left"])
            if k:
                b = r["f"].read(k)
                if len(b) != k:
                    raise IOError("disc read error")
                out += b; n -= k; r["left"] -= k
            if n:
                self._open_volume(r, r["volume"] + 1)
        if r["fd"]["flags"] & 2:                         # obfuscated (rare)
            seed = r.setdefault("seed", 0)
            for i, b in enumerate(out):
                b ^= 0xd5; b = ((b >> 2) | (b << 6)) & 0xff
                out[i] = (b - (seed + i) % 0x47) & 0xff
            r["seed"] = seed + len(out)
        return bytes(out)

    def extract(self, index, fd, dest, progress=None):
        """Write file <index> to <dest>; returns its md5 (hex)."""
        if fd["link_flags"] & 1:
            return self.extract(fd["link_prev"], self.descriptor(fd["link_prev"]), dest, progress)
        r = {"index": index, "fd": dict(fd)}
        v = fd["volume"]
        while True:
            self._open_volume(r, v)
            if self.major <= 5 and index > r["last_i"]:
                v += 1; continue
            break
        md5, done = hashlib.md5(), 0
        left = fd["csize"] if fd["flags"] & 4 else fd["size"]
        with open(dest + ".part", "wb") as out:
            while left > 0:
                if fd["flags"] & 4:
                    n = struct.unpack("<H", self._read(r, 2))[0]
                    if n == 0:
                        raise IOError("bad compressed block")
                    data = zlib.decompressobj(-15).decompress(self._read(r, n) + b"\0")
                    left -= 2 + n
                else:
                    data = self._read(r, min(left, 1 << 20)); left -= len(data)
                md5.update(data); out.write(data); done += len(data)
                if progress:
                    progress(done)
        r["f"].close()
        if done != fd["size"]:
            os.remove(dest + ".part")
            raise IOError("wrong size")
        os.replace(dest + ".part", dest)
        return md5.hexdigest()


# ---------------------------------------------------------------- talking to the player
def ask(msg):
    """Show <msg> (e.g. 'insert disc 2') and wait. True = OK, False = skip the discs."""
    if os.environ.get("RETRO_WIZARD"):
        print("?? " + msg, flush=True)
        return sys.stdin.readline().strip().lower() == "ok"
    if sys.stdin.isatty():
        try:
            return input(msg + "\nPress Enter when ready (or type s and Enter to skip the discs): ").strip().lower() != "s"
        except EOFError:
            return False
    if shutil.which("zenity") and (os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY")):
        return subprocess.call(["zenity", "--question", "--title", "Star Wars Galaxies installer", "--width", "420",
                                "--text", msg, "--ok-label", "OK", "--cancel-label", "Skip the discs"],
                               stderr=subprocess.DEVNULL) == 0
    return False


def status(text):
    print("# " + text, flush=True)


def disc_label(name):
    """Which disc a file is on, for the prompts (the 2003 three-CD set: data1.hdr/1/2 on disc 1, data3 on 2, data4 on 3)."""
    n = int(name[4:-4]) if name[4:-4].isdigit() else 1
    return 1 if n <= 2 else n - 1


def copy_part(part, dest, label):
    s = part.open(); done = 0
    try:
        with open(dest + ".part", "wb") as out:
            while True:
                b = s.read(4 << 20)
                if not b:
                    break
                out.write(b); done += len(b)
                print("%d" % (done * 100 // max(1, part.size)), flush=True)
    finally:
        s.close()
    if done != part.size:
        raise IOError("could only read %d of %d bytes of %s - is the disc scratched?" % (done, part.size, label))
    os.replace(dest + ".part", dest)
    return Part(dest)


def from_cdrom(stage, need_more):
    """Ask for each disc, copy its archives to <stage>; returns the parts, or {} if the player skipped."""
    os.makedirs(stage, exist_ok=True)
    have = {f.lower(): Part(os.path.join(stage, f)) for f in os.listdir(stage) if not f.endswith(".part")}
    want = ["data1.hdr", "data1.cab"]
    asked = None
    while True:
        missing = [w for w in want if w not in have]
        if not missing:
            extra = need_more(have)                     # once the header is in, it says which archives the files are in
            missing = [w for w in extra if w not in have]
            want = sorted(set(want) | set(extra))
            if not missing:
                return have
        disc = disc_label(missing[0])
        found = {}
        for m in cd_mounts():
            found.update(folder_files(m, depth=1))
        if not any(w in found for w in missing):
            if asked == disc:
                time.sleep(2)                           # just answered OK: give the drive a moment to spin up
                for m in cd_mounts():
                    found.update(folder_files(m, depth=1))
            if not any(w in found for w in missing):
                if found or asked is not None:
                    cd_eject()                          # the wrong disc (or the one just copied): open the tray
                if not ask("Insert Star Wars Galaxies disc %d (of the original 2003 discs) and click OK." % disc):
                    return {}
                asked = disc
                continue
        for w in missing:
            if w in found:
                status("Copying %s from disc %d..." % (w, disc))
                have[w] = copy_part(found[w], os.path.join(stage, w), w)
        asked = None


# ---------------------------------------------------------------- main
def main(argv):
    import argparse
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--list", required=True); ap.add_argument("--game", required=True); ap.add_argument("--stage")
    ap.add_argument("sources", nargs="+")
    a = ap.parse_args(argv)

    need = {}                                            # md5 -> [relative path, size]
    for line in open(a.list, encoding="latin-1"):
        p = line.strip().split(";")
        if len(p) == 3:
            need.setdefault(p[2].lower(), []).append((p[0], int(p[1])))

    def wanted(cab):
        out = []
        for i, path, size, md5, fd in cab.files():
            for rel, sz in need.get(md5, []):
                if sz == size:
                    dest = os.path.join(a.game, rel)
                    if not (os.path.exists(dest) and os.path.getsize(dest) == size):
                        out.append((i, rel, fd, dest))
        return out

    parts, cdrom = {}, False
    for src in a.sources:
        src = os.path.expanduser(src.strip())
        if src.lower() in ("cdrom", "cd", "cd drive"):
            cdrom = True
        elif src.lower().endswith(".iso") and os.path.isfile(src):
            parts.update(iso_files(src))
        elif os.path.isdir(src):
            parts.update(folder_files(src))
        elif src:
            status("Not found, skipped: " + src)

    stage = a.stage or os.path.join(a.game, ".disc-copy")
    if cdrom and "data1.hdr" not in parts:
        def need_more(have):
            cab = Cab(have)
            vols = set()
            for i, rel, fd, dest in wanted(cab):
                vols.update(cab.volumes(fd))
            return ["data%d.cab" % v for v in sorted(vols)]
        try:
            parts.update(from_cdrom(stage, need_more))
        except Exception as e:
            status("Couldn't copy from the CD drive (%s) - everything will be downloaded." % e)
            shutil.rmtree(stage, ignore_errors=True)
            return 0
    if "data1.hdr" not in parts and "data1.cab" not in parts:
        status("No Star Wars Galaxies disc files found - everything will be downloaded.")
        return 0

    try:
        cab = Cab(parts)
        todo = wanted(cab)
    except Exception as e:
        status("Those discs couldn't be read (%s) - everything will be downloaded." % e)
        return 0
    if not todo:
        status("Nothing on the discs that this version still uses - everything will be downloaded.")
        return 0
    total = sum(fd["size"] for i, rel, fd, dest in todo) or 1
    done_before, got = 0, 0
    for n, (i, rel, fd, dest) in enumerate(todo, 1):
        status("From your discs (%d/%d): %s" % (n, len(todo), rel))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        try:
            md5 = cab.extract(i, fd, dest, lambda d: print("%d" % ((done_before + d) * 100 // total), flush=True))
            if md5 == fd["md5"]:
                got += 1
            else:
                os.remove(dest)
        except Exception as e:
            status("Couldn't read %s from the discs (%s) - it will be downloaded." % (rel, e))
            try:
                os.remove(dest + ".part")
            except OSError:
                pass
        done_before += fd["size"]
    shutil.rmtree(os.path.join(a.game, ".disc-copy"), ignore_errors=True)
    if a.stage:
        shutil.rmtree(a.stage, ignore_errors=True)
    status("Copied %d game files from your discs." % got)
    print("DISCS-OK %d" % got, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
