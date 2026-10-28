#!/usr/bin/env python3
"""Make the signed update for a release: dist/update-manifest.txt + dist/update-manifest.sig.

usage (in the repo, after committing and tagging vX.Y.Z):  python3 tools/make-update.py X.Y.Z
Then attach both files to the GitHub release vX.Y.Z (mark it Latest). Launchers older than X.Y.Z pick it up on start.
Lists launcher/*.py (Linux) and windows/launcher/*.cs (Windows) at the tag, with SHA-256 and size, and signs the list
with the private key in ~/.config/launcher-signing/update-key.pem (RSA-3072, SHA-256; never commit it). The launchers'
VERSION constants must already say X.Y.Z, or a launcher would keep "updating" to the same release.
"""
import base64, hashlib, os, re, subprocess, sys

KEY = os.path.expanduser("~/.config/launcher-signing/update-key.pem")


def git(*a):
    return subprocess.run(["git"] + list(a), capture_output=True, check=True).stdout


def main(version):
    tag = "v" + version
    url = git("remote", "get-url", "origin").decode().strip()
    repo = re.search(r"github\.com[:/](.+?)(\.git)?$", url).group(1)
    names = git("ls-tree", "-r", "--name-only", tag).decode().split("\n")
    files = [("linux", n) for n in names if re.fullmatch(r"launcher/[^/]+\.py", n)] + \
            [("windows", n) for n in names if re.fullmatch(r"windows/launcher/[^/]+\.cs", n)]
    lines = ["version=" + version, "repo=" + repo, "tag=" + tag]
    for plat, n in files:
        data = git("show", "%s:%s" % (tag, n))
        if re.search(rb'(VERSION|Version)\s*=\s*"(\d+\.\d+\.\d+)"', data):
            v = re.search(rb'(VERSION|Version)\s*=\s*"(\d+\.\d+\.\d+)"', data).group(2).decode()
            if v != version:
                sys.exit("%s says version %s, not %s - bump it, commit, re-tag." % (n, v, version))
        lines.append("%s %s %d %s" % (plat, hashlib.sha256(data).hexdigest(), len(data), n))
    man = ("\n".join(lines) + "\n").encode()
    os.makedirs("dist", exist_ok=True)
    open("dist/update-manifest.txt", "wb").write(man)
    sig = subprocess.run(["openssl", "dgst", "-sha256", "-sign", KEY], input=man, capture_output=True, check=True).stdout
    open("dist/update-manifest.sig", "w").write(base64.b64encode(sig).decode() + "\n")
    print(man.decode() + "signed -> dist/update-manifest.txt + .sig")


if __name__ == "__main__":
    if len(sys.argv) != 2 or not re.fullmatch(r"\d+\.\d+\.\d+", sys.argv[1]):
        sys.exit(__doc__)
    main(sys.argv[1])
