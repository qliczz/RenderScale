using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FloppyUtils.Ptrs;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit)]
public unsafe ref struct SwapChainEx
{
    [FieldOffset(0)]
    public SwapChain _;

    public ref uint Width => ref _.Width;

    public ref uint Height => ref _.Height;

    public ref byte UnkAlways1OnResize => ref Ptr.For(ref _.Height).After<byte>().Ref;
    
    public ref Texture* BackBuffer => ref _.BackBuffer;

    public ref Texture* DepthStencil => ref _.DepthStencil;

    public readonly IDXGISwapChain* DXGISwapChain => (IDXGISwapChain*) _.DXGISwapChain;
}
