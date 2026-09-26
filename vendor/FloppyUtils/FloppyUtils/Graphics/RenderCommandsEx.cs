using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Common.Math;
using System.Runtime.InteropServices;

namespace FloppyUtils.Graphics;

public enum RenderCommandTypeEx
{
    // Fast texture copy, going through resource updates or same-size non-depth blit.
    CopyTextureRegionFast = 11,
    // Slow(er in theory) texture copy, going through much more flexible blit.
    CopyTextureRegionSlow = 12,
}

[StructLayout(LayoutKind.Explicit, Size = 0x78)]
public unsafe struct RenderCommandCopyTextureRegion
{
    [FieldOffset(0x0)]
    public RenderCommandTypeEx Type;

    [FieldOffset(0x8)]
    public Texture* SlotATexture;

    // Fast: Written as 8 bytes 0 but read as int? If int > -1, UpdateSubresource??
    // Slow: (uint)param_10 << 0x1e | puVar4[4] & 0x80000000 | param_3 & 0x3fffffff = 0 // Doesn't make much sense?
    // Slow: (uint)param_9 << 0x1f | (uint)param_10 << 0x1e & 0x7fffffff | param_3 & 0x3fffffff = 0 // Overwritten but earlier val not used?
    [FieldOffset(0x10)]
    public uint SlotAUnkFlags;

    // Fast: Written as 4 bytes 0
    // Slow: Can be 0
    [FieldOffset(0x18)]
    public IntRectangle* SlotARect;

    [FieldOffset(0x20)]
    public Texture* SlotBTexture;

    // Fast: Written as 4 bytes 0
    // Slow: (uint)param_8 << 0x1f | param_6 & 0x7fffffff = 0x80000000?
    [FieldOffset(0x28)]
    public uint SlotBUnkFlags;

    // Fast: Set to RectInline1
    // Slow: Can be 0
    [FieldOffset(0x30)]
    public IntRectangle* SlotBRect;

    [FieldOffset(0x38)]
    public IntRectangle RectInline1;

    // Fast: Read from this struct at this offs, which makes the inline rect even more confusing.
    [FieldOffset(0x48)]
    public void* Unk48;

    // Fast: 0xFFFF00001C000000 & 0XFFFFFFFEFFFFE000ul
    [FieldOffset(0x50)]
    public ulong Unk50;

    // Slow: Another rect inline slot
    [FieldOffset(0x48)]
    public IntRectangle RectInline2;
}
