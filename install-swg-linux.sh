#!/bin/bash
# Star Wars Galaxies (SWGEmu client) for Linux: downloads the client files listed in SWGEmu's required2.txt, checks
# every MD5, sets up a Wine prefix, points the game at your server and adds menu/desktop shortcuts.
# Files come from a "client-files" folder next to this script (offline, e.g. the USB stick), then your LAN mirror (if set),
# then SWGEmu's own patch server. Nothing is bundled in the repo.
#
# usage: install-swg-linux.sh [--dest DIR] [--server HOST] [--mirror URL] [--no-desktop-icon] [--yes]
#   --dest     where to install (default: ask; ~/Games/swg is suggested)
#   --server   login server address (default: SERVER= in swg-installer.conf next to this script)
#   --mirror   LAN mirror base URL, e.g. http://host:8090 (default: MIRROR= in swg-installer.conf; optional)
#   --yes      don't ask anything (uses the defaults)
# Re-running it on an existing install only fetches missing or damaged files ("repair").
set -eu

HERE=$(cd "$(dirname "$0")" && pwd); DEST=""; SERVER=""; MIRROR=""; DESKTOP_ICON=1; ASK=1
SWGEMU=http://www.launchpad2.net/SWGEmu
[ -f "$HERE/swg-installer.conf" ] && . "$HERE/swg-installer.conf"
while [ $# -gt 0 ]; do
  case "$1" in
    --dest) DEST=$2; shift 2;;
    --server) SERVER=$2; shift 2;;
    --mirror) MIRROR=$2; shift 2;;
    --no-desktop-icon) DESKTOP_ICON=0; shift;;
    --yes|-y) ASK=0; shift;;
    -h|--help) sed -n '2,12p' "$0"; exit 0;;
    *) echo "unknown option $1"; exit 1;;
  esac
done

# 0. the game-themed setup window (needs python3 + Tk); it runs this script again with --yes
if [ "$ASK" = 1 ] && [ -z "${RETRO_WIZARD:-}" ] && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && [ -f "$HERE/launcher/swg_launcher.py" ] \
   && python3 -c 'import tkinter' 2>/dev/null; then
  exec python3 "$HERE/launcher/swg_launcher.py" --install
fi

T="Star Wars Galaxies installer"
gui() { [ "$ASK" = 1 ] && command -v zenity >/dev/null && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; }
say() { echo "$*"; gui && zenity --info --title "$T" --text "$*" --width 440 2>/dev/null || true; }
die() { echo "ERROR: $*" >&2; gui && zenity --error --title "$T" --text "$*" --width 440 2>/dev/null; exit 1; }
for c in curl md5sum; do command -v $c >/dev/null || die "Please install '$c' first."; done

# 1. server address
if [ -z "$SERVER" ] || [ "$ASK" = 1 ]; then
  if gui; then
    SERVER=$(zenity --entry --title "$T" --width 440 --text "Server address (name or IP of the SWGEmu login server):" \
      --entry-text "$SERVER" 2>/dev/null) || exit 1
  elif [ "$ASK" = 1 ] && [ -t 0 ]; then
    read -r -p "Server address [$SERVER]: " a; [ -n "$a" ] && SERVER="$a"
  fi
fi
[ -n "$SERVER" ] || die "No server address. Use --server <host> or put SERVER=<host> in swg-installer.conf."

# 2. where to install
if [ -z "$DEST" ]; then
  DEST="$HOME/Games/swg"
  if gui; then
    zenity --question --title "$T" --width 460 --text "Install Star Wars Galaxies into\n\n$DEST\n\n(about 4 GB)?" \
      --ok-label "Install here" --cancel-label "Choose another folder" 2>/dev/null || {
      pick=$(zenity --file-selection --directory --title "Choose a folder - SWG goes into a 'swg' folder inside it" 2>/dev/null) || exit 1
      DEST="$pick/swg"; }
  elif [ "$ASK" = 1 ] && [ -t 0 ]; then
    read -r -p "Install folder [$DEST]: " a; [ -n "$a" ] && DEST="$a"
  fi
fi
DEST=$(realpath -m "$DEST"); P="$DEST/pfx"; G="$P/drive_c/SWGEmu"
mkdir -p "$G"
free=$(df -Pk "$DEST" | awk 'NR==2 {print $4}')
[ "$free" -gt 5242880 ] || die "Not enough space in $DEST: need 5 GB free, have $((free/1048576)) GB."

