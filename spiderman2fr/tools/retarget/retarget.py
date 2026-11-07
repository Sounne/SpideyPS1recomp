"""Retarget the USA Spider-Man 2 port (spiderman2/, SLUS-013.78) to the French PAL
disc "Spider-Man 2 - La Revanche d'Electro" (SLES-036.24).

The French executable is the same build of the same source with small per-function
edits: all 1,767 main-executable functions and every overlay function line up one to
one when the two linear-sweep maps are aligned on function size, and 96% of them keep
their exact size. So nothing has to be rediscovered -- every address the USA port
hard-codes is carried across:

* Code addresses go through the sweep alignment (`Translator`). A function whose size
  is unchanged keeps every interior offset; a resized function only keeps its entry,
  and any interior address in one must be listed in CODE_OVERRIDES after reading both
  sides.
* Data addresses cannot be aligned that way, so each one the port touches is listed in
  DATA with the evidence that pinned it (a FR instruction that forms the address in
  the function the USA patch names). The sections moved by different amounts:
  rodata +0xF50, .data +0xF54, .sdata/.sbss +0xEC0, the shell overlay's data +0x194.
* $gp moved by +0xF54 while the small-data variables moved by +0xEC0, so every
  gp-relative offset in the +0xEC0 region shrinks by 0x94 (GP_OFFSETS).

Inputs (read-only): ../spiderman2/** (the USA port) and this silo's FR sweep maps,
config/funcmaps/*_sweep.json, produced by tools/genmaps.py from the French disc.
Run from spiderman2fr/:   python tools/retarget/retarget.py
It rewrites patches/, port/Program.cs, config/spiderman2.json, the seed funcmaps and
the pipeline scripts, and fails loudly on any USA address it cannot account for.
"""
import difflib
import bisect
import json
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FR = os.path.dirname(os.path.dirname(HERE))
USA = os.path.join(os.path.dirname(FR), 'spiderman2')

USA_GP = 0x800C1764   # derived: FR gp 0x800C26B8 minus the .sdata section delta 0xF54
FR_GP = 0x800C26B8    # lui gp, 0x800C / addiu gp, gp, 0x26B8 at the FR entry point

