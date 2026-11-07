using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace Recompiled;

public static class VideoSetup
{
    static readonly NativeVideoSetup.Bindings Menu=new(0x80243844,0x800A477C,0x800B391C,
        0x80243AD8,0x8006F434,0x6C,0x78,0x300,0x1D4,0x800183AC);
    public static void Options(CpuContext c,IMemory m)=>NativeVideoSetup.RenameScreen(m,Menu);
    public static void Run(CpuContext c,IMemory m)=>NativeVideoSetup.Run(c,m,Menu);
}