# 3. Wine: newest GE-Proton from Steam/Lutris if present (has DXVK), else the system wine
GE=$(ls -d "$HOME"/.local/share/Steam/compatibilitytools.d/GE-Proton*/files "$HOME"/.steam/root/compatibilitytools.d/GE-Proton*/files \
          "$HOME"/.local/share/lutris/runners/wine/*GE*/ 2>/dev/null | sort -V | tail -1 || true)
if [ -n "$GE" ] && [ -x "$GE/bin/wine" ]; then WINEBIN="$GE/bin"; DXVK="$GE/lib/wine/dxvk"
elif command -v wine >/dev/null; then WINEBIN=$(dirname "$(command -v wine)"); DXVK=""
else die "No Wine found. Install GE-Proton (e.g. with ProtonUp-Qt) or your distro's wine package, then run this again."; fi

# 4. download + verify (mirror first, then SWGEmu)
LOCAL="$HERE/client-files"   # offline copy (e.g. on the USB stick), used first when present
fetch() {  # fetch <relative path> <dest file> [md5]: local copy, then LAN mirror, then SWGEmu; with an md5, a source only counts if it matches
  local u
  if [ -f "$LOCAL/$1" ]; then
    cp -f "$LOCAL/$1" "$2.part" && { [ -z "${3:-}" ] || [ "$(md5sum < "$2.part" | cut -c1-32)" = "$3" ]; } && mv -f "$2.part" "$2" && return 0
  fi
  for u in ${MIRROR:+"$MIRROR/$1"} "$SWGEMU/$1"; do
    curl -sSLf --retry 3 -m 3600 -o "$2.part" "$u" 2>/dev/null || continue
    { [ -z "${3:-}" ] || [ "$(md5sum < "$2.part" | cut -c1-32)" = "$3" ]; } && mv -f "$2.part" "$2" && return 0
  done
  rm -f "$2.part"; return 1
}
LIST="$DEST/required2.txt"
fetch required2.txt "$LIST" || die "Can't get the SWGEmu file list (no client-files folder, no internet, no LAN mirror)."
tr -d '\r' < "$LIST" > "$LIST.tmp" && mv -f "$LIST.tmp" "$LIST"
download_all() {
  local n=0 total bad=0 f size sum
  total=$(grep -c ';' "$LIST")
  while IFS=';' read -r f size sum; do
    [ -n "$f" ] || continue
    n=$((n + 1)); sum=$(echo "$sum" | tr 'A-F' 'a-f')
    echo "# ($n/$total) $f"
    mkdir -p "$G/$(dirname "$f")"
    if [ -f "$G/$f" ] && [ "$(stat -c %s "$G/$f")" = "$size" ] && [ "$(md5sum < "$G/$f" | cut -c1-32)" = "$sum" ]; then :
    elif fetch "$f" "$G/$f" "$sum"; then :
    else echo "FAILED $f" >&2; bad=$((bad + 1)); fi
    echo $((n * 100 / total))
  done < "$LIST"
  [ "$bad" = 0 ]
}
echo "Downloading and checking the game files (about 3 GB) ..."
if gui; then
  set +e; download_all 2>"$DEST/install-errors.log" | zenity --progress --auto-close --title "$T" --width 460 \
    --text "Downloading game files..." 2>/dev/null; rc=${PIPESTATUS[0]}; set -e
else download_all && rc=0 || rc=$?; fi
[ "$rc" = 0 ] || die "Some files failed to download or verify (see $DEST/install-errors.log). Run the installer again to retry - it keeps the good files."

# 5. game config: SWGEmu's stock cfgs + our login server
for c in swgemu.cfg swgemu_live.cfg swgemu_preload.cfg; do fetch "$c" "$G/$c" || die "Can't download $c."; done
[ -f "$G/options.cfg" ] || fetch options.cfg "$G/options.cfg" || true
[ -f "$G/user.cfg" ] || fetch user.cfg "$G/user.cfg" || true
printf '[ClientGame]\n\tloginServerPort0=44453\n\tloginServerAddress0=%s\n[Station]\n\tsubscriptionFeatures=1\n\tgameFeatures=65535\n' \
  "$SERVER" > "$G/swgemu_login.cfg"

