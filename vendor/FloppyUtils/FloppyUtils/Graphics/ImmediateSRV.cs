using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using System.Runtime.InteropServices;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct ImmediateSRV
{
    // D3D11ShaderResourceView must be at 0x58 + (GlobalIndex % 3) * 8
    // Smells like something swapchain-adjacent, but have yet to analyze anything with != null.
    [FieldOffset(0x0)]
    public nint Unkx0;

    [FieldOffset(0x8)]
    public Texture* Texture;

    [FieldOffset(0x10)]
    public uint Unkx10;

    [FieldOffset(0x14)]
    public uint Unkx14;
}
