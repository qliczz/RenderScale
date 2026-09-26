using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using System.Runtime.InteropServices;

namespace FloppyUtils.Graphics;

// ImmediateContextEx in FFXIVClientStructs is a bare minimum.
// https://github.com/aers/FFXIVClientStructs/blob/ef5e9a5997671fb2c9a72cb9d57d841855f62085/FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/ImmediateContext.cs
[StructLayout(LayoutKind.Explicit)]
public unsafe struct ImmediateContextEx
{
    [FieldOffset(0)]
    public ImmediateContext _;

    /// <summary>
    /// Determines if DeviceX11.PostTick skips ProcessCommands or not.
    /// Not much more is known about this at the moment.
    /// </summary>
    [FieldOffset(0x18)]
    public nint IfNonZeroSkipPostTickProcess;
}
