using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FloppyUtils.Ptrs;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit, Size = 0x48C0)]
public unsafe struct PostEffectManagerEx
{
    // RenderTargetManager->CallbackUnk
    private static readonly RVASig RenderTargetManagerCallbackUnk = new("48 83 EC ??| (48 8B 05 *?? ?? ?? ??)| 49 8B F9| 49 8B F0| 4C 8B F2| 48 8B 98 ?? ?? 00 00| 48 85 DB|");

    private static PostEffectManagerEx* _instance;

    public static PostEffectManagerEx* Instance()
    {
        if (_instance != null || FService.SigScanner is null)
        {
            return _instance;
        }

        return _instance = (PostEffectManagerEx*) *(nint*) FService.SigScanner.ScanRVASig(RenderTargetManagerCallbackUnk);
    }

    public Span<PostEffectChainEx> PostEffectChains => Ptr.For(ref this).Offs<PostEffectChainEx>(0x28).Span(40);

    // 0x8E8: PostEffectRainbow of size 0x68
    // 0x950: PostEffectLensFlare of size 0x80
    // 0x9D0: PostEffectRoofQuery of size 0x3510
    // 0x3EE0: after ^

    [FieldOffset(0x4210)]
    public FsrData* Fsr;

    [FieldOffset(0x4218)]
    public void* Unk4218;

    /* Identifiable by being used for DLSS initialization when the upscale type == 2,
     * and containing NVSDK NGX capability param attribs at *+0x158 since 2024?
     * The function setting it up is very similar to NVIDIA's sample code,
     * but in case of emergency, CTRL+F NVSDK_NGX_D3D11_GetCapabilityParameters (always exported?)
     * and the two magic strings "SuperSampling.NeedsUpdatedDriver" and "SuperSampling.Available"
     */
    [FieldOffset(0x4220)]
    public void* DLSS;

    // Actual struct name unknown
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct FsrData
    {
        [FieldOffset(0x0)]
        public ConstantBuffer* Unk0;

        // Shaders are shader/sm5/compute/*.shcd

        [FieldOffset(0x8)]
        public ShaderCodeResourceHandle* Fsr1PrepareEasu;

        [FieldOffset(0x10)]
        public ShaderCodeResourceHandle* Fsr1Easu;

        [FieldOffset(0x18)]
        public ShaderCodeResourceHandle* Frs1Rcas;

        // == GameplayTextureUnk1
        [FieldOffset(0x20)]
        public Texture* GameplayTexture;

        [FieldOffset(0x28)]
        public Texture* Unk28;

        // == GameplayTextureUnk1 if FSR is on, otherwise null.
        [FieldOffset(0x30)]
        public Texture* Unk30;

        // == GameplayTextureUnk3 FSR is on, otherwise null.
        [FieldOffset(0x38)]
        public Texture* Unk38;
    }
}
