using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using RecompOne.Runtime.Assets;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace Recompiled;

/// <summary>
/// Spidey powers without the three-slot limit.
///
/// Retail keeps three power IDs per costume in the save block and the shell setter
/// (shell:80249168) turns them into the seven gameplay booleans at 800C2210. The CREATE A
/// SPIDER screen (shell:80249F04) lets the player fill those three slots, refuses
/// duplicates, and is locked until the save block's flag at +0x94 is set.
///
/// This port adds a chosen power set on top of the costume's own slots:
///
/// * the set defaults to the six positive powers (ID 7, two web cartridges, is a malus)
///   and is granted after every run of the retail setter and when the shell's title
///   loop returns to its menus (a new game never runs the setter, and nothing clears the
///   flags between the title and gameplay). Attract-mode demos keep the retail rule of
///   no powers, so their recorded input still plays back the same: the shell clears the
///   flags when it starts one and runs the setter again when the demo ends;
/// * CREATE A SPIDER is always unlocked, and its POWER 1..3 rows become one POWERS row.
///   Its list on the right turns into a checklist of all seven powers: cross toggles the
///   highlighted power, the first entry (DONE) closes the list, triangle leaves the screen
///   as before. The custom spider's own three slots are cleared, so what is checked is
///   exactly what it gets;
/// * the set is saved in the port's settings ("Powers", a bit per power ID).
///
/// SPIDEY_POWERS=0x3F overrides the saved set for one run and is never written back;
/// SPIDEY_POWERS=0 restores retail behaviour (costume powers only).
/// </summary>
public static class Powers
{
    const uint Save = 0x800B3180;          // unlock block; +0x73..0x75 power slots
    const uint Flags = 0x800C2210;         // seven power booleans, ID 1..7
    const uint Strings = 0x800A382C;       // menu string table
    const uint Names = 0x80251644;         // CREATE A SPIDER power names, ID 0..7
    const uint Pad = 0x800B29C8;
    const uint SetCursor = 0x80018278;
    const uint Sound = 0x8006E814;
    const uint IdleTimeout = 0x8023F864;   // shell: nonzero once the menus idled into a demo

    // Return addresses inside CREATE A SPIDER (shell:80249F04).
    const uint PowerRow = 0x80249FA0;      // AddRow(POWER 1)
    const uint PowerRow2 = 0x80249FAC;     // AddRow(POWER 2)
    const uint PowerRow3 = 0x80249FB8;     // AddRow(POWER 3)
    const uint SpacerRow = 0x80249FC8;     // AddRow("")
    const uint SlotValue = 0x8024A178;     // print the name of slot S0-1
    const uint MainInput = 0x8024A470;     // list input on the left-hand list
    const uint ListInput = 0x8024A4A0;     // list input on the right-hand list
    const uint NoneEntry = 0x8024A618;     // AddRow(name of ID 0)
    const uint PowerEntry = 0x8024A660;    // AddRow(name of ID S0)
    const uint EntryMatch = 0x8024A7BC;    // compare the picked entry with name S1

    public const int PowerCount = 7;
    public const int Default = 0x3F;
    const string Key = "Powers";
    const int LabelSize = 48;

    // Set by SPIDEY_POWERS: the run uses (and toggles) this instead of the saved set.
    static int? _session = ParseOverride(Environment.GetEnvironmentVariable("SPIDEY_POWERS"));
    static uint _text;
    static bool _french;
    static bool _freshList;
    static bool _hideRows;