# 6. launcher
cat > "$DEST/swg.sh" <<EOF
#!/bin/bash
# Star Wars Galaxies (SWGEmu). usage: swg.sh [settings]   ("settings" opens the graphics/sound setup tool)
export WINEPREFIX="$P" WINEARCH=win64 WINEDEBUG=-all PATH="$WINEBIN:\$PATH" DXVK_LOG_LEVEL=none
${DXVK:+export WINEDLLOVERRIDES="d3d9=n,b"}
cd "$G"
if [ "\${1:-}" = settings ]; then exec wine SWGEmu_Setup.exe; fi
# controller: start AntiMicroX with the SWG layout if it's installed and not already running
if command -v antimicrox >/dev/null && ! pgrep -x antimicrox >/dev/null && [ -f "$DEST/controller/swg.amgp" ]; then
  antimicrox --tray --profile "$DEST/controller/swg.amgp" >/dev/null 2>&1 &
fi
exec wine SWGEmu.exe
EOF
chmod +x "$DEST/swg.sh"
mkdir -p "$DEST/controller"; cp -f "$HERE"/controller/* "$DEST/controller/" 2>/dev/null || true

# 7. prefix + DXVK
echo "Setting up the Wine prefix (first run takes a minute)..."
export WINEPREFIX="$P" WINEARCH=win64 WINEDEBUG=-all PATH="$WINEBIN:$PATH"
wineboot -u >/dev/null 2>&1 || true
if [ -n "$DXVK" ] && [ -f "$DXVK/i386-windows/d3d9.dll" ]; then
  cp -f "$DXVK/i386-windows/d3d9.dll" "$P/drive_c/windows/syswow64/d3d9.dll"
elif [ -n "$DXVK" ] && [ -f "$DXVK/x32/d3d9.dll" ]; then
  cp -f "$DXVK/x32/d3d9.dll" "$P/drive_c/windows/syswow64/d3d9.dll"
fi
wineserver -w 2>/dev/null || true

# 8. shortcuts
# launcher: the period-style one (art read from the game files) when python3 + Tk are there, else the plain script
LAUNCH="\"$DEST/swg.sh\""; ICON=applications-games
if [ -f "$HERE/launcher/swg_launcher.py" ] && python3 -c 'import tkinter' 2>/dev/null; then
  mkdir -p "$DEST/launcher" && cp -f "$HERE"/launcher/*.py "$DEST/launcher/"
  printf 'SERVER=%s\nSTATUS=%s\n' "$SERVER" "${STATUS:-$SERVER}" > "$DEST/swg-installer.conf"
  echo "Reading the launcher art from the game files..."
  if python3 "$DEST/launcher/swg_launcher.py" --extract >/dev/null 2>&1; then
    LAUNCH="python3 \"$DEST/launcher/swg_launcher.py\""; ICON="$DEST/launcher/art/icon.png"
  fi
fi
APPS="$HOME/.local/share/applications"; mkdir -p "$APPS"
for kind in play settings; do
  if [ $kind = play ]; then name="Star Wars Galaxies"; exec=$LAUNCH; file=swg.desktop
  else name="Star Wars Galaxies Settings"; exec="\"$DEST/swg.sh\" settings"; file=swg-settings.desktop; fi
  cat > "$APPS/$file" <<EOF
[Desktop Entry]
Type=Application
Name=$name
Exec=$exec
Path=$G
Icon=$ICON
Categories=Game;
EOF
done
DESK=$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")
if [ "$DESKTOP_ICON" = 1 ] && [ -d "$DESK" ]; then
  cp -f "$APPS/swg.desktop" "$DESK/" && chmod +x "$DESK/swg.desktop"
  gio set "$DESK/swg.desktop" metadata::trusted true 2>/dev/null || true
fi

msg="Star Wars Galaxies is installed in $DEST.

Start it from the menu or desktop (\"Star Wars Galaxies\").
Server: $SERVER - make up a username and password on the first login (accounts are created automatically).
Graphics/sound: \"Star Wars Galaxies Settings\"."
msg="$msg

Controller: see $DEST/controller/README.md (Steam Input or AntiMicroX)."
say "$msg"
