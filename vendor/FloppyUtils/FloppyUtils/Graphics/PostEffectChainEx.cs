using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit, Size = 0x38)]
public unsafe struct PostEffectChainEx
{
    [FieldOffset(0x0)]
    public void* Vtbl;

    [FieldOffset(0x8)]
    public UnknownPart* UnkParts;

    [FieldOffset(0x10)]
    public uint UnkPartCount;

    // 4 bytes??, have yet to find RW, given that I've once spotted it say "GLSL" in DXVK, I presume it's unused alignment padding. -jade

    // Matching UnkTexture1 of last part, but sometimes null
    // Both are sometimes set to GameplayTextureUnk1, unclear yet if only used as output for final blit though.
    [FieldOffset(0x18)]
    public Texture* UnkTexture;

    // 8 bytes?? Have yet to find RW
    // First 4 always seem to be 61 00 00 00?

    [FieldOffset(0x28)]
    public ConstantBuffer* UnkConstantBuffer;

    // 8 bytes?? Have yet to find RW
    /* Seen:
     * 00 00 80 3F 01 00 00 00
     * DD E4 5A 3F 02 00 00 00
     * DD E4 5A 3F 03 00 00 00
     * DD E4 5A 3F 07 00 00 00
     * DD E4 5A 3F 1F 00 00 00
     * DD E4 5A 3F 5A 00 00 00
     * DD E4 5A 3F 00 01 00 00
     */

    [StructLayout(LayoutKind.Explicit, Size = 0xA0)]
    public struct UnknownPart
    {
        [FieldOffset(0x0)]
        public void* Vtbl;

        [FieldOffset(0x8)]
        public ShaderCodeResourceHandle* ShaderCodeResourceHandle;

        [FieldOffset(0x10)]
        public void* Unk10;

        [FieldOffset(0x18)]
        public void* Unk18;

        [FieldOffset(0x20)]
        public void* Unk20;

        [FieldOffset(0x28)]
        public Texture* UnkTexture1;

        // 0x30 always 00 00 00 00 00 00 00 00?

        [FieldOffset(0x38)]
        public Texture* UnkTexture2;

        // Followed by zeroes until 0x70?
    }
}