# USA data address -> (FR address, evidence)
DATA = {
    0x8009D578: (0x8009E294, 'main text limit; entry code forms 0x8009E294'),
    0x800A1E58: (0x800A2DA8, 'cheat jump table; formed in FR 0x8007B494 (USA 0x8007A750)'),
    0x800A3638: (0x800A4588, 'level descriptors; formed in FR 0x8001A704 (USA 0x8001A700)'),
    0x800A382C: (0x800A477C, 'menu string table; formed in FR shell 0x80243844'),
    0x800A5F38: (0x800A6E8C, 'armor hash pairs; 14 pairs bounded by 0 and a pointer table'),
    0x800A882C: (0x800A9780, 'GPU driver vtable (comment only; .data +0xF54)'),
    0x800ACED8: (0x800ADE2C, 'model table; formed in FR ModelFind 0x80075270'),
    0x800B1264: (0x800B21B8, 'libpad handler table (comment only; .data +0xF54)'),
    0x800B1278: (0x800B21CC, 'libpad handler slot (comment only; .data +0xF54)'),
    0x800B29C8: (0x800B391C, 'pad block; .data +0xF54, 95 FR references'),
    0x800B29D0: (0x800B3924, 'comment only; .data +0xF54'),
    0x800B30E4: (0x800B4038, 'sp_tex table; formed in FR 0x8004EB6C (USA 0x8004E4BC)'),
    0x800B3130: (0x800B4084, 'cheat code table; formed in FR 0x8007B690 (USA 0x8007A94C)'),
    0x800B3180: (0x800B40D4, 'unlock block; formed by every FR unlock handler'),
    0x800B3184: (0x800B40D8, 'current level name; .data +0xF54, 16 FR references'),
    0x800B31F2: (0x800B4146, 'costume selection byte; formed in FR shell'),
    0x800B31F8: (0x800B414C, 'costume unlock mask; .data +0xF54'),
    0x800B3254: (0x800B41A8, 'camera position; .data +0xF54'),
    0x800B32C4: (0x800B4218, 'camera rotation; .data +0xF54, 14 FR references'),
    0x800C09E1: (0x800C1935, 'libcd last command byte; 10 FR references'),
    0x800C1F94: (0x800C2E54, 'simulation tick; .sdata +0xEC0'),
    0x800C1FB8: (0x800C2E78, 'What If flag; FR handler 0x8007B5C8 xors it'),
    0x800C1FD8: (0x800C2E98, 'level select flag; FR handler 0x8007B62C stores it'),
    0x800C1FE0: (0x800C2EA0, 'debug flag; FR handler 0x8007B4CC toggles it'),
    0x800C1FE4: (0x800C2EA4, 'big head flag; FR handler 0x8007B658 toggles it'),
    0x800C1FEC: (0x800C2EAC, 'big feet flag; FR handler 0x8007B5A8 toggles it'),
    0x800C21F4: (0x800C30B4, 'player pointer; read by FR HUD draw 0x800737BC'),
    0x800C2210: (0x800C30D0, 'power booleans; FR shell 0x802491AC writes 0x800C30D0..E8'),
    0x800C2228: (0x800C30E8, 'last power boolean; FR shell 0x802491AC'),
    0x800C2258: (0x800C3118, 'costume mesh slot; gp-formed in FR 0x8004EB6C'),
    0x800C225C: (0x800C311C, 'costume mesh slot; .sdata +0xEC0'),
    0x800C2260: (0x800C3120, 'costume mesh slot; .sdata +0xEC0'),
    0x800C236D: (0x800C322D, 'spidey resource index byte; .sdata +0xEC0'),
    0x800C2434: (0x800C32F4, 'vblank callback counter; read by FR shell 0x80243844'),
    0x800C247C: (0x800C333C, 'frame packet block; read by FR 0x80031770 (USA 0x800314E4)'),
    0x800C2484: (0x800C3344, 'packet cursor; written by FR 0x8007606C'),
    0x800C6BE0: (0x800C6BE0, 'CD.HED table in .bss (comment only; not retargeted)'),
    # Shell overlay data follows its code, which grew by 0x194 bytes.
    0x80250340: (0x802504D4, 'shell power jump table (comment only; shell data +0x194)'),
    0x80251560: (0x802516F4, 'retail costume viewer table; shell data +0x194'),
    0x80251568: (0x802516FC, 'retail costume viewer table +8; shell data +0x194'),
}

# Interior addresses of functions whose size changed, read on both sides.
CODE_OVERRIDES = {
    # USA func_80075438 (1236 bytes) -> FR func_8007606C (1512 bytes). The FR build
    # inserts a packet-type walk that leaves the tail packet in t5; the 20-instruction
    # anchor/tail/ramp block is otherwise unchanged and starts at 0x8007617C.
    0x80075538: 0x8007617C,
    0x80075588: 0x800761CC,
    # The PAL build links libetc's SetVideoMode (0x800979DC) beside GetVideoMode, both
    # on the variable at 0x800C1740; the USA build has GetVideoMode alone.
    0x80096D24: 0x800979F0,
}

# Region constants and other values that only look like addresses.
KEEP = {0x80000000, 0x80000004, 0x80010000, 0x80200000, 0x8027D000, 0x80280000,
        0x802A0000, 0x802B0000, 0x80300000, 0x807C0000, 0x807F0000, 0x807FF000,
        0x80780000, 0x80800000}

# gp-relative byte offsets used by the patches (USA -> FR).
GP_SDATA_SHIFT = 0xF54 - 0xEC0


def gp_fr(usa_off):
    usa = USA_GP + usa_off
    assert 0x800C1F94 - 0x200 <= usa < 0x800C3000, hex(usa)
    return usa + 0xEC0 - FR_GP


