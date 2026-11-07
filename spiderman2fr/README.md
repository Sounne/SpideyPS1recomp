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

## Unattended runs

Every `SPIDEY_*` switch of the USA port works here. Archive anchors use the French
file names (`titlepf.bmr`, `legalpf.rle`). `RECOMP_CAPTURE_HIDDEN=1` hides the window
and `RECOMP_MUTE=1` silences audio.

```bash
SPIDEY_SCRIPT="titlepf.bmr+80:start:10;titlepf.bmr+280:cross:10;titlepf.bmr+480:cross:10;titlepf.bmr+700:cross:10" \
SPIDEY_SHOT_EVERY=400 RECOMP_CAPTURE_HIDDEN=1 RECOMP_MUTE=1 ./SpiderMan2.exe
```
