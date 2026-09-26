using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FloppyUtils.Ptrs;
using System.Runtime.InteropServices;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit)]
public unsafe struct GraphicsConfigEx
{
    [FieldOffset(0)]
    public GraphicsConfig _;

    [FieldOffset(0x43)]
    public byte UnkAffectsRTMGameplayTexture;

    public ref byte DynamicRezoEnable => ref _.DynamicRezoEnable;
    /* Part of checks in TaskUpdateGraphicsRender responsible for firing Device callback OnUnkRenderTargetManager1.
     * CustomResolution previously misused this for allowing scaling beyond 1x.
     * Old info, needs recheck:
     * Set to 0 in main menu and gpose, preventing scaling beyond 1.
     */
    public ref byte DynamicRezoEnableBeyond1 => ref Ptr.For(ref DynamicRezoEnable).Offs<byte>(0x1).Ref;
    public ref byte DynamicRezoEnableCutScene => ref Ptr.For(ref DynamicRezoEnable).Offs<byte>(0x2).Ref;
    public ref byte DynamicRezoEnableUnkx47 => ref Ptr.For(ref DynamicRezoEnable).Offs<byte>(0x3).Ref;
    public ref byte DynamicRezoEnableUnkx48 => ref Ptr.For(ref DynamicRezoEnable).Offs<byte>(0x4).Ref;

    public ref float GraphicsRezoScale => ref _.GraphicsRezoScale;
    public ref float GraphicsRezoUnk1 => ref Ptr.For(ref GraphicsRezoScale).Offs<float>(0x4).Ref;
    public ref byte GraphicsRezoUpscaleType => ref _.GraphicsRezoUpscaleType;
    public ref byte GraphicsRezoUnk2 => ref Ptr.For(ref GraphicsRezoUpscaleType).Offs<byte>(0x1).Ref;
}
