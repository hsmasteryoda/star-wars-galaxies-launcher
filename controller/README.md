# Playing SWG with a controller

SWG (2005 client) has no gamepad support of its own, and its UI can't be replaced the way WoW/ESO did it.
What works well is a translation layer that turns controller input into the keys and mouse SWG already uses.

## Option 1 (recommended): Steam Input — Windows and Linux, any controller

1. Steam → *Games* → *Add a Non-Steam Game to My Library* → browse to `SWGEmu.exe` in the install folder.
   On Linux pick `swg.sh` instead (the launcher the installer made).
2. Right-click it → *Properties* → *Controller* → *Enable Steam Input*.
3. Start it from Steam, press the Steam/Guide button → *Controller Settings* → *Browse Configs* → *Templates* →
   **"Keyboard (WASD) and Mouse"**, then adjust buttons to the layout below.
4. For the hotbar, add a **Radial Menu** on a back/paddle or the touchpad (keys 1–0) — this is the closest thing
   to ESO's console-style ability wheel.

## Option 2: AntiMicroX — free, no Steam needed

Install AntiMicroX (Linux: distro package or Flathub; Windows: github.com/AntiMicroX/antimicrox/releases), map the
layout below and save the profile as `swg.amgp` in this `controller` folder. On Linux the installer's launcher
(`swg.sh`) starts AntiMicroX with that profile automatically.

## Suggested layout (Xbox names; PlayStation in brackets)

| Controller | SWG key / mouse | What it does |
|---|---|---|
| Left stick | W A S D | move / turn |
| Right stick | mouse movement | camera / aim (in mouse-look mode) |
| Left stick click (L3) | R | toggle walk/run (check *Options → Keymap*) |
| Right stick click (R3) | Alt | switch between mouse-look and cursor mode |
| RT (R2) | left mouse | select / attack target |
| LT (L2) | right mouse | radial menu on target / hold to look |
| A (Cross) | 1 | toolbar slot 1 (main attack) |
| B (Circle) | 2 | toolbar slot 2 |
| X (Square) | 3 | toolbar slot 3 |
| Y (Triangle) | 4 | toolbar slot 4 |
| RB (R1) | Tab | next target |
| LB (L1) | Esc | clear target / close window |
| D-pad up / down | 5 / 6 | toolbar slots 5–6 |
| D-pad left / right | Q / E | strafe |
| View (Share) | Ctrl+I | inventory |
| Menu (Options) | Esc | game menu |

Bind toolbar slots 7–0 to a radial menu (Steam) or a second "set" (AntiMicroX, hold LB as a shift).
If a key does nothing, open *Options → Keymap* in SWG — every action can be rebound there, so the controller
layer and the game always agree.
