using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FloppyUtils.Ptrs;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit)]
public unsafe ref struct DeviceEx
{
    [FieldOffset(0)]
    public Device _;

    /* At least confirmed to run on dynamic scale changes, but not sure yet if it's exclusively for that.
     * Exec'd via TaskUpdateGraphicsRender -> RenderTargetManager update.
     */
    [FieldOffset(0x30)]
    public Device.CallbackManager* OnUnkRenderTargetManager1;

    /* Not called via the non-inlined exec func, but referred to in PostTick before OnResizeDestroy if RequestResolutionChangeUnk3.
     * After this is called, RequestResolutionChangeUnk3 is set to 0.
     */
    [FieldOffset(0x38)]
    public Device.CallbackManager* OnUnkRenderTargetManager2;

    public readonly SwapChainEx* SwapChain => (SwapChainEx*) _.SwapChain;

    public ref byte RequestResolutionChange => ref _.RequestResolutionChange;

    /* Checked when copying size from device to RTM, in the same place ToneAdjustSource is checked.
     * Checked in PostTick together with RequestResolutionChange to determine if OnResizeDestroy needs to be called.
     * Copied into RequestResolutionChangeUnk3 in PostTick.
     * Set to 1 by OnUnkRenderTargetManager1, set to 0 in PostTick together with RequestResolutionChange after getting copied to RequestResolutionChangeUnk3.
     */
    public ref byte RequestResolutionChangeUnk1 => ref Ptr.For(ref _.RequestResolutionChange).Offs<byte>(0x1).Ref;

    public ref byte UnkRelatedToDynScale => ref Ptr.For(ref _.RequestResolutionChange).Offs<byte>(0x2).Ref;

    public ref byte RequestResolutionChangeUnk3 => ref Ptr.For(ref _.RequestResolutionChange).Offs<byte>(0x3).Ref;

    public ref uint RequestRender => ref Ptr.For(ref _.Width).Offs<uint>(-0x4).Ref;

    public ref uint Width => ref _.Width;

    public ref uint Height => ref _.Height;

    public ref byte ExclusiveFullscreen => ref Ptr.For(ref _).Offs<nint>(nameof(_.hWnd)).Offs<byte>(-0x10).Ref;

    public ref void* hWnd => ref _.hWnd;

    public ref int DisplayModeIndex => ref Ptr.For(ref _).Offs<nint>(nameof(_.hWnd)).Offs<int>(0x8).Ref;

    public ref uint NewWidth => ref _.NewWidth;

    public ref uint NewHeight => ref _.NewHeight;

    public ref int FrameRate => ref _.FrameRate;

    public DisplayMode* DisplayModes => (DisplayMode*) Ptr.For(ref _.FrameRate).Offs<nint>(0x8).Ref;

    public readonly IDXGIOutput* DXGIOutput => (IDXGIOutput*) _.DXGIOutput;
    public readonly ID3D11DeviceContext* D3D11DeviceContext => (ID3D11DeviceContext*) _.D3D11DeviceContext;
    public readonly ImmediateContextEx* ImmediateContext => (ImmediateContextEx*) _.ImmediateContext;
}