class Translator:
    def __init__(self):
        manifest = json.load(open(os.path.join(USA, 'config', 'overlays', 'manifest.json')))
        self.rows = []
        for mod in ['main'] + list(manifest):
            u = json.load(open(os.path.join(USA, 'config', 'funcmaps', mod + '_sweep.json')))['functions']
            f = json.load(open(os.path.join(FR, 'config', 'funcmaps', mod + '_sweep.json')))['functions']
            sm = difflib.SequenceMatcher(None, [x['size'] for x in u], [x['size'] for x in f],
                                         autojunk=False)
            for tag, i1, i2, j1, j2 in sm.get_opcodes():
                pair = tag in ('equal', 'replace') and i2 - i1 == j2 - j1
                for k in range(i1, i2):
                    a = u[k]
                    b = f[j1 + k - i1] if pair else None
                    self.rows.append((int(a['address'], 16), a['size'],
                                      int(b['address'], 16) if b else None,
                                      b['size'] if b else None, mod))
        self.rows.sort()
        self.starts = [r[0] for r in self.rows]

    def code(self, a):
        """FR address for USA code address `a`, or None if it is not code."""
        i = bisect.bisect_right(self.starts, a) - 1
        if i < 0:
            return None
        us, usz, fs, fsz, mod = self.rows[i]
        if mod == 'main' and a >= 0x8009D578:
            return None
        off = a - us
        if off >= usz:
            # Padding or an entry point the sweep did not split out: translatable when
            # both neighbours moved by the same amount.
            if i + 1 < len(self.rows) and fs is not None:
                nus, _, nfs, _, nmod = self.rows[i + 1]
                if nmod == mod and nfs is not None and a < nus and nfs - nus == fs - us \
                        and usz == fsz:
                    return a + fs - us
            return None
        if fs is None:
            raise KeyError(f'{a:08X}: inside an unaligned {mod} region')
        if usz != fsz and off:
            raise KeyError(f'{a:08X}: interior of resized {mod}:{us:08X} ({usz}->{fsz} bytes)')
        return fs + off

    def __call__(self, a):
        if a in KEEP:
            return a
        if a in CODE_OVERRIDES:
            return CODE_OVERRIDES[a]
        if a in DATA:
            return DATA[a][0]
        r = self.code(a)
        if r is None:
            raise KeyError(f'{a:08X}: not code and not in DATA')
        return r


ADDR = re.compile(r'(?<![0-9A-Za-z])((?:0x)?|func_|call_)(80[0-9A-Fa-f]{6})(?![0-9A-Fa-f])')


def retarget_text(text, xl, where, skip_lines=()):
    out = []
    for n, line in enumerate(text.splitlines(keepends=True), 1):
        if n in skip_lines:
            out.append(line)
            continue

        s = line.lstrip()
        comment = s.startswith(('//', '#', '*', '"""')) or '"_note"' in s or '"_comment"' in s

        def sub(m):
            a = int(m.group(2), 16)
            try:
                t = xl(a)
            except KeyError as e:
                if comment:   # prose about another game or an unported detail
                    print(f'  note {where}:{n}: kept {m.group(0)} ({e})')
                    return m.group(0)
                raise SystemExit(f'{where}:{n}: {e}')
            digits = f'{t:08X}' if m.group(2).upper() == m.group(2) else f'{t:08x}'
            return m.group(1) + digits
        out.append(ADDR.sub(sub, line))
    return ''.join(out)


def exact(text, old, new, count=1, where=''):
    if '\r\n' in text:
        old, new = old.replace('\n', '\r\n'), new.replace('\n', '\r\n')
    if text.count(old) != count:
        raise SystemExit(f'{where}: expected {count} x {old!r}, found {text.count(old)}')
    return text.replace(old, new)


