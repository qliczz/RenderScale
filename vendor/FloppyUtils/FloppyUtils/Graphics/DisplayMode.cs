using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace FloppyUtils.Graphics;

[StructLayout(LayoutKind.Explicit, Size = 0x2C)]
public struct DisplayMode
{
    [FieldOffset(0x0)]
    public uint Width;

    [FieldOffset(0x4)]
    public uint Height;

    [FieldOffset(0x8)]
    public DXGI_RATIONAL RefreshRate;

    [FieldOffset(0x10)]
    public DXGI_MODE_DESC DxgiDesc;
}