    static int? ParseOverride(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        spec = spec.Trim();
        bool hex = spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hex ? spec[2..] : spec, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int mask))
            return null;
        Console.WriteLine($"[powers] SPIDEY_POWERS=0x{mask & 0x7F:X2} for this run");
        return mask & 0x7F;
    }

    /// <summary>Chosen powers, bit (ID - 1).</summary>
    public static int Chosen
    {
        get => _session ?? (ConfigManager.View.Values.ContainsKey(Key)
            ? ConfigManager.View.GetInt(Key, Default) & 0x7F : Default);
        private set
        {
            if (_session != null) { _session = value & 0x7F; return; }
            ConfigManager.View.SetInt(Key, value & 0x7F);
            ConfigManager.RequestSave();
        }
    }

    /// <summary>Post-hook of the retail power setter: adds the chosen powers.</summary>
    public static void Grant(CpuContext c, IMemory m) => Grant(m);

    public static void Grant(IMemory m)
    {
        int chosen = Chosen;
        for (int id = 1; id <= PowerCount; id++)
            if ((chosen & (1 << (id - 1))) != 0) m.WriteU32(Flags + (uint)(id - 1) * 4, 1);
        Console.WriteLine($"[powers] granted 0x{chosen:X2}");
    }

    /// <summary>
    /// Post-hook of the shell main, which runs its title loop as a nested call. A return
    /// on the idle test means an attract-mode demo is next, which plays without powers.
    /// </summary>
    public static void LeaveShell(CpuContext c, IMemory m)
    {
        var saved = c.Snapshot();
        Dispatcher.Call(c, m, IdleTimeout);
        bool demo = c.V0 != 0;
        c.Restore(saved);
        if (demo) Console.WriteLine("[powers] attract demo: retail powers kept");
        else Grant(m);
    }

    /// <summary>Pre-hook of the SPECIAL menu: CREATE A SPIDER is always available.</summary>
    public static void Unlock(CpuContext c, IMemory m) => m.WriteU8(Save + 0x94, 1);

    /// <summary>Pre-hook of CREATE A SPIDER.</summary>
    public static void Enter(CpuContext c, IMemory m)
    {
        if (_text != 0) LooseWadOverrides.TryFree(_text);
        _french = ReadString(m, m.ReadU32(Strings + 0x324)).StartsWith("POUVOIR", StringComparison.Ordinal);
        _text = LooseWadOverrides.AllocateScratch((4 + PowerCount) * LabelSize, "create a spider powers");
        _freshList = false;
        _hideRows = true;
        // Every power can be picked, and the custom spider carries no slot powers of its own.
        m.WriteU32(Save + 0x90, m.ReadU32(Save + 0x90) | 0x7Fu);
        for (uint i = 0; i < 3; i++) m.WriteU8(Save + 0x73 + i, 0);
        Write(m, RowLabel, _french ? "POUVOIRS" : "POWERS");
        Write(m, DoneLabel, _french ? "VALIDER" : "DONE");
        Write(m, Blank, "");
        Relabel(m);
        Console.WriteLine($"[powers] create a spider opened, powers=0x{Chosen:X2}");
    }

    /// <summary>Post-hook of CREATE A SPIDER; the retail setter has already run.</summary>
    public static void Leave(CpuContext c, IMemory m)
    {
        if (_text != 0) LooseWadOverrides.TryFree(_text);
        _text = 0;
        _freshList = false;
        _hideRows = false;
    }

    static uint RowLabel => _text;
    static uint Summary => _text + LabelSize;
    static uint DoneLabel => _text + 2 * LabelSize;
    static uint Blank => _text + 3 * LabelSize;
    static uint EntryLabel(int id) => _text + (uint)(3 + id) * LabelSize;

    /// <summary>
    /// Pre-hook of the list AddRow: one POWERS row, checklist entries. The left-hand
    /// list keeps its five rows so its panel keeps the retail height; POWER 2 and 3
    /// become blank and are hidden on the first input (see Input).
    /// </summary>
    public static bool AddRow(CpuContext c, IMemory m)
    {
        if (_text == 0) return true;
        switch (c.RA)
        {
            case PowerRow: c.A1 = RowLabel; return true;
            case PowerRow2: case PowerRow3: c.A1 = Blank; return true;
            case NoneEntry:
                if (m.ReadU8(c.S5 + 0xE) == 0) return true;
                c.A1 = DoneLabel;
                _freshList = true;
                return true;
            case PowerEntry:
                if (c.S0 is >= 1 and <= PowerCount) c.A1 = EntryLabel((int)c.S0);
                return true;
            default: return true;
        }
    }

    /// <summary>Pre-hook of the text printer: the POWERS row shows a count, no slot names.</summary>
    public static bool DrawValue(CpuContext c, IMemory m)
    {
        if (_text == 0 || c.RA != SlotValue) return true;
        if (c.S0 != 1) return false;
        c.A2 = Summary;
        return true;
    }

    /// <summary>Pre-hook of the case-insensitive compare: DONE stands for retail entry 0.</summary>
    public static bool SameName(CpuContext c, IMemory m)
    {
        if (_text == 0 || c.RA != EntryMatch || c.A0 != DoneLabel) return true;
        c.V0 = c.A1 == m.ReadU32(Names) ? 1u : 0u;
        return false;
    }

    /// <summary>Pre-hook of list input: cross toggles the highlighted power.</summary>
    public static void Input(CpuContext c, IMemory m)
    {
        if (_text == 0) return;
        if (c.RA == MainInput && _hideRows)
        {
            // Row +0x35 clear: the cursor steps over the row. Rows after the last
            // reachable one need the list to wrap, or a step down would never end.
            _hideRows = false;
            int count = m.ReadU8(c.A0 + 0x14);
            for (int hidden = 2; hidden < count; hidden++) m.WriteU8(c.A0 + (uint)hidden * 32 + 0x35, 0);
            m.WriteU8(c.A0 + 0xB, 1);
            return;
        }
        if (c.RA != ListInput || m.ReadU8(c.S5 + 0xE) == 0) return;
        uint list = c.A0;
        if (_freshList)
        {
            _freshList = false;
            var context = c.Snapshot();
            c.A0 = list; c.A1 = 1;
            Dispatcher.Call(c, m, SetCursor);
            c.Restore(context);
        }
        int row = m.ReadU8(list + 0xE);
        bool select = m.ReadU8(Pad + 0x31) != 0 || m.ReadU8(Pad + 0xE1) != 0;
        if (!select || row is < 1 or > PowerCount) return;
        m.WriteU8(Pad + 0x31, 0);
        m.WriteU8(Pad + 0xE1, 0);
        Chosen ^= 1 << (row - 1);
        Relabel(m);
        var saved = c.Snapshot();
        c.A0 = 0x1F; c.A1 = 0x2000; c.A2 = 0;
        Dispatcher.Call(c, m, Sound);
        c.Restore(saved);
        Console.WriteLine($"[powers] power {row} {(Has(row) ? "on" : "off")}, powers=0x{Chosen:X2}");
    }

    static bool Has(int id) => (Chosen & (1 << (id - 1))) != 0;

    static void Relabel(IMemory m)
    {
        int n = BitOperations.PopCount((uint)Chosen);
        string summary = n switch
        {
            0 => _french ? "AUCUN" : "NONE",
            PowerCount => _french ? "TOUS" : "ALL",
            1 => _french ? "1 ACTIF" : "1 ACTIVE",
            _ => _french ? $"{n} ACTIFS" : $"{n} ACTIVE",
        };
        Write(m, Summary, summary);
        for (int id = 1; id <= PowerCount; id++)
        {
            string name = ReadString(m, m.ReadU32(Names + (uint)id * 4));
            string state = Has(id) ? (_french ? "OUI" : "ON") : (_french ? "NON" : "OFF");
            Write(m, EntryLabel(id), _french ? $"{name} : {state}" : $"{name}: {state}");
        }
    }

    static string ReadString(IMemory m, uint p)
    {
        var sb = new StringBuilder();
        for (uint i = 0; p != 0 && i < LabelSize; i++)
        {
            byte b = m.ReadU8(p + i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    static void Write(IMemory m, uint p, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        int length = Math.Min(bytes.Length, LabelSize - 1);
        for (int i = 0; i < length; i++) m.WriteU8(p + (uint)i, bytes[i]);
        m.WriteU8(p + (uint)length, 0);
    }
}
