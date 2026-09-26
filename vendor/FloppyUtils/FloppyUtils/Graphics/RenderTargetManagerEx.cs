using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FloppyUtils.Ptrs;
using FloppyUtils.SourceGen.Attributes;
using System;
using System.Runtime.InteropServices;

namespace FloppyUtils.Graphics;

// RenderTargetManager is updated very sporadically, and some fields we need flew out.
// https://github.com/aers/FFXIVClientStructs/commit/589df2aa5cd9c98b4d62269034cd6da903f49b5f#diff-8e7d9b03cb91cb07a8d7b463b5be4672793a328703bde393e7acd890822a72cf
[StructLayout(LayoutKind.Explicit)]
public unsafe partial struct RenderTargetManagerEx
{
    [FieldOffset(0)]
    public RenderTargetManager _;

    // Title screen: Gets blitted FROM after actual gameplay texture. Actual purpose unknown.
    // In-game outdoors: Gets blitted to backbuffer.
    [FieldOffset(0x68)]
    public Texture* GameplayTextureUnk1;

    // Gets blitted TO after actual gameplay texture, using Unk1. Actual purpose unknown.
    [FieldOffset(0x100)]
    public Texture* GameplayTextureUnk2;

    // Title screen: Gets blitted to backbuffer. (Is this still accurate? Doesn't seem to be with 751hf1)
    // In-game outdoors: Totally unknown.
    [FieldOffset(0x258)]
    public Texture* GameplayTextureUnk3;

    private Span<ushort> DynamicResolutionHeights => Ptr.For(ref _.FrametimeAverage).Offs<ushort>(-0x10).Span(4);

    public ref ushort DynamicResolutionActualTargetHeight => ref DynamicResolutionHeights[0];
    public ref ushort DynamicResolutionTargetHeight => ref DynamicResolutionHeights[1];
    public ref ushort DynamicResolutionMaximumHeight => ref DynamicResolutionHeights[2];
    public ref ushort DynamicResolutionMinimumHeight => ref DynamicResolutionHeights[3];

    public ref float GraphicsRezoScaleUnkNegative => ref Ptr.For(ref _.FrametimeAverage).Offs<float>(0x8).Ref;

    private Span<float> GraphicsRezoScales => Ptr.For(ref GraphicsRezoScaleUnkNegative).After<float>().Span(5);

    /* Matching GraphicsConfig even when CustomResolution does its low VRAM shenanigans and thus the others are set to 1,
     * read when entering the FSR / DLSS command submission codepaths.
     * 751hf1: 140362d04
     * Also gets checked as the previous value to check if any updates are necessary.
     * 751hf2: 1402da777
     */
    public ref float GraphicsRezoScaleMain => ref GraphicsRezoScales[0];
    public ref float GraphicsRezoScaleUnk1 => ref GraphicsRezoScales[1];
    public ref float GraphicsRezoScaleGlassX => ref GraphicsRezoScales[2];
    public ref float GraphicsRezoScaleGlassY => ref GraphicsRezoScales[3];
    public ref float GraphicsRezoScaleUnk2 => ref GraphicsRezoScales[4];
}