# Non-address edits, applied after the address pass: (file, old, new, count).
EDITS = [
    # Disc identity.
    ('port/Program.cs', 'const string BootFile = "SLUS_013.78";', 'const string BootFile = "SLES_036.24";', 1),
    ('port/Program.cs', '"Spider-Man 2: Enter Electro (USA)",', '"Spider-Man 2: La Revanche d\'Electro (France)",', 1),
    ('port/Program.cs', '"SLUS-01378",', '"SLES-03624",', 1),
    ('port/Program.cs', 'Path.Combine(dir, "spiderman2", "extracted")', 'Path.Combine(dir, "spiderman2fr", "extracted")', 1),
    ('port/Program.cs', "Spider-Man's disc does not carry SLUS_013.78", "Spider-Man's disc does not carry SLES_036.24", 1),
    ('config/spiderman2.json', '"id": "SLUS-01378"', '"id": "SLES-03624"', 1),
    ('tools/mkconfig.py', "'id': 'SLUS-01378'", "'id': 'SLES-03624'", 1),
    ('tools/disc.py',
     "DISC = os.path.join(os.path.dirname(os.path.abspath(__file__)),\n"
     "                    '..', '..', 'Spider-Man 2 - Enter - Electro (USA) (Rev 1).bin')\n"
     "GAME_ID = 'SLUS_013.78'",
     "DISC = os.environ.get('SPIDEY_DISC') or os.path.join(\n"
     "    os.path.dirname(os.path.abspath(__file__)), '..', '..', '..',\n"
     "    \"Spider-Man 2 - La Revanche d'Electro (France)\",\n"
     "    \"Spider-Man 2 - La Revanche d'Electro (France).bin\")\n"
     "GAME_ID = 'SLES_036.24'", 1),
    ('tools/genmaps.py', "MAIN_FILE = 'SLUS_013.78'", "MAIN_FILE = 'SLES_036.24'", 1),
    ('tools/genmaps.py', "MAIN_SIZE = 'BF800'", "MAIN_SIZE = 'C0000'", 1),
    ('tools/fixmaps.py', "'SLUS_013.78'", "'SLES_036.24'", 1),
    ('tools/fixmaps.py', 'exe[0x800:0x800 + 0xBF800]', 'exe[0x800:0x800 + 0xC0000]', 1),
    ('tools/xref.py', "EXE = 'extracted/SLUS_013.78'", "EXE = 'extracted/SLES_036.24'", 1),
    ('tools/callers.py', "'extracted/SLUS_013.78'", "'extracted/SLES_036.24'", 1),
    # PAL: the French build calls SetVideoMode(MODE_PAL) and its game loop counts 50 Hz
    # vblanks. SPIDEY_VBLANK_HZ=60 restores the NTSC clock (about 20% faster).
    ('port/Program.cs', '        RecompOne.Runtime.Runtime.VBlanksPerFrame = 2;\n',
     '        RecompOne.Runtime.Runtime.VBlanksPerFrame = 2;\n'
     '        RecompOne.Runtime.Runtime.VBlankHz =\n'
     '            double.TryParse(Environment.GetEnvironmentVariable("SPIDEY_VBLANK_HZ"),\n'
     '                System.Globalization.NumberStyles.Float,\n'
     '                System.Globalization.CultureInfo.InvariantCulture, out double hz) ? hz : 50.0;\n', 1),
    ('port/Program.cs',
     'Console.WriteLine("[timing] 30 FPS maximum; individual 60 Hz vblank interrupts");',
     'Console.WriteLine($"[timing] PAL: {RecompOne.Runtime.Runtime.VBlankHz:0.#} Hz vblank interrupts, "\n'
     '            + $"{RecompOne.Runtime.Runtime.VBlankHz / 2:0.#} FPS maximum");', 1),
    # The project reuses the USA port's icon, bundled payload and suit catalogue.
    ('port/SpiderMan2.csproj', r'<ApplicationIcon>SpiderMan2.ico</ApplicationIcon>',
     r'<ApplicationIcon>..\..\spiderman2\port\SpiderMan2.ico</ApplicationIcon>', 1),
    ('port/SpiderMan2.csproj', r'<EmbeddedResource Include="bundled\runtime-assets.zip">',
     r'<EmbeddedResource Include="..\..\spiderman2\port\bundled\runtime-assets.zip">', 1),
    ('port/SpiderMan2.csproj', r'<None Remove="legacy-suits\**\*" />',
     r'<None Remove="..\..\spiderman2\port\legacy-suits\**\*" />', 1),
    ('port/SpiderMan2.csproj', r'<None Include="legacy-suits\**\*"',
     r'<None Include="..\..\spiderman2\port\legacy-suits\**\*"', 1),
    # gp-relative offsets: the variables moved 0x94 less than gp did.
    ('patches/ArmorTextures.cs', 'c.GP + 0xAA4u', f'c.GP + 0x{gp_fr(0xAA4):X}u', 1),
    ('patches/Costume.cs', 'c.GP + 0xAA8', f'c.GP + 0x{gp_fr(0xAA8):X}', 1),
    ('patches/FramePackets.cs', 'c.GP + 0x8D0', f'c.GP + 0x{gp_fr(0x8D0):X}', 1),
    ('patches/MenuPacketOrder.cs', 'c.GP + 0x1010', f'c.GP + 0x{gp_fr(0x1010):X}', 1),
    ('patches/Timing.cs', 'c.GP + 0xCD0u', f'c.GP + 0x{gp_fr(0xCD0):X}u', 1),
    ('patches/TriggerTest.cs', 'c.GP + 0xBFCu', f'c.GP + 0x{gp_fr(0xBFC):X}u', 1),
    ('patches/TriggerTest.cs', 'c.GP + 0xC00u', f'c.GP + 0x{gp_fr(0xC00):X}u', 1),
    ('patches/TriggerTest.cs', 'gp+0xBFC with gp+0xC00 entries',
     f'gp+0x{gp_fr(0xBFC):X} with gp+0x{gp_fr(0xC00):X} entries', 1),
    # The tail of .sbss moved by +0xEB8, not +0xEC0: FR mesh draw 0x80084F3C tests
    # gp+0x12BC to choose between the 0x80088D14 and 0x80088AE0 transforms.
    ('patches/PlayerShadow.cs', 'c.GP + 0x1358u', 'c.GP + 0x12BCu', 1),
    ('patches/PlayerShadow.cs', 'With gp+0x1358 clear', 'With gp+0x12BC clear', 1),
    ('patches/MovieTrace.cs', 'OffFlag = 1912, OffCurrent = 1944, OffTarget = 1952',
     f'OffFlag = {gp_fr(1912)}, OffCurrent = {gp_fr(1944)}, OffTarget = {gp_fr(1952)}', 1),
    ('patches/MovieTrace.cs', 'stores it at gp+1944, and compares it with a',
     f'stores it at gp+{gp_fr(1944)}, and compares it with a', 1),
    ('patches/MovieTrace.cs', 'target at gp+1952.', f'target at gp+{gp_fr(1952)}.', 1),
    ('patches/MovieTrace.cs', 'finished" flag at gp+1912', f'finished" flag at gp+{gp_fr(1912)}', 1),
    # FR keeps the tail packet in t5 (USA: t2); see CODE_OVERRIDES.
    ('patches/MenuPacketOrder.cs', 'uint anchor = c.T0 & mask, tail = c.T2 & mask;',
     'uint anchor = c.T0 & mask, tail = c.T5 & mask;', 1),
    ('patches/MenuPacketOrder.cs', "// SM2's func_8007606C has the same single-DR_AREA alias as SM1's menu pass.",
     "// SM2's func_8007606C has the same single-DR_AREA alias as SM1's menu pass.\n"
     "    // French build: the tail packet is in t5 and the ramp is gp+0xF7C (USA t2, gp+0x1010).", 1),
    # Shell costume-viewer table offsets in the generated code (lui 0x8025 + lo).
    ('tools/patch_costume_viewer.py', "[('1560', 4), ('1568', 1)]",
     f"[('{DATA[0x80251560][0] & 0xFFFF:04X}', 4), ('{DATA[0x80251568][0] & 0xFFFF:04X}', 1)]", 1),
    ('tools/patch_costume_viewer.py', "' + 8u' if offset == '1568' else ''",
     f"' + 8u' if offset == '{DATA[0x80251568][0] & 0xFFFF:04X}' else ''", 1),
]

