# Spider-Man 2 : La Revanche d'Electro (France, SLES-036.24) — native recompilation

> Portage de la recompilation `spiderman2/` (USA, SLUS-013.78) vers le disque PAL
> français. Même moteur, mêmes patchs, mêmes costumes ; seules les adresses changent.

This silo is the USA Spider-Man 2 port (`../spiderman2`) retargeted to the French PAL
disc. It is **generated** from the USA port by `tools/retarget/retarget.py`; do not
hand-edit `patches/`, `port/Program.cs`, `port/SpiderMan2.csproj`,
`config/spiderman2.json` or the copied pipeline scripts — change the USA source or the
retarget tables and re-run it.

## Why a retarget works

The French executable is the same build of the same source with small edits. Aligning
the two linear-sweep function maps on function size pairs **every** function one to
one: 1,767 in the main executable on both discs and the same count in each of the 28
overlays, 96% of them with an unchanged size. So every hand-identified address in the
USA port has a French equivalent:

| kind | how it is carried across |
|---|---|
| code | sweep alignment; interior offsets are kept only inside same-size functions, resized ones are read by hand (`CODE_OVERRIDES`) |
| rodata | +0xF50 (jump/descriptor tables) |
| .data | +0xF54 |
| .sdata / .sbss | +0xEC0 (+0xEB8 at the tail of .sbss) |
| `$gp` | +0xF54, so gp-relative offsets shrink by 0x94 |
| shell overlay data | +0x194 |

Each data address in `retarget.py`'s `DATA` table records the FR instruction or table
that pinned it. Differences that are not just addresses:

- `func_8007606C` (USA `func_80075438`) gained a packet-type walk; the tail packet is in
  `t5` instead of `t2`, so `MenuPacketOrder` reads `T5` and hooks `0x8007617C`.
- The PAL build links libetc's `SetVideoMode` (`0x800979DC`) beside `GetVideoMode`
  (`0x800979F0`).
- The console clock runs at **50 Hz** (`Runtime.VBlankHz`, new in the shared
  runtime), so gameplay and cinematics keep PAL speed and presentation caps at 25 FPS.
  `SPIDEY_VBLANK_HZ=60` restores the NTSC clock (about 20% faster).

## Building

Same requirements as the USA port (.NET 10, Python 3 with NumPy and Pillow). The disc
image is expected one level above the repository, as it is in this checkout:
`../Spider-Man 2 - La Revanche d'Electro (France)/Spider-Man 2 - La Revanche d'Electro (France).bin`,
or set `SPIDEY_DISC` to its path.

```bash
cd spiderman2fr
python tools/disc.py extract extracted
python tools/cdwad.py extract extracted/wad
python tools/overlays.py build config/overlays
python tools/genmaps.py --force          # FR sweep maps: input to the retarget
python tools/retarget/retarget.py        # USA port -> FR addresses
python tools/build.py                    # maps -> recompile -> closure -> build
```

`port/bin/Release/net10.0/SpiderMan2.exe` then finds `spiderman2fr/extracted` on its
own. `dotnet publish spiderman2fr/port/SpiderMan2.csproj -c Release` produces the
self-contained `spiderman2fr/port/dist/SpiderMan2.exe`, which asks for the French
BIN/CUE on first launch and extracts it beside itself.

## Frame rate and VSync

Settings > Display (shared runtime, so every game in this repository has them):

| setting | choices |
|---|---|
| Frame rate | Original (25 FPS on this disc), 50 FPS (interpolated), **Unlimited (interpolated, default)** |
| VSync | Off, On, Adaptive (selectable only when the driver supports `EXT_swap_control_tear`) |
| Frame limit | None, 30 to 360 FPS; shown with Unlimited, applies on top of VSync |

The game itself always runs at its own 25 updates per second on the 50 Hz console
clock. Unlimited presents in-between frames while the game waits for each tick, so the
picture is one game frame behind. The window title shows both rates, for example
`144 FPS (game 25.0)`. `RECOMP_FRAMERATE=original|doubled|unlimited`,
`RECOMP_VSYNC=0|1|adaptive` and `RECOMP_FPS_LIMIT=n` override the saved settings.

## Powers

Retail gives each costume up to three of the seven powers, and CREER UNE ARAIGNEE lets
you pick three, without duplicates, once it is unlocked. This port grants a chosen power
set on top of the costume's own powers:

* by default the six positive powers are on: FURTIVITE, INVULNERABILITE, TOILE ILLIMITE,
  FORCE ACCRUE, DOUBLE SAUT, DEPLACEMENT ACCRU. DEUX CARTOUCHES (web cartridges capped at
  2 instead of 10) is a malus and stays off;
* SPECIAL > CREER UNE ARAIGNEE is always unlocked. POUVOIR 1-3 become one POUVOIRS row
  (it shows how many are on). Its list is a checklist of all seven powers: cross toggles
  OUI/NON, VALIDER closes the list, triangle leaves the screen as before;
* the choice is saved in the port's settings (`Powers` in `interface.ini`, one bit per
  power) and applies to every costume, save and level select. Attract-mode demos still
  play without powers so their recorded input stays in sync.

`SPIDEY_POWERS=0x3F` sets the powers for one run without saving them; `SPIDEY_POWERS=0`
gives retail behaviour (costume powers only).

## Unattended runs

Every `SPIDEY_*` switch of the USA port works here. Archive anchors use the French
file names (`titlepf.bmr`, `legalpf.rle`). `RECOMP_CAPTURE_HIDDEN=1` hides the window
and ignores physical gamepads (SDL reads them even without focus, so a capture running
beside another game would take that player's presses), and `RECOMP_MUTE=1` silences
audio. Use `RECOMP_FPS_LIMIT` to keep an unlimited frame rate from loading the GPU.

```bash
SPIDEY_SCRIPT="titlepf.bmr+80:start:10;titlepf.bmr+280:cross:10;titlepf.bmr+480:cross:10;titlepf.bmr+700:cross:10" \
SPIDEY_SHOT_EVERY=400 RECOMP_CAPTURE_HIDDEN=1 RECOMP_MUTE=1 ./SpiderMan2.exe
```