# Lines that carry another game's addresses (Spider-Man 1) and must stay as written.
SKIP = {
    'tools/mkconfig.py': lambda text: {n for n, l in enumerate(text.splitlines(), 1)
                                       if 'frustum 8007B1B4' in l},
}

TEXT_DIRS = ['patches', 'tools']
TEXT_FILES = ['port/Program.cs', 'port/SpiderMan2.csproj', 'config/spiderman2.json',
              'config/funcmaps/manual.json', 'config/funcmaps/psyq_main.json']


def retarget_syms(xl):
    """The PsyQ symbol dump is a developer aid (tools/mipsdis.py); keep what maps."""
    src = json.load(open(os.path.join(USA, 'config', 'funcmaps', 'psyq_main.json.syms.json')))
    out, dropped = {}, 0
    for name, addr in src.items():
        try:
            out[name] = '0x%08X' % xl(int(addr, 16))
        except KeyError:
            dropped += 1
    with open(os.path.join(FR, 'config', 'funcmaps', 'psyq_main.json.syms.json'), 'w') as fh:
        json.dump(out, fh, indent=1, sort_keys=True)
        fh.write('\n')
    print(f'  psyq symbols: {len(out)} kept, {dropped} data symbols dropped')


def main():
    xl = Translator()
    files = list(TEXT_FILES)
    for d in TEXT_DIRS:
        for name in sorted(os.listdir(os.path.join(USA, d))):
            if name.endswith(('.cs', '.py', '.sh')):
                files.append(f'{d}/{name}')
    edits = {}
    for f, old, new, count in EDITS:
        edits.setdefault(f, []).append((old, new, count))

    for rel in files:
        src = os.path.join(USA, rel)
        text = open(src, encoding='utf-8', newline='').read()
        skip = SKIP[rel](text) if rel in SKIP else ()
        text = retarget_text(text, xl, rel, skip)
        for old, new, count in edits.pop(rel, []):
            text = exact(text, old, new, count, rel)
        dst = os.path.join(FR, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(dst, 'w', encoding='utf-8', newline='') as fh:
            fh.write(text)
    if edits:
        raise SystemExit(f'unapplied edits: {sorted(edits)}')
    retarget_syms(xl)

    # Seed map sizes: a manual symbol whose sweep function was resized gets the FR size.
    path = os.path.join(FR, 'config', 'funcmaps', 'manual.json')
    manual = json.load(open(path))
    fr_sweep = {int(x['address'], 16): x['size'] for x in json.load(
        open(os.path.join(FR, 'config', 'funcmaps', 'main_sweep.json')))['functions']}
    us_sweep = {int(x['address'], 16): x['size'] for x in json.load(
        open(os.path.join(USA, 'config', 'funcmaps', 'main_sweep.json')))['functions']}
    usa_manual = {x['name']: x for x in json.load(open(os.path.join(USA, 'config', 'funcmaps', 'manual.json')))['functions']}
    for fn in manual['functions']:
        a = int(fn['address'], 16)
        ua = int(usa_manual[fn['name']]['address'], 16)
        if a in fr_sweep and us_sweep.get(ua) == fn['size'] and fr_sweep[a] != fn['size']:
            print(f'  manual {fn["name"]}: size {fn["size"]} -> {fr_sweep[a]}')
            fn['size'] = fr_sweep[a]
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(manual, fh, indent=1)
        fh.write('\n')
    print(f'retargeted {len(files)} files to SLES-036.24')


if __name__ == '__main__':
    main()
